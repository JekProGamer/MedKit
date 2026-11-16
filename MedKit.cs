using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using System.Drawing;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace MedKit;

public sealed class MedKitPlugin : BasePlugin, IPluginConfig<MedKitConfig>
{
    public MedKitConfig Config { get; set; } = null!;

    public void OnConfigParsed(MedKitConfig config)
    {
        Config = config;
    }
    private const string HealthshotName = "weapon_healthshot";
    private const int HealTargetHealth = 100;
    private const float AimStartHeight = 64.0f;
    private MedKitConfig Settings => Config;

    private readonly Dictionary<ulong, List<DateTime>> _healAttempts = new();
    private readonly Dictionary<ulong, DateTime> _healCooldowns = new();
    private readonly Dictionary<ulong, (int Count, DateTime CooldownUntil)> _hintMessageLimits = new();
    private readonly HashSet<ulong> _pendingHealthshotRestoreChecks = new();

    public override string ModuleName => "MedKit";
    public override string ModuleVersion => "0.9.11";
    public override string ModuleAuthor => "RezTop";
    public override string ModuleDescription => "Team-only healthshot medkit for Deathmatch";

    public override void Load(bool hotReload)
    {
        AddCommand("css_medkit", "Give yourself a MedKit healthshot (admins only)", (player, _) => OnMedKitCommand(player));
        RegisterListener<Listeners.OnPlayerButtonsChanged>(OnPlayerButtonsChanged);
        RegisterListener<Listeners.OnServerPrecacheResources>(OnServerPrecacheResources);
        RegisterEventHandler<EventItemPickup>(OnHealthshotPickup);
        RegisterEventHandler<EventItemEquip>(OnHealthshotEquip);

        Logger.LogInformation("MedKit loaded. Team-heal input listener registered. Admin test command: !medkit");
    }

    private void OnServerPrecacheResources(ResourceManifest manifest)
    {
        if (Settings.ParticleEnabled && !string.IsNullOrWhiteSpace(Settings.ParticleEffect))
            manifest.AddResource(Settings.ParticleEffect);
    }

    private HookResult OnHealthshotPickup(EventItemPickup gameEvent, GameEventInfo info)
    {
        var player = gameEvent.Userid;
        if (player != null && player.IsValid && IsHealthshotItem(gameEvent.Item))
        {
            PrintMedKitHint(player);
            ScheduleHealthshotLimit(player);
        }

        return HookResult.Continue;
    }

    private HookResult OnHealthshotEquip(EventItemEquip gameEvent, GameEventInfo info)
    {
        var player = gameEvent.Userid;
        if (player != null && player.IsValid && IsHealthshotItem(gameEvent.Item))
        {
            PrintMedKitHint(player);
            ScheduleHealthshotLimit(player);
        }

        return HookResult.Continue;
    }

    private static bool IsHealthshotItem(string item)
    {
        return !string.IsNullOrWhiteSpace(item) &&
               item.Contains("healthshot", StringComparison.OrdinalIgnoreCase);
    }

    private static List<CBasePlayerWeapon> GetHealthshots(CCSPlayerPawn pawn)
    {
        var healthshots = new List<CBasePlayerWeapon>();
        if (!pawn.IsValid)
            return healthshots;

        var activeWeapon = pawn.WeaponServices?.ActiveWeapon.Value;
        if (activeWeapon != null && activeWeapon.IsValid &&
            string.Equals(activeWeapon.DesignerName, HealthshotName, StringComparison.OrdinalIgnoreCase))
            healthshots.Add(activeWeapon);

        var weapons = pawn.WeaponServices?.MyWeapons;
        if (weapons == null)
            return healthshots;

        foreach (var weaponHandle in weapons)
        {
            var weapon = weaponHandle?.Value;
            if (weapon == null || !weapon.IsValid ||
                !string.Equals(weapon.DesignerName, HealthshotName, StringComparison.OrdinalIgnoreCase))
                continue;

            // The active weapon is also normally present in MyWeapons.
            if (activeWeapon != null && activeWeapon.IsValid &&
                weapon.EntityHandle.Raw == activeWeapon.EntityHandle.Raw)
                continue;

            healthshots.Add(weapon);
        }

        return healthshots;
    }

    private static bool HasHealthshot(CCSPlayerPawn pawn) => GetHealthshots(pawn).Count > 0;

    private static void EnforceOneHealthshot(CCSPlayerPawn pawn)
    {
        var healthshots = GetHealthshots(pawn);
        for (var i = 1; i < healthshots.Count; i++)
        {
            if (healthshots[i].IsValid)
                healthshots[i].Remove();
        }
    }

    private void OnMedKitCommand(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid)
            return;

        if (!AdminManager.PlayerHasPermissions(player, "@css/root"))
        {
            SendMessage(player, Settings.NoPermissionMessage);
            return;
        }

        if (!player.PawnIsAlive)
        {
            SendMessage(player, Settings.AliveOnlyMessage);
            return;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return;

        if (HasHealthshot(pawn))
            return;

        var item = player.GiveNamedItem(HealthshotName);
        if (item == nint.Zero)
        {
            SendMessage(player, Settings.GiveFailedMessage);
            return;
        }

        PrintMedKitHint(player);
        Logger.LogInformation("MedKit: admin {Player} ({SteamId}) received a test healthshot.",
            player.PlayerName, player.SteamID);
    }

    private void OnPlayerButtonsChanged(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released)
    {
        if (!player.IsValid)
            return;

        if (!player.PawnIsAlive || (pressed & PlayerButtons.Attack) == 0)
            return;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return;

        var activeWeapon = pawn.WeaponServices?.ActiveWeapon.Value;
        if (activeWeapon == null || !activeWeapon.IsValid ||
            !string.Equals(activeWeapon.DesignerName, HealthshotName, StringComparison.OrdinalIgnoreCase))
            return;

        // Remove the native healthshot immediately so its built-in self-heal
        // cannot be executed by the weapon after this input callback. For an
        // invalid target the item is restored once, after the current frame.
        activeWeapon.Remove();

        if (_healCooldowns.TryGetValue(player.SteamID, out var cooldownUntil))
        {
            if (DateTime.UtcNow < cooldownUntil)
            {
                RestoreHealthshotAfterBlockedUse(player);
                return;
            }

            _healCooldowns.Remove(player.SteamID);
            _healAttempts.Remove(player.SteamID);
        }

        var start = pawn.AbsOrigin;
        if (start == null)
        {
            PrintSelfUseHint(player);
            RestoreHealthshotAfterBlockedUse(player);
            return;
        }

        var eyePosition = new Vector(start.X, start.Y, start.Z + AimStartHeight);
        var trace = Trace.TraceShape(eyePosition, pawn.EyeAngles, pawn);
        if (!trace.DidHit())
        {
            PrintSelfUseHint(player);
            RestoreHealthshotAfterBlockedUse(player);
            return;
        }

        var hitEntity = trace.HitEntity();
        if (hitEntity == null || !hitEntity.IsValid)
        {
            PrintSelfUseHint(player);
            RestoreHealthshotAfterBlockedUse(player);
            return;
        }

        CCSPlayerPawn? targetPawn;
        try
        {
            targetPawn = hitEntity.As<CCSPlayerPawn>();
        }
        catch
        {
            PrintSelfUseHint(player);
            RestoreHealthshotAfterBlockedUse(player);
            return;
        }

        if (targetPawn == null || !targetPawn.IsValid)
        {
            PrintSelfUseHint(player);
            RestoreHealthshotAfterBlockedUse(player);
            return;
        }

        var target = targetPawn.Controller.Value?.As<CCSPlayerController>();
        if (target == null || !target.IsValid || !target.PawnIsAlive ||
            target.Slot == player.Slot || target.Team != player.Team)
        {
            PrintSelfUseHint(player);
            RestoreHealthshotAfterBlockedUse(player);
            return;
        }

        if (RegisterHealAttempt(player))
        {
            SendMessage(player, Settings.AntiSpamMessage, ("COOLDOWN", Settings.AntiSpamCooldownSeconds));
            RestoreHealthshotAfterBlockedUse(player);
            return;
        }

        var oldHealth = targetPawn.Health;
        if (oldHealth >= HealTargetHealth)
        {
            SendMessage(player, Settings.AlreadyFullHealthMessage);
            RestoreHealthshotAfterBlockedUse(player);
            return;
        }

        // The native healthshot was already removed; successful healing consumes it.
        var restoredHealth = HealTargetHealth - oldHealth;
        targetPawn.Health = HealTargetHealth;
        Utilities.SetStateChanged(targetPawn, "CBaseEntity", "m_iHealth");
        SpawnHealParticles(targetPawn);

        SendMessage(player, Settings.HealerSuccessMessage,
            ("HEAL_AMOUNT", restoredHealth), ("OLD_HP", oldHealth), ("NEW_HP", HealTargetHealth));
        SendMessage(target, Settings.TargetSuccessMessage,
            ("HEAL_AMOUNT", restoredHealth), ("OLD_HP", oldHealth), ("NEW_HP", HealTargetHealth));
        Logger.LogInformation("MedKit: {Healer} healed {Target} from {OldHealth} to 100 HP.",
            player.PlayerName, target.PlayerName, oldHealth);
    }

    private void SpawnHealParticles(CCSPlayerPawn targetPawn)
    {
        if (!Settings.ParticleEnabled || !targetPawn.IsValid ||
            string.IsNullOrWhiteSpace(Settings.ParticleEffect))
            return;

        try
        {
            var particle = Utilities.CreateEntityByName<CParticleSystem>("info_particle_system");
            if (particle == null || !particle.IsValid)
                return;

            particle.EffectName = Settings.ParticleEffect;
            particle.StartActive = true;
            particle.Tint = Color.FromArgb(70, 255, 110);

            var origin = targetPawn.AbsOrigin;
            if (origin == null)
            {
                particle.Remove();
                return;
            }

            particle.ControlPointEnts[0] = new CHandle<CBaseEntity>(targetPawn.EntityHandle.Raw);
            particle.Teleport(
                new Vector(origin.X, origin.Y, origin.Z + Settings.ParticleHeightOffset),
                null,
                null);
            particle.DispatchSpawn();

            var duration = Math.Clamp(Settings.ParticleDurationSeconds, 0.1f, 30.0f);
            var fadeOut = Math.Clamp(Settings.ParticleFadeOutSeconds, 0.1f, 10.0f);

            AddTimer(duration, () =>
            {
                if (!particle.IsValid)
                    return;

                particle.AcceptInput("StopPlayEndCap");
                AddTimer(fadeOut, () =>
                {
                    if (particle.IsValid)
                        particle.Remove();
                });
            });
        }
        catch (Exception ex)
        {
            Logger.LogWarning("MedKit: failed to create healing particles: {Error}", ex.Message);
        }
    }

    // Returns true when the fifth teammate-use attempt occurs within 3 seconds.
    private bool RegisterHealAttempt(CCSPlayerController player)
    {
        var now = DateTime.UtcNow;
        var steamId = player.SteamID;

        if (!_healAttempts.TryGetValue(steamId, out var attempts))
        {
            attempts = new List<DateTime>();
            _healAttempts[steamId] = attempts;
        }

        attempts.RemoveAll(time => now - time > TimeSpan.FromSeconds(Math.Max(1, Settings.AntiSpamWindowSeconds)));
        attempts.Add(now);

        if (attempts.Count < Math.Max(1, Settings.AntiSpamMaxAttempts))
            return false;

        _healAttempts.Remove(steamId);
        _healCooldowns[steamId] = now + TimeSpan.FromSeconds(Math.Max(1, Settings.AntiSpamCooldownSeconds));
        return true;
    }

    private void RestoreHealthshotAfterBlockedUse(CCSPlayerController player)
    {
        var steamId = player.SteamID;

        // Only one restore sequence may be pending per player.
        if (!_pendingHealthshotRestoreChecks.Add(steamId))
            return;

        // Restore after 0.1 seconds, then keep the inventory capped at one
        // while delayed game-side item grants finish processing.
        AddTimer(0.1f, () =>
        {
            try
            {
                if (!player.IsValid || !player.PawnIsAlive)
                    return;

                var pawn = player.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid)
                    return;

                var healthshots = GetHealthshots(pawn);
                if (healthshots.Count == 0)
                    player.GiveNamedItem(HealthshotName);
                else
                    EnforceOneHealthshot(pawn);

                foreach (var delay in new[] { 0.25f, 0.75f, 1.5f, 3.0f, 5.0f })
                {
                    AddTimer(delay, () =>
                    {
                        if (!player.IsValid || !player.PawnIsAlive)
                            return;

                        var currentPawn = player.PlayerPawn.Value;
                        if (currentPawn != null && currentPawn.IsValid)
                            EnforceOneHealthshot(currentPawn);
                    });
                }
            }
            finally
            {
                AddTimer(5.1f, () => _pendingHealthshotRestoreChecks.Remove(steamId));
            }
        });
    }

    private void ScheduleHealthshotLimit(CCSPlayerController player)
    {
        AddTimer(0.1f, () =>
        {
            if (!player.IsValid || !player.PawnIsAlive)
                return;

            var pawn = player.PlayerPawn.Value;
            if (pawn != null && pawn.IsValid)
                EnforceOneHealthshot(pawn);
        });
    }

    private void PrintSelfUseHint(CCSPlayerController player)
    {
        SendLimitedHint(player, Settings.SelfUseMessage);
    }

    private void PrintMedKitHint(CCSPlayerController player)
    {
        SendLimitedHint(player, Settings.MedKitHintMessage);
    }

    private void SendLimitedHint(CCSPlayerController player, string message)
    {
        if (!player.IsValid)
            return;

        const int maxHintMessages = 6;
        var cooldown = TimeSpan.FromSeconds(7);
        var steamId = player.SteamID;
        var now = DateTime.UtcNow;
        var state = _hintMessageLimits.TryGetValue(steamId, out var existing)
            ? existing
            : (Count: 0, CooldownUntil: DateTime.MinValue);

        if (state.CooldownUntil > now)
            return;

        if (state.CooldownUntil != DateTime.MinValue)
            state = (0, DateTime.MinValue);

        SendMessage(player, message);
        state.Count++;
        if (state.Count >= maxHintMessages)
            state.CooldownUntil = now + cooldown;

        _hintMessageLimits[steamId] = state;
    }

    private static void SendMessage(CCSPlayerController player, string template, params (string Key, object Value)[] values)
    {
        var message = template;
        foreach (var (key, value) in values)
            message = message.Replace("{" + key + "}", value.ToString() ?? string.Empty);

        message = message
            .Replace("{WHITE}", ChatColors.White.ToString())
            .Replace("{RED}", ChatColors.Red.ToString())
            .Replace("{GREEN}", ChatColors.Green.ToString())
            .Replace("{YELLOW}", ChatColors.Yellow.ToString())
            .Replace("{PURPLE}", ChatColors.Purple.ToString())
            .Replace("{GREY}", ChatColors.Grey.ToString());

        player.PrintToChat(message);
    }

}


public sealed class MedKitConfig : BasePluginConfig
{
    public override int Version { get; set; } = 1;

    public int AntiSpamMaxAttempts { get; set; } = 5;
    public int AntiSpamWindowSeconds { get; set; } = 3;
    public int AntiSpamCooldownSeconds { get; set; } = 10;

    public bool ParticleEnabled { get; set; } = true;
    public string ParticleEffect { get; set; } = "particles/lightfountain/wonderinglights.vpcf";
    public float ParticleDurationSeconds { get; set; } = 2.5f;
    public float ParticleFadeOutSeconds { get; set; } = 1.5f;
    public float ParticleHeightOffset { get; set; } = 8.0f;

    // Color tags: {WHITE}, {RED}, {GREEN}, {YELLOW}, {PURPLE}, {GREY}.
    // Placeholders: {HEAL_AMOUNT}, {OLD_HP}, {NEW_HP}, {COOLDOWN}.
    public string NoPermissionMessage { get; set; } = "{RED}[MedKit] {WHITE}У тебя нет доступа к этой команде.";
    public string AliveOnlyMessage { get; set; } = "{RED}[MedKit] {WHITE}Использовать команду можно только после возрождения.";
    public string GiveFailedMessage { get; set; } = "{RED}[MedKit] {WHITE}Не удалось выдать аптечку.";
    public string ReturnFailedMessage { get; set; } = "{RED}[MedKit] {WHITE}Не удалось вернуть шприц.";
    public string SelfUseMessage { get; set; } = "{WHITE}[{PURPLE}MedKit{WHITE}] {RED}Нельзя {WHITE}использовать шприц на себе, лечи {GREEN}тиммейта{WHITE}.";
    public string MedKitHintMessage { get; set; } = "{WHITE}[{PURPLE}Med-kit {WHITE}- {YELLOW}Friend {WHITE}| {GREY}Используй его на тиммейта, чтобы пополнить его {GREEN}HP 100{WHITE}.";
    public string AlreadyFullHealthMessage { get; set; } = "{YELLOW}[MedKit] {WHITE}У тиммейта уже {GREEN}100 HP{WHITE}, лечить не нужно.";
    public string AntiSpamMessage { get; set; } = "{WHITE}[{PURPLE}MedKit{WHITE}] {RED}Антиспам! {WHITE}Лечение заблокировано на {YELLOW}{COOLDOWN} секунд{WHITE}.";
    public string HealerSuccessMessage { get; set; } = "{WHITE}[{PURPLE}MedKit{WHITE}] {WHITE}Ты восстановил тиммейту {YELLOW}{HEAL_AMOUNT} HP {GREY}({YELLOW}{OLD_HP}{GREY} -> {GREEN}{NEW_HP} HP{GREY}).";
    public string TargetSuccessMessage { get; set; } = "{WHITE}[{PURPLE}MedKit{WHITE}] {WHITE}Тебе восстановили {YELLOW}{HEAL_AMOUNT} HP {GREY}(теперь {GREEN}{NEW_HP} HP{GREY}).";
}

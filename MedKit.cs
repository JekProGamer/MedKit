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
    private readonly HashSet<ulong> _pendingHealthshotReturns = new();

    public override string ModuleName => "MedKit";
    public override string ModuleVersion => "0.9.2";
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
            PrintMedKitHint(player);

        return HookResult.Continue;
    }

    private HookResult OnHealthshotEquip(EventItemEquip gameEvent, GameEventInfo info)
    {
        var player = gameEvent.Userid;
        if (player != null && player.IsValid && IsHealthshotItem(gameEvent.Item))
            PrintMedKitHint(player);

        return HookResult.Continue;
    }

    private static bool IsHealthshotItem(string item)
    {
        return !string.IsNullOrWhiteSpace(item) &&
               item.Contains("healthshot", StringComparison.OrdinalIgnoreCase);
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
        if (!player.IsValid || !player.PawnIsAlive || (pressed & PlayerButtons.Attack) == 0)
            return;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return;

        var activeWeapon = pawn.WeaponServices?.ActiveWeapon.Value;
        if (activeWeapon == null || !activeWeapon.IsValid ||
            !string.Equals(activeWeapon.DesignerName, HealthshotName, StringComparison.OrdinalIgnoreCase))
            return;

        // Remove the healthshot before the game can run its native self-heal.
        // It is restored below unless a valid teammate is successfully healed.
        if (!player.RemoveItemByDesignerName(HealthshotName))
        {
            activeWeapon.Remove();
        }

        // During cooldown, consume the click without allowing the game's native self-heal.
        if (_healCooldowns.TryGetValue(player.SteamID, out var cooldownUntil))
        {
            if (DateTime.UtcNow < cooldownUntil)
            {
                ReturnHealthshot(player);
                return;
            }

            _healCooldowns.Remove(player.SteamID);
            _healAttempts.Remove(player.SteamID);
        }

        var start = pawn.AbsOrigin;
        if (start == null)
        {
            ReturnHealthshot(player);
            return;
        }

        var eyePosition = new Vector(start.X, start.Y, start.Z + AimStartHeight);
        var trace = Trace.TraceShape(eyePosition, pawn.EyeAngles, pawn);
        if (!trace.DidHit())
        {
            ReturnHealthshot(player);
            PrintSelfUseHint(player);
            return;
        }

        var hitEntity = trace.HitEntity();
        if (hitEntity == null || !hitEntity.IsValid)
        {
            ReturnHealthshot(player);
            PrintSelfUseHint(player);
            return;
        }

        CCSPlayerPawn? targetPawn;
        try
        {
            targetPawn = hitEntity.As<CCSPlayerPawn>();
        }
        catch
        {
            ReturnHealthshot(player);
            PrintSelfUseHint(player);
            return;
        }

        if (targetPawn == null || !targetPawn.IsValid)
        {
            ReturnHealthshot(player);
            PrintSelfUseHint(player);
            return;
        }

        var target = targetPawn.Controller.Value?.As<CCSPlayerController>();
        if (target == null || !target.IsValid || !target.PawnIsAlive ||
            target.Slot == player.Slot || target.Team != player.Team)
        {
            ReturnHealthshot(player);
            PrintSelfUseHint(player);
            return;
        }

        if (RegisterHealAttempt(player))
        {
            ReturnHealthshot(player);
            SendMessage(player, Settings.AntiSpamMessage, ("COOLDOWN", Settings.AntiSpamCooldownSeconds));
            return;
        }

        var oldHealth = targetPawn.Health;
        if (oldHealth >= HealTargetHealth)
        {
            ReturnHealthshot(player);
            SendMessage(player, Settings.AlreadyFullHealthMessage);
            return;
        }

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

    private void ReturnHealthshot(CCSPlayerController player)
    {
        if (!player.IsValid || !player.PawnIsAlive)
            return;

        var originalPawn = player.PlayerPawn.Value;
        if (originalPawn == null || !originalPawn.IsValid)
            return;

        var steamId = player.SteamID;

        // Several input callbacks can request a return before the timer fires.
        // Keep only one pending return per player.
        if (!_pendingHealthshotReturns.Add(steamId))
            return;

        var originalPawnHandle = originalPawn.EntityHandle.Raw;

        // Wait until native healthshot-use handling has finished.
        AddTimer(0.15f, () =>
        {
            _pendingHealthshotReturns.Remove(steamId);

            if (!player.IsValid || !player.PawnIsAlive)
                return;

            var currentPawn = player.PlayerPawn.Value;
            if (currentPawn == null || !currentPawn.IsValid ||
                currentPawn.EntityHandle.Raw != originalPawnHandle)
                return;

            // The game may already have restored/kept the healthshot.
            // Never give another one if one is present in the inventory.
            var hasHealthshot = currentPawn.WeaponServices?.MyWeapons.Any(weapon =>
                weapon != null && weapon.IsValid &&
                weapon.Value != null && weapon.Value.IsValid &&
                string.Equals(weapon.Value.DesignerName, HealthshotName, StringComparison.OrdinalIgnoreCase)) == true;

            if (hasHealthshot)
                return;

            var item = player.GiveNamedItem(HealthshotName);
            if (item == nint.Zero)
                SendMessage(player, Settings.ReturnFailedMessage);
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

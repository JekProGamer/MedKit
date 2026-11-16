using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;

namespace MedKit;

public sealed class MedKitPlugin : BasePlugin
{
    private const string HealthshotName = "weapon_healthshot";
    private const int HealTargetHealth = 100;
    private const float AimStartHeight = 64.0f;

    public override string ModuleName => "MedKit";
    public override string ModuleVersion => "0.3.0";
    public override string ModuleAuthor => "RezTop";
    public override string ModuleDescription => "Team-only healthshot medkit for Deathmatch";

    public override void Load(bool hotReload)
    {
        AddCommand("css_medkit", "Give yourself a MedKit healthshot (admins only)", OnMedKitCommand);
        RegisterListener<Listeners.OnPlayerButtonsChanged>(OnPlayerButtonsChanged);

        Logger.LogInformation("MedKit loaded. Team-heal input listener registered. Admin test command: !medkit");
    }

    private void OnMedKitCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || !player.IsValid)
            return;

        if (!AdminManager.PlayerHasPermissions(player, "@css/root"))
        {
            player.PrintToChat($"{ChatColors.Red}[MedKit] {ChatColors.White}У тебя нет доступа к этой команде.");
            return;
        }

        if (!player.PawnIsAlive)
        {
            player.PrintToChat($"{ChatColors.Red}[MedKit] {ChatColors.White}Использовать команду можно только после возрождения.");
            return;
        }

        var item = player.GiveNamedItem(HealthshotName);
        if (item == nint.Zero)
        {
            player.PrintToChat($"{ChatColors.Red}[MedKit] {ChatColors.White}Не удалось выдать аптечку.");
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

        var target = targetPawn.Controller.Value;
        if (target == null || !target.IsValid || !target.PawnIsAlive ||
            target.Slot == player.Slot || target.Team != player.Team)
        {
            ReturnHealthshot(player);
            PrintSelfUseHint(player);
            return;
        }

        var oldHealth = targetPawn.Health;
        if (oldHealth >= HealTargetHealth)
        {
            ReturnHealthshot(player);
            player.PrintToChat($"{ChatColors.Yellow}[MedKit] {ChatColors.White}У тиммейта уже полное здоровье.");
            return;
        }

        targetPawn.Health = HealTargetHealth;
        Utilities.SetStateChanged(targetPawn, "CBaseEntity", "m_iHealth");

        player.PrintToChat($"{ChatColors.Green}[MedKit] {ChatColors.White}Ты вылечил тиммейта: {ChatColors.Green}{oldHealth} {ChatColors.White}-> {ChatColors.Green}100 HP");
        target.PrintToChat($"{ChatColors.Green}[MedKit] {ChatColors.White}Тебя вылечил тиммейт до {ChatColors.Green}100 HP{ChatColors.White}.");
        Logger.LogInformation("MedKit: {Healer} healed {Target} from {OldHealth} to 100 HP.",
            player.PlayerName, target.PlayerName, oldHealth);
    }

    private static void ReturnHealthshot(CCSPlayerController player)
    {
        // Restore the item when no valid teammate heal took place.
        var item = player.GiveNamedItem(HealthshotName);
        if (item == nint.Zero)
        {
            player.PrintToChat($"{ChatColors.Red}[MedKit] {ChatColors.White}Не удалось вернуть шприц.");
        }
    }

    private static void PrintSelfUseHint(CCSPlayerController player)
    {
        player.PrintToChat(
            $"{ChatColors.White}[{ChatColors.Purple}MedKit{ChatColors.White}] {ChatColors.Red}Нельзя {ChatColors.White}использовать шприц на себе, лечи {ChatColors.Green}тиммейта{ChatColors.White}.");
    }

    private static void PrintMedKitHint(CCSPlayerController player)
    {
        player.PrintToChat(
            $"{ChatColors.White}[{ChatColors.Purple}Med-kit {ChatColors.White}- " +
            $"{ChatColors.Yellow}Friend {ChatColors.White}| {ChatColors.Grey}" +
            $"Используй его на тиммейта, чтобы пополнить его {ChatColors.Green}HP 100{ChatColors.White}.");
    }
}

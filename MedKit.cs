using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;

namespace MedKit;

public sealed class MedKitPlugin : BasePlugin
{
    public override string ModuleName => "MedKit";
    public override string ModuleVersion => "0.1.0";
    public override string ModuleAuthor => "RezTop";
    public override string ModuleDescription => "Team-only healthshot medkit for Deathmatch";

    public override void Load(bool hotReload)
    {
        // CounterStrikeSharp converts css_medkit into the chat command !medkit.
        AddCommand("css_medkit", "Give yourself a MedKit healthshot (admins only)", OnMedKitCommand);

        Logger.LogInformation("MedKit loaded. Admin test command: !medkit");
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

        var item = player.GiveNamedItem("weapon_healthshot");
        if (item == nint.Zero)
        {
            player.PrintToChat($"{ChatColors.Red}[MedKit] {ChatColors.White}Не удалось выдать аптечку.");
            return;
        }

        PrintMedKitHint(player);
        Logger.LogInformation("MedKit: admin {Player} ({SteamId}) received a test healthshot.",
            player.PlayerName, player.SteamID);
    }

    private static void PrintMedKitHint(CCSPlayerController player)
    {
        player.PrintToChat(
            $"{ChatColors.White}[{ChatColors.Purple}Med-kit {ChatColors.White}- " +
            $"{ChatColors.Yellow}Friend {ChatColors.White}| {ChatColors.Grey}" +
            $"Используй его на тиммейта, чтобы пополнить его {ChatColors.Green}HP 100{ChatColors.White}.");
    }
}

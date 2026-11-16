# MedKit

CounterStrikeSharp plugin for CS2 Deathmatch.

## Current draft: 0.6.0

Implemented in source:
- `!medkit` test command (registered as `css_medkit`).
- Restricted to admins with the `@css/root` permission.
- Gives the admin a standard `weapon_healthshot`.
- Listens for the primary-attack button while the healthshot is active.
- Removes the healthshot before native use is allowed to proceed, preventing the owner's standard self-heal.
- Traces the player's aim and heals a living same-team player hit by the trace to 100 HP.
- Prints the colored Russian instruction and healing feedback.


## Configuration

The plugin uses the CounterStrikeSharp plugin configuration system. On first load, it creates:

```
addons/counterstrikesharp/configs/plugins/MedKit/MedKit.json
```

Anti-spam settings (values are in seconds):
- `AntiSpamMaxAttempts`: teammate-use attempts required to trigger the block (default: `5`).
- `AntiSpamWindowSeconds`: time window for counting attempts (default: `3`).
- `AntiSpamCooldownSeconds`: duration of the block (default: `10`).

All player-facing chat messages are configurable in the same JSON file. Message templates support color tags `{WHITE}`, `{RED}`, `{GREEN}`, `{YELLOW}`, `{PURPLE}`, `{GREY}` and placeholders `{HEAL_AMOUNT}`, `{OLD_HP}`, `{NEW_HP}`, `{COOLDOWN}`.

After editing the JSON, reload the plugin or restart the server to apply changes.

## Validation status

The source implementation has been added, but it has **not yet been compiled or tested on a live CS2 server**. In particular, verify that the button-change listener runs early enough on the server build to remove the healthshot before the native weapon action. If the game processes the healthshot first, a lower-level pre-hook will be required.

## Requirements

- .NET 8 SDK
- CounterStrikeSharp.API 1.0.380

## Build

Run from the repository root:

```bash
dotnet build -c Release
```

Install `bin/Release/net8.0/MedKit.dll` into:

```
game/csgo/addons/counterstrikesharp/plugins/MedKit/
```

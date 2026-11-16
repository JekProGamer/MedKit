# MedKit

CounterStrikeSharp plugin for CS2 Deathmatch.

## Current draft: 0.1.0

Implemented:
- `!medkit` test command (registered as `css_medkit`).
- Restricted to admins with the `@css/root` permission.
- Gives the admin a `weapon_healthshot`.
- Prints the colored Russian instruction message.

Not implemented yet:
- Aiming at a teammate and using the healthshot to heal them.
- Blocking the native self-heal.
- Restoring teammate health up to 100 HP.

The current commit is a scaffold for the admin test command, not the completed teammate-healing mechanic. The latter requires hooking/intercepting the healthshot use behavior and must be tested on the target CounterStrikeSharp/CS2 server build.

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

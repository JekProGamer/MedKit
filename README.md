# MedKit

CounterStrikeSharp plugin for CS2 Deathmatch.

## Current draft: 0.7.0

Implemented in source:
- `!medkit` test command (registered as `css_medkit`).
- Restricted to admins with the `@css/root` permission.
- Gives the admin a standard `weapon_healthshot`.
- Listens for the primary-attack button while the healthshot is active.
- Removes the healthshot before native use is allowed to proceed, preventing the owner's standard self-heal.
- Traces the player's aim and heals a living same-team player hit by the trace to 100 HP.
- Prints the colored Russian instruction and healing feedback.
- Spawns a green-tinted built-in CS2 particle effect on the healed teammate; the effect is attached to the player, stopped after a configurable duration, and then removed after its fade-out window.


## Configuration

The plugin uses the CounterStrikeSharp plugin configuration system. On first load, it creates:

```
addons/counterstrikesharp/configs/plugins/MedKit/MedKit.json
```

Anti-spam settings (values are in seconds):
- `AntiSpamMaxAttempts`: teammate-use attempts required to trigger the block (default: `5`).
- `AntiSpamWindowSeconds`: time window for counting attempts (default: `3`).
- `AntiSpamCooldownSeconds`: duration of the block (default: `10`).

Particle settings:
- `ParticleEnabled`: enable/disable the healing effect (default: `true`).
- `ParticleEffect`: built-in particle resource path (default: `particles/lightfountain/wonderinglights.vpcf`).
- `ParticleDurationSeconds`: how long the effect emits before it is told to stop (default: `2.5`).
- `ParticleFadeOutSeconds`: cleanup delay after the stop/end-cap input (default: `1.5`).
- `ParticleHeightOffset`: vertical offset from the teammate origin (default: `8`).

The particle is tinted green by the plugin. The movement speed is defined inside the built-in particle asset and is not exposed as a generic CounterStrikeSharp setting; changing it would require a particle asset that supports a speed control.

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

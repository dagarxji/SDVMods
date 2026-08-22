# TimeSpeed Profiles

A small SMAPI companion for **TimeSpeed 2.8.1** that maintains two complete TimeSpeed configurations:

- **Single Player Profile**
- **Multiplayer / Co-op Profile** (also used for split-screen)

The active profile is chosen using `Context.IsMultiplayer` and copied into TimeSpeed's normal `config.json`. TimeSpeed's own private reload routine is then invoked, so **TimeSpeed still owns all timing behavior**; this mod only chooses which complete config it receives.

## What is included in each profile

Every TimeSpeed 2.8.1 config setting is mirrored:

- `EnableOnFestivalDays`
- `LocationNotify`
- `SecondsPerMinute`
  - `Indoors`
  - `Outdoors`
  - `Mines`
  - `SkullCavern`
  - `VolcanoDungeon`
  - `ByLocationName` custom overrides
- `FreezeTime`
  - `AnywhereAtTime`
  - `PassOut`
  - `Indoors`
  - `Outdoors`
  - `Mines`
  - `SkullCavern`
  - `VolcanoDungeon`
  - `ByLocationName`
  - `ExceptLocationNames`
- `LetFarmhandsManageTime`
- `Keys`
  - `FreezeTime`
  - `IncreaseTickInterval`
  - `DecreaseTickInterval`
  - `ReloadConfig`

Notably, the companion exposes **`SecondsPerMinute.ByLocationName` in GMCM**, even though TimeSpeed 2.8.1 only exposes that setting through its JSON file. Enter it as e.g.:

```text
Farm=0.9, FarmHouse=2.0
```

## First run

If this companion has no `config.json` yet, it imports the current TimeSpeed `config.json` into **both profiles**. This preserves your current setup as the starting point.

After that, the companion's own `config.json` is authoritative. TimeSpeed's `config.json` is an automatically generated active copy and should not be edited directly.

## Generic Mod Config Menu

If Generic Mod Config Menu is installed, **TimeSpeed Profiles** gets a main page with links to separate Single Player and Multiplayer / Co-op profile pages.

The original TimeSpeed GMCM registration is hidden while this companion is installed, since edits made there would only affect the generated active copy and would be overwritten the next time a profile is applied.

## When profiles are applied

The selected profile is applied:

1. after TimeSpeed's `SaveLoaded` handler;
2. immediately after saving settings in this companion's GMCM menu while a world is loaded; and
3. if `Context.IsMultiplayer` changes while a world is running.

## Requirements

- Stardew Valley 1.6.15+
- SMAPI 4.1.10+
- TimeSpeed 2.8.1+
- Generic Mod Config Menu 1.15+ (optional, but recommended)

This source targets TimeSpeed **2.8.1's exact config schema**. If a later TimeSpeed release adds config fields, the companion logs a warning so its schema can be updated.

## Build

This project uses `Pathoschild.Stardew.ModBuildConfig` 4.4.0, the same build-config version used by the current Stardew mod ecosystem.

On a PC with Stardew Valley and the .NET SDK installed:

```powershell
dotnet build -c Release
```

ModBuildConfig should locate your Stardew installation and copy/package the built mod. If it can't find the game, follow the ModBuildConfig instructions to set your game path.

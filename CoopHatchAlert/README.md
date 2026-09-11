# Coop Hatch Alert

A small Stardew Valley 1.6 SMAPI mod that makes incubators much harder to forget.

## Features

- **Hatch-ready notification**: shows a HUD message as soon as an incubated egg becomes ready.
- **Full-coop warning**: if the egg is finished but the coop has no animal slot, the alert says so.
- **Exterior marker**: a bobbing egg/`!` marker appears above the specific coop while a hatch is waiting.
- **Incubator hover tooltip**: hover the incubator inside the coop to see:
  - `Empty`;
  - egg type + exact remaining incubation time; or
  - `Ready to hatch!` / `Ready — coop is full`.
- Supports multiple animal buildings and checks Stardew 1.6's `IsIncubator` machine flag rather than hard-coding the vanilla incubator item ID.

The mod does **not** alter incubation or hatch mechanics. It only reads the same incubator state vanilla uses.

## Install (compiled mod)

1. Install SMAPI for Stardew Valley 1.6.
2. Build this project (instructions below).
3. Copy the resulting `CoopHatchAlert` mod folder into your Stardew Valley `Mods` folder.
4. Launch Stardew through SMAPI.

## Build

This is a source package. It uses the standard `Pathoschild.Stardew.ModBuildConfig` NuGet package, which automatically locates Stardew Valley and adds the game/SMAPI references.

### Windows

Open PowerShell in this folder and run:

```powershell
dotnet restore
dotnet build -c Release
```

### Linux / Steam Deck / macOS

```bash
dotnet restore
dotnet build -c Release
```

If the build package can't find your Stardew installation, follow its `stardewvalley.targets` game-path instructions.

## Configuration

SMAPI creates/uses `config.json`. Defaults:

```json
{
  "EnableNotifications": true,
  "NotifyWhenCoopIsFull": true,
  "EnableExteriorMarker": true,
  "EnableExteriorTooltip": true,
  "EnableIncubatorTooltip": true,
  "ShowEmptyIncubatorTooltip": true,
  "MarkerScale": 3.0
}
```

`MarkerScale` is clamped from 1 to 6.

## Notes

- A hatch is considered ready when the incubator has an egg and `MinutesUntilReady <= 0`.
- Vanilla itself starts the hatch/naming event when you enter the AnimalHouse and only if the house isn't full; this mod mirrors that condition for alerts.
- The marker disappears as soon as vanilla consumes the egg during the hatch process.
- Notifications are intentionally not replayed just because you load a save which already has a ready egg; the exterior marker still appears.

## Files

- `ModEntry.cs` — state scanning, alerts, marker drawing, and tooltip.
- `ModConfig.cs` — user configuration.
- `manifest.json` — SMAPI metadata.
- `CoopHatchAlert.csproj` — .NET/SMAPI build project.

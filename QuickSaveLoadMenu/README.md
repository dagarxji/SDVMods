# QuickSave Load Menu

A small SMAPI companion mod for **DLX.QuickSave**. It adds a `QS` button beside each farm on Stardew Valley's **Load Game** screen.

## Behavior

- Normal click on a save slot: unchanged vanilla behavior.
- `QS` button enabled: that farm has a `Quicksave` midday save.
- `QS` button disabled/gray: no midday QuickSave was found for that farm.
- Hovering an enabled `QS` button shows the QuickSave's season, day, year, and time of day.
- Clicking enabled `QS`:
  1. starts Stardew's normal load for that farm;
  2. waits until the save is initialized and QuickSave's normal load guards allow loading;
  3. calls QuickSave's public `TryLoad` API for the `Quicksave` file.

So the user only needs one click from the title-screen load menu even though QuickSave still requires the vanilla save to initialize internally first.

## Requirements

- Stardew Valley 1.6.x (targeted at 1.6.15)
- SMAPI 4.x
- QuickSave 1.4.0+ (targeted/tested against the 1.5.0 API shape)

## Build

From this directory:

```powershell
dotnet build -c Release
```

`Pathoschild.Stardew.ModBuildConfig` will locate Stardew Valley, reference the game/SMAPI/Harmony assemblies, deploy the mod to your Mods folder, and create a release zip under `bin`.

If your Stardew install isn't auto-detected, add this inside the `.csproj` `<PropertyGroup>`:

```xml
<GamePath>C:\Path\To\Stardew Valley</GamePath>
```

## Files

- `ModEntry.cs` — load-menu patch, QuickSave handoff, and save-folder detection.
- `IQuickSaveApi.cs` — public subset interface for QuickSave's SMAPI API.
- `manifest.json` — declares QuickSave as a required dependency.
- `QuickSaveLoadMenu.csproj` — .NET 6 SMAPI project with Harmony enabled.

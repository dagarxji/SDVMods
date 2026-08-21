# Fishing Forecast 0.1.1

A Stardew Valley 1.6 SMAPI mod that ranks the best accessible fishing locations throughout the current day.

## What it shows

Press **P** (configurable) to open a forecast with five four-hour windows:

- 6:00 AM–10:00 AM
- 10:00 AM–2:00 PM
- 2:00 PM–6:00 PM
- 6:00 PM–10:00 PM
- 10:00 PM–2:00 AM

Each window shows the top three reachable fishing locations, estimated gold/hour, approximate travel time, and the catch contributing the most expected value.

## Ranking model

The mod samples Stardew Valley's own 1.6 `GameLocation.GetFishFromLocationData` selector on representative fishable tiles in each location. That means the spawn side automatically inherits most vanilla/content-pack rules, including:

- season and local weather;
- time of day;
- fishing level;
- water depth/distance from shore;
- fish areas and bobber-position rules;
- daily luck / luck level when a fish rule uses them;
- Magic Bait, targeted bait, Curiosity Lure and other fish-spawn modifiers used by the game's location fish data;
- catch limits and game-state conditions;
- modded fish/location rules implemented through Stardew 1.6 `Data/Locations`.

It also estimates bite frequency using the game's vanilla fishing-level, bait, Spinner, and Dressed Spinner timing formula, then converts expected catch value into an approximate gold/hour rate.

### Important equipment note

**Select/hold the fishing rod you intend to use before pressing P.** Stardew's fish selector reads bait/tackle from the player's current tool. If no fishing rod is selected, the forecast still works but does not include bait/tackle bonuses.

### Travel / reachability

**World Navigator is optional but recommended.** Fishing Forecast asks World Navigator 1.4+ for `GetRoutesForCurrentlyReachableLocations()` when available. That gives much better handling for locked doors, transport and modded locations.

If World Navigator isn't installed (or that API isn't available), Fishing Forecast falls back to traversing currently loaded vanilla warps. The fallback is intentionally approximate and can miss special transports or treat some conditional map connections imperfectly.

World Navigator's public API describes route steps/means of travel. If its returned route object exposes a numeric travel duration, Fishing Forecast uses it. Otherwise it estimates **10 in-game minutes per route transition**. The fallback uses the same 10-minute-per-warp estimate.

Travel affects the ranking by subtracting estimated travel time from the four-hour block before calculating expected block earnings.

## Deliberate approximations in 0.1.1

- It assumes you successfully catch hooked fish; it does not guess the player's minigame failure rate.
- Sell values use the caught item's current `sellToStorePrice()` value at normal quality; fish-quality/perfect-catch modeling is not yet included.
- The forecast assumes you successfully land every hooked fish; player-specific minigame failure rate is not modeled.
- Treasure-chest expected value and temporary map phenomena such as the exact future position of bubbles/frenzies are not included.
- Fish/location content added through Stardew 1.6 `Data/Locations` participates automatically, but a mod which replaces fishing behavior purely through custom C#/Harmony code may not be represented exactly.
- Current fishing bubbles/frenzies are not used for remote future locations.
- Each four-hour block samples once per in-game hour and uses representative fishing tiles instead of every water tile.
- Future opening-hour changes are handled by fish spawn conditions, but reachability itself is based on routes available when the menu is opened.

These choices make the forecast useful without modifying the save or causing a large pause every time P is pressed.

## RNG safety

Fish selection is random. Fishing Forecast temporarily substitutes a deterministic `Random` while it samples and restores Stardew's original random generator and `Game1.timeOfDay` afterward. It does not intentionally advance time or consume the normal gameplay RNG stream.

## Installation / building

This package contains source because it must be compiled against your local Stardew Valley + SMAPI assemblies.

1. Install Stardew Valley 1.6 and SMAPI.
2. Install the **.NET 6 SDK** if you don't already have a C# build environment.
3. Put this source folder anywhere convenient.
4. On Windows, run `BUILD-WINDOWS.ps1` from the source package root. From a PowerShell terminal you can use `powershell -ExecutionPolicy Bypass -File .\BUILD-WINDOWS.ps1`. You can also open `FishingForecast.csproj` in Visual Studio/Rider or run `dotnet build -c Release` manually.
5. `Pathoschild.Stardew.ModBuildConfig` will locate your Stardew installation and copy/build the mod. It also creates a release zip in the project's `bin` output.
6. Optionally install **World Navigator 1.4+** (and its required dependencies) for accurate reachability.

If ModBuildConfig can't locate Stardew automatically, set its `GamePath` as described in the SMAPI mod build package documentation.

## Configuration

SMAPI creates `config.json` after the first run. Defaults:

```json
{
  "OpenMenu": "P",
  "SamplesPerHour": 24,
  "TileScanStride": 2,
  "MaxTilesPerLocation": 6,
  "CastOverheadMilliseconds": 2600,
  "RealMillisecondsPerGameMinute": 700,
  "UseWorldNavigatorWhenAvailable": true
}
```

Increasing `SamplesPerHour` or `MaxTilesPerLocation` improves stability at the cost of a slower menu calculation.

## Debug command

From the SMAPI console:

```
fish_forecast
```

This recalculates and opens the same forecast window.

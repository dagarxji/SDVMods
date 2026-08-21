# SDVMods

A single repository containing all of my Stardew Valley SMAPI mods. Each mod lives in its own top-level directory.

## Mods

| Directory | Mod | Description |
|-----------|-----|-------------|
| [AutoEatEideeBridge](./AutoEatEideeBridge) | Auto-Eat + Eidee Easy Fishing Bridge | Bridges Auto Eat and Eidee's Easy Fishing mods. |
| [BetterBundleOverview](./BetterBundleOverview) | Better Bundle Overview | UI mod to create a better UI view for the community center. |
| [CrowdedTrees](./CrowdedTrees) | Crowded Wild Trees | Allow trees next to each other. |
| [FishingForecast](./FishingForecast) | Fishing Forecast | Shows best fishing locations for the day. |
| [RemoteGifts](./RemoteGifts) | Remote Social Interactions | Remotely interact with NPCs. |

## Building

Each mod has its own `.csproj` and (where applicable) a `build.ps1` script. Open or build the project file from within its directory. `Pathoschild.Stardew.ModBuildConfig` is used across all mods to resolve Stardew Valley and SMAPI references automatically.
Single repo for Stardew Valley mods

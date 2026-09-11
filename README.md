# SDVMods

A single repository containing all of my Stardew Valley SMAPI mods. Each mod lives in its own top-level directory.

## Mods

| Directory | Mod | Description |
|-----------|-----|-------------|
| [AutoEatEideeBridge](./AutoEatEideeBridge) | Auto-Eat + Eidee Easy Fishing Bridge | Bridges Auto-Eat and Eidee's Easy Fishing, with optional Fast Animations fishing-speed and TimeSpeed sync. |
| [BetterBundleOverview](./BetterBundleOverview) | Better Bundle Overview | Shows bundle requirements directly on the Community Center overview page. |
| [CoopHatchAlert](./CoopHatchAlert) | Coop Hatch Alert | Notifies you when an incubated egg is ready, marks the coop outside, and shows incubator status on hover. |
| [CrowdedTrees](./CrowdedTrees) | Crowded Trees | Allows regular and producing trees to grow beside trees of the same kind. |
| [FishingForecast](./FishingForecast) | Fishing Forecast | Ranks the best accessible fishing locations for five four-hour periods each day. |
| [ForegroundControllerInput](./ForegroundControllerInput) | Foreground Controller Input | Prevents Stardew Valley from receiving controller input while another application has focus. |
| [InventoryInsight](./InventoryInsight) | Inventory Insight | Adds a compact keep-or-sell panel to item tooltips with item details and uses. |
| [QuickSaveLoadMenu](./QuickSaveLoadMenu) | QuickSave Load Menu | Adds a QuickSave button to each farm on the Load Game screen. |
| [RemoteGifts](./RemoteGifts) | Remote Social Interactions | Makes Social-menu gift and talk icons clickable for remote gifting, quest delivery, and conversation. |
| [SeasonFlexibleCommunityCenter](./SeasonFlexibleCommunityCenter) | Season-Flexible Community Center | Trades currently obtainable seasonal items at scaled quantities for future-season bundle requirements. |
| [TimeSpeedProfiles](./TimeSpeedProfiles) | TimeSpeed Profiles | Automatically switches between TimeSpeed configuration profiles for single-player and multiplayer. |

## Building

Each mod has its own `.csproj` and (where applicable) a `build.ps1` script. Open or build the project file from within its directory. `Pathoschild.Stardew.ModBuildConfig` is used across all mods to resolve Stardew Valley and SMAPI references automatically.

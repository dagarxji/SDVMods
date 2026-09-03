# Repository instructions

This repository (SDVMods) contains **multiple independent Stardew Valley SMAPI mods**, each in
its own top-level directory (see the table in [README.md](../README.md)). Code, assets, and
config for one mod are unrelated to the others, so treat each mod directory as its own isolated
project.

## Context-efficient file search

When asked to make a change or investigate something in this repo, minimize context usage by
scoping your search instead of scanning the whole repository:

1. **Prefer already-open files first.** If the user has files open in the editor, check whether
   they are relevant to the request before searching anywhere else. If they're sufficient to
   complete the task, use them directly and skip a broader search.
2. **If the open files aren't enough, scope to the relevant mod directory.** Identify which
   top-level mod directory (e.g. `AutoEatEideeBridge/`, `BetterBundleOverview/`, `CrowdedTrees/`,
   `FishingForecast/`, `ForegroundControllerInput/`, `InventoryInsight/`, `QuickSaveLoadMenu/`,
   `RemoteGifts/`, `SeasonFlexibleCommunityCenter/`, `TimeSpeedProfiles/`) the request applies to
   — from the currently open files, the mod name/description mentioned, or the README table — and
   restrict searches (`grep`/`glob`/exploration) to that directory.
3. **Only fan out across the whole repo** if the mod can't be determined from context, the request
   is explicitly cross-mod/repo-wide, or a scoped search comes up empty.

# SDVMods Architecture

This file is an orientation aid for agents. Keep it factual and compact. Update it when repository structure or shared conventions materially change.

## Repository shape

SDVMods is a monorepo containing independent Stardew Valley SMAPI mods. Each top-level mod directory generally owns its own project, manifest, source, and build output.

Current top-level mod directories include:

- `AutoEatEideeBridge/`
- `BetterBundleOverview/`
- `CrowdedTrees/`
- `FishingForecast/`
- `ForegroundControllerInput/`
- `InventoryInsight/`
- `QuickSaveLoadMenu/`
- `RemoteGifts/`
- `SeasonFlexibleCommunityCenter/`
- `TimeSpeedProfiles/`

Do not assume this list is exhaustive forever; inspect the repository when the task depends on current contents.

## Layout is not perfectly uniform

Most mods place their `.csproj` near the mod directory root, but not every project uses the same nesting. For example, a mod may use a `src/<Project>/` layout.

Therefore, when entering a mod:

1. locate the `.csproj`;
2. locate `manifest.json`;
3. locate the `Mod` subclass / entry point;
4. inspect the `.csproj` before choosing syntax or dependencies;
5. inspect integration/config/i18n folders as relevant.

Do not impose a repository-wide directory refactor merely to normalize layouts.

## Build model

Projects currently target `.NET 6` and use `Pathoschild.Stardew.ModBuildConfig` across the repository.

Important consequences:

- build the target `.csproj` rather than assuming a root solution;
- ModBuildConfig resolves Stardew/SMAPI references;
- depending on local configuration, builds can deploy output into the installed Stardew `Mods` directory;
- a successful build verifies compilation, not runtime correctness.

The repository contains mixed C# language-version settings. Follow the target project's actual `.csproj`; do not mass-normalize `LangVersion`.

## Runtime architecture

A typical mod consists of:

- `ModEntry` (or another `Mod` subclass) as the SMAPI entry point;
- SMAPI event handlers;
- configuration model;
- optional GMCM registration;
- optional Harmony patches;
- optional APIs/integrations with other mods;
- `manifest.json`;
- optional `i18n/` files.

Keep `ModEntry` primarily as lifecycle/orchestration code when a feature is complex enough to justify separate components. For small mods, do not create extra layers merely to satisfy a pattern.

## State boundaries

Classify state before implementing it:

- process-global/static;
- per-screen;
- per-local-player;
- per-save;
- per-day;
- shared multiplayer world state;
- transient menu/UI state.

Initialize and clear state at the lifecycle boundary that owns it.

## Compatibility boundary

The default design assumption is that other content/expansion mods may be present.

Prefer:

- stable/qualified IDs;
- runtime data assets and registries;
- documented mod APIs;
- narrow Harmony patches;
- graceful optional-dependency behavior.

Avoid fixed vanilla content lists unless vanilla-only behavior is a deliberate feature constraint.

## Where knowledge belongs

- Stable repository structure and conventions: this file.
- Verified debugging discoveries and recurring traps: `ai/LEARNINGS.md`.
- Global Copilot behavior/context limits: `.github/copilot-instructions.md`.
- File-type/domain rules: `.github/instructions/`.
- Task-specialized behavior: `.github/agents/`.

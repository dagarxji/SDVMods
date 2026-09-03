---
description: 'SMAPI, Stardew Valley, Harmony, multiplayer, UI, and mod-compatibility rules.'
applyTo: '**/*.cs,**/*.csproj,**/manifest.json,**/i18n/*.json'
---

# Stardew Valley / SMAPI instructions

This is a multi-mod repository. Each top-level mod is independent unless explicitly designed as an integration.

## Project and build rules

- Locate and build the target mod's actual `.csproj`; do not assume a root solution exists.
- Existing projects target .NET 6, but always trust the target `.csproj` over repository-wide assumptions.
- Many projects use `Pathoschild.Stardew.ModBuildConfig`. When it is present, do not manually add direct SMAPI/Stardew DLL references unless there is a demonstrated need.
- ModBuildConfig may copy build output into the local Stardew `Mods` folder. Treat build output/deployment as a possible side effect.
- Use Release configuration only when release/package behavior is relevant; normal verification can use the project's usual build command.
- Do not change project/package versions as an incidental fix.

## SMAPI lifecycle

Use the narrowest appropriate SMAPI event.

Common lifecycle expectations:

- `GameLaunched`: acquire optional mod APIs and register GMCM options.
- `SaveLoaded`: initialize save-dependent state.
- `DayStarted` / `DayEnding`: daily state transitions.
- `ReturnedToTitle`: clear save-specific state and references.
- `UpdateTicked`: only for work that genuinely needs polling.
- `Input`: use SMAPI input events before low-level input patches when possible.
- `Display`: keep rendering hooks side-effect-light.

Do not access save-dependent game state during mod construction or before the relevant lifecycle state exists.

## Prefer public hooks over patches

Before adding Harmony, search SMAPI events and public game methods for a stable hook.

When Harmony is necessary:

- Patch the smallest method that owns the behavior.
- Prefer prefix/postfix over transpilers.
- Keep prefixes/postfixes short and defensive.
- Avoid replacing a whole vanilla method when a small state correction is sufficient.
- Do not suppress vanilla behavior unless the mod actually intends to replace it.
- Log patch failures clearly.
- Do not assume private method/field names without verifying the target game version.
- Consider other mods patching the same method; minimize side effects and ordering assumptions.

Use reflection only when no appropriate public API exists. Isolate reflection so version-sensitive code has one maintenance point.

## Diagnose before fixing

For an unknown runtime bug:

1. Read the relevant code and any supplied SMAPI log.
2. Identify the actual event/method/state path.
3. If necessary, add targeted debug logging that answers one specific question.
4. Only then implement the behavioral fix.

Do not invent Stardew/SMAPI members from memory when the project references can be inspected.

## Content and expansion-mod compatibility

Prefer runtime data over vanilla assumptions.

- Use qualified item IDs where applicable.
- Prefer game data/assets and runtime registries to fixed lists.
- Do not identify content by localized display name.
- Expect custom NPCs, locations, items, fish, recipes, machines, bundles, maps, and buildings.
- If a feature intentionally supports only vanilla content, make that boundary explicit rather than accidentally excluding modded content.
- When reading data from another mod, use its documented API or content contract when available.

## Optional integrations

For GMCM and other mod APIs:

- Check `Helper.ModRegistry.IsLoaded` or request the API safely.
- Handle a missing API without crashing unless it is a declared required dependency.
- Keep integration-specific code separated enough that the base mod still works without the optional mod.
- Respect the other mod's ownership of its config/state.

## Multiplayer and split-screen

Determine authority and scope explicitly.

- Use `Context.IsWorldReady`, `Context.IsMainPlayer`, `Context.IsMultiplayer`, and per-screen state only when their semantics match the feature.
- Shared world mutations generally need a host-authoritative design.
- Local UI/input preferences usually should not be host-authoritative.
- Use SMAPI multiplayer messaging for state that must cross peers.
- Avoid duplicate event effects on host and farmhands.
- Consider split-screen/per-screen state where UI or player-local state is involved.

## Save data

- Prefer SMAPI save-data APIs for mod-owned persistent state.
- Version persistent data when schema evolution is likely.
- Handle missing/old fields with safe defaults.
- Do not write save data every tick.
- Clear static/save-bound caches on return to title or save transition.

## UI and input

- Respect UI scale and viewport differences.
- Avoid fixed screen coordinates when existing menus/components provide layout anchors.
- Keep clickable component neighbor IDs and controller navigation coherent when editing menus.
- Use SMAPI input suppression only when the mod intentionally consumes the input.
- Do not globally suppress controller/keyboard input as a workaround for a menu-specific problem.
- Avoid mutating game state from a draw method unless the existing code path requires it.

## Logging

Use `Monitor.Log` with meaningful severity.

- `Trace`/`Debug`: diagnostic detail and high-frequency information.
- `Info`: meaningful lifecycle/user-visible state changes.
- `Warn`: recoverable unexpected conditions or degraded compatibility.
- `Error`: failed operations that prevent intended behavior.

Avoid per-tick log spam. Include stable identifiers and relevant state when diagnosing content.

## i18n

For user-facing text:

- Prefer `i18n/default.json` plus `Helper.Translation.Get(...)`.
- Do not use localized display strings as logic keys.
- Keep log-only technical diagnostics separate from player-facing translations unless they are intentionally shown to the player.

## Final verification

After editing a mod:

- Build its exact `.csproj`.
- Check new warnings/errors.
- If Harmony targets changed, verify the target method/signature against the referenced game version.
- If manifest/dependency behavior changed, verify `manifest.json`.
- State what requires an actual SMAPI/game test.

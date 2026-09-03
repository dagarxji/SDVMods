# SDVMods Agent Guide

This repository is a monorepo of independent Stardew Valley SMAPI mods. Treat each top-level mod directory as its own project unless the task explicitly spans multiple mods.

## Instruction priority

1. Follow the user's current request.
2. Follow `.github/copilot-instructions.md` for repository-wide context and behavior limits.
3. Follow matching `.github/instructions/*.instructions.md` files.
4. Follow this file.
5. Follow established patterns in the target mod.

Do not duplicate or expand repository-wide context merely because it exists elsewhere. Read additional files only when they are relevant to the current task.

## Before changing code

1. Identify the exact target mod and its `.csproj`, `manifest.json`, entry point, config model, integrations, and relevant existing code.
2. Inspect the target mod's actual project settings. Do not assume all mods have the same directory layout, C# language version, dependencies, or Harmony usage.
3. Search for an existing implementation or convention before adding a new abstraction.
4. For bugs, establish the root cause before implementing a fix.
5. If the root cause is uncertain, prefer narrow diagnostic logging or a minimal reproducer over a speculative workaround.
6. Preserve known-working behavior. Do not combine unrelated cleanup or refactoring with a bug fix.

## Scope discipline

- Make the smallest coherent change that satisfies the request.
- Do not modify other mods unless the task requires it.
- Do not rename, reorganize, or reformat unrelated code.
- Do not add dependencies when the existing SMAPI, game, .NET, or project APIs are sufficient.
- Do not change target framework, language version, package versions, manifest version, or release metadata unless the task requires it.
- Do not commit, push, tag, publish, or create releases unless explicitly requested.

## Root-cause-first debugging

For a reported runtime problem:

1. Read the relevant code path.
2. Read supplied SMAPI logs or stack traces before editing.
3. Identify what state or API behavior makes the symptom occur.
4. State the root cause in concrete terms.
5. Fix the cause at the narrowest reliable layer.
6. Build the target project.
7. Distinguish compile-time verification from in-game verification.

Avoid "try this and see" patches when the code can instead be inspected or instrumented.

## SMAPI design preferences

Prefer, in order:

1. Public SMAPI APIs and events.
2. Public Stardew Valley APIs and data assets.
3. Optional integration APIs exposed by other mods.
4. Harmony prefix/postfix patches when no stable public hook exists.
5. Reflection only when necessary and isolated.
6. Harmony transpilers only as a last resort.

A lower item is not automatically wrong, but it should have a concrete reason.

## Compatibility

Assume a user's save may include expansion or content mods unless a feature is intentionally vanilla-only.

- Prefer stable IDs, qualified item IDs, data assets, and runtime discovery over display names or fixed vanilla lists.
- Do not assume every NPC, location, item, recipe, fish, bundle, building, or map is vanilla.
- Handle optional mod integrations as optional: detect availability and fail gracefully.
- Do not mutate another mod's state or config unless that is explicitly the integration contract.
- Avoid broad Harmony patches that may conflict with other mods when a narrower hook is possible.

## Multiplayer

When gameplay state is involved, explicitly determine:

- whether the state is local-player-only, per-screen, per-farmhand, or shared;
- whether the host must be authoritative;
- whether synchronization is required;
- what happens on peer connect/disconnect and save load/unload.

Do not add `Context.IsMainPlayer` as a blanket fix. Use it only when host authority is actually correct.

## Performance

Stardew and SMAPI run most mod logic on the game thread.

- Keep `UpdateTicked`, input, draw, menu, and Harmony hot paths cheap.
- Avoid repeated full-world scans, disk I/O, reflection, asset loading, or large LINQ allocations per tick/frame.
- Cache expensive derived data when safe and define how that cache is invalidated.
- Prefer event-driven invalidation over polling.
- Never block the game thread waiting for background work.

## User-facing behavior

- Use SMAPI translation/i18n for user-facing text when the target mod already has i18n or the feature introduces meaningful UI text.
- Keep config backward compatible where practical.
- Treat Generic Mod Config Menu as an optional integration unless the manifest intentionally requires it.
- Log actionable failures through SMAPI's `Monitor`; avoid console spam.

## Verification

After code changes:

1. Build the exact target `.csproj`.
2. Resolve compiler errors rather than bypassing them with guessed APIs.
3. Review warnings introduced by the change.
4. Check that the build did not unintentionally alter generated/deployed files.
5. For runtime behavior, report what still requires testing under SMAPI.

A successful `dotnet build` proves compilation, not correct in-game behavior.

## Repository knowledge

Read `ai/ARCHITECTURE.md` when you need repository layout or build orientation.

Read `ai/LEARNINGS.md` when debugging a recurring problem or when a previous investigation may be relevant. Add to it only when a finding has been verified and is likely to prevent repeated mistakes.

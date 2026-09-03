# SDVMods Engineering Learnings

This is durable engineering memory, not a scratchpad.

Add an entry only when the finding is verified and likely to prevent a future repeated mistake. Do not add guesses, temporary hypotheses, generic coding advice, or one-off task notes.

## Verified repository baseline

### Build/runtime baseline

- Projects in this repository currently target `.NET 6`.
- C# language-version settings are not uniform across all mods; use the target `.csproj` as the source of truth.
- `Pathoschild.Stardew.ModBuildConfig` is used across the repository to resolve Stardew Valley / SMAPI references and may deploy builds to a local Mods directory.
- The repository is a multi-mod monorepo and project layouts are not perfectly uniform.
- Compilation is not sufficient proof for SMAPI runtime behavior; significant behavior changes require an in-game verification case.

## Durable debugging rules

These are intentionally strict because they prevent repeated low-quality fixes:

- Verify the relevant game/SMAPI API or existing code path instead of inventing members from memory.
- Establish a concrete root cause before replacing behavior.
- If the root cause cannot yet be established, add targeted diagnostics that answer a specific question.
- Keep bug fixes separate from unrelated refactors so regressions remain attributable.
- Prefer correcting the faulty state/data/transition over building a parallel replacement system.
- Preserve working behavior around the failing path unless evidence shows it must change.

## Adding a learning

Use this format:

```markdown
### YYYY-MM-DD — <Mod>: <short title>

**Symptom:** What was observed.

**Root cause:** The verified reason it happened.

**Fix/pattern:** What solved it or what implementation rule should be reused.

**Evidence:** Log, stack trace, API behavior, test result, or code path that verified the conclusion.

**Regression risk:** What future changes could reintroduce the problem.
```

## Mod-specific learnings

<!-- Add verified entries below this line. Keep newest entries first. -->

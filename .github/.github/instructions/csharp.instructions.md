---
description: 'C# implementation rules for the SDVMods repository.'
applyTo: '**/*.cs'
---

# C# instructions

Follow the target project's existing compiler settings and local style.

## Compatibility and project settings

- Do not change `TargetFramework`, `LangVersion`, nullable settings, or analyzer settings merely to make new syntax convenient.
- This repository currently contains .NET 6 projects with differing language-version settings. Write code that compiles under the target project's actual `.csproj`.
- Respect nullable annotations. Fix nullability issues with correct state modeling or guards, not indiscriminate `!`.
- Avoid new NuGet dependencies unless they provide clear value that cannot reasonably be achieved with existing dependencies.

## Implementation style

- Prefer clear, direct code over speculative frameworks or abstractions.
- Keep methods focused, but do not extract working logic solely to make a diff look cleaner.
- Use descriptive names tied to game/mod concepts.
- Preserve existing public/internal visibility unless a wider surface is needed.
- Prefer immutable/local state where practical; make ownership of mutable state obvious.
- Avoid magic strings for stable identifiers when an existing constant or typed API exists.
- Do not swallow exceptions. Catch only where recovery or better context is possible.
- When logging an exception, include enough context to identify the operation that failed.

## Hot paths

Treat update, input, rendering, menu drawing, and Harmony-patched methods as performance-sensitive.

- Avoid unnecessary allocations and repeated LINQ enumeration in per-frame/per-tick code.
- Avoid repeated reflection in hot paths; resolve and cache metadata outside the hot path when reflection is unavoidable.
- Do not perform file/network I/O on a game-loop hot path.
- Do not repeatedly enumerate every location/NPC/object when an event or cache can narrow the work.

## Change safety

- Do not mix behavior changes with unrelated refactors.
- Preserve established patterns in the target mod unless they are the cause of the bug.
- Prefer a narrow guard or state fix over broad exception handling.
- Remove diagnostic-only code after the diagnosis unless the user requests persistent diagnostics.

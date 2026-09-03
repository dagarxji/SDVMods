# Copilot Agent Pack for SDVMods

This pack is designed to be copied into the repository root.

It intentionally does **not** include or replace `.github/copilot-instructions.md`, so an existing repository-wide context-limiting file remains untouched.

## Files

```text
AGENTS.md
.github/
  agents/
    smapi-architect.agent.md
    smapi-debugger.agent.md
    smapi-implementer.agent.md
    smapi-reviewer.agent.md
  instructions/
    csharp.instructions.md
    smapi.instructions.md
ai/
  ARCHITECTURE.md
  LEARNINGS.md
COPILOT-SETUP.md
```

## What applies automatically

- `AGENTS.md`: standing repository guidance for supported Copilot agent workflows.
- `csharp.instructions.md`: applies to C# source.
- `smapi.instructions.md`: applies to C#, project files, manifests, and i18n JSON.

The existing `.github/copilot-instructions.md` continues to provide the repository-wide Copilot rules.

## Agents

### SMAPI Implementer

Use for normal feature implementation and scoped bug fixes after the problem is understood.

### SMAPI Debugger

Use when behavior is broken, intermittent, version-sensitive, Harmony-related, or unclear. It is explicitly root-cause-first and will prefer narrow diagnostics over speculative workarounds.

### SMAPI Reviewer

Use after a meaningful change or before merging/releasing. It is read-only by tool configuration and focuses on correctness, regressions, mod compatibility, multiplayer, lifecycle, Harmony, and performance.

Automatic model invocation is disabled for this agent so it is normally selected deliberately.

### SMAPI Architect

Use before large features, cross-cutting changes, multiplayer state changes, complex UI work, or Harmony-heavy changes. It is read-only by tool configuration.

Automatic model invocation is disabled for this agent so it is normally selected deliberately.

## Model selection

The agent files intentionally do not set `model:`. They inherit the currently selected/default Copilot model, so the repository does not become coupled to a model name that may later change.

## Context control

The pack is designed to avoid loading all repository knowledge all the time.

- Global behavior stays in the existing `copilot-instructions.md`.
- Language/SMAPI rules are path-scoped.
- `ARCHITECTURE.md` and `LEARNINGS.md` are read only when relevant.
- Specialized agent prompts are loaded only when those agents are used.

That keeps durable guidance available without turning every request into a large context dump.

## Maintenance

Update `ai/ARCHITECTURE.md` when shared repository structure materially changes.

Add to `ai/LEARNINGS.md` only after a root cause or reusable finding has been verified. Do not turn it into a running task log.

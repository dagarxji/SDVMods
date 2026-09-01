# Foreground Controller Input

A tiny SMAPI mod for Stardew Valley 1.6.x on Windows.

## What it does

- If Stardew Valley (or its SMAPI process) has foreground focus, controller input works normally.
- If you Alt+Tab to another game/app, Stardew keeps running but controller input is suppressed.
- When you return to Stardew, controller input works again normally.
- Inputs that were already held when focus was lost are also suppressed, so they can't continue affecting Stardew in the background.

This is designed to coexist with **Better Always Active**. It does **not** use Stardew's `Game1.IsActive` flag; it checks the actual foreground Windows process instead.

## Why this should stop the local co-op issue

SMAPI's input suppression prevents the game itself from handling a suppressed button. If you press Start/options or other controller buttons while playing Forza, those presses are discarded by Stardew while Forza owns foreground focus.

## Build

1. Install the .NET SDK if you don't already have it.
2. Double-click `build.bat`, or run:

   `dotnet build -c Release`

3. `Pathoschild.Stardew.ModBuildConfig` will detect your Stardew installation and normally deploy/build a release zip automatically.
4. Put the built `ForegroundControllerInput` mod folder in your Stardew `Mods` directory if it wasn't auto-deployed.

## Compatibility

- Stardew Valley 1.6.x
- SMAPI 4.x (tested against the current API shape used by SMAPI 4.5.x)
- Windows only for actual focus detection
- Compatible by design with Better Always Active

## Notes

The mod blocks only controller input. Keyboard/mouse input and the game's background simulation are untouched.

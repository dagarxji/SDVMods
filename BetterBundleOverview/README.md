# Better Bundle Overview

A small SMAPI mod for Stardew Valley 1.6 that changes only the Community Center / Junimo Note **overview** page.

## What it does

- Stacks the current room's colored bundle packages in one vertical column on the left.
- Shows the number of ingredient slots required for each item bundle.
- Shows every candidate ingredient icon for that bundle on the same overview page.
- Highlights ingredients which have already been deposited/used.
- Keeps Stardew's original bundle package objects as the click targets.
- Clicking a package still opens the normal, unmodified bundle donation page.
- Reads the live bundle data instead of hard-coding vanilla bundles, so remixed bundles and data-edited bundles are supported where they use normal bundle ingredient data.
- Leaves unreadable/scrambled Junimo notes alone until the player can normally read them.

For category-based custom ingredients, Stardew's own representative-item logic is used (the same mechanism its bundle UI uses) rather than trying to display every object in an entire category.

## Build

Requirements:

1. Stardew Valley 1.6 installed.
2. SMAPI installed.
3. .NET 6 SDK.

From this folder, either run:

```powershell
.\build.ps1
```

That builds the DLL and creates an installable ZIP under `dist/`. Or build directly:

```powershell
dotnet build -c Release
```

`Pathoschild.Stardew.ModBuildConfig` will resolve the Stardew/SMAPI references from the installed game. The compiled DLL will be under `bin/Release/net6.0/` (and ModBuildConfig may also copy the mod into your Mods folder depending on your local setup).

## Install after building

Create a folder such as:

```text
Stardew Valley/Mods/BetterBundleOverview/
```

and put these files in it:

- `manifest.json`
- `BetterBundleOverview.dll`

Then launch the game through SMAPI.

## Scope

This intentionally doesn't replace donation behavior. It only rearranges the bundle package positions on the overview and draws requirement information beside them. The detail/donation page remains Stardew's own `JunimoNoteMenu` page.

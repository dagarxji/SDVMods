# Crowded Wild Trees

A small SMAPI mod for Stardew Valley 1.6.x which lets **oak, maple, and pine** trees reach full maturity even when a mature wild tree occupies one of the surrounding 8 tiles.

## Behavior

- Affects vanilla wild tree IDs:
  - `1` = oak
  - `2` = maple
  - `3` = pine
- Does **not** make trees grow instantly.
- Uses each tree's current `Data/WildTrees` growth chance, so content edits to `GrowthChance` and `FertilizedGrowthChance` are respected.
- Tree fertilizer still uses the fertilized growth chance and can grow trees in winter.
- Other wild tree types and fruit trees are left alone.
- If another mod already allows the tree to mature, this mod detects that and does nothing extra.

## Build

Requirements:

1. Stardew Valley 1.6.x installed.
2. SMAPI installed.
3. .NET 6 SDK installed.

Then either double-click `build.bat` or run:

```powershell
dotnet build -c Release
```

`Pathoschild.Stardew.ModBuildConfig` should detect your Stardew Valley install and reference the game/SMAPI assemblies automatically. It normally also deploys the built mod to your Stardew Valley `Mods` folder.

If ModBuildConfig can't locate your game, add this inside the first `<PropertyGroup>` in `CrowdedWildTrees.csproj`, changing the path to your actual install:

```xml
<GamePath>C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley</GamePath>
```

## Test

Plant two or more oak/maple/pine seeds directly beside one another. They should all be able to reach maturity eventually. Their normal random growth rate still applies unless they're fertilized.

The SMAPI console should show:

`Crowded Wild Trees loaded. Oak, maple, and pine trees can mature beside other wild trees.`

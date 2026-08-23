# Crowded Trees

A SMAPI mod for Stardew Valley 1.6.x which lets regular and producing trees grow beside trees of the same kind.

## Behavior

- Supports every regular tree type represented by Stardew Valley's `Tree` terrain feature, including tree types added through `Data/WildTrees`.
- Supports fruit and other producing trees represented by the `FruitTree` terrain feature, including placing their saplings near each other.
- Does **not** make trees grow instantly.
- Regular trees use their current `Data/WildTrees` growth chance, so content edits to `GrowthChance` and `FertilizedGrowthChance` are respected.
- Tree fertilizer still uses the fertilized growth chance and can grow trees in winter.
- Producing trees still respect nearby regular trees and non-tree obstructions such as objects, paths, and buildings.
- If another mod already allows the tree to mature, this mod detects that and does nothing extra.

## Configuration

The optional [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098) integration provides two independent options, both enabled by default:

- **Regular trees**: allows all regular trees to reach maturity beside mature regular trees.
- **Fruit / producing trees**: allows producing trees to grow beside other producing trees.

The same settings are stored in `config.json` and can be edited without Generic Mod Config Menu.

## Build

Requirements:

1. Stardew Valley 1.6.x installed.
2. SMAPI installed.
3. .NET 6 SDK installed.

Run:

```powershell
dotnet build -c Release
```

`Pathoschild.Stardew.ModBuildConfig` should detect your Stardew Valley install and reference the game/SMAPI assemblies automatically. It normally also deploys the built mod to your Stardew Valley `Mods` folder.

If ModBuildConfig can't locate your game, add this inside the first `<PropertyGroup>` in `CrowdedWildTrees.csproj`, changing the path to your actual install:

```xml
<GamePath>C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley</GamePath>
```

## Test

1. Plant two or more regular tree seeds directly beside one another. They should eventually reach maturity using their normal random growth rates.
2. Plant two or more fruit or producing tree saplings directly beside one another. They should continue progressing toward maturity.
3. Disable each option through Generic Mod Config Menu and verify that vanilla spacing behavior returns for that tree category.

The SMAPI console should show:

`Crowded Trees loaded.`

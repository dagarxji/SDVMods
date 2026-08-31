# Building Season-Flexible Community Center

## Prerequisites

1. Stardew Valley 1.6 installed.
2. SMAPI installed.
3. .NET 6 SDK or a newer .NET SDK capable of targeting `net6.0`.

The project uses `Pathoschild.Stardew.ModBuildConfig`, which normally auto-detects your Stardew installation and adds the game/SMAPI references.

## Build

From the repository root:

```powershell
cd src/SeasonFlexibleCommunityCenter
dotnet restore
dotnet build -c Release
```

Or from the mod directory on Windows PowerShell:

```powershell
.\build.ps1
```

On a normal Stardew development setup, Stardew.ModBuildConfig will also deploy the mod to your Mods folder and generate a release ZIP under the project's `bin/Release/net6.0/` directory.

## If the game path isn't detected

Copy `Directory.Build.props.example` to `Directory.Build.props` beside the solution/project and edit `GamePath`.

Example Windows path:

```xml
<Project>
  <PropertyGroup>
    <GamePath>C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley</GamePath>
  </PropertyGroup>
</Project>
```

## First test checklist

Use a disposable/test save if possible.

1. Start creating a new farm and confirm **Season Exchange Settings** appears on the character/farm creation screen.
2. Pick non-default values, save them, create the farm, and confirm those values carry into the new save.
3. Separately test a new farm without using that button and confirm the Spring 1 fallback setup opens after the intro/initial event is clear.
4. Save/reload and verify the per-farm values persist.
5. Open GMCM and verify the per-farm sliders match.
6. Open the Pantry's Quality Crops bundle during Spring (or another bundle with future-season requirements).
7. Confirm **Season Exchange** is shown only while physically at the Community Center, not from the remote bundle viewer.
8. Put a Spring crop in the backpack and confirm it appears as a candidate for a future crop requirement.
9. Verify `Need` changes appropriately between normal/gold/iridium quality stacks.
10. Complete one exchange and confirm the original requirement becomes checked after returning to the area page.
11. Complete the final needed slot in a choose-N bundle and verify optional unfilled ingredients resolve as complete, matching vanilla.
12. Confirm the normal bundle reward icon appears and can be collected.
13. Complete the final bundle in an area and verify the normal area completion/restoration behavior triggers.
14. With an expansion installed, run `sfc_rebuild_catalog` and check the SMAPI trace log for the number of seasonal definitions found.
15. In co-op, confirm the farmhand receives host settings and sees the same calculated exchange quantities.

## Debug commands

```text
sfc_setup
sfc_rebuild_catalog
```

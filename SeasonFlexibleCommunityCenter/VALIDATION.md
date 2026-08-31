# Validation notes

## Completed in the source-generation environment

- Parsed `manifest.json` and `compatibility.json` as valid JSON.
- Parsed the project/props files as valid XML.
- Ran a lexical C# balance pass across every `.cs` file (braces, brackets, parentheses, comments, strings, and character literals).
- Checked the source tree for leftover `TODO`/`FIXME` markers and accidental direct accesses to the Junimo menu state fields that are handled through the compatibility helper.
- Cross-checked the Stardew Valley 1.6 bundle model used by the code:
  - `BundleIngredientDescription.id` is a string item ID;
  - category requirements use `category` with no item ID;
  - `stack`, `quality`, `completed`, and `preservesId` are present;
  - `Bundle.numberOfIngredientSlots`, `bundleIndex`, `ingredients`, and `depositsAllowed` are available;
  - completing a choose-N bundle sets all synchronized ingredient flags, matching the behavior implemented here;
  - `CharacterCustomization.Source.NewGame` and `HostNewFarm` are distinct from farmhand/customization sources.
- Verified `Pathoschild.Stardew.ModBuildConfig` 4.4.0 exists and is the current published ModBuildConfig package version at the time of this source pass.

## Not possible in this environment

The container used to generate this source does not have `dotnet`, Stardew Valley, or SMAPI assemblies installed, so the project has **not** been compiled or launched in-game here. Running `build.sh` correctly reaches the build step but stops with `dotnet: not found`.

Use `RELEASE_CHECKLIST.md` on a normal Stardew/SMAPI development machine before treating 1.0.0 as a binary release.

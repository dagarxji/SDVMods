# Season-Flexible Community Center

A SMAPI mod for Stardew Valley 1.6 which removes **hard seasonal waiting** from Community Center bundles without removing seasonal gameplay.

Instead of changing what grows or can be caught in each season, the mod lets you trade a larger quantity of a **currently-seasonal item from the same category** to satisfy an incomplete requirement whose normal season is still in the future.

## Example

With the default **Balanced** preset, the season penalty is ×10 per season ahead:

- Summer requirement while it is Spring: base ×10.
- Fall requirement while it is Spring: base ×100.
- Winter requirement while it is Spring: base ×1000.

The final quantity is then adjusted for the original item's sell value, the substitute's sell value, and the substitute's quality.

So a cheap Spring crop can replace a Fall crop, but it may take a substantial stack. Waiting for Fall remains the cheapest route.

## Features

- Keeps vanilla crop/fish/forage seasons unchanged.
- Works on the **currently selected bundle set**, including vanilla remixed bundles and bundle edits from other mods.
- Runtime **Season Exchange** button on eligible incomplete Community Center bundles.
- Same-category substitutions only:
  - crop -> crop;
  - fish -> fish;
  - forage -> forage;
  - fruit-tree fruit -> fruit-tree fruit.
- Per-season exponential difficulty scaling.
- Sell-value scaling so cheap items require larger stacks.
- Quality credit for silver/gold/iridium substitutes.
- Per-farm settings.
- New-farm character-creation button with Relaxed / Balanced / Challenging presets, sliders, and category toggles, plus a Spring 1 fallback.
- Optional Generic Mod Config Menu integration with difficulty sliders during play.
- Multiplayer settings sync: host settings are authoritative.
- Expansion/content-pack aware seasonal catalog.
- Manual compatibility overrides for unusual custom item frameworks.
- Lists every valid current-season substitute, with items in the backpack shown first.
- Supports bundle menus opened outside the Community Center by bundle-access mods.

## How to use in game

1. Enter the Community Center normally.
2. Open an incomplete bundle.
3. If that bundle has an incomplete requirement which is seasonally ahead of the current season, a **Season Exchange** button appears.
4. Click it.
5. Pick the future requirement on the left.
6. Pick a same-category current-season item from your backpack on the right.
7. The screen shows **Need X / Have Y** before you exchange.

The original bundle requirement is then completed in Stardew's normal Community Center state. This does **not** create a second completion system.

## Difficulty formula

Conceptually:

```
required quantity
  = original required stack
  × (season penalty ^ seasons ahead)
  × value adjustment
```

The value adjustment interpolates between no price scaling and the full ratio of:

```
original item sell value / substitute credited sell value
```

The substitute's credited value can include some or all of its quality premium depending on **Quality credit**.

### Presets

| Preset | Season penalty | Value scaling | Quality credit |
|---|---:|---:|---:|
| Relaxed | ×1.5 / season | 70% | 100% |
| Balanced | ×10 / season | 100% | 100% |
| Challenging | ×2.5 / season | 100% | 75% |

All values can be customized.

## New farm setup

While Stardew's character/farm creation screen is open, the mod overlays a **Season Exchange Settings** button. It opens the same preset/sliders/category setup before the farm is created, and those choices are carried into the new save.

If that creation-screen setup is skipped or unavailable, a fallback configuration window opens on **Spring 1, Year 1** once the intro/event sequence has finished and normal player input is available. The title-screen GMCM page controls the defaults used by both paths.

For an existing farm, the mod initializes the default Balanced settings (×10 per season) without forcing the first-time setup window.

You can reopen the per-farm setup screen from the SMAPI console with:

```
sfc_setup
```

## Expansion-mod compatibility

The mod intentionally does **not** hard-code vanilla item IDs.

At save load and each day start, it reads the final game assets *after other mods have patched them*:

- `Data/Crops` for crop harvest items and growing seasons;
- `Data/FruitTrees` for tree fruit and fruit seasons;
- `Data/Fish` to identify fish items;
- `Data/Locations` for fish/forage seasons;
- the active Community Center `Bundle` objects / synchronized bundle state for the actual selected requirements.

That makes it compatible by design with expansion/content packs which add their content through Stardew 1.6's standard data assets or Content Patcher. This is the same general compatibility strategy used by other bundle-planning mods which support SVE and custom crops.

### Expected compatibility

- Stardew Valley Expanded: designed to work automatically with standard SVE/Content Patcher item and bundle edits.
- Ridgeside Village: designed to recognize seasonal items exposed through standard game data/Content Patcher.
- East Scarp and add-ons: designed to recognize items exposed through standard game data/Content Patcher.
- Cornucopia / other crop packs: crop items added to `Data/Crops` are discovered automatically.
- Custom/remixed `Data/Bundles`: the mod works from the active bundle objects instead of assuming vanilla bundle indexes/items.

### Limits

No mod can safely infer seasonality from arbitrary custom code. An expansion may need an override when it:

- creates an item through a custom framework but never adds a matching crop/fish/forage/fruit entry to standard game data;
- uses a complex item-query-only spawn rule which doesn't expose a direct item ID the scanner can associate with a season;
- implements a completely separate Community Center replacement rather than Stardew's normal bundle state.

For those cases, edit `compatibility.json`.

Example:

```json
{
  "Items": {
    "(O)Example.ModId_MoonMelon": {
      "Kind": "Crop",
      "Seasons": [ "summer" ]
    },
    "(O)Example.ModId_RidgeTrout": {
      "Kind": "Fish",
      "Seasons": [ "fall", "winter" ]
    }
  }
}
```

Supported kinds: `Crop`, `Fish`, `Forage`, `Fruit`.

After editing the file while the game is open, run:

```
sfc_rebuild_catalog
```

## Multiplayer

- The host owns the per-farm difficulty settings.
- Settings are synchronized to farmhands through SMAPI mod messages.
- Community Center completion itself uses Stardew's synchronized Community Center NetFields.
- All players should install the mod for consistent UI/price calculation.

## Requirements

- Stardew Valley 1.6.x
- SMAPI 4.x (current 4.5.x recommended)
- Generic Mod Config Menu: optional, recommended

## Building

See [`BUILDING.md`](BUILDING.md).

## Technical notes

A pure `Data/Bundles` content patch is intentionally not used. Stardew bundle data represents a finite list of fixed ingredient slots (up to 12); it can't express a rule like "any current-season crop, but require a dynamically calculated quantity based on how early it is." The runtime exchange layer leaves the selected bundle definition intact and only marks the original ingredient's normal synchronized completion flag after payment.

The implementation also avoids mutating `BundleIngredientDescription` instances directly. It updates Stardew's synchronized Community Center bundle flags and reconstructs the vanilla note page from that state.

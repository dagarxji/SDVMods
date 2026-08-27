# Inventory Insight 0.1.0

A Stardew Valley 1.6 / SMAPI mod for quickly deciding whether an inventory item is worth keeping.

## Compact hover panel

The panel is deliberately fixed-size and anchored to the bottom-left of the screen so it doesn't compete with Stardew's cursor tooltip. Each row has a game-native icon for quick scanning. It shows, in this order:

1. **Loves** — up to 5 NPC names, comma-delimited.
2. **CC** — `Needed` only when that item/quality still satisfies an unfinished Community Center ingredient in the *current save's generated bundle set*.
3. **Museum** — `Needed` only when the item is museum-donatable and hasn't been donated yet.
4. **Quest / order** — `Needed` for active item-delivery/lost-item quests and active Special Order donate, ship, deliver, or gift objectives which can use an item already in inventory. Collect/fish objectives are intentionally excluded because those require newly obtained items.
5. **Crafting** — Yes/No. This scans all currently loaded crafting recipes, including content-patched recipes, not cooking recipes.
6. **Sell price** — current per-item sell value, including the player's relevant profession modifiers as applied by the game.
7. **Safe to sell** — conservative Yes/No.

Hold **Left Shift** or **Right Shift** while hovering to open the fixed-size expanded panel.

## Shift details

The expanded view adds:

- More NPCs who love the item (default cap: 20).
- Active quest/order names when relevant.
- Crafting uses as ingredient-count → crafted-item icon flows, followed by the recipe name.
- Profitable deterministic machine-processing routes from `Data/Machines`.
- Machine routes as machine + input/extra-ingredient → finished-product icon flows.
- Compact values for Normal / Silver / Gold / Iridium input quality when the machine accepts those qualities.
- Each machine value shows processed sell value and the gain versus selling the required raw input. Extra consumed inputs (for example coal) are included in the comparison when the installed game version declares them in machine data.

Example machine line:

`Fish Smoker → Smoked Fish`

`Normal 300g (+135g) | Silver 375g (+165g) | Gold 450g (+195g) | Iridium 600g (+255g) • extras 15g`

## Community Center behavior

This intentionally does **not** use a hardcoded vanilla bundle list. It reads:

- `Game1.netWorldState.Value.BundleData`, which is the generated bundle set stored for the save; and
- the Community Center's live bundle ingredient completion flags.

That means remixed bundles and content-patched/generated bundle data are respected, and already-filled ingredients stop marking the item as needed.

If the Joja membership path is active, Community Center requirements are treated as not needed.

## Safe-to-sell definition

`Safe to sell = YES` only when the item has a positive sell value and Inventory Insight detects none of these:

- remaining Community Center use;
- undonated museum use;
- active item-delivery/lost-item or existing-inventory Special Order use;
- crafting-recipe use;
- an NPC who loves the item (configurable); or
- a profitable deterministic machine-processing route (configurable).

Unsellable items never get a green `YES`.

This is intentionally conservative. It does not try to predict arbitrary future quest rolls or opaque custom C# mechanics from other mods.

## Stardew 1.6.15 / 1.6.16 compatibility

The project targets the Stardew 1.6-era .NET 6 API and avoids a compile-time dependency on the `AdditionalConsumedItems` machine field added in Stardew 1.6.16. If that field exists, Inventory Insight reads it reflectively and includes those extra inputs in the value comparison; on 1.6.15, it simply isn't available to read.

## Machine-price safety

The hover panel should never alter gameplay RNG merely to calculate a price. For that reason, Inventory Insight only prices **deterministic** machine outputs. It skips output rules with multiple random eligible outputs, advanced `OutputMethod` callbacks, or complex random item-query expressions.

This means the result is safe and stable for scanning, but an exotic modded machine with randomized/custom C# output may not be listed.

## Build

Requirements:

- Stardew Valley 1.6.x installed;
- SMAPI 4.x installed;
- .NET 6 SDK.

From this folder:

```powershell
 dotnet build -c Release
```

`Stardew.ModBuildConfig` should find your Stardew installation and copy the built mod to your Mods folder when configured normally.

If it doesn't detect the game path, follow the Stardew ModBuildConfig setup for your install and rerun the command.

## Main configuration

SMAPI generates `config.json` after launch. Useful values:

- `CompactWidth` / `CompactHeight`
- `ExpandedWidth` / `ExpandedHeight`
- `CompactLoveLimit`
- `ExpandedLoveLimit`
- `ExpandedRecipeLimit`
- `ExpandedMachineLimit`
- `GiftsPreventSafeSell`
- `ProfitableMachinesPreventSafeSell`

## Notes for this first build

The native game's deepest `IClickableMenu.drawHoverText` overload is Harmony-patched after it draws a normal item tooltip. This gives broad coverage across the inventory, chests, shops, and other standard menus without replacing those menus or Lookup Anything. Inventory Insight stays at the bottom-left of the screen while Stardew's normal tooltip remains beside the cursor.

The compact panel never wraps row values. Long values are ellipsized so every hovered item keeps the same panel size and row positions.

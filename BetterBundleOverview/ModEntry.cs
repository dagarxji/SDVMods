using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace BetterBundleOverview;

internal sealed class ModEntry : Mod
{
    private readonly Dictionary<string, Item> itemCache = new();
    private Item? hoveredOverviewItem;

    public override void Entry(IModHelper helper)
    {
        // Move Stardew's existing Bundle click targets before the menu is drawn.
        // Since they're the game's own Bundle objects, clicking them still runs
        // JunimoNoteMenu's normal setUpBundleSpecificPage flow.
        helper.Events.Display.RenderingActiveMenu += OnRenderingActiveMenu;

        // Draw requirement information after the vanilla overview has rendered.
        helper.Events.Display.RenderedActiveMenu += OnRenderedActiveMenu;
        helper.Events.Display.MenuChanged += OnMenuChanged;
    }

    private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        // Display items are cheap to recreate and this prevents stale cache entries
        // if another mod changes bundle data between menu instances.
        this.itemCache.Clear();
        this.hoveredOverviewItem = null;
    }

    private void OnRenderingActiveMenu(object? sender, RenderingActiveMenuEventArgs e)
    {
        if (!TryGetOverview(out JunimoNoteMenu? menu))
        {
            this.hoveredOverviewItem = null;
            return;
        }

        ApplyBundleLayout(menu);

        // JunimoNoteMenu already exposes hoveredItem, which Lookup Anything knows
        // how to inspect. Populate it for our custom overview icons so F1 lookups
        // work exactly like they do for items on Stardew's normal bundle page.
        this.hoveredOverviewItem = GetHoveredOverviewItem(menu);
        menu.hoveredItem = this.hoveredOverviewItem;
    }

    private void OnRenderedActiveMenu(object? sender, RenderedActiveMenuEventArgs e)
    {
        if (!TryGetOverview(out JunimoNoteMenu? menu))
            return;

        DrawRequirements(menu, e.SpriteBatch);

        // Show the item name using Stardew's normal hover-text style. Drawing this
        // after our icons keeps the tooltip above the custom overview content.
        if (this.hoveredOverviewItem is not null)
        {
            IClickableMenu.drawHoverText(
                e.SpriteBatch,
                this.hoveredOverviewItem.DisplayName,
                Game1.dialogueFont
            );
        }

        // The vanilla menu already drew its cursor before RenderedActiveMenu fired.
        // Draw it once more so our ingredient icons/tooltip never cover it.
        menu.drawMouse(e.SpriteBatch);
    }

    private static bool TryGetOverview(out JunimoNoteMenu? menu)
    {
        menu = Game1.activeClickableMenu as JunimoNoteMenu;
        return menu is not null
            && !menu.specificBundlePage
            && !menu.scrambledText
            && menu.bundles is { Count: > 0 };
    }

    private static void ApplyBundleLayout(JunimoNoteMenu menu)
    {
        OverviewLayout layout = GetLayout(menu);

        for (int i = 0; i < menu.bundles.Count; i++)
        {
            Bundle bundle = menu.bundles[i];
            int y = layout.Top + i * layout.RowHeight + (layout.RowHeight - bundle.bounds.Height) / 2;

            bundle.bounds.X = layout.PackageX;
            bundle.bounds.Y = y;
            bundle.sprite.position = new Vector2(bundle.bounds.X, bundle.bounds.Y);
        }
    }

    private void DrawRequirements(JunimoNoteMenu menu, SpriteBatch b)
    {
        OverviewLayout layout = GetLayout(menu);

        for (int bundleIndex = 0; bundleIndex < menu.bundles.Count; bundleIndex++)
        {
            Bundle bundle = menu.bundles[bundleIndex];
            int rowY = layout.Top + bundleIndex * layout.RowHeight;
            int rowCenterY = rowY + layout.RowHeight / 2;

            // A subtle separator makes each package's requirements read as one row
            // without hiding the Community Center tree artwork underneath.
            Rectangle separator = new(
                layout.InfoX,
                rowY + layout.RowHeight - 2,
                Math.Max(1, layout.Right - layout.InfoX),
                2
            );
            b.Draw(Game1.fadeToBlackRect, separator, Color.Black * 0.18f);

            if (menu.whichArea == 4 || bundle.ingredients.Count == 0)
            {
                // Vault bundles use a purchase flow instead of item donation.
                // Leave those as labels rather than rendering their internal
                // data as though it were an ordinary item requirement.
                Vector2 labelSize = Game1.smallFont.MeasureString(bundle.label);
                b.DrawString(
                    Game1.smallFont,
                    bundle.label,
                    new Vector2(layout.InfoX, rowCenterY - labelSize.Y / 2f),
                    Game1.textColor
                );
                continue;
            }

            string requiredText = $"{bundle.numberOfIngredientSlots} required";
            Vector2 requiredSize = Game1.smallFont.MeasureString(requiredText);
            b.DrawString(
                Game1.smallFont,
                requiredText,
                new Vector2(layout.InfoX, rowCenterY - requiredSize.Y / 2f),
                Game1.textColor
            );

            int ingredientCount = bundle.ingredients.Count;
            int availableWidth = Math.Max(48, layout.Right - layout.ItemsX);
            float slotWidth = Math.Min(64f, availableWidth / (float)Math.Max(1, ingredientCount));
            float iconPixels = Math.Clamp(slotWidth - 8f, 32f, 54f);
            float iconScale = iconPixels / 64f;

            for (int ingredientIndex = 0; ingredientIndex < ingredientCount; ingredientIndex++)
            {
                BundleIngredientDescription ingredient = bundle.ingredients[ingredientIndex];
                Item item = GetDisplayItem(bundle, ingredientIndex, ingredient);

                float slotX = layout.ItemsX + ingredientIndex * slotWidth;
                Vector2 itemPos = new(
                    slotX + (slotWidth - iconPixels) / 2f,
                    rowCenterY - iconPixels / 2f
                );

                if (ingredient.completed)
                {
                    // "Used" ingredients get a strong bundle-color highlight rather
                    // than being dimmed, so progress is obvious at a glance.
                    Color bundleColor = Bundle.getColorFromColorIndex(bundle.bundleColor);
                    Rectangle highlight = new(
                        (int)itemPos.X - 4,
                        (int)itemPos.Y - 4,
                        (int)iconPixels + 8,
                        (int)iconPixels + 8
                    );
                    DrawHighlight(b, highlight, bundleColor);
                }

                item.drawInMenu(
                    b,
                    itemPos,
                    iconScale,
                    1f,
                    0.9f,
                    StackDrawType.Draw,
                    ingredient.completed ? Color.White : Color.White * 0.9f,
                    drawShadow: false
                );
            }
        }
    }

    private Item? GetHoveredOverviewItem(JunimoNoteMenu menu)
    {
        // Vault bundles don't display ingredient icons on the overview.
        if (menu.whichArea == 4)
            return null;

        OverviewLayout layout = GetLayout(menu);
        int mouseX = Game1.getOldMouseX();
        int mouseY = Game1.getOldMouseY();

        for (int bundleIndex = 0; bundleIndex < menu.bundles.Count; bundleIndex++)
        {
            Bundle bundle = menu.bundles[bundleIndex];
            int ingredientCount = bundle.ingredients.Count;
            if (ingredientCount == 0)
                continue;

            int rowY = layout.Top + bundleIndex * layout.RowHeight;
            int rowCenterY = rowY + layout.RowHeight / 2;

            int availableWidth = Math.Max(48, layout.Right - layout.ItemsX);
            float slotWidth = Math.Min(64f, availableWidth / (float)Math.Max(1, ingredientCount));
            float iconPixels = Math.Clamp(slotWidth - 8f, 32f, 54f);

            for (int ingredientIndex = 0; ingredientIndex < ingredientCount; ingredientIndex++)
            {
                float slotX = layout.ItemsX + ingredientIndex * slotWidth;
                Vector2 itemPos = new(
                    slotX + (slotWidth - iconPixels) / 2f,
                    rowCenterY - iconPixels / 2f
                );

                // Use the same bounds as the completion highlight, with a small
                // margin around uncompleted icons too so hovering isn't finicky.
                Rectangle hoverBounds = new(
                    (int)itemPos.X - 4,
                    (int)itemPos.Y - 4,
                    (int)iconPixels + 8,
                    (int)iconPixels + 8
                );

                if (hoverBounds.Contains(mouseX, mouseY))
                {
                    BundleIngredientDescription ingredient = bundle.ingredients[ingredientIndex];
                    return GetDisplayItem(bundle, ingredientIndex, ingredient);
                }
            }
        }

        return null;
    }

    private Item GetDisplayItem(Bundle bundle, int ingredientIndex, BundleIngredientDescription ingredient)
    {
        string id = JunimoNoteMenu.GetRepresentativeItemId(ingredient);
        string key = string.Join(
            '|',
            bundle.bundleIndex,
            ingredientIndex,
            id,
            ingredient.preservesId ?? "",
            ingredient.stack,
            ingredient.quality
        );

        if (this.itemCache.TryGetValue(key, out Item? cached))
            return cached;

        Item item = ingredient.preservesId is not null
            ? Utility.CreateFlavoredItem(id, ingredient.preservesId, ingredient.quality, ingredient.stack)
            : ItemRegistry.Create(id, ingredient.stack, ingredient.quality);

        this.itemCache[key] = item;
        return item;
    }

    private static void DrawHighlight(SpriteBatch b, Rectangle bounds, Color color)
    {
        // Translucent fill + opaque-ish border. Game1.fadeToBlackRect is the same
        // simple texture Stardew uses for menu overlays, so no asset is required.
        b.Draw(Game1.fadeToBlackRect, bounds, color * 0.30f);

        const int border = 3;
        Color edge = color * 0.85f;
        b.Draw(Game1.fadeToBlackRect, new Rectangle(bounds.X, bounds.Y, bounds.Width, border), edge);
        b.Draw(Game1.fadeToBlackRect, new Rectangle(bounds.X, bounds.Bottom - border, bounds.Width, border), edge);
        b.Draw(Game1.fadeToBlackRect, new Rectangle(bounds.X, bounds.Y, border, bounds.Height), edge);
        b.Draw(Game1.fadeToBlackRect, new Rectangle(bounds.Right - border, bounds.Y, border, bounds.Height), edge);
    }

    private static OverviewLayout GetLayout(JunimoNoteMenu menu)
    {
        int count = Math.Max(1, menu.bundles.Count);

        // Leave room for the room-navigation controls at the top and the lower
        // edge of the note. Six vanilla rows fit comfortably at ~88px each.
        int top = menu.yPositionOnScreen + 78;
        int bottom = menu.yPositionOnScreen + menu.height - 70;
        int availableHeight = Math.Max(1, bottom - top);
        int rowHeight = Math.Clamp(availableHeight / count, 68, 94);

        int packageX = menu.xPositionOnScreen + 84;
        int infoX = packageX + 84;
        int itemsX = infoX + 136;
        int right = menu.xPositionOnScreen + menu.width - 72;

        return new OverviewLayout(top, rowHeight, packageX, infoX, itemsX, right);
    }

    private readonly record struct OverviewLayout(
        int Top,
        int RowHeight,
        int PackageX,
        int InfoX,
        int ItemsX,
        int Right
    );
}

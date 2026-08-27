using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace InventoryInsight;

internal sealed class TooltipRenderer
{
    private readonly ModEntry Mod;
    private readonly ItemAnalyzer Analyzer;
    private readonly Dictionary<string, Item?> IconCache = new();
    private long LastTick = -1;
    private Item? LastItem;

    private const int Padding = 14;
    private const int LineHeight = 30;
    private const int CompactMaxWidth = 390;
    private const int CompactMaxHeight = 275;
    private const int ExpandedMaxWidth = 700;
    private const int ExpandedMaxHeight = 500;
    private const float IconScale = 0.375f;
    private const int IconSpace = 27;

    public TooltipRenderer(ModEntry mod, ItemAnalyzer analyzer)
    {
        Mod = mod;
        Analyzer = analyzer;
    }

    public void Draw(SpriteBatch b, Item? hoveredItem)
    {
        if (!Context.IsWorldReady || hoveredItem is null || Game1.player is null)
            return;

        long tick = Game1.ticks;
        if (tick == LastTick && ReferenceEquals(LastItem, hoveredItem))
            return;
        LastTick = tick;
        LastItem = hoveredItem;

        bool expanded = Mod.Helper.Input.IsDown(SButton.LeftShift) || Mod.Helper.Input.IsDown(SButton.RightShift);
        ItemInsight insight;
        try
        {
            insight = Analyzer.Analyze(hoveredItem);
        }
        catch (Exception ex)
        {
            Mod.Monitor.LogOnce($"Failed to analyze {hoveredItem.QualifiedItemId}: {ex}", LogLevel.Error);
            return;
        }

        int width = expanded
            ? Math.Min(Mod.Config.ExpandedWidth, ExpandedMaxWidth)
            : Math.Min(Mod.Config.CompactWidth, CompactMaxWidth);
        int height = expanded
            ? Math.Min(Math.Min(Mod.Config.ExpandedHeight, ExpandedMaxHeight), GetExpandedHeight(insight))
            : Math.Min(Mod.Config.CompactHeight, CompactMaxHeight);
        Rectangle safe = Utility.getSafeArea();
        width = Math.Clamp(width, 320, safe.Width - 16);
        height = Math.Clamp(height, expanded ? 180 : 260, safe.Height - 16);

        int x = safe.Left + 8;
        int y = safe.Bottom - height - 8;

        IClickableMenu.drawTextureBox(b, x, y, width, height, Color.White);

        float cursorY = y + Padding;
        DrawHeader(b, insight.DisplayName, x + Padding, ref cursorY, width - Padding * 2);

        if (expanded)
            DrawExpanded(b, insight, x + Padding, cursorY + 2, width - Padding * 2, y + height - Padding);
        else
            DrawCompactRows(b, insight, x + Padding, ref cursorY, width - Padding * 2);
    }

    private int GetExpandedHeight(ItemInsight data)
    {
        int leftHeight = LineHeight * 2;
        if (data.QuestUses.Count > 0)
            leftHeight += 4 + LineHeight * 2;

        leftHeight += 4 + LineHeight;
        int recipesShown = Math.Min(data.CraftingUses.Count, Mod.Config.ExpandedRecipeLimit);
        leftHeight += recipesShown == 0 ? LineHeight : recipesShown * 36;
        if (recipesShown < data.CraftingUses.Count)
            leftHeight += LineHeight;

        int routesShown = Math.Min(data.MachineRoutes.Count, Mod.Config.ExpandedMachineLimit);
        int rightHeight = LineHeight + (routesShown == 0 ? LineHeight * 2 : routesShown * 68);
        if (routesShown < data.MachineRoutes.Count)
            rightHeight += LineHeight;

        int headerHeight = Padding + LineHeight + 9;
        return headerHeight + Math.Max(leftHeight, rightHeight) + Padding;
    }

    private static void DrawHeader(SpriteBatch b, string text, int x, ref float y, int width)
    {
        string fitted = FitSingleLine(text, Game1.smallFont, width);
        Utility.drawTextWithShadow(b, fitted, Game1.smallFont, new Vector2(x, y), Game1.textColor);
        y += LineHeight;
        b.Draw(Game1.staminaRect, new Rectangle(x, (int)y, width, 2), Game1.textColor * 0.35f);
        y += 7;
    }

    private void DrawCompactRows(SpriteBatch b, ItemInsight data, int x, ref float y, int width)
    {
        // Exactly the first N names here; overflow belongs in the Shift view, not the scan view.
        string loves = data.LovedBy.Count == 0
            ? "—"
            : string.Join(", ", data.LovedBy.Take(Math.Max(1, Mod.Config.CompactLoveLimit)));

        DrawRow(b, "(O)458", "Loves", loves, x, ref y, width);
        DrawRow(b, "(O)434", "CC", data.CommunityCenterNeeded ? "Needed" : "—", x, ref y, width);
        DrawRow(b, "(O)96", "Museum", data.MuseumNeeded ? "Needed" : "—", x, ref y, width);
        DrawRow(b, "(O)79", "Quest", data.QuestUses.Count > 0 ? "Needed" : "—", x, ref y, width);
        DrawRow(b, "(O)388", "Crafting", data.CraftingUses.Count > 0 ? "Yes" : "No", x, ref y, width);
        DrawRow(b, "(O)336", "Sell", data.SellPrice > 0 ? $"{data.SellPrice:N0}g" : "Unsellable", x, ref y, width);
        DrawRow(b, data.SafeToSell ? "(O)446" : "(O)168", "Safe", data.SafeToSell ? "YES" : "NO", x, ref y, width);
    }

    private void DrawExpanded(SpriteBatch b, ItemInsight data, int x, float top, int width, float bottom)
    {
        const int columnGap = 20;
        int leftWidth = (width - columnGap) / 2;
        int rightWidth = width - columnGap - leftWidth;
        int rightX = x + leftWidth + columnGap;

        float leftY = top;
        float rightY = top;

        // Left column: identity/use details.
        DrawSectionTitle(b, "Loved by", x, ref leftY, bottom);
        string loved = data.LovedBy.Count == 0
            ? "—"
            : JoinLimited(data.LovedBy, Mod.Config.ExpandedLoveLimit, showOverflow: true);
        DrawSingleLine(b, loved, x + 8, ref leftY, leftWidth - 8, bottom, secondary: true);

        if (data.QuestUses.Count > 0)
        {
            leftY += 4;
            DrawSectionTitle(b, "Active quest / order", x, ref leftY, bottom);
            string quests = string.Join(", ", data.QuestUses.Select(p => p.Name).Distinct());
            DrawSingleLine(b, quests, x + 8, ref leftY, leftWidth - 8, bottom, secondary: true);
        }

        leftY += 4;
        DrawSectionTitle(b, "Crafting recipes", x, ref leftY, bottom);
        if (data.CraftingUses.Count == 0)
        {
            DrawSingleLine(b, "—", x + 8, ref leftY, leftWidth - 8, bottom);
        }
        else
        {
            int shown = 0;
            foreach (CraftingUse recipe in data.CraftingUses)
            {
                if (shown >= Mod.Config.ExpandedRecipeLimit || leftY > bottom - 36)
                    break;

                DrawCraftingUse(b, recipe, x + 8, ref leftY, leftWidth - 8, bottom);
                shown++;
            }

            if (shown < data.CraftingUses.Count && leftY <= bottom - LineHeight)
                DrawSingleLine(b, $"+ {data.CraftingUses.Count - shown} more", x + 8, ref leftY, leftWidth - 8, bottom, secondary: true);
        }

        // Right column: machine-value comparisons for every quality which the rule accepts.
        DrawSectionTitle(b, "Machine upgrades", rightX, ref rightY, bottom);
        if (data.MachineRoutes.Count == 0)
        {
            DrawWrappedText(b, "No profitable deterministic machine route found.", rightX + 8, ref rightY, rightWidth - 8, bottom, secondary: true);
        }
        else
        {
            int shown = 0;
            foreach (MachineRoute route in data.MachineRoutes)
            {
                if (shown >= Mod.Config.ExpandedMachineLimit || rightY > bottom - 68)
                    break;

                DrawMachineRoute(b, data.ItemId, route, rightX + 8, ref rightY, rightWidth - 8, bottom);
                shown++;
            }

            if (shown < data.MachineRoutes.Count && rightY <= bottom - LineHeight)
                DrawSingleLine(b, $"+ {data.MachineRoutes.Count - shown} more routes", rightX + 8, ref rightY, rightWidth - 8, bottom, secondary: true);
        }
    }

    private void DrawCraftingUse(SpriteBatch b, CraftingUse recipe, int x, ref float y, int width, float bottom)
    {
        if (y > bottom - 36)
            return;

        DrawIcon(b, recipe.OutputItemId, new Vector2(x, y));
        int textX = x + IconSpace;
        string requiredText = $"needs {recipe.RequiredCount}";
        int requiredWidth = (int)Math.Ceiling(Game1.tinyFont.MeasureString(requiredText).X);
        int nameWidth = Math.Max(40, width - IconSpace - requiredWidth - 10);
        string fitted = FitSingleLine(recipe.RecipeName, Game1.smallFont, nameWidth);
        Utility.drawTextWithShadow(b, fitted, Game1.smallFont, new Vector2(textX, y), Game1.textColor);
        Utility.drawTextWithShadow(b, requiredText, Game1.tinyFont, new Vector2(x + width - requiredWidth, y + 5), Game1.textColor * 0.75f);
        y += 36;
    }

    private void DrawMachineRoute(SpriteBatch b, string inputItemId, MachineRoute route, int x, ref float y, int width, float bottom)
    {
        if (y > bottom - 68)
            return;

        DrawIcon(b, route.MachineItemId, new Vector2(x, y));
        string machineName = FitSingleLine(route.MachineName, Game1.smallFont, width - IconSpace);
        Utility.drawTextWithShadow(b, machineName, Game1.smallFont, new Vector2(x + IconSpace, y), Game1.textColor);
        y += 34;

        int cursorX = x;
        DrawIcon(b, inputItemId, new Vector2(cursorX, y));
        cursorX += IconSpace;
        if (route.RequiredInputCount > 1)
        {
            string quantity = $"×{route.RequiredInputCount}";
            Utility.drawTextWithShadow(b, quantity, Game1.tinyFont, new Vector2(cursorX, y + 5), Game1.textColor * 0.75f);
            cursorX += (int)Math.Ceiling(Game1.tinyFont.MeasureString(quantity).X) + 5;
        }

        ConsumedItem? extra = route.AdditionalInputs.FirstOrDefault();
        if (extra is not null)
        {
            Utility.drawTextWithShadow(b, "+", Game1.tinyFont, new Vector2(cursorX, y + 5), Game1.textColor * 0.75f);
            cursorX += 15;
            DrawIcon(b, extra.ItemId, new Vector2(cursorX, y));
            cursorX += IconSpace;
            if (extra.Count > 1)
            {
                string quantity = $"×{extra.Count}";
                Utility.drawTextWithShadow(b, quantity, Game1.tinyFont, new Vector2(cursorX, y + 5), Game1.textColor * 0.75f);
                cursorX += (int)Math.Ceiling(Game1.tinyFont.MeasureString(quantity).X) + 5;
            }
        }

        Utility.drawTextWithShadow(b, "→", Game1.smallFont, new Vector2(cursorX, y), Game1.textColor * 0.75f);
        cursorX += 27;
        DrawIcon(b, route.OutputItemId, new Vector2(cursorX, y));
        cursorX += IconSpace;

        QualityValue best = route.Values.MaxBy(v => v.OutputValue - v.RawValue - route.AdditionalInputCost)!;
        int gain = best.OutputValue - best.RawValue - route.AdditionalInputCost;
        string summary = $"{route.OutputName} · +{gain:N0}g best";
        string fitted = FitSingleLine(summary, Game1.tinyFont, Math.Max(30, width - (cursorX - x)));
        Utility.drawTextWithShadow(b, fitted, Game1.tinyFont, new Vector2(cursorX, y + 5), Game1.textColor * 0.75f);
        y += 34;
    }

    private static void DrawSectionTitle(SpriteBatch b, string text, int x, ref float y, float bottom)
    {
        if (y > bottom - LineHeight)
            return;
        Utility.drawTextWithShadow(b, text, Game1.smallFont, new Vector2(x, y), Game1.textColor);
        y += LineHeight;
    }

    private void DrawRow(SpriteBatch b, string iconId, string label, string value, int x, ref float y, int width)
    {
        const int valueGap = 8;
        DrawIcon(b, iconId, new Vector2(x - 2, y - 3));
        Utility.drawTextWithShadow(b, label + ":", Game1.smallFont, new Vector2(x + IconSpace, y), Game1.textColor * 0.8f);

        int valueX = x + IconSpace + (int)Math.Ceiling(Game1.smallFont.MeasureString(label + ":").X) + valueGap;
        string fitted = FitSingleLine(value, Game1.smallFont, Math.Max(20, width - (valueX - x)));
        Utility.drawTextWithShadow(b, fitted, Game1.smallFont, new Vector2(valueX, y), Game1.textColor);
        y += LineHeight;
    }

    private void DrawIcon(SpriteBatch b, string itemId, Vector2 position)
    {
        if (!IconCache.TryGetValue(itemId, out Item? icon))
        {
            icon = ItemRegistry.Create(itemId, allowNull: true);
            IconCache[itemId] = icon;
        }

        Vector2 alignedPosition = position + new Vector2(-14f, -8f);
        icon?.drawInMenu(b, alignedPosition, IconScale, 1f, 0.9f, StackDrawType.Hide, Color.White, drawShadow: false);
    }

    private static void DrawSingleLine(SpriteBatch b, string text, int x, ref float y, int width, float bottom, bool secondary = false)
    {
        if (y > bottom - LineHeight)
            return;

        string fitted = FitSingleLine(text, Game1.smallFont, width);
        Utility.drawTextWithShadow(b, fitted, Game1.smallFont, new Vector2(x, y), secondary ? Game1.textColor * 0.75f : Game1.textColor);
        y += LineHeight;
    }

    private static void DrawWrappedText(SpriteBatch b, string text, int x, ref float y, int width, float bottom, bool secondary = false)
    {
        if (y > bottom - LineHeight)
            return;

        string parsed = Game1.parseText(text, Game1.smallFont, width);
        float available = bottom - y;
        float measured = Game1.smallFont.MeasureString(parsed).Y;
        if (measured > available)
        {
            // Keep the fixed panel invariant: when text would run below the panel, fall back to one clipped line.
            DrawSingleLine(b, text, x, ref y, width, bottom, secondary);
            return;
        }

        Utility.drawTextWithShadow(b, parsed, Game1.smallFont, new Vector2(x, y), secondary ? Game1.textColor * 0.75f : Game1.textColor);
        y += Math.Max(LineHeight, measured + 3);
    }

    private static string JoinLimited(IReadOnlyList<string> values, int limit, bool showOverflow)
    {
        int count = Math.Min(values.Count, Math.Max(1, limit));
        string result = string.Join(", ", values.Take(count));
        if (showOverflow && values.Count > count)
            result += $", +{values.Count - count}";
        return result;
    }

    private static string FitSingleLine(string text, SpriteFont font, int maxWidth)
    {
        if (string.IsNullOrEmpty(text) || font.MeasureString(text).X <= maxWidth)
            return text;

        const string ellipsis = "…";
        float ellipsisWidth = font.MeasureString(ellipsis).X;
        if (ellipsisWidth >= maxWidth)
            return ellipsis;

        int low = 0;
        int high = text.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            string candidate = text[..mid];
            if (font.MeasureString(candidate).X + ellipsisWidth <= maxWidth)
                low = mid;
            else
                high = mid - 1;
        }

        return text[..low].TrimEnd() + ellipsis;
    }
}

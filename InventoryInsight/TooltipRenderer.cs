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
    private long LastTick = -1;
    private Item? LastItem;

    private const int Padding = 20;
    private const int LineHeight = 30;

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

        int width = expanded ? Mod.Config.ExpandedWidth : Mod.Config.CompactWidth;
        int height = expanded ? Mod.Config.ExpandedHeight : Mod.Config.CompactHeight;
        Rectangle safe = Utility.getSafeArea();
        width = Math.Min(width, safe.Width - 16);
        height = Math.Min(height, safe.Height - 16);

        // Stardew normally opens its own item tooltip to the right of the mouse. Prefer our companion
        // panel on the left so the two remain visible together while scanning an inventory.
        int mouseX = Game1.getOldMouseX();
        int mouseY = Game1.getOldMouseY();
        int leftX = mouseX - width - 28;
        int rightX = mouseX + 40;
        int x = leftX >= safe.Left + 8 ? leftX : rightX;
        int y = mouseY + 24;

        if (x + width > safe.Right - 8)
            x = safe.Right - width - 8;
        if (x < safe.Left + 8)
            x = safe.Left + 8;
        if (y + height > safe.Bottom - 8)
            y = safe.Bottom - height - 8;
        if (y < safe.Top + 8)
            y = safe.Top + 8;

        IClickableMenu.drawTextureBox(b, x, y, width, height, Color.White);

        float cursorY = y + Padding;
        DrawHeader(b, insight.DisplayName, x + Padding, ref cursorY, width - Padding * 2);
        DrawCompactRows(b, insight, x + Padding, ref cursorY, width - Padding * 2);

        if (expanded)
            DrawExpanded(b, insight, x + Padding, cursorY + 5, width - Padding * 2, y + height - Padding);
        else
            DrawHint(b, x + Padding, y + height - 38, width - Padding * 2);
    }

    private static void DrawHeader(SpriteBatch b, string text, int x, ref float y, int width)
    {
        string fitted = FitSingleLine(text, Game1.smallFont, width);
        Utility.drawTextWithShadow(b, fitted, Game1.smallFont, new Vector2(x, y), Game1.textColor);
        y += LineHeight;
        b.Draw(Game1.staminaRect, new Rectangle(x, (int)y, width, 2), Game1.textColor * 0.35f);
        y += 10;
    }

    private void DrawCompactRows(SpriteBatch b, ItemInsight data, int x, ref float y, int width)
    {
        // Exactly the first N names here; overflow belongs in the Shift view, not the scan view.
        string loves = data.LovedBy.Count == 0
            ? "—"
            : string.Join(", ", data.LovedBy.Take(Math.Max(1, Mod.Config.CompactLoveLimit)));

        DrawRow(b, "Loves", loves, x, ref y, width);
        DrawRow(b, "Community Center", data.CommunityCenterNeeded ? "Needed" : "—", x, ref y, width);
        DrawRow(b, "Museum", data.MuseumNeeded ? "Needed" : "—", x, ref y, width);
        DrawRow(b, "Quest / order", data.QuestUses.Count > 0 ? "Needed" : "—", x, ref y, width);
        DrawRow(b, "Crafting", data.CraftingUses.Count > 0 ? "Yes" : "No", x, ref y, width);
        DrawRow(b, "Sell price", data.SellPrice > 0 ? $"{data.SellPrice:N0}g" : "Unsellable", x, ref y, width);
        DrawRow(b, "Safe to sell", data.SafeToSell ? "YES" : "NO", x, ref y, width);
    }

    private void DrawExpanded(SpriteBatch b, ItemInsight data, int x, float top, int width, float bottom)
    {
        const int columnGap = 28;
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
        DrawWrappedText(b, loved, x + 10, ref leftY, leftWidth - 10, bottom, secondary: true);

        if (data.QuestUses.Count > 0)
        {
            leftY += 4;
            DrawSectionTitle(b, "Active quest / order", x, ref leftY, bottom);
            string quests = string.Join(", ", data.QuestUses.Select(p => p.Name).Distinct());
            DrawWrappedText(b, quests, x + 10, ref leftY, leftWidth - 10, bottom, secondary: true);
        }

        leftY += 4;
        DrawSectionTitle(b, "Crafting recipes", x, ref leftY, bottom);
        if (data.CraftingUses.Count == 0)
        {
            DrawSingleLine(b, "—", x + 10, ref leftY, leftWidth - 10, bottom);
        }
        else
        {
            int shown = 0;
            foreach (CraftingUse recipe in data.CraftingUses)
            {
                if (shown >= Mod.Config.ExpandedRecipeLimit || leftY > bottom - LineHeight)
                    break;

                DrawSingleLine(
                    b,
                    $"• {recipe.RecipeName} (x{recipe.RequiredCount})",
                    x + 10,
                    ref leftY,
                    leftWidth - 10,
                    bottom
                );
                shown++;
            }

            if (shown < data.CraftingUses.Count && leftY <= bottom - LineHeight)
                DrawSingleLine(b, $"+ {data.CraftingUses.Count - shown} more", x + 10, ref leftY, leftWidth - 10, bottom, secondary: true);
        }

        // Right column: machine-value comparisons for every quality which the rule accepts.
        DrawSectionTitle(b, "Machine upgrades", rightX, ref rightY, bottom);
        if (data.MachineRoutes.Count == 0)
        {
            DrawWrappedText(b, "No profitable deterministic machine route found.", rightX + 10, ref rightY, rightWidth - 10, bottom, secondary: true);
        }
        else
        {
            int shown = 0;
            foreach (MachineRoute route in data.MachineRoutes)
            {
                if (shown >= Mod.Config.ExpandedMachineLimit || rightY > bottom - 65)
                    break;

                string prefix = $"{route.MachineName} → {route.OutputName}";
                if (route.RequiredInputCount > 1)
                    prefix += $" ({route.RequiredInputCount} input)";

                DrawSingleLine(b, prefix, rightX + 10, ref rightY, rightWidth - 10, bottom);
                DrawWrappedText(
                    b,
                    FormatQualityValues(route),
                    rightX + 22,
                    ref rightY,
                    rightWidth - 22,
                    bottom,
                    secondary: true
                );
                rightY += 5;
                shown++;
            }

            if (shown < data.MachineRoutes.Count && rightY <= bottom - LineHeight)
                DrawSingleLine(b, $"+ {data.MachineRoutes.Count - shown} more routes", rightX + 10, ref rightY, rightWidth - 10, bottom, secondary: true);
        }
    }

    private static string FormatQualityValues(MachineRoute route)
    {
        static string Q(int quality) => quality switch
        {
            1 => "Silver",
            2 => "Gold",
            4 => "Iridium",
            _ => "Normal"
        };

        string values = string.Join(" | ", route.Values.Select(v =>
        {
            int cost = v.RawValue + route.AdditionalInputCost;
            int gain = v.OutputValue - cost;
            string gainText = gain >= 0 ? $"+{gain:N0}" : gain.ToString("N0");
            return $"{Q(v.Quality)} {v.OutputValue:N0}g ({gainText}g)";
        }));

        if (route.AdditionalInputCost > 0)
            values += $" • extras {route.AdditionalInputCost:N0}g";
        return values;
    }

    private static void DrawSectionTitle(SpriteBatch b, string text, int x, ref float y, float bottom)
    {
        if (y > bottom - LineHeight)
            return;
        Utility.drawTextWithShadow(b, text, Game1.smallFont, new Vector2(x, y), Game1.textColor);
        y += LineHeight;
    }

    private static void DrawRow(SpriteBatch b, string label, string value, int x, ref float y, int width)
    {
        const int labelWidth = 170;
        Utility.drawTextWithShadow(b, label + ":", Game1.smallFont, new Vector2(x, y), Game1.textColor * 0.8f);

        string fitted = FitSingleLine(value, Game1.smallFont, Math.Max(80, width - labelWidth));
        Utility.drawTextWithShadow(b, fitted, Game1.smallFont, new Vector2(x + labelWidth, y), Game1.textColor);
        y += LineHeight;
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

    private static void DrawHint(SpriteBatch b, int x, int y, int width)
    {
        string text = "Hold Shift for details + machine values";
        string fitted = FitSingleLine(text, Game1.smallFont, width);
        Vector2 size = Game1.smallFont.MeasureString(fitted);
        Utility.drawTextWithShadow(
            b,
            fitted,
            Game1.smallFont,
            new Vector2(x + Math.Max(0, (width - size.X) / 2f), y),
            Game1.textColor * 0.65f
        );
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

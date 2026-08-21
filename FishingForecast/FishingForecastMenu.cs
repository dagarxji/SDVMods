using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace FishingForecast;

public sealed class FishingForecastMenu : IClickableMenu
{
    private readonly ForecastReport report;
    private readonly Action refresh;
    private readonly ISet<string> excludedLocations;
    private readonly Action<string, bool> selectionChanged;

    private readonly List<AreaSummary> areas;
    private readonly List<AreaRowHitbox> areaHitboxes = new();
    private readonly List<FishIconHitbox> fishHitboxes = new();
    private readonly Dictionary<string, Item> itemCache = new(StringComparer.OrdinalIgnoreCase);

    private ClickableTextureComponent? refreshButton;
    private Rectangle allButtonBounds;
    private Rectangle noneButtonBounds;

    private int sideX;
    private int sideY;
    private int sideWidth;
    private int areaScrollIndex;
    private int visibleAreaRows;

    private string? hoverText;

    /// <summary>
    /// Lookup Anything explicitly recognizes this property name in custom menus.
    /// When F1 is pressed over one of our fish icons it will look up this item.
    /// </summary>
    // Keep both names for maximum compatibility with Lookup Anything's custom-menu scanner.
    // The menu class itself is public too; some reflection paths only inspect exported types.
    public Item? hoveredItem;
    public Item? HoveredItem => this.hoveredItem;

    internal FishingForecastMenu(
        ForecastReport report,
        Action refresh,
        ISet<string> excludedLocations,
        Action<string, bool> selectionChanged)
    {
        this.report = report;
        this.refresh = refresh;
        this.excludedLocations = excludedLocations;
        this.selectionChanged = selectionChanged;

        this.areas = report.Slots
            .SelectMany(slot => slot.Locations)
            .GroupBy(location => location.LocationName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                LocationForecast first = group.First();
                return new AreaSummary(
                    first.LocationName,
                    first.DisplayName,
                    group.Min(p => p.EstimatedTravelMinutes),
                    group.Max(p => p.TravelAdjustedGold)
                );
            })
            .OrderByDescending(p => p.BestBlockGold)
            .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        this.Reposition();
        this.initializeUpperRightCloseButton();
        this.CreateRefreshButton();
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        base.gameWindowSizeChanged(oldBounds, newBounds);
        this.Reposition();
        this.initializeUpperRightCloseButton();
        this.CreateRefreshButton();
    }

    private void Reposition()
    {
        int viewportWidth = Game1.uiViewport.Width;
        int viewportHeight = Game1.uiViewport.Height;
        int margin = 22;
        int gap = 12;

        this.height = Math.Max(500, Math.Min(780, viewportHeight - margin * 2));
        if (this.height > viewportHeight - 16)
            this.height = Math.Max(320, viewportHeight - 16);

        int totalAvailable = Math.Max(760, viewportWidth - margin * 2);
        this.sideWidth = Math.Clamp((int)(viewportWidth * 0.22f), 235, 330);
        this.width = Math.Min(1040, totalAvailable - this.sideWidth - gap);

        if (this.width < 680)
        {
            this.width = Math.Max(560, totalAvailable - 220 - gap);
            this.sideWidth = Math.Max(190, totalAvailable - this.width - gap);
        }

        int totalWidth = this.width + gap + this.sideWidth;
        this.xPositionOnScreen = Math.Max(8, (viewportWidth - totalWidth) / 2);
        this.yPositionOnScreen = Math.Max(8, (viewportHeight - this.height) / 2);

        this.sideX = this.xPositionOnScreen + this.width + gap;
        this.sideY = this.yPositionOnScreen;
    }

    private void CreateRefreshButton()
    {
        this.refreshButton = new ClickableTextureComponent(
            new Rectangle(this.xPositionOnScreen + this.width - 108, this.yPositionOnScreen + 18, 48, 48),
            Game1.mouseCursors,
            new Rectangle(128, 256, 64, 64),
            0.75f
        )
        {
            hoverText = "Recalculate"
        };
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (this.upperRightCloseButton?.containsPoint(x, y) == true)
        {
            this.exitThisMenu(playSound);
            return;
        }

        if (this.refreshButton?.containsPoint(x, y) == true)
        {
            if (playSound)
                Game1.playSound("smallSelect");
            this.refresh();
            return;
        }

        if (this.allButtonBounds.Contains(x, y))
        {
            foreach (AreaSummary area in this.areas)
            {
                this.excludedLocations.Remove(area.LocationName);
                this.selectionChanged(area.LocationName, true);
            }
            if (playSound)
                Game1.playSound("smallSelect");
            return;
        }

        if (this.noneButtonBounds.Contains(x, y))
        {
            foreach (AreaSummary area in this.areas)
            {
                this.excludedLocations.Add(area.LocationName);
                this.selectionChanged(area.LocationName, false);
            }
            if (playSound)
                Game1.playSound("smallSelect");
            return;
        }

        foreach (AreaRowHitbox hitbox in this.areaHitboxes)
        {
            if (!hitbox.Bounds.Contains(x, y))
                continue;

            bool include = this.excludedLocations.Contains(hitbox.LocationName);
            if (include)
                this.excludedLocations.Remove(hitbox.LocationName);
            else
                this.excludedLocations.Add(hitbox.LocationName);

            this.selectionChanged(hitbox.LocationName, include);
            if (playSound)
                Game1.playSound("drumkit6");
            return;
        }

        base.receiveLeftClick(x, y, playSound);
    }

    public override void receiveScrollWheelAction(int direction)
    {
        if (this.areas.Count <= this.visibleAreaRows)
        {
            base.receiveScrollWheelAction(direction);
            return;
        }

        int maxScroll = Math.Max(0, this.areas.Count - this.visibleAreaRows);
        if (direction > 0)
            this.areaScrollIndex = Math.Max(0, this.areaScrollIndex - 1);
        else if (direction < 0)
            this.areaScrollIndex = Math.Min(maxScroll, this.areaScrollIndex + 1);

        Game1.playSound("shiny4");
    }

    public override void performHoverAction(int x, int y)
    {
        this.refreshButton?.tryHover(x, y, 0.15f);
        this.UpdateHoveredFish(x, y);
        base.performHoverAction(x, y);
    }

    private void UpdateHoveredFish(int x, int y)
    {
        this.hoveredItem = null;
        this.hoverText = null;

        foreach (FishIconHitbox hitbox in this.fishHitboxes)
        {
            if (!hitbox.Bounds.Contains(x, y))
                continue;

            if (!this.itemCache.TryGetValue(hitbox.Fish.QualifiedItemId, out Item? item))
            {
                try
                {
                    item = ItemRegistry.Create(hitbox.Fish.QualifiedItemId);
                    this.itemCache[hitbox.Fish.QualifiedItemId] = item;
                }
                catch
                {
                    item = null;
                }
            }

            this.hoveredItem = item;

            string limitText = hitbox.Fish.RemainingCatchLimit >= 0
                ? $"\nRemaining catch limit: {hitbox.Fish.RemainingCatchLimit}"
                : string.Empty;

            this.hoverText =
                $"{hitbox.Fish.DisplayName}\n" +
                $"Expected: {hitbox.Fish.ExpectedCount:0.##} of {hitbox.Location.ExpectedCatches:0.#} catches " +
                $"({hitbox.Fish.ShareOfCatchSlots:P1})\n" +
                $"Sell value: {hitbox.Fish.SaleValue}g{limitText}\n" +
                "Press F1 for Lookup Anything";
            break;
        }
    }

    public override void draw(SpriteBatch b)
    {
        // Game1.uiViewport is xTile.Dimensions.Rectangle. Construct an XNA rectangle
        // explicitly so SpriteBatch.Draw always gets the correct overload.
        b.Draw(
            Game1.fadeToBlackRect,
            new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height),
            Color.Black * 0.68f
        );

        this.fishHitboxes.Clear();
        this.areaHitboxes.Clear();

        this.DrawForecastPanel(b);
        this.DrawAreaPanel(b);

        // Re-evaluate from the freshly rebuilt icon hitboxes every frame. This keeps
        // Lookup Anything's HoveredItem valid even if F1 is pressed without a mouse-move event.
        this.UpdateHoveredFish(Game1.getMouseX(), Game1.getMouseY());

        this.refreshButton?.draw(b);
        this.upperRightCloseButton?.draw(b);

        if (!string.IsNullOrWhiteSpace(this.hoverText))
            IClickableMenu.drawHoverText(b, this.hoverText, Game1.smallFont);

        this.drawMouse(b);
    }

    private void DrawForecastPanel(SpriteBatch b)
    {
        drawTextureBox(
            b,
            this.xPositionOnScreen,
            this.yPositionOnScreen,
            this.width,
            this.height,
            Color.White
        );

        int left = this.xPositionOnScreen + 24;
        int top = this.yPositionOnScreen + 20;
        int innerWidth = this.width - 48;
        int rightReserved = 125;

        Utility.drawTextWithShadow(
            b,
            "Fishing Forecast",
            Game1.dialogueFont,
            new Vector2(left, top),
            Game1.textColor
        );

        string subtitle =
            $"Fishing {this.report.FishingLevel}  •  {this.report.WeatherSummary}  •  Reach: {ShortReachability(this.report.ReachabilitySource)}";
        DrawBoldClippedText(
            b,
            Game1.smallFont,
            subtitle,
            new Vector2(left, top + 51),
            innerWidth - rightReserved,
            Game1.textColor * 0.88f,
            0.70f
        );

        DrawBoldClippedText(
            b,
            Game1.smallFont,
            this.report.EquipmentSummary,
            new Vector2(left, top + 71),
            innerWidth - rightReserved,
            Game1.textColor * 0.82f,
            0.64f
        );

        int tableTop = top + 101;
        int footerHeight = 34;
        int availableTableHeight = this.yPositionOnScreen + this.height - footerHeight - tableTop;
        int slotHeight = Math.Max(78, availableTableHeight / 5);

        int timeWidth = Math.Clamp((int)(innerWidth * 0.12f), 88, 118);
        int colGap = 7;
        int rankWidth = Math.Max(130, (innerWidth - timeWidth - colGap * 3) / 3);

        for (int slotIndex = 0; slotIndex < this.report.Slots.Count && slotIndex < 5; slotIndex++)
        {
            ForecastSlot slot = this.report.Slots[slotIndex];
            int y = tableTop + slotIndex * slotHeight;

            if (slotIndex > 0)
                b.Draw(Game1.staminaRect, new Rectangle(left, y - 3, innerWidth, 2), Color.Black * 0.18f);

            DrawBoldClippedText(
                b,
                Game1.smallFont,
                slot.Label,
                new Vector2(left, y + 12),
                timeWidth - 6,
                Game1.textColor,
                0.70f
            );

            LocationForecast[] visible = slot.Locations
                .Where(location => !this.excludedLocations.Contains(location.LocationName))
                .OrderByDescending(location => location.TravelAdjustedGold)
                .ThenByDescending(location => location.GoldPerHour)
                .Take(3)
                .ToArray();

            for (int rank = 0; rank < 3; rank++)
            {
                int x = left + timeWidth + colGap + rank * (rankWidth + colGap);
                Rectangle card = new Rectangle(x, y + 2, rankWidth, Math.Max(68, slotHeight - 8));
                b.Draw(Game1.staminaRect, card, Color.Black * 0.075f);

                if (rank >= visible.Length)
                {
                    string empty = rank == 0 && slot.Locations.Count > 0
                        ? "No selected area"
                        : $"#{rank + 1}  —";
                    DrawBoldClippedText(
                        b,
                        Game1.smallFont,
                        empty,
                        new Vector2(card.X + 8, card.Y + 8),
                        card.Width - 16,
                        Game1.textColor * 0.52f,
                        0.68f
                    );
                    continue;
                }

                this.DrawLocationCard(b, card, rank + 1, visible[rank]);
            }
        }

        string footer =
            "Fish % = expected share of the 4-hour catch slots after catch limits • hover fish for details • F1 = Lookup Anything";
        DrawBoldClippedText(
            b,
            Game1.smallFont,
            footer,
            new Vector2(left, this.yPositionOnScreen + this.height - 28),
            innerWidth,
            Game1.textColor * 0.68f,
            0.50f
        );
    }

    private void DrawLocationCard(SpriteBatch b, Rectangle card, int rank, LocationForecast location)
    {
        int left = card.X + 8;
        int width = card.Width - 16;

        DrawBoldClippedText(
            b,
            Game1.smallFont,
            $"#{rank}  {location.DisplayName}",
            new Vector2(left, card.Y + 5),
            width,
            Game1.textColor,
            0.72f
        );

        double blockValue = location.EstimatedTravelMinutes > 0
            ? location.TravelAdjustedGold
            : location.FourHourGross;

        DrawBoldClippedText(
            b,
            Game1.smallFont,
            $"{ShortGold(location.GoldPerHour)}/hr  •  {ShortGold(blockValue)}/4h",
            new Vector2(left, card.Y + 26),
            width,
            new Color(45, 115, 48),
            0.62f
        );

        string travel = IsCurrentLocation(location.LocationName)
            ? "here"
            : $"~{Math.Max(10, location.EstimatedTravelMinutes)}m travel";
        double catches = location.EstimatedTravelMinutes > 0
            ? location.TravelAdjustedExpectedCatches
            : location.ExpectedCatches;

        DrawBoldClippedText(
            b,
            Game1.smallFont,
            $"~{catches:0.#} catches  •  {travel}",
            new Vector2(left, card.Y + 44),
            width,
            Game1.textColor * 0.77f,
            0.58f
        );

        int iconY = card.Y + 67;
        int availableIconHeight = card.Bottom - iconY - 4;
        if (availableIconHeight < 20)
            return;

        FishCatchForecast[] fish = location.Fish.Take(4).ToArray();
        if (fish.Length == 0)
        {
            DrawBoldClippedText(
                b,
                Game1.smallFont,
                "No fish sampled",
                new Vector2(left, iconY + 3),
                width,
                Game1.textColor * 0.55f,
                0.56f
            );
            return;
        }

        int slotWidth = Math.Max(28, width / 4);
        int iconSize = Math.Clamp(Math.Min(slotWidth - 5, availableIconHeight - 11), 20, 30);

        for (int i = 0; i < fish.Length; i++)
        {
            FishCatchForecast entry = fish[i];
            int centerX = left + i * slotWidth + slotWidth / 2;
            Rectangle iconBounds = new Rectangle(centerX - iconSize / 2, iconY, iconSize, iconSize);

            try
            {
                var data = ItemRegistry.GetDataOrErrorItem(entry.QualifiedItemId);
                b.Draw(
                    data.GetTexture(),
                    iconBounds,
                    data.GetSourceRect(),
                    Color.White
                );
            }
            catch
            {
                b.Draw(Game1.staminaRect, iconBounds, Color.Black * 0.12f);
            }

            this.fishHitboxes.Add(new FishIconHitbox(iconBounds, location, entry));

            DrawBoldCenteredText(
                b,
                Game1.smallFont,
                $"{entry.ShareOfCatchSlots:P0}",
                new Vector2(centerX, iconBounds.Bottom - 1),
                Math.Max(26, slotWidth - 2),
                Game1.textColor * 0.82f,
                0.55f
            );
        }
    }

    private void DrawAreaPanel(SpriteBatch b)
    {
        drawTextureBox(
            b,
            this.sideX,
            this.sideY,
            this.sideWidth,
            this.height,
            Color.White
        );

        int left = this.sideX + 16;
        int width = this.sideWidth - 32;

        DrawBoldClippedText(
            b,
            Game1.smallFont,
            "Areas",
            new Vector2(left, this.sideY + 18),
            width,
            Game1.textColor,
            0.86f
        );
        DrawBoldClippedText(
            b,
            Game1.smallFont,
            "profit order • click to include/exclude",
            new Vector2(left, this.sideY + 43),
            width,
            Game1.textColor * 0.67f,
            0.58f
        );

        int buttonY = this.sideY + 64;
        this.allButtonBounds = new Rectangle(left, buttonY, 52, 23);
        this.noneButtonBounds = new Rectangle(left + 58, buttonY, 56, 23);
        DrawSmallButton(b, this.allButtonBounds, "All");
        DrawSmallButton(b, this.noneButtonBounds, "None");

        int listTop = this.sideY + 96;
        int listBottom = this.sideY + this.height - 18;
        int rowHeight = 40;
        this.visibleAreaRows = Math.Max(1, (listBottom - listTop) / rowHeight);
        int maxScroll = Math.Max(0, this.areas.Count - this.visibleAreaRows);
        this.areaScrollIndex = Math.Clamp(this.areaScrollIndex, 0, maxScroll);

        int end = Math.Min(this.areas.Count, this.areaScrollIndex + this.visibleAreaRows);
        for (int index = this.areaScrollIndex; index < end; index++)
        {
            AreaSummary area = this.areas[index];
            int row = index - this.areaScrollIndex;
            int y = listTop + row * rowHeight;
            Rectangle rowBounds = new Rectangle(left, y, width, rowHeight - 2);
            bool included = !this.excludedLocations.Contains(area.LocationName);

            if (row > 0)
                b.Draw(Game1.staminaRect, new Rectangle(left, y - 1, width, 1), Color.Black * 0.10f);

            Rectangle checkbox = new Rectangle(left + 1, y + 10, 18, 18);
            b.Draw(Game1.staminaRect, checkbox, Color.Black * 0.22f);
            Rectangle inner = new Rectangle(checkbox.X + 2, checkbox.Y + 2, checkbox.Width - 4, checkbox.Height - 4);
            b.Draw(
                Game1.staminaRect,
                inner,
                included ? new Color(74, 126, 70) : Color.White * 0.45f
            );

            if (included)
            {
                DrawBoldCenteredText(
                    b,
                    Game1.smallFont,
                    "X",
                    new Vector2(checkbox.Center.X, checkbox.Y + 1),
                    checkbox.Width,
                    Color.White,
                    0.55f
                );
            }

            string distance = IsCurrentLocation(area.LocationName)
                ? "here"
                : $"~{Math.Max(10, area.TravelMinutes)}m";

            DrawBoldClippedText(
                b,
                Game1.smallFont,
                area.DisplayName,
                new Vector2(left + 27, y + 3),
                width - 80,
                included ? Game1.textColor : Game1.textColor * 0.48f,
                0.68f
            );

            DrawBoldRightAlignedText(
                b,
                Game1.smallFont,
                distance,
                new Vector2(left + width - 1, y + 4),
                58,
                Game1.textColor * (included ? 0.72f : 0.40f),
                0.58f
            );

            DrawBoldClippedText(
                b,
                Game1.smallFont,
                $"best {ShortGold(area.BestBlockGold)}/4h",
                new Vector2(left + 27, y + 21),
                width - 32,
                included ? new Color(45, 112, 48) : Game1.textColor * 0.38f,
                0.56f
            );

            this.areaHitboxes.Add(new AreaRowHitbox(rowBounds, area.LocationName));
        }

        if (this.areas.Count > this.visibleAreaRows)
        {
            string page = $"{this.areaScrollIndex + 1}–{end} / {this.areas.Count}";
            DrawBoldRightAlignedText(
                b,
                Game1.smallFont,
                page,
                new Vector2(this.sideX + this.sideWidth - 18, this.sideY + 69),
                100,
                Game1.textColor * 0.55f,
                0.55f
            );
        }
    }

    private static void DrawSmallButton(SpriteBatch b, Rectangle bounds, string text)
    {
        b.Draw(Game1.staminaRect, bounds, Color.Black * 0.10f);
        DrawBoldCenteredText(
            b,
            Game1.smallFont,
            text,
            new Vector2(bounds.Center.X, bounds.Y + 3),
            bounds.Width - 4,
            Game1.textColor,
            0.58f
        );
    }

    private static bool IsCurrentLocation(string locationName)
    {
        return string.Equals(
            locationName,
            Game1.currentLocation?.NameOrUniqueName,
            StringComparison.OrdinalIgnoreCase
        );
    }

    private static string ShortReachability(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Unknown";

        if (value.Contains("World Navigator", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
            return "World Navigator";

        if (value.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
            return "Fallback (WN unavailable)";

        if (value.Contains("fallback", StringComparison.OrdinalIgnoreCase))
            return "Fallback";

        return value;
    }

    private static string ShortGold(double value)
    {
        double abs = Math.Abs(value);
        if (abs >= 1_000_000)
            return $"~{value / 1_000_000d:0.#}m g";
        if (abs >= 10_000)
            return $"~{value / 1_000d:0.#}k g";
        if (abs >= 1_000)
            return $"~{value / 1_000d:0.##}k g";
        return $"~{value:0} g";
    }

    private static void DrawBoldClippedText(
        SpriteBatch b,
        SpriteFont font,
        string? text,
        Vector2 position,
        float maxWidth,
        Color color,
        float scale)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 1f || scale <= 0f)
            return;

        string fitted = font.MeasureString(text).X * scale <= maxWidth
            ? text
            : TruncateToPixelWidth(font, text, maxWidth / scale);

        DrawBoldText(b, font, fitted, position, color, scale);
    }

    private static void DrawBoldCenteredText(
        SpriteBatch b,
        SpriteFont font,
        string text,
        Vector2 centerTop,
        float maxWidth,
        Color color,
        float scale)
    {
        string fitted = font.MeasureString(text).X * scale <= maxWidth
            ? text
            : TruncateToPixelWidth(font, text, maxWidth / scale);
        float width = font.MeasureString(fitted).X * scale;
        DrawBoldText(b, font, fitted, new Vector2(centerTop.X - width / 2f, centerTop.Y), color, scale);
    }

    private static void DrawBoldRightAlignedText(
        SpriteBatch b,
        SpriteFont font,
        string text,
        Vector2 rightTop,
        float maxWidth,
        Color color,
        float scale)
    {
        string fitted = font.MeasureString(text).X * scale <= maxWidth
            ? text
            : TruncateToPixelWidth(font, text, maxWidth / scale);
        float width = font.MeasureString(fitted).X * scale;
        DrawBoldText(b, font, fitted, new Vector2(rightTop.X - width, rightTop.Y), color, scale);
    }

    private static void DrawBoldText(
        SpriteBatch b,
        SpriteFont font,
        string text,
        Vector2 position,
        Color color,
        float scale)
    {
        // Repeated same-color offset passes made the downscaled SpriteFont look smeared.
        // Use one subtle shadow plus one foreground pass instead; the larger scales above
        // keep the text compact while preserving much cleaner glyph edges.
        Color shadow = Color.Black * (color.A / 255f) * 0.28f;
        b.DrawString(font, text, position + new Vector2(1f, 1f), shadow, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        b.DrawString(font, text, position, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }

    private static string TruncateToPixelWidth(SpriteFont font, string text, float maxUnscaledWidth)
    {
        if (font.MeasureString(text).X <= maxUnscaledWidth)
            return text;

        const string ellipsis = "…";
        float ellipsisWidth = font.MeasureString(ellipsis).X;
        if (ellipsisWidth >= maxUnscaledWidth)
            return string.Empty;

        int low = 0;
        int high = text.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            string candidate = text[..mid];
            if (font.MeasureString(candidate).X + ellipsisWidth <= maxUnscaledWidth)
                low = mid;
            else
                high = mid - 1;
        }

        return low <= 0 ? ellipsis : text[..low].TrimEnd() + ellipsis;
    }

    private sealed record AreaSummary(
        string LocationName,
        string DisplayName,
        int TravelMinutes,
        double BestBlockGold
    );

    private sealed record AreaRowHitbox(Rectangle Bounds, string LocationName);
    private sealed record FishIconHitbox(Rectangle Bounds, LocationForecast Location, FishCatchForecast Fish);
}

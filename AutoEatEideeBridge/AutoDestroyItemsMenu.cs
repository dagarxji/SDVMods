using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace AutoEatEideeBridge;

/// <summary>
/// Editor for the auto-destroy item list. The top half lists the configured items with an X button
/// to remove each; the bottom half shows the player's inventory, where clicking an item adds it.
/// </summary>
internal sealed class AutoDestroyItemsMenu : IClickableMenu
{
    private const int SlotSize = 64;
    private const int RowHeight = 48;
    private const int VisibleRows = 5;
    private const int Columns = 12;
    private const int Padding = 32;

    /// <summary>The live list from <see cref="ModConfig.AutoDestroyItemIds"/>; edits are written back directly and saved.</summary>
    private readonly List<string> _itemIds;
    private readonly Action _save;
    private readonly Dictionary<string, Item?> _displayItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ClickableTextureComponent> _removeButtons = new();
    private readonly List<ClickableComponent> _inventorySlots = new();
    private readonly ClickableTextureComponent _scrollUpButton;
    private readonly ClickableTextureComponent _scrollDownButton;

    private int _scrollRow;
    private readonly int _listAreaY;
    private readonly int _inventoryAreaY;
    private string? _hoverText;

    public AutoDestroyItemsMenu(List<string> itemIds, Action save)
        : base(0, 0, 0, 0, showUpperRightCloseButton: true)
    {
        _itemIds = itemIds;
        _save = save;

        int inventoryRows = Math.Max(1, (Game1.player.Items.Count + Columns - 1) / Columns);
        width = Columns * SlotSize + Padding * 2;

        int listAreaOffset = Padding + 80;
        int inventoryAreaOffset = listAreaOffset + VisibleRows * RowHeight + 48;
        height = inventoryAreaOffset + inventoryRows * SlotSize + Padding;

        xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
        yPositionOnScreen = Math.Max(0, (Game1.uiViewport.Height - height) / 2);

        _listAreaY = yPositionOnScreen + listAreaOffset;
        _inventoryAreaY = yPositionOnScreen + inventoryAreaOffset;

        if (upperRightCloseButton is not null)
        {
            upperRightCloseButton.bounds.X = xPositionOnScreen + width - 60;
            upperRightCloseButton.bounds.Y = yPositionOnScreen + 12;
        }

        _scrollUpButton = new ClickableTextureComponent(
            new Rectangle(xPositionOnScreen + width - Padding - 32, _listAreaY - 36, 44, 48),
            Game1.mouseCursors,
            new Rectangle(352, 495, 12, 11),
            4f);
        _scrollDownButton = new ClickableTextureComponent(
            new Rectangle(xPositionOnScreen + width - Padding - 32, _listAreaY + VisibleRows * RowHeight - 12, 44, 48),
            Game1.mouseCursors,
            new Rectangle(365, 495, 12, 11),
            4f);

        LayoutComponents();
    }

    private bool ContainsItemId(string itemId)
    {
        foreach (string listedId in _itemIds)
        {
            if (string.Equals(listedId, itemId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private Item? GetDisplayItem(string itemId)
    {
        if (!_displayItems.TryGetValue(itemId, out Item? item))
        {
            try
            {
                item = ItemRegistry.Create(itemId, 1, 0, allowNull: true);
            }
            catch
            {
                item = null;
            }

            _displayItems[itemId] = item;
        }

        return item;
    }

    /// <summary>Rebuild the clickable components for the current scroll position and list contents.</summary>
    private void LayoutComponents()
    {
        _removeButtons.Clear();
        int visibleCount = Math.Min(VisibleRows, _itemIds.Count - _scrollRow);
        for (int row = 0; row < visibleCount; row++)
        {
            _removeButtons.Add(new ClickableTextureComponent(
                _itemIds[_scrollRow + row],
                new Rectangle(xPositionOnScreen + width - Padding - RowHeight, _listAreaY + row * RowHeight, RowHeight, RowHeight),
                null,
                "Remove",
                Game1.mouseCursors,
                new Rectangle(268, 470, 16, 16),
                3f));
        }

        _inventorySlots.Clear();
        IList<Item> items = Game1.player.Items;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is null)
                continue;

            _inventorySlots.Add(new ClickableComponent(
                new Rectangle(xPositionOnScreen + Padding + (i % Columns) * SlotSize, _inventoryAreaY + (i / Columns) * SlotSize, SlotSize, SlotSize),
                items[i]!.QualifiedItemId)
            {
                item = items[i]
            });
        }
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);

        if (upperRightCloseButton is not null && upperRightCloseButton.containsPoint(x, y))
        {
            exitThisMenu();
            return;
        }

        if (ScrollByIfClicked(_scrollUpButton, x, y, -1) || ScrollByIfClicked(_scrollDownButton, x, y, 1))
            return;

        foreach (ClickableTextureComponent button in _removeButtons)
        {
            if (!button.containsPoint(x, y))
                continue;

            _itemIds.RemoveAll(id => string.Equals(id, button.name, StringComparison.OrdinalIgnoreCase));
            _scrollRow = Math.Min(_scrollRow, Math.Max(0, _itemIds.Count - VisibleRows));
            Game1.playSound("bigDeSelect");
            LayoutComponents();
            _save();
            return;
        }

        foreach (ClickableComponent slot in _inventorySlots)
        {
            if (!slot.containsPoint(x, y) || slot.item is null)
                continue;

            if (ContainsItemId(slot.item.QualifiedItemId))
            {
                Game1.playSound("cancel");
            }
            else
            {
                _itemIds.Add(slot.item.QualifiedItemId);
                Game1.playSound("smallSelect");
                LayoutComponents();
                _save();
            }
            return;
        }
    }

    private bool ScrollByIfClicked(ClickableTextureComponent button, int x, int y, int direction)
    {
        int maxScroll = Math.Max(0, _itemIds.Count - VisibleRows);
        if (maxScroll == 0 || !button.containsPoint(x, y))
            return false;

        _scrollRow = Math.Clamp(_scrollRow + direction, 0, maxScroll);
        Game1.playSound("shiny4");
        LayoutComponents();
        return true;
    }

    public override void receiveScrollWheelAction(int direction)
    {
        base.receiveScrollWheelAction(direction);

        int maxScroll = Math.Max(0, _itemIds.Count - VisibleRows);
        int newScrollRow = Math.Clamp(_scrollRow + (direction > 0 ? -1 : 1), 0, maxScroll);
        if (newScrollRow != _scrollRow)
        {
            _scrollRow = newScrollRow;
            LayoutComponents();
        }
    }

    public override void receiveKeyPress(Keys key)
    {
        if (Game1.options.doesInputListContain(Game1.options.menuButton, key) && readyToClose())
        {
            exitThisMenu();
            return;
        }

        base.receiveKeyPress(key);
    }

    public override void receiveGamePadButton(Buttons b)
    {
        if (b == Buttons.B)
            exitThisMenu();
        else
            base.receiveGamePadButton(b);
    }

    public override void performHoverAction(int x, int y)
    {
        base.performHoverAction(x, y);

        _hoverText = null;
        foreach (ClickableTextureComponent button in _removeButtons)
        {
            button.tryHover(x, y, 0.4f);
            if (button.containsPoint(x, y))
                _hoverText = "Remove";
        }

        _scrollUpButton.tryHover(x, y, 0.2f);
        _scrollDownButton.tryHover(x, y, 0.2f);

        foreach (ClickableComponent slot in _inventorySlots)
        {
            if (slot.containsPoint(x, y) && slot.item is not null)
                _hoverText = slot.item.DisplayName;
        }
    }

    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);

        drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), xPositionOnScreen, yPositionOnScreen, width, height, Color.White);

        string title = "Auto-Destroy Items";
        Vector2 titleSize = Game1.dialogueFont.MeasureString(title);
        b.DrawString(Game1.dialogueFont, title, new Vector2(xPositionOnScreen + (width - titleSize.X) / 2, yPositionOnScreen + Padding - 12), Game1.textColor);

        b.DrawString(Game1.smallFont, "These items are destroyed automatically when caught while fishing.",
            new Vector2(xPositionOnScreen + Padding, yPositionOnScreen + Padding + 40), Game1.textColor);

        if (_itemIds.Count == 0)
        {
            b.DrawString(Game1.smallFont, "No items yet.", new Vector2(xPositionOnScreen + Padding, _listAreaY + 8), Color.Gray);
        }
        else
        {
            int visibleCount = Math.Min(VisibleRows, _itemIds.Count - _scrollRow);
            for (int row = 0; row < visibleCount; row++)
            {
                string itemId = _itemIds[_scrollRow + row];
                int rowY = _listAreaY + row * RowHeight;

                Item? displayItem = GetDisplayItem(itemId);
                displayItem?.drawInMenu(b, new Vector2(xPositionOnScreen + Padding, rowY), 0.75f);
                b.DrawString(Game1.smallFont, displayItem?.DisplayName ?? itemId,
                    new Vector2(xPositionOnScreen + Padding + RowHeight + 8, rowY + 12), Game1.textColor);
            }

            foreach (ClickableTextureComponent button in _removeButtons)
                button.draw(b);

            if (_itemIds.Count > VisibleRows)
            {
                _scrollUpButton.draw(b);
                _scrollDownButton.draw(b);
            }
        }

        b.DrawString(Game1.smallFont, "Click an item in your inventory to add it:",
            new Vector2(xPositionOnScreen + Padding, _inventoryAreaY - 32), Game1.textColor);

        IList<Item> inventory = Game1.player.Items;
        for (int i = 0; i < inventory.Count; i++)
        {
            int slotX = xPositionOnScreen + Padding + (i % Columns) * SlotSize;
            int slotY = _inventoryAreaY + (i / Columns) * SlotSize;
            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), slotX, slotY, SlotSize, SlotSize, Color.White);

            Item? item = inventory[i];
            if (item is null)
                continue;

            bool alreadyListed = ContainsItemId(item.QualifiedItemId);
            if (alreadyListed)
                drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), slotX, slotY, SlotSize, SlotSize, Color.Gray * 0.6f);
            item.drawInMenu(b, new Vector2(slotX, slotY), 1f, alreadyListed ? 0.5f : 1f, 1f, StackDrawType.Draw, Color.White, true);
        }

        upperRightCloseButton?.draw(b);

        if (_hoverText is not null)
            drawHoverText(b, _hoverText, Game1.smallFont);

        drawMouse(b);
    }
}

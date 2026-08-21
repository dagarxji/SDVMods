using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Quests;
using SObject = StardewValley.Object;

namespace RemoteSocialInteractions;

internal sealed class RemoteItemPickerMenu : IClickableMenu
{
    private readonly ModEntry mod;
    private readonly GameMenu parentMenu;
    private readonly NPC npc;
    private readonly InventoryMenu inventory;

    private Item? hoveredItem;
    private string statusText = "";

    public RemoteItemPickerMenu(ModEntry mod, GameMenu parentMenu, NPC npc)
        : base(
            Game1.uiViewport.Width / 2 - 448,
            Game1.uiViewport.Height / 2 - 220,
            896,
            440,
            showUpperRightCloseButton: true)
    {
        this.mod = mod;
        this.parentMenu = parentMenu;
        this.npc = npc;
        int inventoryX = xPositionOnScreen + 64;
        int inventoryY = yPositionOnScreen + 116;

        inventory = new InventoryMenu(
            inventoryX,
            inventoryY,
            playerInventory: true,
            actualInventory: Game1.player.Items,
            highlightMethod: IsSelectableItem,
            capacity: 36,
            rows: 3
        );

    }

    private bool IsSelectableItem(Item item)
    {
        // Delivery-quest items are always selectable for the matching NPC, even
        // if the item itself isn't normally giftable. Everything else follows
        // vanilla gift eligibility.
        return mod.FindMatchingDeliveryQuest(npc, item) is not null
            || item is SObject obj && obj.canBeGivenAsGift();
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (upperRightCloseButton?.containsPoint(x, y) == true)
        {
            ReturnToSocial();
            Game1.playSound("bigDeSelect");
            return;
        }

        int slot = inventory.getInventoryPositionOfClick(x, y);
        if (slot < 0 || slot >= Game1.player.Items.Count)
            return;

        Item? item = Game1.player.Items[slot];
        if (item is null)
            return;

        if (!IsSelectableItem(item))
        {
            inventory.ShakeItem(slot);
            Game1.playSound("cancel");
            statusText = "That item can't be given to this NPC.";
            return;
        }

        // If this is the item an active delivery quest wants, treat it as a
        // quest hand-in. Otherwise this is a normal gift. This lets the one
        // vanilla gift icon serve both purposes cleanly.
        if (mod.FindMatchingDeliveryQuest(npc, item) is not null)
            DeliverQuestItem(slot, item);
        else
            GiveGift(slot);
    }

    private void GiveGift(int slot)
    {
        bool handled = WithInventorySlotAsActiveObject(slot, () =>
        {
            if (Game1.player.ActiveObject is null || !npc.tryToReceiveActiveObject(Game1.player, probe: true))
                return false;

            mod.PrepareReturnToSocial(parentMenu);
            return npc.tryToReceiveActiveObject(Game1.player);
        });

        if (!handled)
        {
            mod.CancelPendingReturn();
            Game1.activeClickableMenu = this;
            statusText = $"{npc.displayName} can't accept that item right now.";
            Game1.playSound("cancel");
            return;
        }

        mod.RestoreImmediatelyIfNoDialogue(parentMenu);
    }

    private void DeliverQuestItem(int slot, Item item)
    {
        ItemDeliveryQuest? quest = mod.FindMatchingDeliveryQuest(npc, item);
        if (quest is null)
        {
            inventory.ShakeItem(slot);
            statusText = "That item no longer matches an active delivery quest.";
            Game1.playSound("cancel");
            return;
        }

        bool completed = WithInventorySlotAsActiveObject(slot, () =>
        {
            if (Game1.player.ActiveObject is null)
                return false;

            mod.PrepareReturnToSocial(parentMenu);

            // Stardew 1.6.9+ replaced Farmer.checkForQuestComplete with
            // Farmer.NotifyQuests and Quest.OnItemOfferedToNpc. Use the same
            // vanilla quest event so item consumption, friendship, dialogue,
            // rewards, and completion bookkeeping stay in the game's hands.
            return Game1.player.NotifyQuests(q =>
                q.OnItemOfferedToNpc(npc, Game1.player.ActiveObject)
            );
        });

        if (!completed)
        {
            mod.CancelPendingReturn();
            Game1.activeClickableMenu = this;
            statusText = "The quest couldn't be completed with that item.";
            Game1.playSound("cancel");
            return;
        }

        mod.RestoreImmediatelyIfNoDialogue(parentMenu);
    }

    private static T WithInventorySlotAsActiveObject<T>(int selectedSlot, Func<T> action)
    {
        int activeSlot = Game1.player.CurrentToolIndex;
        if (activeSlot < 0 || activeSlot >= Game1.player.Items.Count)
            activeSlot = 0;

        if (selectedSlot == activeSlot)
            return action();

        // Vanilla gifting and ItemDeliveryQuest consume Game1.player.ActiveObject.
        // Temporarily swap the selected inventory item into the current hotbar
        // slot, run vanilla logic, then swap the remaining stack back. This works
        // for items selected from any of the 36 backpack slots without changing
        // the player's selected hotbar slot.
        (Game1.player.Items[activeSlot], Game1.player.Items[selectedSlot]) =
            (Game1.player.Items[selectedSlot], Game1.player.Items[activeSlot]);

        try
        {
            return action();
        }
        finally
        {
            (Game1.player.Items[activeSlot], Game1.player.Items[selectedSlot]) =
                (Game1.player.Items[selectedSlot], Game1.player.Items[activeSlot]);
        }
    }

    public override void performHoverAction(int x, int y)
    {
        hoveredItem = inventory.getItemAt(x, y);
    }

    public override void receiveKeyPress(Keys key)
    {
        if (Game1.options.doesInputListContain(Game1.options.menuButton, key))
        {
            ReturnToSocial();
            Game1.playSound("bigDeSelect");
            return;
        }

        base.receiveKeyPress(key);
    }

    private void ReturnToSocial()
    {
        mod.CancelPendingReturn();
        Game1.activeClickableMenu = parentMenu;
    }

    public override void draw(SpriteBatch b)
    {
        if (!Game1.options.showMenuBackground && !Game1.options.showClearBackgrounds)
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);

        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, speaker: false, drawOnlyBox: true);

        bool hasQuest = mod.HasItemDeliveryQuestFor(npc);
        string title = hasQuest
            ? $"Send an item to {npc.displayName}"
            : $"Give a gift to {npc.displayName}";

        Vector2 titleSize = Game1.dialogueFont.MeasureString(title);
        b.DrawString(
            Game1.dialogueFont,
            title,
            new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 40),
            Game1.textColor
        );

        inventory.draw(b);

        string instruction = hasQuest
            ? "Choose an item. A matching delivery-quest item will be turned in; other highlighted items will be given as gifts."
            : "Choose an item from your backpack to give it remotely.";

        string wrapped = Game1.parseText(instruction, Game1.smallFont, width - 128);
        b.DrawString(Game1.smallFont, wrapped, new Vector2(xPositionOnScreen + 64, yPositionOnScreen + 336), Game1.textColor);

        if (!string.IsNullOrWhiteSpace(statusText))
        {
            string status = Game1.parseText(statusText, Game1.smallFont, width - 128);
            b.DrawString(Game1.smallFont, status, new Vector2(xPositionOnScreen + 64, yPositionOnScreen + 388), Color.DarkRed);
        }

        upperRightCloseButton?.draw(b);

        if (hoveredItem is not null)
            IClickableMenu.drawToolTip(b, hoveredItem.getDescription(), hoveredItem.DisplayName, hoveredItem);

        if (!Game1.options.hardwareCursor)
            drawMouse(b);
    }
}

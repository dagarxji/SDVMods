using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Quests;

namespace RemoteSocialInteractions;

internal enum RemoteAction
{
    Talk,
    Gift
}

internal sealed class ModEntry : Mod
{
    internal static ModEntry Instance { get; private set; } = null!;

    private GameMenu? returnToSocialMenu;

    public override void Entry(IModHelper helper)
    {
        Instance = this;

        helper.Events.Display.MenuChanged += OnMenuChanged;

        Harmony harmony = new(ModManifest.UniqueID);
        harmony.Patch(
            original: AccessTools.Method(typeof(SocialPage), nameof(SocialPage.draw), new[] { typeof(SpriteBatch) }),
            postfix: new HarmonyMethod(typeof(ModEntry), nameof(AfterSocialPageDraw))
        );
        harmony.Patch(
            original: AccessTools.Method(typeof(SocialPage), nameof(SocialPage.receiveLeftClick), new[] { typeof(int), typeof(int), typeof(bool) }),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(BeforeSocialPageReceiveLeftClick))
        );
    }

    private static void AfterSocialPageDraw(SocialPage __instance, SpriteBatch b)
    {
        Instance.DrawActionButtons(__instance, b);
    }

    private static bool BeforeSocialPageReceiveLeftClick(SocialPage __instance, int x, int y, bool playSound = true)
    {
        return !Instance.TryHandleSocialPageClick(__instance, x, y);
    }

    private void DrawActionButtons(SocialPage page, SpriteBatch b)
    {
        int mouseX = Game1.getMouseX();
        int mouseY = Game1.getMouseY();

        for (int i = 0; i < page.characterSlots.Count; i++)
        {
            SocialPage.SocialEntry? entry = page.GetSocialEntry(i);
            if (!CanInteractWith(entry) || entry!.Character is not NPC npc)
                continue;

            Rectangle row = page.characterSlots[i].bounds;
            if (!IsVisibleRow(page, row))
                continue;

            foreach ((RemoteAction action, Rectangle bounds, bool enabled) in GetActionTargets(page, row, npc))
            {
                if (!bounds.Contains(mouseX, mouseY))
                    continue;

                DrawIconHover(b, bounds, enabled);

                string tooltip = action switch
                {
                    RemoteAction.Gift when HasItemDeliveryQuestFor(npc) => $"Gift / Quest — {npc.displayName}",
                    RemoteAction.Gift => $"Gift — {npc.displayName}",
                    RemoteAction.Talk => $"Talk — {npc.displayName}",
                    _ => npc.displayName
                };

                IClickableMenu.drawHoverText(b, tooltip, Game1.smallFont);
                break;
            }
        }
    }

    private bool TryHandleSocialPageClick(SocialPage page, int x, int y)
    {
        if (Game1.activeClickableMenu is not GameMenu parentMenu || parentMenu.GetCurrentPage() != page)
            return false;

        for (int i = 0; i < page.characterSlots.Count; i++)
        {
            SocialPage.SocialEntry? entry = page.GetSocialEntry(i);
            if (!CanInteractWith(entry) || entry!.Character is not NPC npc)
                continue;

            Rectangle row = page.characterSlots[i].bounds;
            if (!IsVisibleRow(page, row))
                continue;

            foreach ((RemoteAction action, Rectangle bounds, bool enabled) in GetActionTargets(page, row, npc))
            {
                if (!bounds.Contains(x, y))
                    continue;

                if (!enabled)
                {
                    Game1.playSound("cancel");
                    return true;
                }

                Game1.playSound("smallSelect");
                switch (action)
                {
                    case RemoteAction.Talk:
                        TalkToNpc(parentMenu, npc);
                        break;

                    case RemoteAction.Gift:
                        Game1.activeClickableMenu = new RemoteItemPickerMenu(this, parentMenu, npc);
                        break;
                }

                return true;
            }
        }

        return false;
    }

    private static bool CanInteractWith(SocialPage.SocialEntry? entry)
    {
        return entry is
        {
            IsMet: true,
            IsPlayer: false,
            IsChild: false,
            Character: NPC
        };
    }

    private static bool IsVisibleRow(SocialPage page, Rectangle row)
    {
        int top = page.yPositionOnScreen + IClickableMenu.borderWidth + 32;
        int bottom = page.yPositionOnScreen + page.height - IClickableMenu.borderWidth;
        return row.Bottom > top && row.Top < bottom;
    }

    private IEnumerable<(RemoteAction Action, Rectangle Bounds, bool Enabled)> GetActionTargets(
        SocialPage page,
        Rectangle row,
        NPC npc)
    {
        // These line up with the vanilla Social-page icons themselves. There are
        // deliberately no extra text buttons: the present/gift icon and speech
        // bubble are the controls. The bounds include a few pixels of padding so
        // they feel natural to click without spilling into neighboring columns.
        Rectangle giftIcon = new(page.xPositionOnScreen + 684, row.Y + 4, 64, 56);
        Rectangle talkIcon = new(page.xPositionOnScreen + 804, row.Y + 8, 60, 52);

        yield return (RemoteAction.Gift, giftIcon, npc.CanReceiveGifts() || HasItemDeliveryQuestFor(npc));
        yield return (RemoteAction.Talk, talkIcon, true);
    }

    private static void DrawIconHover(SpriteBatch b, Rectangle bounds, bool enabled)
    {
        Color tint = enabled ? Color.White : Color.Gray;

        // A light overlay + thin frame makes the vanilla icon read as clickable
        // without adding a permanent button behind it.
        b.Draw(Game1.staminaRect, bounds, tint * 0.12f);

        const int thickness = 2;
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, bounds.Width, thickness), tint * 0.75f);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Bottom - thickness, bounds.Width, thickness), tint * 0.75f);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, thickness, bounds.Height), tint * 0.75f);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.Right - thickness, bounds.Y, thickness, bounds.Height), tint * 0.75f);
    }

    private void TalkToNpc(GameMenu parentMenu, NPC npc)
    {
        // CurrentDialogue lazily loads the NPC's normal dialogue for the day.
        // We intentionally don't call NPC.checkAction here: that method depends
        // on physical location, sleeping state, and the player's held item, which
        // would defeat the point of a remote Talk button.
        if (npc.CurrentDialogue.Count <= 0)
        {
            Game1.showRedMessage($"{npc.displayName} has nothing to say right now.");
            return;
        }

        PrepareReturnToSocial(parentMenu);
        npc.grantConversationFriendship(Game1.player);
        Game1.drawDialogue(npc);
        RestoreImmediatelyIfNoDialogue(parentMenu);
    }

    internal bool HasItemDeliveryQuestFor(NPC npc)
    {
        foreach (Quest quest in Game1.player.questLog)
        {
            if (quest is ItemDeliveryQuest delivery && delivery.target.Value == npc.Name)
                return true;
        }
        return false;
    }

    internal ItemDeliveryQuest? FindMatchingDeliveryQuest(NPC npc, Item item)
    {
        foreach (Quest quest in Game1.player.questLog)
        {
            if (quest is not ItemDeliveryQuest delivery)
                continue;

            if (delivery.target.Value != npc.Name)
                continue;

            // Stardew 1.6 stores the requested item as a qualified item ID.
            // Match the item here for the picker, then let Farmer.NotifyQuests +
            // Quest.OnItemOfferedToNpc execute the actual vanilla quest logic.
            if (item.QualifiedItemId == delivery.ItemId.Value
                && item.Stack >= delivery.number.Value)
            {
                return delivery;
            }
        }
        return null;
    }

    internal void PrepareReturnToSocial(GameMenu parentMenu)
    {
        returnToSocialMenu = parentMenu;
    }

    internal void CancelPendingReturn()
    {
        returnToSocialMenu = null;
    }

    internal void RestoreImmediatelyIfNoDialogue(GameMenu parentMenu)
    {
        if (Game1.activeClickableMenu is DialogueBox)
            return;

        returnToSocialMenu = null;
        Game1.activeClickableMenu = parentMenu;
    }

    private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        if (returnToSocialMenu is null)
            return;

        // Wait until the final dialogue box closes, then put the exact GameMenu
        // object back. That preserves the Social page's current scroll position.
        if (e.OldMenu is DialogueBox && e.NewMenu is null)
        {
            GameMenu menu = returnToSocialMenu;
            returnToSocialMenu = null;
            Game1.activeClickableMenu = menu;
        }
    }
}

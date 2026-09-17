using System;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using xTile.Dimensions;

namespace BulkQiCoinPurchases;

public sealed class ModEntry : Mod
{
    private const int GoldPerQiCoin = 10;
    private const int DefaultQuantity = 1000;

    private static ModEntry Instance = null!;

    public override void Entry(IModHelper helper)
    {
        Instance = this;

        MethodInfo? performAction = AccessTools.Method(
            typeof(GameLocation),
            nameof(GameLocation.performAction),
            new[] { typeof(string[]), typeof(Farmer), typeof(Location) }
        );

        if (performAction is null)
        {
            this.Monitor.Log("Couldn't find GameLocation.performAction(string[], Farmer, Location); bulk Qi Coin purchases are disabled.", LogLevel.Error);
            return;
        }

        Harmony harmony = new(this.ModManifest.UniqueID);
        harmony.Patch(
            original: performAction,
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(GameLocation_PerformAction_Prefix))
        );
    }

    private static bool GameLocation_PerformAction_Prefix(
        GameLocation __instance,
        string[] action,
        Farmer who,
        ref bool __result)
    {
        if (__instance is not Club
            || !who.IsLocalPlayer
            || action.Length == 0
            || action[0] != "BuyQiCoins")
        {
            return true;
        }

        Instance.OpenPurchaseMenu(who);
        __result = true;
        return false;
    }

    private void OpenPurchaseMenu(Farmer who)
    {
        int maxQuantity = Math.Min(who.Money / GoldPerQiCoin, int.MaxValue - who.clubCoins);
        if (maxQuantity < 1)
        {
            Game1.drawObjectDialogue(Game1.content.LoadString("Strings\\StringsFromCSFiles:GameLocation.cs.8715"));
            return;
        }

        string message = this.Helper.Translation.Get("purchase.prompt");
        Game1.activeClickableMenu = new NumberSelectionMenu(
            message,
            this.PurchaseCoins,
            price: GoldPerQiCoin,
            minValue: 1,
            maxValue: maxQuantity,
            defaultNumber: Math.Min(DefaultQuantity, maxQuantity)
        );
    }

    private void PurchaseCoins(int quantity, int price, Farmer who)
    {
        long totalPrice = (long)quantity * price;
        if (quantity < 1
            || totalPrice > who.Money
            || quantity > int.MaxValue - who.clubCoins)
        {
            Game1.drawObjectDialogue(Game1.content.LoadString("Strings\\StringsFromCSFiles:GameLocation.cs.8715"));
            return;
        }

        who.Money -= (int)totalPrice;
        who.clubCoins += quantity;
        Game1.playSound("Pickup_Coin15");
        Game1.showGlobalMessage(this.Helper.Translation.Get("purchase.complete", new { count = quantity }));
    }
}
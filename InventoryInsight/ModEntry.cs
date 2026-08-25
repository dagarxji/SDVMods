using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace InventoryInsight;

internal sealed class ModEntry : Mod
{
    internal static ModEntry? Instance { get; private set; }
    internal ModConfig Config { get; private set; } = new();
    internal ItemAnalyzer Analyzer { get; private set; } = null!;
    internal TooltipRenderer Renderer { get; private set; } = null!;

    public override void Entry(IModHelper helper)
    {
        Instance = this;
        Config = helper.ReadConfig<ModConfig>();
        Analyzer = new ItemAnalyzer(this);
        Renderer = new TooltipRenderer(this, Analyzer);

        Harmony harmony = new(ModManifest.UniqueID);
        harmony.PatchAll();

        helper.Events.GameLoop.SaveLoaded += OnWorldChanged;
        helper.Events.GameLoop.DayStarted += OnWorldChanged;
        helper.Events.GameLoop.ReturnedToTitle += OnWorldChanged;
        helper.Events.Content.AssetsInvalidated += OnAssetInvalidated;

        Monitor.Log("Inventory Insight loaded. Hover an item for the compact panel; hold Shift for details.", LogLevel.Info);
    }

    private void OnWorldChanged(object? sender, EventArgs e)
    {
        Analyzer.ClearCaches();
    }

    private void OnAssetInvalidated(object? sender, AssetsInvalidatedEventArgs e)
    {
        // Gift tastes, crafting recipes, machines, and bundle data can all be content-patched.
        if (e.NamesWithoutLocale.Any(name =>
            name.IsEquivalentTo("Data/NPCGiftTastes")
            || name.IsEquivalentTo("Data/CraftingRecipes")
            || name.IsEquivalentTo("Data/Machines")
            || name.IsEquivalentTo("Data/Bundles")))
        {
            Analyzer.ClearCaches();
        }
    }
}

using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace CrowdedWildTrees;

internal sealed class ModEntry : Mod
{
    private const string GmcmId = "spacechase0.GenericModConfigMenu";

    private ModConfig Config = new();

    public override void Entry(IModHelper helper)
    {
        this.Config = helper.ReadConfig<ModConfig>();
        TreePatches.Initialize(this.Monitor, () => this.Config);
        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;

        MethodInfo? regularTreeGrowthCheck = AccessTools.Method(
            typeof(Tree),
            nameof(Tree.IsGrowthBlockedByNearbyTree)
        );
        MethodInfo? fruitTreeGrowthCheck = AccessTools.Method(
            typeof(FruitTree),
            nameof(FruitTree.IsGrowthBlocked)
        );
        MethodInfo? fruitTreeProximityCheck = AccessTools.Method(
            typeof(FruitTree),
            nameof(FruitTree.IsTooCloseToAnotherTree)
        );

        Harmony harmony = new(this.ModManifest.UniqueID);
        if (regularTreeGrowthCheck is not null)
        {
            harmony.Patch(
                original: regularTreeGrowthCheck,
                prefix: new HarmonyMethod(typeof(TreePatches), nameof(TreePatches.RegularTreeGrowthCheck_Prefix))
            );
        }
        else
        {
            this.Monitor.Log("Couldn't find Tree.IsGrowthBlockedByNearbyTree; regular-tree spacing won't be changed.", LogLevel.Error);
        }

        if (fruitTreeGrowthCheck is not null)
        {
            harmony.Patch(
                original: fruitTreeGrowthCheck,
                prefix: new HarmonyMethod(typeof(TreePatches), nameof(TreePatches.FruitTreeGrowthCheck_Prefix)),
                postfix: new HarmonyMethod(typeof(TreePatches), nameof(TreePatches.FruitTreeGrowthCheck_Postfix))
            );
        }
        else
        {
            this.Monitor.Log("Couldn't find FruitTree.IsGrowthBlocked; producing-tree growth spacing won't be changed.", LogLevel.Error);
        }

        if (fruitTreeProximityCheck is not null)
        {
            harmony.Patch(
                original: fruitTreeProximityCheck,
                postfix: new HarmonyMethod(typeof(TreePatches), nameof(TreePatches.FruitTreeProximityCheck_Postfix))
            );
        }
        else
        {
            this.Monitor.Log("Couldn't find FruitTree.IsTooCloseToAnotherTree; producing-tree placement spacing won't be changed.", LogLevel.Error);
        }

        this.Monitor.Log("Crowded Trees loaded.", LogLevel.Info);
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        IGenericModConfigMenuApi? gmcm = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>(GmcmId);
        if (gmcm is null)
            return;

        gmcm.Register(
            this.ModManifest,
            reset: () => this.Config = new ModConfig(),
            save: () => this.Helper.WriteConfig(this.Config)
        );
        gmcm.AddBoolOption(
            this.ModManifest,
            getValue: () => this.Config.AllowRegularTrees,
            setValue: value => this.Config.AllowRegularTrees = value,
            name: () => "Regular trees",
            tooltip: () => "Allow all regular trees to reach maturity beside other mature regular trees.",
            fieldId: nameof(ModConfig.AllowRegularTrees)
        );
        gmcm.AddBoolOption(
            this.ModManifest,
            getValue: () => this.Config.AllowProducingTrees,
            setValue: value => this.Config.AllowProducingTrees = value,
            name: () => "Fruit / producing trees",
            tooltip: () => "Allow fruit and other producing trees to be planted and grow beside each other and paths.",
            fieldId: nameof(ModConfig.AllowProducingTrees)
        );
    }

}

internal static class TreePatches
{
    private static IMonitor Monitor = null!;
    private static Func<ModConfig> GetConfig = null!;

    internal static void Initialize(IMonitor monitor, Func<ModConfig> getConfig)
    {
        Monitor = monitor;
        GetConfig = getConfig;
    }

    internal static bool RegularTreeGrowthCheck_Prefix(ref bool __result)
    {
        if (!GetConfig().AllowRegularTrees)
            return true;

        __result = false;
        return false;
    }

    internal static void FruitTreeGrowthCheck_Prefix(
        Vector2 tileLocation,
        GameLocation environment,
        out ProducingGrowthState? __state
    )
    {
        __state = null;

        try
        {
            if (!GetConfig().AllowProducingTrees)
                return;

            List<(Vector2 Tile, TerrainFeature Feature)> removed = new();
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    if (x == 0 && y == 0)
                        continue;

                    Vector2 neighborTile = tileLocation + new Vector2(x, y);
                    if (environment.terrainFeatures.TryGetValue(neighborTile, out TerrainFeature feature)
                        && feature is FruitTree or Flooring)
                    {
                        removed.Add((neighborTile, feature));
                        environment.terrainFeatures.Remove(neighborTile);
                    }
                }
            }

            if (removed.Count > 0)
                __state = new ProducingGrowthState(environment, removed);
        }
        catch (Exception ex)
        {
            Monitor.Log($"Error while preparing crowded producing-tree growth; leaving vanilla behavior unchanged.\n{ex}", LogLevel.Error);
        }
    }

    internal static void FruitTreeGrowthCheck_Postfix(ProducingGrowthState? __state)
    {
        if (__state is null)
            return;

        foreach ((Vector2 tile, TerrainFeature feature) in __state.Removed)
        {
            if (!__state.Location.terrainFeatures.ContainsKey(tile))
                __state.Location.terrainFeatures.Add(tile, feature);
        }
    }

    internal static void FruitTreeProximityCheck_Postfix(
        Vector2 tileLocation,
        GameLocation environment,
        bool fruitTreesOnly,
        ref bool __result
    )
    {
        if (!__result || fruitTreesOnly || !GetConfig().AllowProducingTrees)
            return;

        for (int x = (int)tileLocation.X - 2; x <= (int)tileLocation.X + 2; x++)
        {
            for (int y = (int)tileLocation.Y - 2; y <= (int)tileLocation.Y + 2; y++)
            {
                if (environment.terrainFeatures.TryGetValue(new Vector2(x, y), out TerrainFeature feature) && feature is Tree)
                    return;
            }
        }

        __result = false;
    }

    internal sealed record ProducingGrowthState(
        GameLocation Location,
        List<(Vector2 Tile, TerrainFeature Feature)> Removed
    );
}

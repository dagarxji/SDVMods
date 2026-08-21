using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace CrowdedWildTrees;

internal sealed class ModEntry : Mod
{
    public override void Entry(IModHelper helper)
    {
        TreePatches.Initialize(this.Monitor);

        MethodInfo? dayUpdate = typeof(Tree)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name == "dayUpdate")
            .OrderByDescending(method => method.GetParameters().Any(p => p.ParameterType == typeof(GameLocation)))
            .ThenByDescending(method => method.GetParameters().Any(p => p.ParameterType == typeof(Vector2)))
            .FirstOrDefault();

        if (dayUpdate is null)
        {
            this.Monitor.Log("Couldn't find Tree.dayUpdate; the mod won't change tree growth.", LogLevel.Error);
            return;
        }

        Harmony harmony = new(this.ModManifest.UniqueID);
        harmony.Patch(
            original: dayUpdate,
            prefix: new HarmonyMethod(typeof(TreePatches), nameof(TreePatches.DayUpdate_Prefix)),
            postfix: new HarmonyMethod(typeof(TreePatches), nameof(TreePatches.DayUpdate_Postfix))
        );

        this.Monitor.Log("Crowded Wild Trees loaded. Oak, maple, and pine trees can mature beside other wild trees.", LogLevel.Info);
    }
}

internal static class TreePatches
{
    private const int FinalPreMatureStage = 4;
    private const int MatureStage = 5;

    private static readonly HashSet<string> SupportedTreeIds = new(StringComparer.Ordinal)
    {
        "1", // oak
        "2", // maple
        "3"  // pine
    };

    private static IMonitor Monitor = null!;

    internal static void Initialize(IMonitor monitor)
    {
        Monitor = monitor;
    }

    internal static void DayUpdate_Prefix(Tree __instance, object[] __args, out GrowthState __state)
    {
        __state = default;

        try
        {
            if (GetInt(__instance, "growthStage") != FinalPreMatureStage)
                return;

            string? treeId = GetValue(__instance, "treeType")?.ToString();
            if (treeId is null || !SupportedTreeIds.Contains(treeId))
                return;

            GameLocation? location = null;
            Vector2? tile = null;

            foreach (object? arg in __args)
            {
                if (arg is GameLocation foundLocation)
                    location = foundLocation;
                else if (arg is Vector2 foundTile)
                    tile = foundTile;
            }

            if (location is null || tile is null)
                return;

            // Only intervene when the vanilla spacing rule actually matters.
            if (!HasAdjacentMatureWildTree(location, tile.Value))
                return;

            bool fertilized = GetBool(__instance, "fertilized");
            double chance = GetGrowthChance(__instance, fertilized);

            // Unfertilized vanilla wild trees don't grow in winter unless their
            // Data/WildTrees entry explicitly allows winter growth.
            if (!fertilized && IsWinter(location) && !GetTreeDataBool(__instance, "GrowsInWinter", fallback: false))
                chance = 0;

            __state = new GrowthState(Handle: chance > 0, GrowthChance: Math.Clamp(chance, 0, 1));
        }
        catch (Exception ex)
        {
            Monitor.Log($"Error while checking crowded-tree growth; leaving vanilla behavior unchanged.\n{ex}", LogLevel.Error);
        }
    }

    internal static void DayUpdate_Postfix(Tree __instance, GrowthState __state)
    {
        if (!__state.Handle)
            return;

        try
        {
            // If vanilla or another mod already matured it, don't touch it.
            if (GetInt(__instance, "growthStage") != FinalPreMatureStage)
                return;

            if (Game1.random.NextDouble() >= __state.GrowthChance)
                return;

            if (!SetValue(__instance, "growthStage", MatureStage))
            {
                Monitor.Log("A crowded tree passed its growth roll, but growthStage couldn't be updated.", LogLevel.Warn);
            }
        }
        catch (Exception ex)
        {
            Monitor.Log($"Error while applying crowded-tree growth; leaving the tree unchanged.\n{ex}", LogLevel.Error);
        }
    }

    private static bool HasAdjacentMatureWildTree(GameLocation location, Vector2 tile)
    {
        for (int x = -1; x <= 1; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                if (x == 0 && y == 0)
                    continue;

                Vector2 neighborTile = tile + new Vector2(x, y);
                if (!location.terrainFeatures.TryGetValue(neighborTile, out TerrainFeature feature))
                    continue;

                if (feature is Tree neighbor && GetInt(neighbor, "growthStage") >= MatureStage)
                    return true;
            }
        }

        return false;
    }

    private static double GetGrowthChance(Tree tree, bool fertilized)
    {
        string propertyName = fertilized ? "FertilizedGrowthChance" : "GrowthChance";
        double fallback = fertilized ? 1.0 : 0.2;
        return GetTreeDataDouble(tree, propertyName, fallback);
    }

    private static bool IsWinter(GameLocation location)
    {
        try
        {
            MethodInfo? getSeason = FindMethod(location.GetType(), "GetSeason", Type.EmptyTypes);
            object? season = getSeason?.Invoke(location, null);
            if (season is not null)
                return string.Equals(season.ToString(), "winter", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Fall back to the global season below.
        }

        return string.Equals(Game1.currentSeason, "winter", StringComparison.OrdinalIgnoreCase);
    }

    private static object? GetTreeData(Tree tree)
    {
        try
        {
            MethodInfo? getData = FindMethod(tree.GetType(), "GetData", Type.EmptyTypes);
            return getData?.Invoke(tree, null);
        }
        catch
        {
            return null;
        }
    }

    private static double GetTreeDataDouble(Tree tree, string name, double fallback)
    {
        object? data = GetTreeData(tree);
        object? value = data is null ? null : GetRawMemberValue(data, name);

        try
        {
            return value is null ? fallback : Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private static bool GetTreeDataBool(Tree tree, string name, bool fallback)
    {
        object? data = GetTreeData(tree);
        object? value = data is null ? null : GetRawMemberValue(data, name);

        try
        {
            return value is null ? fallback : Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private static int GetInt(object target, string name)
    {
        object? value = GetValue(target, name);
        if (value is null)
            return -1;

        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return -1;
        }
    }

    private static bool GetBool(object target, string name)
    {
        object? value = GetValue(target, name);
        if (value is null)
            return false;

        try
        {
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Read a field/property, unwrapping Netcode fields through their Value property when needed.</summary>
    private static object? GetValue(object target, string name)
    {
        object? raw = GetRawMemberValue(target, name);
        if (raw is null)
            return null;

        PropertyInfo? valueProperty = raw.GetType().GetProperty(
            "Value",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );

        return valueProperty?.GetValue(raw) ?? raw;
    }

    /// <summary>Set a field/property, including Netcode fields which expose a writable Value property.</summary>
    private static bool SetValue(object target, string name, object value)
    {
        MemberInfo? member = FindMember(target.GetType(), name);
        if (member is null)
            return false;

        object? raw = member switch
        {
            FieldInfo field => field.GetValue(target),
            PropertyInfo property when property.CanRead => property.GetValue(target),
            _ => null
        };

        if (raw is not null)
        {
            PropertyInfo? valueProperty = raw.GetType().GetProperty(
                "Value",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );

            if (valueProperty?.CanWrite == true)
            {
                valueProperty.SetValue(raw, ConvertFor(value, valueProperty.PropertyType));
                return true;
            }
        }

        switch (member)
        {
            case FieldInfo field:
                field.SetValue(target, ConvertFor(value, field.FieldType));
                return true;

            case PropertyInfo property when property.CanWrite:
                property.SetValue(target, ConvertFor(value, property.PropertyType));
                return true;

            default:
                return false;
        }
    }

    private static object? GetRawMemberValue(object target, string name)
    {
        MemberInfo? member = FindMember(target.GetType(), name);
        return member switch
        {
            FieldInfo field => field.GetValue(target),
            PropertyInfo property when property.CanRead => property.GetValue(target),
            _ => null
        };
    }

    private static MemberInfo? FindMember(Type type, string name)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            FieldInfo? field = current.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase
            );
            if (field is not null)
                return field;

            PropertyInfo? property = current.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase
            );
            if (property is not null)
                return property;
        }

        return null;
    }

    private static MethodInfo? FindMethod(Type type, string name, Type[] parameterTypes)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            MethodInfo? method = current.GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: parameterTypes,
                modifiers: null
            );
            if (method is not null)
                return method;
        }

        return null;
    }

    private static object? ConvertFor(object value, Type targetType)
    {
        Type actualType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (actualType.IsInstanceOfType(value))
            return value;

        return Convert.ChangeType(value, actualType, CultureInfo.InvariantCulture);
    }

    internal readonly record struct GrowthState(bool Handle, double GrowthChance);
}

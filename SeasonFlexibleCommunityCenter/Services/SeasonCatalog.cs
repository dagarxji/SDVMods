using System.Collections;
using System.Reflection;
using SeasonFlexibleCommunityCenter.Models;
using StardewModdingAPI;

namespace SeasonFlexibleCommunityCenter.Services;

internal sealed class SeasonCatalog
{
    private static readonly string[] AllSeasons = new[] { "spring", "summer", "fall", "winter" };

    private readonly IModHelper Helper;
    private readonly IMonitor Monitor;
    private readonly Dictionary<string, ItemSeasonInfo> Items = new(StringComparer.OrdinalIgnoreCase);
    private CompatibilityOverrides Overrides = new();

    public SeasonCatalog(IModHelper helper, IMonitor monitor)
    {
        Helper = helper;
        Monitor = monitor;
    }

    public void Rebuild()
    {
        Items.Clear();
        Overrides = Helper.Data.ReadJsonFile<CompatibilityOverrides>("compatibility.json") ?? new CompatibilityOverrides();
        Overrides.Items ??= new Dictionary<string, CompatibilityItemRule>(StringComparer.OrdinalIgnoreCase);

        try { ScanCrops(); }
        catch (Exception ex) { Monitor.Log($"Couldn't scan Data/Crops: {ex.Message}", LogLevel.Warn); }

        try { ScanFruitTrees(); }
        catch (Exception ex) { Monitor.Log($"Couldn't scan Data/FruitTrees: {ex.Message}", LogLevel.Warn); }

        try { ScanFishKinds(); }
        catch (Exception ex) { Monitor.Log($"Couldn't scan Data/Fish: {ex.Message}", LogLevel.Warn); }

        try { ScanLocations(); }
        catch (Exception ex) { Monitor.Log($"Couldn't scan Data/Locations: {ex.Message}", LogLevel.Warn); }

        ApplyOverrides();
        Monitor.Log($"Season catalog contains {Items.Count} seasonal item definitions.", LogLevel.Trace);
    }

    public bool TryGet(string itemId, out ItemSeasonInfo info)
    {
        itemId = NormalizeObjectId(itemId);
        return Items.TryGetValue(itemId, out info!);
    }

    public IEnumerable<(string Id, ItemSeasonInfo Info)> GetItems(ItemKind kind, string season)
    {
        foreach ((string id, ItemSeasonInfo info) in Items)
        {
            if (info.Kinds.HasFlag(kind) && info.Seasons.Contains(season))
                yield return (id, info);
        }
    }

    public static string NormalizeObjectId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "";

        id = id.Trim();
        if (id.StartsWith("(O)", StringComparison.OrdinalIgnoreCase))
            return "(O)" + id[3..];
        if (id.StartsWith("(", StringComparison.Ordinal) || id.Contains(' '))
            return id;
        return "(O)" + id;
    }

    private void ScanCrops()
    {
        object data = Helper.GameContent.Load<object>("Data/Crops");
        foreach ((_, object value) in EnumerateDictionary(data))
        {
            string? harvestId = GetString(value, "HarvestItemId");
            HashSet<string> seasons = GetSeasonSet(GetProperty(value, "Seasons"));
            Add(harvestId, ItemKind.Crop, seasons);
        }
    }

    private void ScanFruitTrees()
    {
        object data = Helper.GameContent.Load<object>("Data/FruitTrees");
        foreach ((_, object value) in EnumerateDictionary(data))
        {
            HashSet<string> treeSeasons = GetSeasonSet(GetProperty(value, "Seasons"));
            object? fruits = GetProperty(value, "Fruit");
            foreach (object fruit in Enumerate(fruits))
            {
                string? itemId = GetString(fruit, "ItemId");
                HashSet<string> seasons = GetSeasonFromEntry(fruit, treeSeasons);
                Add(itemId, ItemKind.Fruit, seasons);
            }
        }
    }

    private void ScanFishKinds()
    {
        object data = Helper.GameContent.Load<object>("Data/Fish");
        foreach ((object key, _) in EnumerateDictionary(data))
            AddKind(key.ToString(), ItemKind.Fish);
    }

    private void ScanLocations()
    {
        object data = Helper.GameContent.Load<object>("Data/Locations");
        foreach ((_, object location) in EnumerateDictionary(data))
        {
            foreach (object forage in Enumerate(GetProperty(location, "Forage")))
            {
                string? itemId = GetString(forage, "ItemId");
                HashSet<string> seasons = GetSeasonFromEntry(forage, null);
                Add(itemId, ItemKind.Forage, seasons);
            }

            foreach (object fish in Enumerate(GetProperty(location, "Fish")))
            {
                string? itemId = GetString(fish, "ItemId");
                HashSet<string> seasons = GetSeasonFromEntry(fish, null);
                Add(itemId, ItemKind.Fish, seasons);
            }
        }
    }

    private void ApplyOverrides()
    {
        foreach ((string id, CompatibilityItemRule rule) in Overrides.Items)
        {
            ItemKind kind = ParseKind(rule.Kind);
            if (kind == ItemKind.None)
            {
                Monitor.Log($"Ignoring compatibility rule for '{id}': unknown kind '{rule.Kind}'.", LogLevel.Warn);
                continue;
            }

            HashSet<string> seasons = new(rule.Seasons.Select(NormalizeSeason).OfType<string>(), StringComparer.OrdinalIgnoreCase);
            if (seasons.Count == 0)
                seasons.UnionWith(AllSeasons);
            Add(id, kind, seasons);
        }
    }

    private static ItemKind ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "crop" or "crops" => ItemKind.Crop,
        "fish" => ItemKind.Fish,
        "forage" or "foraging" => ItemKind.Forage,
        "fruit" or "fruittree" or "fruit tree" => ItemKind.Fruit,
        _ => ItemKind.None
    };

    private void AddKind(string? id, ItemKind kind)
    {
        string normalized = NormalizeObjectId(id);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains(' '))
            return;

        if (!Items.TryGetValue(normalized, out ItemSeasonInfo? info))
            Items[normalized] = info = new ItemSeasonInfo();
        info.Kinds |= kind;
    }

    private void Add(string? id, ItemKind kind, IEnumerable<string> seasons)
    {
        string normalized = NormalizeObjectId(id);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains(' '))
            return;

        if (!Items.TryGetValue(normalized, out ItemSeasonInfo? info))
            Items[normalized] = info = new ItemSeasonInfo();

        info.Kinds |= kind;
        foreach (string season in seasons)
        {
            string? normalizedSeason = NormalizeSeason(season);
            if (normalizedSeason is not null)
                info.Seasons.Add(normalizedSeason);
        }
    }

    private static HashSet<string> GetSeasonFromEntry(object entry, HashSet<string>? fallback)
    {
        string? season = GetString(entry, "Season");
        string? normalizedSeason = NormalizeSeason(season);
        if (normalizedSeason is not null)
            return new HashSet<string>(new[] { normalizedSeason }, StringComparer.OrdinalIgnoreCase);

        string? condition = GetString(entry, "Condition");
        HashSet<string> parsed = ParseSeasonsFromCondition(condition);
        if (parsed.Count > 0)
            return parsed;

        if (fallback is { Count: > 0 })
            return new HashSet<string>(fallback, StringComparer.OrdinalIgnoreCase);

        return new HashSet<string>(AllSeasons, StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> ParseSeasonsFromCondition(string? condition)
    {
        // Only infer seasonality from direct top-level SEASON / LOCATION_SEASON clauses.
        // Game State Queries can nest arbitrary queries inside ANY strings; scanning for words like
        // "summer" anywhere would misclassify those complex conditions. If we can't prove a
        // season restriction, callers deliberately fall back to all seasons instead.
        HashSet<string> allowed = new(AllSeasons, StringComparer.OrdinalIgnoreCase);
        bool foundSeasonConstraint = false;

        foreach (string rawClause in SplitTopLevelQuery(condition))
        {
            string clause = rawClause.Trim();
            if (clause.Length == 0)
                continue;

            bool negated = clause[0] == '!';
            string body = negated ? clause[1..].TrimStart() : clause;
            string[] tokens = body.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length < 2)
                continue;

            int seasonStart;
            if (tokens[0].Equals("SEASON", StringComparison.OrdinalIgnoreCase))
                seasonStart = 1;
            else if (tokens[0].Equals("LOCATION_SEASON", StringComparison.OrdinalIgnoreCase) && tokens.Length >= 3)
                seasonStart = 2; // token 1 is Here / Target / a location context.
            else
                continue;

            HashSet<string> clauseSeasons = new(StringComparer.OrdinalIgnoreCase);
            for (int i = seasonStart; i < tokens.Length; i++)
            {
                string token = tokens[i].Trim('\"');
                string? normalized = NormalizeSeason(token);
                if (normalized is not null)
                    clauseSeasons.Add(normalized);
            }

            if (clauseSeasons.Count == 0)
                continue;

            foundSeasonConstraint = true;
            if (negated)
                allowed.ExceptWith(clauseSeasons);
            else
                allowed.IntersectWith(clauseSeasons);
        }

        return foundSeasonConstraint
            ? allowed
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SplitTopLevelQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            yield break;

        int start = 0;
        bool inQuotes = false;
        bool escaped = false;
        for (int i = 0; i < query.Length; i++)
        {
            char c = query[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (c == '\\')
            {
                escaped = true;
                continue;
            }
            if (c == '\"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (c != ',' || inQuotes)
                continue;

            yield return query[start..i];
            start = i + 1;
        }

        if (start <= query.Length)
            yield return query[start..];
    }

    private static HashSet<string> GetSeasonSet(object? value)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (object season in Enumerate(value))
        {
            string? normalized = NormalizeSeason(season.ToString());
            if (normalized is not null)
                result.Add(normalized);
        }
        return result;
    }

    private static string? NormalizeSeason(string? season) => season?.Trim().ToLowerInvariant() switch
    {
        "spring" => "spring",
        "summer" => "summer",
        "fall" or "autumn" => "fall",
        "winter" => "winter",
        _ => null
    };

    private static IEnumerable<(object Key, object Value)> EnumerateDictionary(object? dictionary)
    {
        if (dictionary is not IEnumerable enumerable)
            yield break;

        foreach (object? entry in enumerable)
        {
            if (entry is null)
                continue;
            object? key = GetProperty(entry, "Key");
            object? value = GetProperty(entry, "Value");
            if (key is not null && value is not null)
                yield return (key, value);
        }
    }

    private static IEnumerable<object> Enumerate(object? value)
    {
        if (value is not IEnumerable enumerable || value is string)
            yield break;
        foreach (object? item in enumerable)
        {
            if (item is not null)
                yield return item;
        }
    }

    private static object? GetProperty(object? value, string name)
    {
        if (value is null)
            return null;

        Type type = value.GetType();
        PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        if (property is not null)
            return property.GetValue(value);

        FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        return field?.GetValue(value);
    }

    private static string? GetString(object? value, string name) => GetProperty(value, name)?.ToString();
}

internal sealed class ItemSeasonInfo
{
    public ItemKind Kinds { get; set; }
    public HashSet<string> Seasons { get; } = new(StringComparer.OrdinalIgnoreCase);
}

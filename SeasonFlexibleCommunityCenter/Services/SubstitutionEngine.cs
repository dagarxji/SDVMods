using SeasonFlexibleCommunityCenter.Models;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace SeasonFlexibleCommunityCenter.Services;

internal sealed class SubstitutionEngine
{
    private static readonly string[] Seasons = new[] { "spring", "summer", "fall", "winter" };

    private readonly SeasonCatalog Catalog;
    private readonly Func<SaveSettings> GetSettings;
    private readonly IMonitor Monitor;

    public SubstitutionEngine(SeasonCatalog catalog, Func<SaveSettings> getSettings, IMonitor monitor)
    {
        Catalog = catalog;
        GetSettings = getSettings;
        Monitor = monitor;
    }

    public bool HasFutureTarget(Bundle bundle)
    {
        if (bundle.complete)
            return false;

        SaveSettings settings = GetSettings();
        string currentSeason = NormalizeSeason(Game1.currentSeason);
        foreach (BundleIngredientDescription ingredient in bundle.ingredients)
        {
            if (!CanSubstituteIngredient(ingredient))
                continue;
            if (!Catalog.TryGet(SeasonCatalog.NormalizeObjectId(ingredient.id), out ItemSeasonInfo? info))
                continue;

            ItemKind kind = ChooseKind(info.Kinds, bundle.name);
            if (kind == ItemKind.None || !IsKindEnabled(kind, settings))
                continue;

            HashSet<string> seasons = new(info.Seasons, StringComparer.OrdinalIgnoreCase);
            if (seasons.Count == 0)
                seasons.UnionWith(ParseSeasonsFromBundleName(bundle.name));
            if (seasons.Count > 0 && GetFutureSeasonGap(currentSeason, seasons) > 0)
                return true;
        }
        return false;
    }

    public List<TargetOption> GetTargets(Bundle bundle)
    {
        List<TargetOption> targets = new();
        if (bundle.complete)
            return targets;

        SaveSettings settings = GetSettings();
        string currentSeason = NormalizeSeason(Game1.currentSeason);

        for (int i = 0; i < bundle.ingredients.Count; i++)
        {
            BundleIngredientDescription ingredient = bundle.ingredients[i];
            if (!CanSubstituteIngredient(ingredient))
                continue; // category/preserve requirements are intentionally left to vanilla.

            string targetId = SeasonCatalog.NormalizeObjectId(ingredient.id);
            if (!Catalog.TryGet(targetId, out ItemSeasonInfo? info))
                continue;

            ItemKind kind = ChooseKind(info.Kinds, bundle.name);
            if (kind == ItemKind.None || !IsKindEnabled(kind, settings))
                continue;

            HashSet<string> targetSeasons = new(info.Seasons, StringComparer.OrdinalIgnoreCase);
            if (targetSeasons.Count == 0)
                targetSeasons.UnionWith(ParseSeasonsFromBundleName(bundle.name));
            if (targetSeasons.Count == 0)
                continue;

            int gap = GetFutureSeasonGap(currentSeason, targetSeasons);
            if (gap <= 0)
                continue; // target is seasonally obtainable now; use the vanilla requirement.

            Item? display = TryCreateItem(targetId, 1, Math.Max(0, ingredient.quality));
            if (display is null)
                continue;

            targets.Add(new TargetOption(i, ingredient, display, kind, targetSeasons, gap));
        }

        return targets;
    }

    public List<CandidateOption> GetCandidates(TargetOption target)
    {
        SaveSettings settings = GetSettings();
        string currentSeason = NormalizeSeason(Game1.currentSeason);
        List<CandidateOption> candidates = new();
        foreach ((string id, _) in Catalog.GetItems(target.Kind, currentSeason))
        {
            Item? sample = TryCreateItem(id, 1, 0);
            if (sample is not SObject)
                continue;

            string qid = sample.QualifiedItemId;
            if (string.Equals(qid, target.DisplayItem.QualifiedItemId, StringComparison.OrdinalIgnoreCase))
                continue;

            int quality = new[] { 4, 2, 1, 0 }.First(q => q == 0 || CountExact(Game1.player, qid, q) > 0);
            Item qualitySample = TryCreateItem(qid, 1, quality) ?? sample;
            candidates.Add(new CandidateOption(
                qid,
                quality,
                qualitySample,
                CountExact(Game1.player, qid, quality),
                CalculateRequiredQuantity(target, qualitySample, settings)
            ));
        }

        return candidates
            .OrderByDescending(p => p.Have > 0)
            .ThenBy(p => p.Need > p.Have)
            .ThenBy(p => p.Need)
            .ThenByDescending(p => GetSellPrice(p.Sample))
            .ThenBy(p => p.Sample.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public ExchangeResult TryExchange(Bundle bundle, TargetOption target, CandidateOption candidate)
    {
        SaveSettings settings = GetSettings();
        int need = CalculateRequiredQuantity(target, candidate.Sample, settings);
        int have = CountExact(Game1.player, candidate.QualifiedItemId, candidate.Quality);
        if (have < need)
            return new ExchangeResult(false, false, $"You need {need}, but only have {have}.");

        CommunityCenter? cc = Game1.getLocationFromName("CommunityCenter") as CommunityCenter;
        if (cc is null || !cc.bundles.FieldDict.TryGetValue(bundle.bundleIndex, out var state))
            return new ExchangeResult(false, false, "The Community Center bundle state wasn't available.");
        if (target.IngredientIndex < 0 || target.IngredientIndex >= state.Length)
            return new ExchangeResult(false, false, "That bundle requirement changed; reopen the bundle and try again.");
        if (state[target.IngredientIndex])
            return new ExchangeResult(false, false, "That requirement is already complete.");

        RemoveExact(Game1.player, candidate.QualifiedItemId, candidate.Quality, need);
        state[target.IngredientIndex] = true;

        int completedCount = 0;
        for (int i = 0; i < state.Length; i++)
        {
            if (state[i])
                completedCount++;
        }

        bool completed = completedCount >= bundle.numberOfIngredientSlots;
        if (completed)
        {
            // Match vanilla behavior for choose-N bundles: once enough slots are filled, all ingredient
            // flags are considered complete and the bundle reward becomes available.
            for (int i = 0; i < state.Length; i++)
                state[i] = true;
            cc.bundleRewards[bundle.bundleIndex] = true;
            cc.checkForNewJunimoNotes();
        }

        Monitor.Log(
            $"Exchanged {need}x {candidate.Sample.DisplayName} ({candidate.QualifiedItemId}, quality {candidate.Quality}) " +
            $"for bundle {bundle.bundleIndex} ingredient {target.IngredientIndex} ({target.DisplayItem.DisplayName}).",
            LogLevel.Trace
        );

        return new ExchangeResult(true, completed, $"Exchanged {need} {candidate.Sample.DisplayName} for {target.DisplayItem.DisplayName}.");
    }

    public int CalculateRequiredQuantity(TargetOption target, Item candidate, SaveSettings settings)
    {
        settings.Validate();

        double seasonBase = settings.SeasonPenaltyPercent / 100d;
        double seasonFactor = Math.Pow(seasonBase, target.SeasonGap);

        int targetPrice = Math.Max(1, GetTargetUnitPrice(target));
        int normalCandidatePrice = Math.Max(1, GetSellPrice(TryCreateItem(candidate.QualifiedItemId, 1, 0) ?? candidate));
        int actualCandidatePrice = Math.Max(1, GetSellPrice(candidate));

        double qualityWeight = settings.QualityCreditPercent / 100d;
        double creditedCandidatePrice = normalCandidatePrice + (actualCandidatePrice - normalCandidatePrice) * qualityWeight;
        creditedCandidatePrice = Math.Max(1d, creditedCandidatePrice);

        double rawPriceRatio = targetPrice / creditedCandidatePrice;
        double valueWeight = settings.ValueScalingPercent / 100d;
        double priceFactor = 1d + (rawPriceRatio - 1d) * valueWeight;
        // Value and quality can make a substitute more expensive, but they should not
        // erase the minimum seasonal cost of exchanging for a future-season item.
        priceFactor = Math.Max(1d, priceFactor);

        double raw = Math.Max(1, target.Ingredient.stack) * seasonFactor * priceFactor;
        int quantity = (int)Math.Ceiling(raw - 0.000001d);
        return Math.Clamp(quantity, settings.MinimumQuantity, settings.MaximumQuantity);
    }

    private static int GetTargetUnitPrice(TargetOption target)
    {
        Item? targetItem = TryCreateItem(target.DisplayItem.QualifiedItemId, 1, Math.Max(0, target.Ingredient.quality));
        return targetItem is null ? 1 : GetSellPrice(targetItem);
    }

    private static int GetSellPrice(Item item)
    {
        // Use the object's base sell value plus vanilla quality multipliers, rather than
        // sellToStorePrice(). That keeps exchange difficulty stable across professions
        // such as Tiller/Angler and makes the same farm settings deterministic for co-op.
        if (item is not SObject obj)
            return 0;

        double qualityMultiplier = obj.Quality switch
        {
            1 => 1.25d,
            2 => 1.50d,
            4 => 2.00d,
            _ => 1.00d
        };
        return Math.Max(0, (int)Math.Floor(obj.Price * qualityMultiplier));
    }

    private static Item? TryCreateItem(string id, int stack, int quality)
    {
        try
        {
            if (!ItemRegistry.Exists(id))
                return null;
            return ItemRegistry.Create(id, stack, quality);
        }
        catch
        {
            return null;
        }
    }

    private static int CountExact(Farmer player, string qualifiedId, int quality)
    {
        int count = 0;
        foreach (Item? item in player.Items)
        {
            if (item is SObject obj
                && string.Equals(item.QualifiedItemId, qualifiedId, StringComparison.OrdinalIgnoreCase)
                && obj.Quality == quality)
                count += item.Stack;
        }
        return count;
    }

    private static void RemoveExact(Farmer player, string qualifiedId, int quality, int count)
    {
        for (int i = player.Items.Count - 1; i >= 0 && count > 0; i--)
        {
            if (player.Items[i] is not SObject obj
                || !string.Equals(obj.QualifiedItemId, qualifiedId, StringComparison.OrdinalIgnoreCase)
                || obj.Quality != quality)
                continue;

            int take = Math.Min(count, obj.Stack);
            obj.Stack -= take;
            count -= take;
            if (obj.Stack <= 0)
                player.Items[i] = null;
        }
    }


    private static bool CanSubstituteIngredient(BundleIngredientDescription ingredient)
    {
        // Stardew 1.6 represents category requirements with id == null and category set.
        // Preserved-item requirements carry extra flavor identity (e.g. a specific jelly/pickle),
        // which isn't safely interchangeable with a raw seasonal item, so leave those to vanilla too.
        return !ingredient.completed
            && !ingredient.category.HasValue
            && ingredient.preservesId is null
            && !string.IsNullOrWhiteSpace(ingredient.id);
    }

    private static ItemKind ChooseKind(ItemKind kinds, string? bundleName)
    {
        string name = bundleName?.ToLowerInvariant() ?? "";
        if (name.Contains("fish") && kinds.HasFlag(ItemKind.Fish)) return ItemKind.Fish;
        if ((name.Contains("forage") || name.Contains("foraging")) && kinds.HasFlag(ItemKind.Forage)) return ItemKind.Forage;
        if (name.Contains("fruit") && kinds.HasFlag(ItemKind.Fruit)) return ItemKind.Fruit;
        if (name.Contains("crop") && kinds.HasFlag(ItemKind.Crop)) return ItemKind.Crop;

        if (kinds.HasFlag(ItemKind.Crop)) return ItemKind.Crop;
        if (kinds.HasFlag(ItemKind.Fish)) return ItemKind.Fish;
        if (kinds.HasFlag(ItemKind.Forage)) return ItemKind.Forage;
        if (kinds.HasFlag(ItemKind.Fruit)) return ItemKind.Fruit;
        return ItemKind.None;
    }

    private static bool IsKindEnabled(ItemKind kind, SaveSettings s) => kind switch
    {
        ItemKind.Crop => s.EnableCrops,
        ItemKind.Fish => s.EnableFish,
        ItemKind.Forage => s.EnableForage,
        ItemKind.Fruit => s.EnableFruit,
        _ => false
    };

    private static int GetFutureSeasonGap(string currentSeason, IEnumerable<string> targetSeasons)
    {
        int current = Array.IndexOf(Seasons, currentSeason);
        if (current < 0)
            return 0;

        int best = 4;
        foreach (string season in targetSeasons)
        {
            int target = Array.IndexOf(Seasons, NormalizeSeason(season));
            if (target < 0)
                continue;
            int gap = (target - current + 4) % 4;
            if (gap == 0)
                return 0;
            best = Math.Min(best, gap);
        }
        return best == 4 ? 0 : best;
    }

    private static HashSet<string> ParseSeasonsFromBundleName(string? name)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        string lower = name?.ToLowerInvariant() ?? "";
        foreach (string season in Seasons)
        {
            if (lower.Contains(season, StringComparison.OrdinalIgnoreCase))
                result.Add(season);
        }
        return result;
    }

    private static string NormalizeSeason(string? season) => season?.Trim().ToLowerInvariant() switch
    {
        "spring" => "spring",
        "summer" => "summer",
        "fall" or "autumn" => "fall",
        "winter" => "winter",
        _ => "spring"
    };
}

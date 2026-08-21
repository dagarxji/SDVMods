using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.Locations;
using StardewValley.Objects;
using StardewValley.Tools;

namespace FishingForecast;

internal sealed class ForecastCalculator
{
    private static readonly (string Label, int Start, int End, int[] Hours)[] TimeSlots =
    {
        ("6 AM – 10 AM", 600, 1000, new[] { 600, 700, 800, 900 }),
        ("10 AM – 2 PM", 1000, 1400, new[] { 1000, 1100, 1200, 1300 }),
        ("2 PM – 6 PM", 1400, 1800, new[] { 1400, 1500, 1600, 1700 }),
        ("6 PM – 10 PM", 1800, 2200, new[] { 1800, 1900, 2000, 2100 }),
        ("10 PM – 2 AM", 2200, 2600, new[] { 2200, 2300, 2400, 2500 })
    };

    private readonly ModConfig config;
    private readonly IMonitor monitor;
    private readonly WorldNavigatorBridge worldNavigator;

    private readonly Dictionary<string, List<CandidateFishingTile>> representativeTileCache = new(StringComparer.OrdinalIgnoreCase);
    private int representativeTileCacheDay = -1;

    // Sampling Stardew's selector is the expensive part of the forecast. Cache each
    // hour/tile/equipment distribution for the current day so reopening the menu is
    // fast, while still re-applying live CatchLimit state (legendary fish caught
    // since the previous opening are therefore handled correctly).
    private readonly Dictionary<string, HourCatchDistribution> distributionCache = new(StringComparer.Ordinal);
    private int distributionCacheDay = -1;

    public ForecastCalculator(ModConfig config, IModHelper helper, IMonitor monitor)
    {
        this.config = config;
        this.monitor = monitor;
        this.worldNavigator = new WorldNavigatorBridge(helper, monitor);
    }

    public void InvalidateReachabilityCache()
    {
        this.worldNavigator.InvalidateCache();
    }

    public void InvalidateAllCaches()
    {
        this.worldNavigator.InvalidateCache();
        this.representativeTileCache.Clear();
        this.representativeTileCacheDay = -1;
        this.distributionCache.Clear();
        this.distributionCacheDay = -1;
    }

    public ForecastReport Calculate()
    {
        if (Game1.player is null || Game1.currentLocation is null)
            throw new InvalidOperationException("A save must be loaded before calculating a fishing forecast.");

        Stopwatch totalTimer = Stopwatch.StartNew();
        Stopwatch phaseTimer = Stopwatch.StartNew();

        Dictionary<string, int> reachable = this.GetReachableLocations(out string reachabilitySource);
        List<GameLocation> locations = this.ResolveCandidateLocations(reachable.Keys);
        long reachabilityMs = phaseTimer.ElapsedMilliseconds;
        phaseTimer.Restart();

        FishingRod? rod = Game1.player.CurrentTool as FishingRod;
        double estimatedCatchesPerHour = GetEstimatedCatchesPerGameHour(
            rod,
            Game1.player.FishingLevel,
            this.config.CastOverheadMilliseconds,
            this.config.RealMillisecondsPerGameMinute
        );
        string equipmentSummary = $"{GetEquipmentSummary(rod)} • ~{estimatedCatchesPerHour:0.#} catches/hr";
        string weatherSummary = GetWeatherSummary();

        Dictionary<string, List<CandidateFishingTile>> tilesByLocation = new(StringComparer.OrdinalIgnoreCase);
        foreach (GameLocation location in locations)
        {
            try
            {
                List<CandidateFishingTile> tiles = this.GetRepresentativeFishingTiles(location);
                if (tiles.Count > 0)
                    tilesByLocation[location.NameOrUniqueName] = tiles;
            }
            catch (Exception ex)
            {
                this.monitor.Log($"Skipping fishing scan for {location.NameOrUniqueName}: {ex.Message}", LogLevel.Trace);
            }
        }

        long tileScanMs = phaseTimer.ElapsedMilliseconds;
        phaseTimer.Restart();

        var slots = new List<ForecastSlot>(TimeSlots.Length);

        int originalTime = Game1.timeOfDay;
        Vector2 originalPlayerPosition = Game1.player.Position;
        using var randomScope = new GameRandomScope(this.monitor);
        try
        {
            foreach ((string label, int start, int end, int[] hours) in TimeSlots)
            {
                var ranked = new List<LocationForecast>();

                foreach (GameLocation location in locations)
                {
                    if (!tilesByLocation.TryGetValue(location.NameOrUniqueName, out List<CandidateFishingTile>? tiles))
                        continue;

                    int travel = reachable.TryGetValue(location.NameOrUniqueName, out int minutes) ? minutes : 0;
                    LocationForecast? result = this.EvaluateLocation(location, tiles, hours, travel, rod, randomScope);
                    if (result is not null)
                        ranked.Add(result);
                }

                slots.Add(new ForecastSlot(
                    label,
                    start,
                    end,
                    ranked
                        .OrderByDescending(p => p.TravelAdjustedGold)
                        .ThenByDescending(p => p.GoldPerHour)
                        .ToArray()
                ));
            }
        }
        finally
        {
            Game1.timeOfDay = originalTime;
            Game1.player.Position = originalPlayerPosition;
        }

        long evaluationMs = phaseTimer.ElapsedMilliseconds;
        this.monitor.Log(
            $"Fishing Forecast timing: reachability {reachabilityMs}ms, fishing-tile scan {tileScanMs}ms, probability model {evaluationMs}ms, total {totalTimer.ElapsedMilliseconds}ms.",
            LogLevel.Debug
        );

        return new ForecastReport(
            slots,
            reachabilitySource,
            equipmentSummary,
            Game1.player.FishingLevel,
            weatherSummary,
            reachable.Count,
            tilesByLocation.Count
        );
    }

    private Dictionary<string, int> GetReachableLocations(out string source)
    {
        if (this.config.UseWorldNavigatorWhenAvailable
            && this.worldNavigator.IsInstalled
            && this.worldNavigator.TryGetReachableLocations(out Dictionary<string, int> viaNavigator))
        {
            source = "World Navigator";
            Dictionary<string, int> normalized = this.NormalizeLocationNames(viaNavigator);
            return this.EstimateTravelMinutesForReachable(normalized);
        }

        source = this.worldNavigator.IsInstalled ? "vanilla warp fallback (World Navigator API unavailable)" : "vanilla warp fallback";
        return this.GetReachableLocationsFromWarps();
    }

    private Dictionary<string, int> NormalizeLocationNames(Dictionary<string, int> source)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, int travel) in source)
        {
            GameLocation? location = Game1.getLocationFromName(name);
            if (location is not null)
            {
                // World Navigator is the primary reachability authority, but apply a
                // few unambiguous vanilla progression gates too. These prevent raw map
                // connectivity from recommending places like the Secret Woods before
                // the player can actually remove the blocking log.
                if (location != Game1.currentLocation && IsKnownFallbackBlocked(location))
                    continue;

                result[location.NameOrUniqueName] = travel;
            }
            else
            {
                result[name] = travel;
            }
        }

        if (Game1.currentLocation is not null)
            result[Game1.currentLocation.NameOrUniqueName] = 0;

        return result;
    }

    /// <summary>
    /// World Navigator's route values describe special transition edges, not total
    /// walking time, so an empty list can still mean a remote location. Use its API
    /// to decide WHAT is reachable, then estimate ordinary travel from warp hops.
    /// Only the current location is ever allowed to have a zero-minute estimate.
    /// </summary>
    private Dictionary<string, int> EstimateTravelMinutesForReachable(Dictionary<string, int> reachable)
    {
        if (Game1.currentLocation is null)
            return reachable;

        var hops = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [Game1.currentLocation.NameOrUniqueName] = 0
        };
        var queue = new Queue<GameLocation>();
        queue.Enqueue(Game1.currentLocation);

        // A generated mine floor doesn't expose a normal overworld warp chain. Treat
        // returning to the mine entrance as one transition when World Navigator says
        // the entrance is reachable.
        if (IsGeneratedMineLevel(Game1.currentLocation))
        {
            GameLocation? mine = Game1.getLocationFromName("Mine");
            if (mine is not null && reachable.ContainsKey(mine.NameOrUniqueName))
            {
                hops[mine.NameOrUniqueName] = 1;
                queue.Enqueue(mine);
            }
        }

        while (queue.Count > 0)
        {
            GameLocation location = queue.Dequeue();
            int currentHops = hops[location.NameOrUniqueName];
            if (currentHops >= 20)
                continue;

            foreach (object? warp in location.warps)
            {
                string? targetName = GetWarpTargetName(warp);
                if (string.IsNullOrWhiteSpace(targetName))
                    continue;

                GameLocation? target = Game1.getLocationFromName(targetName);
                if (target is null)
                    continue;

                string targetKey = target.NameOrUniqueName;
                if (!reachable.ContainsKey(targetKey) || IsKnownFallbackBlocked(target))
                    continue;

                int next = currentHops + 1;
                if (hops.TryGetValue(targetKey, out int old) && old <= next)
                    continue;

                hops[targetKey] = next;
                queue.Enqueue(target);
            }
        }

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string currentName = Game1.currentLocation.NameOrUniqueName;
        foreach ((string name, int navigatorEstimate) in reachable)
        {
            if (string.Equals(name, currentName, StringComparison.OrdinalIgnoreCase))
            {
                result[name] = 0;
                continue;
            }

            if (hops.TryGetValue(name, out int hopCount) && hopCount > 0)
                result[name] = hopCount * 10;
            else if (navigatorEstimate > 0)
                result[name] = Math.Max(10, navigatorEstimate);
            else
                result[name] = 10; // reachable but World Navigator supplied no ETA
        }

        return result;
    }

    private Dictionary<string, int> GetReachableLocationsFromWarps()
    {
        var allByName = new Dictionary<string, GameLocation>(StringComparer.OrdinalIgnoreCase);
        foreach (GameLocation location in Game1.locations)
            allByName[location.NameOrUniqueName] = location;
        allByName[Game1.currentLocation.NameOrUniqueName] = Game1.currentLocation;

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [Game1.currentLocation.NameOrUniqueName] = 0
        };
        var queue = new Queue<(GameLocation Location, int Hops)>();
        queue.Enqueue((Game1.currentLocation, 0));

        void SeedLocation(string locationName, int hops)
        {
            GameLocation? seed = Game1.getLocationFromName(locationName);
            if (seed is null || IsKnownFallbackBlocked(seed))
                return;

            string key = seed.NameOrUniqueName;
            int minutes = Math.Max(0, hops * 10);
            if (result.TryGetValue(key, out int existingMinutes) && existingMinutes <= minutes)
                return;

            result[key] = minutes;
            queue.Enqueue((seed, hops));
        }

        // Generated mine floors don't expose the normal overworld warp graph in a
        // way this simple fallback can traverse. The elevator/ladder gets the
        // player back to the mine entrance, so seed that normal location and let
        // the regular warp scan continue into the overworld from there.
        if (IsGeneratedMineLevel(Game1.currentLocation))
            SeedLocation("Mine", 1);

        while (queue.Count > 0)
        {
            (GameLocation location, int hops) = queue.Dequeue();
            if (hops >= 12)
                continue;

            foreach (object? warp in location.warps)
            {
                string? targetName = GetWarpTargetName(warp);
                if (string.IsNullOrWhiteSpace(targetName))
                    continue;

                GameLocation? target = Game1.getLocationFromName(targetName);
                if (target is null && allByName.TryGetValue(targetName, out GameLocation? known))
                    target = known;
                if (target is null || IsKnownFallbackBlocked(target))
                    continue;

                string key = target.NameOrUniqueName;
                if (result.ContainsKey(key))
                    continue;

                int nextHops = hops + 1;
                result[key] = nextHops * 10;
                queue.Enqueue((target, nextHops));
            }
        }

        return result;
    }

    private static bool IsGeneratedMineLevel(GameLocation location)
    {
        string typeName = location.GetType().Name;
        string name = location.NameOrUniqueName ?? location.Name ?? string.Empty;

        return typeName.Equals("MineShaft", StringComparison.Ordinal)
            || name.StartsWith("UndergroundMine", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownFallbackBlocked(GameLocation location)
    {
        string name = location.NameOrUniqueName ?? location.Name ?? string.Empty;

        // The raw Forest -> Woods warp exists even while the large log still
        // blocks the entrance. World Navigator handles this properly; the vanilla
        // fallback needs a conservative progression check of its own.
        if (name.Equals("Woods", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                Game1.currentLocation?.NameOrUniqueName,
                "Woods",
                StringComparison.OrdinalIgnoreCase
            ))
        {
            bool hasSteelOrBetterAxe = Game1.player.Items
                .OfType<Axe>()
                .Any(axe => axe.UpgradeLevel >= 2);

            if (!hasSteelOrBetterAxe)
                return true;
        }

        // The Railroad location can be loaded before the Summer 3 earthquake
        // actually opens its path.
        if (name.Equals("Railroad", StringComparison.OrdinalIgnoreCase)
            && Game1.stats.DaysPlayed < 31)
        {
            return true;
        }

        if (name.Equals("Sewer", StringComparison.OrdinalIgnoreCase)
            && !Game1.player.hasRustyKey
            && !Game1.player.hasOrWillReceiveMail("openedSewer"))
        {
            return true;
        }

        // Calico Desert isn't normally reachable until the Vault/Joja bus repair.
        if (name.Equals("Desert", StringComparison.OrdinalIgnoreCase)
            && !Game1.player.hasOrWillReceiveMail("ccVault")
            && !Game1.player.hasOrWillReceiveMail("jojaVault"))
        {
            return true;
        }

        // Ginger Island's maps are loaded into the save before every sub-area is
        // necessarily available. Keep the obvious vanilla progression gates even
        // if World Navigator is configured to ignore some restrictions.
        bool isIslandLocation = name.StartsWith("Island", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("VolcanoDungeon", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Caldera", StringComparison.OrdinalIgnoreCase);
        if (isIslandLocation && !Game1.player.hasOrWillReceiveMail("willyBoatFixed"))
            return true;

        if (name.StartsWith("IslandWest", StringComparison.OrdinalIgnoreCase)
            && !Game1.player.hasOrWillReceiveMail("Island_Turtle"))
            return true;

        if ((name.StartsWith("IslandNorth", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("VolcanoDungeon", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Caldera", StringComparison.OrdinalIgnoreCase))
            && !Game1.player.hasOrWillReceiveMail("Island_FirstParrot"))
            return true;

        if (name.StartsWith("IslandSouthEast", StringComparison.OrdinalIgnoreCase)
            && !Game1.player.hasOrWillReceiveMail("Island_Resort"))
            return true;

        // The Dark Talisman opens the route from the Railroad cave to the Witch's
        // Swamp. (BugLand itself must NOT be blocked here; that's where the player
        // obtains the talisman during the quest.)
        if (name.Equals("WitchSwamp", StringComparison.OrdinalIgnoreCase)
            && !Game1.player.hasDarkTalisman)
        {
            return true;
        }

        return false;
    }

    private static string? GetWarpTargetName(object? warp)
    {
        if (warp is null)
            return null;

        Type type = warp.GetType();
        foreach (string propertyName in new[] { "TargetName", "targetName" })
        {
            PropertyInfo? property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            object? value = property?.GetValue(warp);
            if (value is string str)
                return str;
            if (value is not null)
            {
                PropertyInfo? valueProperty = value.GetType().GetProperty("Value");
                if (valueProperty?.GetValue(value) is string nested)
                    return nested;
            }
        }

        foreach (string fieldName in new[] { "TargetName", "targetName" })
        {
            FieldInfo? field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
            object? value = field?.GetValue(warp);
            if (value is string str)
                return str;
            if (value is not null && value.GetType().GetProperty("Value")?.GetValue(value) is string nested)
                return nested;
        }

        return null;
    }

    private List<GameLocation> ResolveCandidateLocations(IEnumerable<string> reachableNames)
    {
        var result = new Dictionary<string, GameLocation>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in reachableNames)
        {
            GameLocation? location = Game1.getLocationFromName(name);
            if (location is not null && !IsTemporaryLike(location))
                result[location.NameOrUniqueName] = location;
        }

        // Some generated locations only exist as the current location.
        if (!IsTemporaryLike(Game1.currentLocation))
            result[Game1.currentLocation.NameOrUniqueName] = Game1.currentLocation;

        return result.Values.ToList();
    }

    private List<CandidateFishingTile> GetRepresentativeFishingTiles(GameLocation location)
    {
        int day = Game1.Date?.TotalDays ?? -1;
        if (this.representativeTileCacheDay != day)
        {
            this.representativeTileCache.Clear();
            this.representativeTileCacheDay = day;
        }

        string key = location.NameOrUniqueName;
        if (this.representativeTileCache.TryGetValue(key, out List<CandidateFishingTile>? cached))
            return cached;

        List<CandidateFishingTile> scanned = this.FindRepresentativeFishingTiles(location);
        this.representativeTileCache[key] = scanned;
        return scanned;
    }

    private List<CandidateFishingTile> FindRepresentativeFishingTiles(GameLocation location)
    {
        if (!location.canFishHere() || location.Map is null)
            return new List<CandidateFishingTile>();

        xTile.Layers.Layer? back = location.Map.GetLayer("Back");
        if (back is null)
            return new List<CandidateFishingTile>();

        int stride = Math.Clamp(Math.Max(this.config.TileScanStride, 4), 4, 8);
        int maxTiles = Math.Clamp(Math.Min(this.config.MaxTilesPerLocation, 2), 1, 2);

        var representatives = new List<CandidateFishingTile>();
        var bestByDepth = new Dictionary<int, CandidateFishingTile>();
        CandidateFishingTile? deepest = null;

        // Offset successive scan rows so narrow rivers aren't systematically skipped when stride > 1.
        for (int y = 0; y < back.LayerHeight; y += stride)
        {
            int xOffset = ((y / stride) % stride);
            for (int x = xOffset; x < back.LayerWidth; x += stride)
            {
                if (!location.isTileFishable(x, y))
                    continue;

                Vector2 bobberTile = new Vector2(x, y);
                Vector2? playerTile = FindNearbyStandingTile(location, bobberTile, 5);
                if (playerTile is null)
                    continue;

                int depth = FishingRod.distanceToLand(x, y, location);
                var candidate = new CandidateFishingTile(bobberTile, depth, playerTile.Value);

                if (deepest is null || depth > deepest.WaterDepth)
                    deepest = candidate;

                int bucket = Math.Clamp(depth, 0, 5);
                if (!bestByDepth.ContainsKey(bucket))
                    bestByDepth[bucket] = candidate;
            }
        }

        if (deepest is not null)
            representatives.Add(deepest);

        foreach (CandidateFishingTile candidate in bestByDepth.Values.OrderByDescending(p => p.WaterDepth))
        {
            if (representatives.All(p => p.Tile != candidate.Tile))
                representatives.Add(candidate);
            if (representatives.Count >= maxTiles)
                break;
        }

        // Add geographically distributed tiles as insurance for BobberPosition/FishArea rules.
        if (representatives.Count < maxTiles)
        {
            int coarseX = Math.Max(stride, back.LayerWidth / 4);
            int coarseY = Math.Max(stride, back.LayerHeight / 4);
            for (int y = 0; y < back.LayerHeight && representatives.Count < maxTiles; y += coarseY)
            {
                for (int x = 0; x < back.LayerWidth && representatives.Count < maxTiles; x += coarseX)
                {
                    CandidateFishingTile? nearby = FindNearbyFishableTile(location, x, y, Math.Max(coarseX, coarseY) / 2);
                    if (nearby is not null && representatives.All(p => p.Tile != nearby.Tile))
                        representatives.Add(nearby);
                }
            }
        }

        return representatives.Take(maxTiles).ToList();
    }

    private static CandidateFishingTile? FindNearbyFishableTile(GameLocation location, int centerX, int centerY, int radius)
    {
        int boundedRadius = Math.Clamp(radius, 1, 16);
        for (int r = 0; r <= boundedRadius; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                foreach (int dx in new[] { -r, r })
                {
                    int x = centerX + dx;
                    int y = centerY + dy;
                    if (x >= 0 && y >= 0 && location.isTileOnMap(new Vector2(x, y)) && location.isTileFishable(x, y))
                    {
                        Vector2 bobber = new Vector2(x, y);
                        Vector2? playerTile = FindNearbyStandingTile(location, bobber, 5);
                        if (playerTile is not null)
                            return new CandidateFishingTile(bobber, FishingRod.distanceToLand(x, y, location), playerTile.Value);
                    }
                }
            }
        }
        return null;
    }

    private static Vector2? FindNearbyStandingTile(GameLocation location, Vector2 bobberTile, int maxRadius)
    {
        int centerX = (int)bobberTile.X;
        int centerY = (int)bobberTile.Y;
        for (int radius = 1; radius <= maxRadius; radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                        continue;

                    int x = centerX + dx;
                    int y = centerY + dy;
                    Vector2 tile = new Vector2(x, y);
                    if (x < 0 || y < 0 || !location.isTileOnMap(tile) || location.isWaterTile(x, y))
                        continue;

                    if (location.isTilePassable(tile))
                        return tile;
                }
            }
        }

        return null;
    }

    private LocationForecast? EvaluateLocation(
        GameLocation location,
        IReadOnlyList<CandidateFishingTile> tiles,
        IReadOnlyList<int> hours,
        int travelMinutes,
        FishingRod? rod,
        GameRandomScope randomScope)
    {
        CandidateFishingTile? bestTile = null;
        BlockExpectation? bestBlock = null;
        BlockExpectation? bestTravelAdjustedBlock = null;

        // Keep the first calculation reasonably quick, but cache these distributions
        // for the rest of the day. The finite-block CatchLimit calculation below is
        // reapplied every time, so catching a legendary invalidates its contribution
        // without forcing us to re-sample every ordinary fish.
        int samplesPerHour = Math.Clamp(this.config.SamplesPerHour, 12, 64);
        Dictionary<string, int> remainingCatchLimits = GetRemainingCatchLimits(location);

        double catchesPerGameHour = GetEstimatedCatchesPerGameHour(
            rod,
            Game1.player.FishingLevel,
            this.config.CastOverheadMilliseconds,
            this.config.RealMillisecondsPerGameMinute
        );

        int[] fullCatchCounts = BuildCatchCountsByHour(hours.Count, catchesPerGameHour, travelMinutes: 0);
        int[] travelAdjustedCatchCounts = BuildCatchCountsByHour(hours.Count, catchesPerGameHour, travelMinutes);

        foreach (CandidateFishingTile tile in tiles)
        {
            // PlayerPosition fish rules should be evaluated as if the player were
            // standing at this fishing spot.
            Game1.player.Position = tile.PlayerTile * Game1.tileSize;

            var hourlyDistributions = new List<HourCatchDistribution>(hours.Count);
            foreach (int hour in hours)
            {
                HourCatchDistribution distribution = this.GetOrSampleHourDistribution(
                    location,
                    tile,
                    hour,
                    rod,
                    randomScope,
                    samplesPerHour
                );
                hourlyDistributions.Add(distribution);
            }

            BlockExpectation block = CalculateBlockExpectation(
                hourlyDistributions,
                remainingCatchLimits,
                fullCatchCounts
            );
            BlockExpectation adjustedBlock = CalculateBlockExpectation(
                hourlyDistributions,
                remainingCatchLimits,
                travelAdjustedCatchCounts
            );

            // Pick the actual fishing tile with the best expected four-hour return,
            // not the tile which happened to sample one expensive fish.
            if (bestBlock is null || block.Gold > bestBlock.Gold)
            {
                bestTile = tile;
                bestBlock = block;
                bestTravelAdjustedBlock = adjustedBlock;
            }
        }

        if (bestTile is null || bestBlock is null || bestTravelAdjustedBlock is null || bestBlock.Gold <= 0)
            return null;

        double goldPerHour = bestBlock.Gold / Math.Max(1, hours.Count);

        return new LocationForecast(
            location.NameOrUniqueName,
            GetLocationDisplayName(location),
            goldPerHour,
            bestBlock.Gold,
            bestTravelAdjustedBlock.Gold,
            Math.Max(0, travelMinutes),
            bestBlock.BestCatchName,
            bestBlock.BestCatchShare,
            bestTile.Tile,
            bestTile.WaterDepth,
            bestBlock.ExpectedCatches,
            bestTravelAdjustedBlock.ExpectedCatches,
            bestBlock.Fish
        );
    }

    private HourCatchDistribution GetOrSampleHourDistribution(
        GameLocation location,
        CandidateFishingTile tile,
        int hour,
        FishingRod? rod,
        GameRandomScope randomScope,
        int samplesPerHour)
    {
        int day = Game1.Date?.TotalDays ?? -1;
        if (this.distributionCacheDay != day)
        {
            this.distributionCache.Clear();
            this.distributionCacheDay = day;
        }

        string equipmentSignature = GetEquipmentCacheSignature(rod);
        string cacheKey = string.Join(
            "|",
            day,
            location.NameOrUniqueName,
            (int)tile.Tile.X,
            (int)tile.Tile.Y,
            tile.WaterDepth,
            hour,
            Game1.player.FishingLevel,
            Game1.player.LuckLevel,
            samplesPerHour,
            equipmentSignature
        );

        if (this.distributionCache.TryGetValue(cacheKey, out HourCatchDistribution? cached))
            return cached;

        Game1.timeOfDay = hour;
        int seed = StableSeed(location.NameOrUniqueName, tile.Tile, hour);
        randomScope.SetSeed(seed);

        var catches = new Dictionary<string, CatchAggregate>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < samplesPerHour; i++)
        {
            Item? item;
            try
            {
                item = GameLocation.GetFishFromLocationData(
                    location.Name,
                    tile.Tile,
                    tile.WaterDepth,
                    Game1.player,
                    isTutorialCatch: false,
                    isInherited: false,
                    location
                );
            }
            catch
            {
                item = null;
            }

            // GameLocation.getFish() falls back to ordinary Trash when the
            // Data/Locations selector returns no matching fish.
            item ??= ItemRegistry.Create("(O)168");

            double saleValue = GetRealizableSaleValue(item);
            string catchKey = item.QualifiedItemId ?? item.Name ?? "unknown";
            if (!catches.TryGetValue(catchKey, out CatchAggregate? aggregate))
            {
                aggregate = new CatchAggregate
                {
                    Name = item.DisplayName ?? item.Name ?? catchKey,
                    QualifiedItemId = catchKey,
                    IsFish = IsFishItem(item)
                };
                catches[catchKey] = aggregate;
            }

            aggregate.Count++;
            aggregate.TotalValue += saleValue;
        }

        var outcomes = new List<CatchOutcome>(catches.Count);
        foreach ((string key, CatchAggregate aggregate) in catches)
        {
            if (aggregate.Count <= 0)
                continue;

            outcomes.Add(new CatchOutcome(
                key,
                aggregate.Name,
                (double)aggregate.Count / samplesPerHour,
                aggregate.TotalValue / aggregate.Count,
                aggregate.IsFish
            ));
        }

        var result = new HourCatchDistribution(hour, outcomes);
        this.distributionCache[cacheKey] = result;
        return result;
    }

    private static string GetEquipmentCacheSignature(FishingRod? rod)
    {
        if (rod is null)
            return "no-rod";

        string bait = rod.GetBait()?.QualifiedItemId ?? "-";
        string tackle = string.Join(",", rod.GetTackleQualifiedItemIDs());
        return $"{rod.QualifiedItemId ?? rod.Name}|{bait}|{tackle}";
    }

    private static bool IsFishItem(Item item)
    {
        return item is StardewValley.Object obj
            && obj.Category == StardewValley.Object.FishCategory;
    }

    /// <summary>
    /// Turn a sustained catches/hour estimate into chronological catch opportunities.
    /// Travel is consumed from the start of the four-hour block, so a 30-minute trip
    /// reduces catches in the first hour rather than scaling every hour equally.
    /// </summary>
    private static int[] BuildCatchCountsByHour(int hourCount, double catchesPerHour, int travelMinutes)
    {
        var result = new int[Math.Max(0, hourCount)];
        if (result.Length == 0 || catchesPerHour <= 0)
            return result;

        double travelRemaining = Math.Max(0, travelMinutes);
        double cumulativeExpected = 0;
        int allocated = 0;

        for (int i = 0; i < result.Length; i++)
        {
            double blockedMinutes = Math.Min(60d, travelRemaining);
            travelRemaining = Math.Max(0, travelRemaining - 60d);
            double productiveFraction = Math.Clamp((60d - blockedMinutes) / 60d, 0, 1);

            cumulativeExpected += catchesPerHour * productiveFraction;
            int cumulativeRounded = Math.Max(0, (int)Math.Round(cumulativeExpected, MidpointRounding.AwayFromZero));
            result[i] = Math.Max(0, cumulativeRounded - allocated);
            allocated = cumulativeRounded;
        }

        return result;
    }

    /// <summary>
    /// Calculate the expected result catch-by-catch in chronological order. Each
    /// hour uses its own sampled distribution, and CatchLimit fish are removed from
    /// later opportunities once their per-player remaining limit is exhausted.
    /// This is what makes a one-time legendary contribute at most one expected fish
    /// across the entire four-hour block.
    /// </summary>
    private static BlockExpectation CalculateBlockExpectation(
        IReadOnlyList<HourCatchDistribution> hourlyDistributions,
        IReadOnlyDictionary<string, int> remainingCatchLimits,
        IReadOnlyList<int> catchesByHour)
    {
        int totalCatchOpportunities = catchesByHour.Sum();
        if (totalCatchOpportunities <= 0 || hourlyDistributions.Count == 0)
            return new BlockExpectation(0, "—", 0, 0, Array.Empty<FishCatchForecast>());

        var observedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (HourCatchDistribution hour in hourlyDistributions)
        {
            foreach (CatchOutcome outcome in hour.Outcomes)
                observedKeys.Add(outcome.Key);
        }

        // Encode the remaining limited-catch state as a mixed-radix integer. In
        // normal Stardew data this state space is tiny (legendary fish are limit 1).
        // Ignore limits which cannot bind within this block, and cap pathological
        // modded state spaces so the forecast itself remains fast.
        var limitedKeys = new List<string>();
        var limitedLimits = new List<int>();
        var limitedMultipliers = new List<int>();
        int stateCapacity = 1;

        foreach ((string key, int remaining) in remainingCatchLimits
            .Where(p => observedKeys.Contains(p.Key) && p.Value < totalCatchOpportunities)
            .OrderBy(p => p.Value))
        {
            int effectiveLimit = Math.Clamp(remaining, 0, totalCatchOpportunities);
            int radix = effectiveLimit + 1;

            if (radix > 1 && stateCapacity > 4096 / radix)
                continue;

            limitedKeys.Add(key);
            limitedLimits.Add(effectiveLimit);
            limitedMultipliers.Add(stateCapacity);
            stateCapacity *= Math.Max(1, radix);
        }

        var limitedIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < limitedKeys.Count; i++)
            limitedIndex[limitedKeys[i]] = i;

        var stateProbabilities = new Dictionary<int, double> { [0] = 1d };
        var expectedCounts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var expectedGoldByCatch = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var saleValues = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var isFish = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        double expectedGold = 0;

        int hourCount = Math.Min(hourlyDistributions.Count, catchesByHour.Count);
        for (int hourIndex = 0; hourIndex < hourCount; hourIndex++)
        {
            HourCatchDistribution hour = hourlyDistributions[hourIndex];
            int opportunities = Math.Max(0, catchesByHour[hourIndex]);

            foreach (CatchOutcome outcome in hour.Outcomes)
            {
                names[outcome.Key] = outcome.Name;
                saleValues[outcome.Key] = outcome.SaleValue;
                isFish[outcome.Key] = outcome.IsFish;
            }

            for (int opportunity = 0; opportunity < opportunities; opportunity++)
            {
                var nextStates = new Dictionary<int, double>();

                foreach ((int state, double stateProbability) in stateProbabilities)
                {
                    if (stateProbability <= 0)
                        continue;

                    double availableMass = 0;
                    foreach (CatchOutcome outcome in hour.Outcomes)
                    {
                        if (IsOutcomeAvailable(outcome.Key, state, limitedIndex, limitedLimits, limitedMultipliers))
                            availableMass += outcome.Probability;
                    }

                    // If every sampled outcome was a now-exhausted limited fish,
                    // Stardew ultimately falls back to trash for the cast.
                    if (availableMass <= 0.0000001)
                    {
                        AddProbability(nextStates, state, stateProbability);
                        AddExpected(expectedCounts, "(O)168", stateProbability);
                        names["(O)168"] = "Trash";
                        saleValues["(O)168"] = 0;
                        isFish["(O)168"] = false;
                        continue;
                    }

                    foreach (CatchOutcome outcome in hour.Outcomes)
                    {
                        if (!IsOutcomeAvailable(outcome.Key, state, limitedIndex, limitedLimits, limitedMultipliers))
                            continue;

                        double conditionalProbability = outcome.Probability / availableMass;
                        double branchProbability = stateProbability * conditionalProbability;
                        if (branchProbability <= 0)
                            continue;

                        int nextState = state;
                        if (limitedIndex.TryGetValue(outcome.Key, out int limitedSlot))
                            nextState += limitedMultipliers[limitedSlot];

                        AddProbability(nextStates, nextState, branchProbability);
                        AddExpected(expectedCounts, outcome.Key, branchProbability);

                        double goldContribution = branchProbability * outcome.SaleValue;
                        expectedGold += goldContribution;
                        AddExpected(expectedGoldByCatch, outcome.Key, goldContribution);
                    }
                }

                stateProbabilities = nextStates;
                if (stateProbabilities.Count == 0)
                    break;
            }
        }

        string bestName = "—";
        double bestExpectedCount = 0;
        double bestContribution = 0;
        foreach ((string key, double contribution) in expectedGoldByCatch)
        {
            if (contribution <= bestContribution)
                continue;

            bestContribution = contribution;
            expectedCounts.TryGetValue(key, out bestExpectedCount);
            bestName = names.TryGetValue(key, out string? name) ? name : key;
        }

        double bestShare = totalCatchOpportunities > 0
            ? bestExpectedCount / totalCatchOpportunities
            : 0;

        FishCatchForecast[] fish = expectedCounts
            .Where(pair =>
                pair.Value > 0.0001
                && isFish.TryGetValue(pair.Key, out bool value)
                && value)
            .Select(pair =>
            {
                expectedGoldByCatch.TryGetValue(pair.Key, out double fishGold);
                saleValues.TryGetValue(pair.Key, out double saleValue);
                remainingCatchLimits.TryGetValue(pair.Key, out int remainingLimit);

                return new FishCatchForecast(
                    pair.Key,
                    names.TryGetValue(pair.Key, out string? name) ? name : pair.Key,
                    pair.Value,
                    Math.Clamp(pair.Value / totalCatchOpportunities, 0, 1),
                    fishGold,
                    Math.Max(0, (int)Math.Round(saleValue)),
                    remainingCatchLimits.ContainsKey(pair.Key) ? remainingLimit : -1
                );
            })
            .OrderByDescending(p => p.ExpectedCount)
            .ThenByDescending(p => p.ExpectedGold)
            .Take(6)
            .ToArray();

        return new BlockExpectation(
            expectedGold,
            bestName,
            Math.Clamp(bestShare, 0, 1),
            totalCatchOpportunities,
            fish
        );
    }

    private static bool IsOutcomeAvailable(
        string key,
        int state,
        IReadOnlyDictionary<string, int> limitedIndex,
        IReadOnlyList<int> limitedLimits,
        IReadOnlyList<int> multipliers)
    {
        if (!limitedIndex.TryGetValue(key, out int slot))
            return true;

        int radix = limitedLimits[slot] + 1;
        int caught = radix <= 1 ? 0 : (state / multipliers[slot]) % radix;
        return caught < limitedLimits[slot];
    }

    private static void AddProbability(Dictionary<int, double> values, int key, double amount)
    {
        values.TryGetValue(key, out double old);
        values[key] = old + amount;
    }

    private static void AddExpected(Dictionary<string, double> values, string key, double amount)
    {
        values.TryGetValue(key, out double old);
        values[key] = old + amount;
    }

    /// <summary>
    /// Resolve per-player remaining CatchLimit values from Data/Locations. Stardew
    /// compares SpawnFishData.CatchLimit against fishCaught[qid][0]; the forecast
    /// uses that same remaining count across the finite catch opportunities.
    /// </summary>
    private static Dictionary<string, int> GetRemainingCatchLimits(GameLocation location)
    {
        var limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var explicitlyUnlimited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, LocationData> allLocations = DataLoader.Locations(Game1.content);
        IEnumerable<SpawnFishData> spawns = Enumerable.Empty<SpawnFishData>();
        if (allLocations.TryGetValue("Default", out LocationData? defaultData) && defaultData.Fish is not null)
            spawns = defaultData.Fish;

        LocationData? localData = location.GetData();
        if (localData?.Fish is { Count: > 0 })
            spawns = spawns.Concat(localData.Fish);

        foreach (SpawnFishData spawn in spawns)
        {
            if (string.IsNullOrWhiteSpace(spawn.ItemId))
                continue;

            string? qualifiedId = null;
            try
            {
                qualifiedId = ItemRegistry.QualifyItemId(spawn.ItemId);
                if (string.IsNullOrWhiteSpace(qualifiedId) || !ItemRegistry.Exists(qualifiedId))
                    qualifiedId = null;
            }
            catch
            {
                // Item-query-only entries can't be mapped to one stable item ID here.
            }

            if (string.IsNullOrWhiteSpace(qualifiedId))
                continue;

            if (spawn.CatchLimit < 0)
            {
                explicitlyUnlimited.Add(qualifiedId);
                limits.Remove(qualifiedId);
                continue;
            }

            if (explicitlyUnlimited.Contains(qualifiedId))
                continue;

            int alreadyCaught = 0;
            if (Game1.player.fishCaught.TryGetValue(qualifiedId, out int[]? values)
                && values is { Length: > 0 })
            {
                alreadyCaught = values[0];
            }

            int remaining = Math.Max(0, spawn.CatchLimit - alreadyCaught);
            if (!limits.TryGetValue(qualifiedId, out int oldRemaining) || remaining > oldRemaining)
                limits[qualifiedId] = remaining;
        }

        return limits;
    }

    private static double GetEstimatedCatchesPerGameHour(
        FishingRod? rod,
        int fishingLevel,
        int castOverheadMilliseconds,
        int realMillisecondsPerGameMinute)
    {
        double expectedBiteMs = GetExpectedBiteMilliseconds(rod, fishingLevel);
        double cycleMs = Math.Max(500, expectedBiteMs + Math.Max(0, castOverheadMilliseconds));
        double hourMs = 60d * Math.Max(1, realMillisecondsPerGameMinute);
        return Math.Clamp(hourMs / cycleMs, 0.25, 30);
    }

    private sealed record CatchOutcome(string Key, string Name, double Probability, double SaleValue, bool IsFish);
    private sealed record HourCatchDistribution(int Hour, IReadOnlyList<CatchOutcome> Outcomes);
    private sealed record BlockExpectation(double Gold, string BestCatchName, double BestCatchShare, double ExpectedCatches, IReadOnlyList<FishCatchForecast> Fish);


    private static double GetRealizableSaleValue(Item item)
    {
        // Furniture includes fishing rewards like paintings. Their item data can have
        // a price used by other game systems, but they aren't normal sale/shipping income.
        if (item is Furniture)
            return 0;

        if (!item.canBeShipped())
            return 0;

        return Math.Max(0, item.sellToStorePrice());
    }

    private static double GetExpectedBiteMilliseconds(FishingRod? rod, int fishingLevel)
    {
        int reduction = 0;
        string? baitId = null;

        if (rod is not null)
        {
            List<string> tackleIds = rod.GetTackleQualifiedItemIDs();
            reduction += tackleIds.Count(id => id == "(O)687") * 10000;
            reduction += tackleIds.Count(id => id == "(O)686") * 5000;
            baitId = rod.GetBait()?.QualifiedItemId;
        }

        int min = FishingRod.minFishingBiteTime;
        int maxExclusive = Math.Max(min, FishingRod.maxFishingBiteTime - 250 * fishingLevel - reduction);
        double average = maxExclusive <= min ? min : (min + (maxExclusive - 1)) / 2d;

        // The forecast is a sustained-rate estimate, so omit the game's one-time first-cast 0.75 multiplier.
        if (baitId is not null)
        {
            average *= 0.5;
            if (baitId is "(O)774" or "(O)ChallengeBait")
                average *= 0.75;
            if (baitId == "(O)DeluxeBait")
                average *= 0.66;
        }

        return Math.Max(500, average);
    }

    private static string GetEquipmentSummary(FishingRod? rod)
    {
        if (rod is null)
            return "No fishing rod selected — bait/tackle bonuses are not included";

        string bait = rod.GetBait()?.DisplayName ?? "no bait";
        List<string> tackle = rod.GetTackle()
            .Where(p => p is not null)
            .Select(p => p.DisplayName)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        string tackleText = tackle.Count > 0 ? string.Join(", ", tackle) : "no tackle";
        return $"{rod.DisplayName}: {bait}; {tackleText}";
    }

    private static string GetWeatherSummary()
    {
        // These Game1 compatibility flags are still exposed in Stardew 1.6.
        // This is only the human-readable header; the fish simulation itself uses
        // Stardew's location-data fishing routine, which evaluates the applicable
        // location/weather rules for each sampled catch.
        if (Game1.isLightning)
            return "Storm";
        if (Game1.isRaining)
            return "Rain";
        if (Game1.isSnowing)
            return "Snow";
        if (Game1.isDebrisWeather)
            return "Wind";
        return "Sun";
    }

    private static bool IsTemporaryLike(GameLocation location)
    {
        string name = location.NameOrUniqueName ?? location.Name ?? "";
        return name.StartsWith("Temp", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Festival", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetLocationDisplayName(GameLocation location)
    {
        try
        {
            return string.IsNullOrWhiteSpace(location.DisplayName) ? location.NameOrUniqueName : location.DisplayName;
        }
        catch
        {
            return location.NameOrUniqueName;
        }
    }

    private static int StableSeed(string locationName, Vector2 tile, int hour)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (int)(Game1.uniqueIDForThisGame & 0x7FFFFFFF);
            hash = hash * 31 + (int)Game1.stats.DaysPlayed;
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(locationName);
            hash = hash * 31 + (int)tile.X;
            hash = hash * 31 + (int)tile.Y;
            hash = hash * 31 + hour;
            return hash;
        }
    }

    private sealed class GameRandomScope : IDisposable
    {
        private readonly FieldInfo? randomField;
        private readonly PropertyInfo? randomProperty;
        private readonly object? original;

        public GameRandomScope(IMonitor monitor)
        {
            Type game1 = typeof(Game1);
            this.randomField = game1.GetField("random", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            this.randomProperty = this.randomField is null
                ? game1.GetProperty("random", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                : null;
            this.original = this.GetCurrent();

            bool writable = (this.randomField is not null && !this.randomField.IsInitOnly)
                || this.randomProperty?.CanWrite == true;
            if (!writable || this.original is null)
                throw new InvalidOperationException("This Stardew version doesn't expose a replaceable Game1.random; aborting forecast rather than consuming gameplay RNG.");
        }

        public void SetSeed(int seed)
        {
            if (!this.TrySet(new Random(seed)))
                throw new InvalidOperationException("Fishing Forecast couldn't substitute Stardew's RNG; aborting forecast rather than consuming gameplay RNG.");
        }

        public void Dispose()
        {
            if (this.original is not null)
                this.TrySet(this.original);
        }

        private object? GetCurrent()
        {
            if (this.randomField is not null)
                return this.randomField.GetValue(null);
            return this.randomProperty?.GetValue(null);
        }

        private bool TrySet(object value)
        {
            try
            {
                if (this.randomField is not null && !this.randomField.IsInitOnly)
                {
                    this.randomField.SetValue(null, value);
                    return true;
                }
                if (this.randomProperty?.CanWrite == true)
                {
                    this.randomProperty.SetValue(null, value);
                    return true;
                }
            }
            catch
            {
                // handled by caller
            }
            return false;
        }
    }
}

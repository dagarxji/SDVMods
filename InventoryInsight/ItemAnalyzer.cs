using StardewValley;
using StardewValley.GameData.Machines;
using StardewValley.Locations;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Quests;
using StardewValley.SpecialOrders;
using StardewValley.SpecialOrders.Objectives;
using SObject = StardewValley.Object;

namespace InventoryInsight;

internal sealed class ItemAnalyzer
{
    private readonly ModEntry Mod;
    private readonly Dictionary<string, IReadOnlyList<CraftingUse>> CraftingCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> GiftCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<MachineRoute>> MachineCache = new(StringComparer.Ordinal);

    public ItemAnalyzer(ModEntry mod)
    {
        Mod = mod;
    }

    public void ClearCaches()
    {
        CraftingCache.Clear();
        GiftCache.Clear();
        MachineCache.Clear();
    }

    public ItemInsight Analyze(Item item)
    {
        string itemIdKey = item.QualifiedItemId;
        string variantKey = GetVariantCacheKey(item);
        IReadOnlyList<string> lovedBy = GetLovedBy(item, variantKey);
        bool communityCenterNeeded = IsNeededForCommunityCenter(item);
        bool museumNeeded = LibraryMuseum.IsItemSuitableForDonation(item.QualifiedItemId);
        IReadOnlyList<QuestUse> questUses = GetQuestUses(item);
        IReadOnlyList<CraftingUse> craftingUses = GetCraftingUses(item, itemIdKey);
        int sellPrice = Math.Max(0, item.sellToStorePrice(-1L));
        IReadOnlyList<MachineRoute> machineRoutes = GetMachineRoutes(item, variantKey);

        bool hasProfitableMachine = machineRoutes.Any(p => p.IsProfitable);
        bool safe = sellPrice > 0
            && !communityCenterNeeded
            && !museumNeeded
            && questUses.Count == 0
            && craftingUses.Count == 0
            && (!Mod.Config.GiftsPreventSafeSell || lovedBy.Count == 0)
            && (!Mod.Config.ProfitableMachinesPreventSafeSell || !hasProfitableMachine);

        return new ItemInsight
        {
            ItemId = item.QualifiedItemId,
            DisplayName = item.DisplayName,
            LovedBy = lovedBy,
            CommunityCenterNeeded = communityCenterNeeded,
            MuseumNeeded = museumNeeded,
            QuestUses = questUses,
            CraftingUses = craftingUses,
            SellPrice = sellPrice,
            MachineRoutes = machineRoutes,
            SafeToSell = safe
        };
    }

    private IReadOnlyList<string> GetLovedBy(Item item, string cacheKey)
    {
        if (GiftCache.TryGetValue(cacheKey, out IReadOnlyList<string>? cached))
            return cached;

        List<string> names = new();
        foreach (NPC npc in Utility.getAllVillagers())
        {
            try
            {
                if (npc.CanReceiveGifts() && npc.getGiftTasteForThisItem(item) == NPC.gift_taste_love)
                    names.Add(npc.displayName);
            }
            catch (Exception ex)
            {
                Mod.Monitor.LogOnce($"Couldn't evaluate gift taste for {npc.Name}: {ex.Message}", StardewModdingAPI.LogLevel.Trace);
            }
        }

        names.Sort(StringComparer.CurrentCultureIgnoreCase);
        GiftCache[cacheKey] = names;
        return names;
    }

    /// <summary>
    /// Uses the save's generated bundle data and the save's live completion flags. This deliberately avoids
    /// a hardcoded vanilla bundle table, so remixed/content-patched bundles and completed ingredients are respected.
    /// </summary>
    private bool IsNeededForCommunityCenter(Item item)
    {
        if (item is not SObject obj)
            return false;

        if (obj.bigCraftable.Value)
            return false;

        if (Game1.MasterPlayer.mailReceived.Contains("JojaMember"))
            return false;

        if (Game1.getLocationFromName("CommunityCenter") is not CommunityCenter cc)
            return false;

        Dictionary<int, bool[]> progress = cc.bundlesDict();
        foreach ((string bundleKey, string rawBundle) in Game1.netWorldState.Value.BundleData)
        {
            string[] keyParts = bundleKey.Split('/');
            if (keyParts.Length < 2 || !int.TryParse(keyParts[1], out int bundleId))
                continue;

            if (!progress.TryGetValue(bundleId, out bool[]? completedIngredients))
                continue;

            // Ask the game's live bundle state rather than inferring completion from a hardcoded list.
            if (cc.isBundleComplete(bundleId))
                continue;

            string[] fields = rawBundle.Split('/');
            if (fields.Length < 3)
                continue;

            string[] ingredients = ArgUtility.SplitBySpace(fields[2]);
            for (int i = 0; i + 2 < ingredients.Length; i += 3)
            {
                int ingredientIndex = i / 3;
                if (ingredientIndex < completedIngredients.Length && completedIngredients[ingredientIndex])
                    continue;

                string requiredId = ingredients[i];
                int minimumQuality = int.TryParse(ingredients[i + 2], out int q) ? q : 0;
                if (obj.Quality < minimumQuality)
                    continue;

                if (MatchesItemOrCategory(obj, requiredId))
                    return true;
            }
        }

        return false;
    }

    private IReadOnlyList<QuestUse> GetQuestUses(Item item)
    {
        List<QuestUse> uses = new();

        // Delivery/lost-item quests can consume an existing inventory item. Collection/harvest/fishing quests
        // generally track newly obtained items instead, so they intentionally don't make an old stack look needed.
        foreach (Quest quest in Game1.player.questLog)
        {
            if (quest.completed.Value)
                continue;

            if (quest is ItemDeliveryQuest delivery && ItemRegistry.HasItemId(item, delivery.ItemId.Value))
            {
                uses.Add(new QuestUse(delivery.questTitle, "Quest"));
                continue;
            }

            if (quest is LostItemQuest lost && ItemRegistry.HasItemId(item, lost.ItemId.Value))
            {
                uses.Add(new QuestUse(lost.questTitle, "Quest"));
                continue;
            }

            if (quest is SecretLostItemQuest secretLost && ItemRegistry.HasItemId(item, secretLost.ItemId.Value))
                uses.Add(new QuestUse(secretLost.questTitle, "Quest"));
        }

        foreach (SpecialOrder order in Game1.player.team.specialOrders)
        {
            if (order.questState.Value != SpecialOrderStatus.InProgress)
                continue;

            try
            {
                if (SpecialOrderNeedsExistingItem(order, item))
                    uses.Add(new QuestUse(order.GetName(), "Special order"));
            }
            catch (Exception ex)
            {
                Mod.Monitor.LogOnce($"Couldn't evaluate special order {order.questKey.Value}: {ex.Message}", StardewModdingAPI.LogLevel.Trace);
            }
        }

        return uses;
    }

    private bool SpecialOrderNeedsExistingItem(SpecialOrder order, Item item)
    {
        // Donate/drop-box objectives expose the game's own item-highlighting check.
        if (order.HighlightAcceptableItems(item))
            return true;

        // Only objectives which can be satisfied from an already-owned stack belong in a keep/sell tooltip.
        // Collect/fish objectives intentionally aren't included because they track newly collected/caught items.
        foreach (OrderObjective objective in order.objectives)
        {
            if (objective.IsComplete())
                continue;

            if (objective is ShipObjective ship && MatchesContextTagSets(item, ship.acceptableContextTagSets))
                return true;

            if (objective is DeliverObjective deliver && MatchesContextTagSets(item, deliver.acceptableContextTagSets))
                return true;

            if (objective is GiftObjective gift
                && MatchesContextTagSets(item, gift.acceptableContextTagSets)
                && CanSatisfyGiftObjective(item, gift))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesContextTagSets(Item item, IEnumerable<string> acceptableContextTagSets)
    {
        string[] sets = acceptableContextTagSets.ToArray();
        if (sets.Length == 0)
            return true;

        var itemTags = item.GetContextTags();
        foreach (string set in sets)
        {
            bool allRequirementsMatch = true;
            foreach (string requirement in set.Split(','))
            {
                string[] alternatives = requirement.Split('/');
                if (!ItemContextTagManager.DoAnyTagsMatch(alternatives, itemTags))
                {
                    allRequirementsMatch = false;
                    break;
                }
            }

            if (allRequirementsMatch)
                return true;
        }

        return false;
    }

    private static bool CanSatisfyGiftObjective(Item item, GiftObjective objective)
    {
        GiftObjective.LikeLevels minimum = objective.minimumLikeLevel.Value;
        foreach (NPC npc in Utility.getAllVillagers())
        {
            if (!npc.CanReceiveGifts())
                continue;

            if (minimum == GiftObjective.LikeLevels.None)
                return true;

            GiftObjective.LikeLevels level = npc.getGiftTasteForThisItem(item) switch
            {
                NPC.gift_taste_love => GiftObjective.LikeLevels.Loved,
                NPC.gift_taste_like => GiftObjective.LikeLevels.Liked,
                NPC.gift_taste_neutral => GiftObjective.LikeLevels.Neutral,
                NPC.gift_taste_dislike => GiftObjective.LikeLevels.Disliked,
                NPC.gift_taste_hate => GiftObjective.LikeLevels.Hated,
                _ => GiftObjective.LikeLevels.None
            };

            if (level >= minimum)
                return true;
        }

        return false;
    }

    private IReadOnlyList<CraftingUse> GetCraftingUses(Item item, string cacheKey)
    {
        if (CraftingCache.TryGetValue(cacheKey, out IReadOnlyList<CraftingUse>? cached))
            return cached;

        List<CraftingUse> uses = new();
        Dictionary<string, string> data = DataLoader.CraftingRecipes(Game1.content);
        foreach (string recipeKey in data.Keys)
        {
            try
            {
                CraftingRecipe recipe = new(recipeKey, isCookingRecipe: false);
                foreach ((string ingredientId, int count) in recipe.recipeList)
                {
                    if (CraftingRecipe.ItemMatchesForCrafting(item, ingredientId))
                    {
                        uses.Add(new CraftingUse(recipe.DisplayName, recipe.createItem().QualifiedItemId, count));
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.Monitor.LogOnce($"Couldn't inspect crafting recipe '{recipeKey}': {ex.Message}", StardewModdingAPI.LogLevel.Trace);
            }
        }

        uses = uses.OrderBy(p => p.RecipeName, StringComparer.CurrentCultureIgnoreCase).ToList();
        CraftingCache[cacheKey] = uses;
        return uses;
    }

    private IReadOnlyList<MachineRoute> GetMachineRoutes(Item item, string cacheKey)
    {
        if (item is not SObject)
            return Array.Empty<MachineRoute>();

        if (MachineCache.TryGetValue(cacheKey, out IReadOnlyList<MachineRoute>? cached))
            return cached;

        List<MachineRoute> routes = new();
        Dictionary<string, MachineData> machines = DataLoader.Machines(Game1.content);
        int[] qualities = { 0, 1, 2, 4 };

        foreach ((string machineId, MachineData machineData) in machines)
        {
            if (machineData.OutputRules is null || machineData.OutputRules.Count == 0)
                continue;

            if (ItemRegistry.Create(machineId, allowNull: true) is not SObject machine)
                continue;

            // MachineDataUtility evaluates some output logic using the machine's location.
            machine.Location = Game1.currentLocation;

            List<QualityValue> qualityValues = new();
            string? outputName = null;
            string? outputItemId = null;
            int requiredInputCount = 1;
            bool foundAnyQuality = false;

            foreach (int quality in qualities)
            {
                Item input = item.getOne();
                input.Quality = quality;
                input.Stack = 999;

                if (!MachineDataUtility.TryGetMachineOutputRule(
                    machine,
                    machineData,
                    MachineOutputTrigger.ItemPlacedInMachine,
                    input,
                    Game1.player,
                    Game1.currentLocation,
                    out MachineOutputRule? rule,
                    out MachineOutputTriggerRule? triggerRule,
                    out _,
                    out _))
                {
                    continue;
                }

                if (rule is null || triggerRule is null)
                    continue;

                MachineItemOutput? outputData = GetDeterministicOutput(rule, input);
                if (outputData is null || outputData.OutputMethod is not null)
                    continue;

                // Avoid item-query expressions whose resolution can consume the game's RNG while merely hovering.
                if (!IsSimpleOutputId(outputData.ItemId))
                    continue;

                Item? output;
                try
                {
                    output = MachineDataUtility.GetOutputItem(machine, outputData, input, Game1.player, probe: true, out _);
                }
                catch
                {
                    continue;
                }

                if (output is null)
                    continue;

                requiredInputCount = Math.Max(1, triggerRule.RequiredCount);
                int raw = Math.Max(0, input.sellToStorePrice(-1L)) * requiredInputCount;
                int processed = Math.Max(0, output.sellToStorePrice(-1L)) * Math.Max(1, output.Stack);
                qualityValues.Add(new QualityValue(quality, raw, processed));
                outputName ??= output.DisplayName;
                outputItemId ??= output.QualifiedItemId;
                foundAnyQuality = true;
            }

            if (!foundAnyQuality || qualityValues.Count == 0 || outputName is null || outputItemId is null)
                continue;

            IReadOnlyList<ConsumedItem> additionalInputs = GetAdditionalConsumedItems(machineData);
            MachineRoute route = new(machine.QualifiedItemId, machine.DisplayName, outputItemId, outputName, requiredInputCount, additionalInputs, qualityValues);
            if (route.IsProfitable)
                routes.Add(route);
        }

        routes = routes
            .OrderByDescending(p => p.Values.Max(v => v.OutputValue - v.RawValue - p.AdditionalInputCost))
            .ThenBy(p => p.MachineName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        MachineCache[cacheKey] = routes;
        return routes;
    }

    private static MachineItemOutput? GetDeterministicOutput(MachineOutputRule rule, Item input)
    {
        if (rule.OutputItem is null || rule.OutputItem.Count == 0)
            return null;

        List<MachineItemOutput> valid = rule.OutputItem
            .Where(p => GameStateQuery.CheckConditions(p.Condition, Game1.currentLocation, Game1.player, null, input))
            .ToList();

        if (valid.Count == 0)
            return null;
        if (rule.UseFirstValidOutput || valid.Count == 1)
            return valid[0];

        // Random alternatives aren't useful for a quick deterministic price comparison and shouldn't advance RNG.
        return null;
    }

    private static bool IsSimpleOutputId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        // Direct IDs and the standard machine placeholders are safe. Item-query expressions generally contain spaces.
        return !id.Contains(' ');
    }

    private static IReadOnlyList<ConsumedItem> GetAdditionalConsumedItems(MachineData data)
    {
        // Stardew 1.6.16 adds MachineData.AdditionalConsumedItems. Keep this source buildable against
        // 1.6.15 too by reading that newer field reflectively when it exists.
        var field = typeof(MachineData).GetField("AdditionalConsumedItems");
        if (field?.GetValue(data) is not System.Collections.IEnumerable extras)
            return Array.Empty<ConsumedItem>();

        List<ConsumedItem> items = new();
        foreach (object? extra in extras)
        {
            if (extra is null)
                continue;

            Type type = extra.GetType();
            string? itemId = type.GetField("ItemId")?.GetValue(extra) as string
                ?? type.GetProperty("ItemId")?.GetValue(extra) as string;
            object? countValue = type.GetField("RequiredCount")?.GetValue(extra)
                ?? type.GetProperty("RequiredCount")?.GetValue(extra);
            int requiredCount = countValue is int count ? count : 1;

            if (string.IsNullOrWhiteSpace(itemId))
                continue;

            Item? consumed = ItemRegistry.Create(itemId, allowNull: true);
            if (consumed is not null)
            {
                int quantity = Math.Max(1, requiredCount);
                int value = Math.Max(0, consumed.sellToStorePrice(-1L)) * quantity;
                items.Add(new ConsumedItem(consumed.QualifiedItemId, quantity, value));
            }
        }

        return items;
    }

    private static string GetVariantCacheKey(Item item)
    {
        // Preserve flavored/context-sensitive variants which can share a qualified item ID but differ in
        // gift tastes, context tags, machine outputs, or value.
        string tags = string.Join("|", item.GetContextTags().OrderBy(p => p, StringComparer.Ordinal));
        return $"{item.QualifiedItemId}\u001f{item.DisplayName}\u001f{item.sellToStorePrice(-1L)}\u001f{tags}";
    }

    private static bool MatchesItemOrCategory(SObject item, string requiredId)
    {
        if (int.TryParse(requiredId, out int numeric) && numeric < 0)
            return item.Category == numeric;

        ParsedItemData? parsed = ItemRegistry.GetData(requiredId);
        if (parsed is not null)
            return item.QualifiedItemId == parsed.QualifiedItemId;

        string qualified = requiredId.StartsWith('(') ? requiredId : "(O)" + requiredId;
        return item.QualifiedItemId == qualified;
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Tools;

namespace AutoEatEideeBridge;

/// <summary>
/// Compatibility bridge between Auto-Eat and Eidee Easy Fishing's Auto Recast.
///
/// Auto-Eat can call FishingRod.resetState() while Eidee has dispatched its next
/// automatic cast. If that happens before Eidee observes the rod in-use state,
/// Eidee can be left with _autoRecastDispatched == true forever and won't cast again.
///
/// This mod notices Auto-Eat beginning a meal while Eidee's Auto Recast is armed,
/// waits until the meal has fully finished, then repairs Eidee's private recast
/// state. Eidee itself performs the next cast, so its normal speed, full-power cast,
/// stop-time, fishability, and stamina safeguards remain in control.
///
/// It also tracks fishing rate/gold stats, and optionally keeps Fast Animations'
/// fishing speed and TimeSpeed's flow of time in sync with fishing/mastery level.
/// </summary>
internal sealed class ModEntry : Mod
{
    private readonly record struct CatchInventoryState(string ItemId, int PreviousCount, bool ShouldDestroy);

    /// <summary>A caught item waiting to be destroyed once it actually lands in the inventory or an overflow menu.</summary>
    private readonly record struct PendingCatchDeletion(string ItemId, int PreviousCount, int CaughtCount, long SinceTick)
    {
        /// <summary>How many of this item should be present once the catch fully lands.</summary>
        public int ExpectedCount => PreviousCount + CaughtCount;
    }

    private const string EideeTypeName = "EideeEasyFishing.ModEntry";
    private const string AutoEatTypeName = "AutoEat.ModEntry";
    private const string FastAnimationsTypeName = "Pathoschild.Stardew.FastAnimations.ModEntry";
    private const string TimeSpeedTypeName = "TimeSpeed.ModEntry";

    // Don't leave a stale pending resume around indefinitely if another mod changes state.
    private const long PendingTimeoutTicks = 600; // ~10 seconds at Stardew's normal 60 ticks/sec.

    // How long a pending catch deletion stays armed after the catch was collected. While the fish
    // is still being held overhead (uncollected) there's no time limit, so slow clicks are fine.
    private const long CatchDeletionTimeoutTicks = 600;
    private const int ReadyTicksRequired = 2;
    private const int TrackerHideDelayTicks = 300;
    private const int TrackerMinWidth = 300;
    private const int TrackerMinHeight = 196;
    private const int TrackerPadding = 22;
    private const double StatsRefreshIntervalSeconds = 5d;
    private const string GenericModConfigMenuId = "spacechase0.GenericModConfigMenu";

    // 1x base + up to 10x for fishing level + up to 4x for the other four skills reaching level 10
    // + up to 5x for mastery level = 20x at full fishing/mastery progress.
    private const int MaxOtherSkillsAtMax = 4;
    private const int MaxMasteryLevel = 5;
    private const float SpeedEpsilon = 0.001f;
    private const double DivisorEpsilon = 0.0001d;

    private static ModEntry? Instance;

    private Harmony? _harmony;
    private bool _integrationReady;
    private bool _configMenuRegistered;
    private ModConfig _config = new();

    private object? _eideeInstance;
    private Type? _eideeType;
    private Type? _autoEatType;

    private FieldInfo? _autoRecastRodField;
    private FieldInfo? _autoRecastStopPendingField;
    private FieldInfo? _autoRecastDispatchedField;
    private FieldInfo? _autoRecastForcePowerField;
    private FieldInfo? _prevRodInUseField;
    private FieldInfo? _autoAdvanceCatchAttemptsField;
    private FieldInfo? _autoAdvanceCatchCooldownTicksField;
    private FieldInfo? _castOwnedByAutoRecastField;
    private FieldInfo? _autoEatEatingFoodField;

    private FishingRod? _pendingRod;
    private long _pendingSinceTick;
    private int _readyTicks;

    // Multiple catches can be awaiting deletion at once (e.g. Wild Bait double catches, or
    // back-to-back catches during autocast), so keep a queue rather than a single pending entry.
    private readonly List<PendingCatchDeletion> _pendingCatchDeletions = new();
    private bool _autoDestroyMenuRequested;

    /// <summary>The on-screen bounds of the "Manage auto-destroy items" button drawn inside GMCM, in UI pixels.</summary>
    private Rectangle _autoDestroyButtonBounds;

    /// <summary>The game tick when the button was last drawn; used to confirm it's currently visible before accepting a click.</summary>
    private long _autoDestroyButtonDrawnTick = -1;

    private bool _trackingFishing;
    private int _fishingStartedTime;
    private int _fishingStoppedTime;
    private double _fishingStartedRealSeconds;
    private double _fishingStoppedRealSeconds;
    private double _fishingStoppedGameMinutes;
    private long _trackerHiddenSinceTick;
    private int _fishCaughtThisSession;
    private int _fishCaughtAtSessionStart;
    private double _lastFishPerHour;
    private double _mostFishPerHour;
    private bool _draggingTracker;
    private Point _dragOffset;
    private int _trackerWidth = TrackerMinWidth;
    private int _trackerHeight = TrackerMinHeight;

    private int _fishCaughtAtDayStart;
    private double _lastStatsRefreshRealSeconds;
    private double _cachedFishPerHour;
    private double _cachedGoldPerHourSession;
    private double _lastGoldPerHour;
    private int _sessionGoldEarned;
    private int _dayGoldEarned;

    private bool _fastAnimationsIntegrationReady;
    private object? _fastAnimationsInstance;
    private FieldInfo? _fastAnimationsConfigField;
    private MethodInfo? _fastAnimationsUpdateConfigMethod;
    private PropertyInfo? _fastAnimationsFishingSpeedProperty;
    private float? _fastAnimationsBaselineFishingSpeed;
    private float? _lastAppliedFishingAnimationSpeed;

    private readonly record struct TimeSpeedBaseline(double Outdoors, double Indoors, double Mines, double SkullCavern, double VolcanoDungeon, Dictionary<string, double> ByLocationName);

    private bool _timeSpeedIntegrationReady;
    private object? _timeSpeedInstance;
    private FieldInfo? _timeSpeedConfigField;
    private MethodInfo? _timeSpeedUpdateSettingsMethod;
    private PropertyInfo? _timeSpeedSecondsPerMinuteProperty;
    private PropertyInfo? _timeSpeedOutdoorsProperty;
    private PropertyInfo? _timeSpeedIndoorsProperty;
    private PropertyInfo? _timeSpeedMinesProperty;
    private PropertyInfo? _timeSpeedSkullCavernProperty;
    private PropertyInfo? _timeSpeedVolcanoDungeonProperty;
    private PropertyInfo? _timeSpeedByLocationNameProperty;
    private TimeSpeedBaseline? _timeSpeedBaseline;
    private double? _lastAppliedTimeSpeedDivisor;

    public override void Entry(IModHelper helper)
    {
        Instance = this;
        _config = helper.ReadConfig<ModConfig>();
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.UpdateTicking += OnUpdateTicking;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.Player.Warped += OnWarped;
        helper.Events.Display.RenderedHud += OnRenderedHud;
        helper.Events.Input.ButtonPressed += OnButtonPressed;
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        RegisterConfigMenu();

        _harmony = new Harmony(ModManifest.UniqueID);

        try
        {
            // Hooked independently of the Eidee/Auto-Eat integration below so catch tracking and
            // trash deletion work even if one of those mods isn't installed.
            MethodInfo? playerCaughtFish = AccessTools.Method(typeof(FishingRod), "playerCaughtFishEndFunction");
            if (playerCaughtFish is not null)
            {
                _harmony.Patch(
                    original: playerCaughtFish,
                    prefix: new HarmonyMethod(typeof(ModEntry), nameof(BeforePlayerCaughtFish)),
                    postfix: new HarmonyMethod(typeof(ModEntry), nameof(AfterPlayerCaughtFish))
                );
            }
            else
            {
                Monitor.Log("Couldn't find FishingRod.playerCaughtFishEndFunction; catch tracking and trash deletion will be unavailable.", LogLevel.Warn);
            }
        }
        catch (Exception ex)
        {
            Monitor.Log($"Failed to patch FishingRod.playerCaughtFishEndFunction for catch tracking and trash deletion: {ex}", LogLevel.Warn);
        }

        // Each integration is independent: if one mod is missing or has changed its internals,
        // the others still initialize normally.
        TryPatchEideeAutoEat();
        TryPatchFastAnimations();
        TryPatchTimeSpeed();
    }

    /// <summary>Patch Eidee Easy Fishing and Auto-Eat so their compatibility issue can be repaired.</summary>
    private void TryPatchEideeAutoEat()
    {
        try
        {
            _eideeType = AccessTools.TypeByName(EideeTypeName);
            _autoEatType = AccessTools.TypeByName(AutoEatTypeName);

            if (_eideeType is null)
            {
                DisableIntegration($"Couldn't find {EideeTypeName}. Is Eidee Easy Fishing 1.4.0+ loaded?");
                return;
            }

            if (_autoEatType is null)
            {
                DisableIntegration($"Couldn't find {AutoEatTypeName}. Is Auto-Eat 2.3.3+ loaded?");
                return;
            }

            MethodInfo? updateAutoRecast = AccessTools.Method(_eideeType, "UpdateAutoRecast");
            MethodInfo? eatFood = AccessTools.Method(_autoEatType, "EatFood");

            _autoRecastRodField = AccessTools.Field(_eideeType, "_autoRecastRod");
            _autoRecastStopPendingField = AccessTools.Field(_eideeType, "_autoRecastStopPending");
            _autoRecastDispatchedField = AccessTools.Field(_eideeType, "_autoRecastDispatched");
            _autoRecastForcePowerField = AccessTools.Field(_eideeType, "_autoRecastForcePower");
            _prevRodInUseField = AccessTools.Field(_eideeType, "_prevRodInUse");
            _autoAdvanceCatchAttemptsField = AccessTools.Field(_eideeType, "_autoAdvanceCatchAttempts");
            _autoAdvanceCatchCooldownTicksField = AccessTools.Field(_eideeType, "_autoAdvanceCatchCooldownTicks");
            _castOwnedByAutoRecastField = AccessTools.Field(_eideeType, "_castOwnedByAutoRecast");
            _autoEatEatingFoodField = AccessTools.Field(_autoEatType, "eatingFood");

            if (updateAutoRecast is null || eatFood is null ||
                _autoRecastRodField is null || _autoRecastStopPendingField is null ||
                _autoRecastDispatchedField is null || _autoRecastForcePowerField is null ||
                _prevRodInUseField is null || _autoAdvanceCatchAttemptsField is null ||
                _autoAdvanceCatchCooldownTicksField is null)
            {
                DisableIntegration("A required private method/field was not found. One of the two mods probably changed its internals.");
                return;
            }

            // Capture Eidee's actual mod instance without requiring Eidee to expose a public API.
            _harmony!.Patch(
                original: updateAutoRecast,
                postfix: new HarmonyMethod(typeof(ModEntry), nameof(AfterEideeUpdateAutoRecast))
            );

            // Detect the exact moment Auto-Eat is about to interrupt the rod and start eating.
            _harmony.Patch(
                original: eatFood,
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(BeforeAutoEatEatFood))
            );

            _integrationReady = true;
            Monitor.Log("Auto-Eat/Eidee bridge initialized.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            DisableIntegration($"Failed to initialize compatibility patches: {ex}");
        }
    }

    /// <summary>Patch Fast Animations so its fishing speed multiplier can be kept in sync with fishing/mastery level.</summary>
    private void TryPatchFastAnimations()
    {
        try
        {
            Type? fastAnimationsType = AccessTools.TypeByName(FastAnimationsTypeName);
            if (fastAnimationsType is null)
            {
                Monitor.Log($"Couldn't find {FastAnimationsTypeName}; fishing animation speed sync is unavailable. Install Fast Animations to use it.", LogLevel.Info);
                return;
            }

            MethodInfo? onUpdateTicked = AccessTools.Method(fastAnimationsType, "OnUpdateTicked");
            _fastAnimationsConfigField = AccessTools.Field(fastAnimationsType, "Config");
            _fastAnimationsUpdateConfigMethod = AccessTools.Method(fastAnimationsType, "UpdateConfig");

            if (onUpdateTicked is null || _fastAnimationsConfigField is null || _fastAnimationsUpdateConfigMethod is null)
            {
                Monitor.Log("Fast Animations' internals have changed; fishing animation speed sync is unavailable.", LogLevel.Warn);
                return;
            }

            _harmony!.Patch(
                original: onUpdateTicked,
                postfix: new HarmonyMethod(typeof(ModEntry), nameof(AfterFastAnimationsUpdateTicked))
            );

            _fastAnimationsIntegrationReady = true;
            Monitor.Log("Fast Animations fishing speed sync initialized.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Monitor.Log($"Failed to initialize Fast Animations fishing speed sync: {ex}", LogLevel.Warn);
        }
    }

    /// <summary>Patch TimeSpeed so its flow of time can be kept in sync with the fishing animation speed.</summary>
    private void TryPatchTimeSpeed()
    {
        try
        {
            Type? timeSpeedType = AccessTools.TypeByName(TimeSpeedTypeName);
            if (timeSpeedType is null)
            {
                Monitor.Log($"Couldn't find {TimeSpeedTypeName}; time speed sync is unavailable. Install TimeSpeed to use it.", LogLevel.Info);
                return;
            }

            MethodInfo? onUpdateTicked = AccessTools.Method(timeSpeedType, "OnUpdateTicked");
            _timeSpeedConfigField = AccessTools.Field(timeSpeedType, "Config");
            _timeSpeedUpdateSettingsMethod = AccessTools.Method(timeSpeedType, "UpdateSettingsForLocation");

            if (onUpdateTicked is null || _timeSpeedConfigField is null || _timeSpeedUpdateSettingsMethod is null)
            {
                Monitor.Log("TimeSpeed's internals have changed; time speed sync is unavailable.", LogLevel.Warn);
                return;
            }

            _harmony!.Patch(
                original: onUpdateTicked,
                postfix: new HarmonyMethod(typeof(ModEntry), nameof(AfterTimeSpeedUpdateTicked))
            );

            _timeSpeedIntegrationReady = true;
            Monitor.Log("TimeSpeed time speed sync initialized.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Monitor.Log($"Failed to initialize TimeSpeed time speed sync: {ex}", LogLevel.Warn);
        }
    }

    /// <summary>Harmony postfix for Pathoschild.Stardew.FastAnimations.ModEntry.OnUpdateTicked(...).</summary>
    private static void AfterFastAnimationsUpdateTicked(object __instance)
    {
        if (Instance is not null)
        {
            Instance._fastAnimationsInstance = __instance;
            Instance.UpdateFishingAnimationSync();
        }
    }

    /// <summary>Harmony postfix for TimeSpeed.ModEntry.OnUpdateTicked(...).</summary>
    private static void AfterTimeSpeedUpdateTicked(object __instance)
    {
        if (Instance is not null)
        {
            Instance._timeSpeedInstance = __instance;
            Instance.UpdateTimeSpeedSync();
        }
    }

    /// <summary>Harmony prefix used to snapshot the inventory before the catch lands in it.</summary>
    private static void BeforePlayerCaughtFish(FishingRod __instance, out CatchInventoryState __state)
    {
        __state = default;

        if (__instance.lastUser?.IsLocalPlayer != true || Game1.isFestival())
            return;

        string? itemId = __instance.whichFish?.QualifiedItemId;
        if (itemId is null || !ShouldAutoDestroy(itemId))
            return;

        __state = new CatchInventoryState(itemId, CountInventoryItem(itemId), ShouldDestroy: true);
    }

    /// <summary>Harmony postfix after Stardew finalizes a catch and applies perfect-catch quality upgrades.</summary>
    private static void AfterPlayerCaughtFish(FishingRod __instance, CatchInventoryState __state)
    {
        // The caught item is not in the inventory yet at this point: the game only adds it once the
        // player clicks through the hold-up pose (or into an overflow ItemGrabMenu when the
        // inventory is full). Queue the deletion and let OnUpdateTicked apply it once the item
        // actually lands somewhere.
        if (__state.ShouldDestroy && Instance is not null)
        {
            int caughtCount = Math.Max(1, __instance.numberOfFishCaught);
            Instance._pendingCatchDeletions.Add(new PendingCatchDeletion(__state.ItemId, __state.PreviousCount, caughtCount, Game1.ticks));
        }

        if (!Game1.isFestival() && __instance.lastUser?.IsLocalPlayer == true)
            Instance?.RecordFishCatch(__instance.whichFish.QualifiedItemId, __instance.fishQuality, __instance.numberOfFishCaught);
    }

    /// <summary>Whether a caught item should be destroyed: junk-category trash when enabled, or any item the user listed.</summary>
    private static bool ShouldAutoDestroy(string itemId)
    {
        if (Instance is null)
            return false;

        foreach (string listedId in Instance._config.AutoDestroyItemIds)
        {
            if (string.Equals(listedId, itemId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return Instance._config.DeleteFishingTrash && IsFishingTrash(itemId);
    }

    private static bool IsFishingTrash(string itemId)
    {
        Item? item;
        try
        {
            item = ItemRegistry.Create(itemId, 1, 0, allowNull: true);
        }
        catch
        {
            return false;
        }

        return item is StardewValley.Object caughtObject &&
            (caughtObject.Category == StardewValley.Object.junkCategory || item.QualifiedItemId == "(O)167");
    }

    private static int CountInventoryItem(string itemId)
    {
        int count = 0;
        foreach (Item? item in Game1.player.Items)
        {
            if (item?.QualifiedItemId == itemId)
                count += item.Stack;
        }

        return count;
    }

    private static void DeleteInventoryIncrease(string itemId, int previousCount)
    {
        int amountToDelete = Math.Max(0, CountInventoryItem(itemId) - previousCount);
        for (int slot = Game1.player.Items.Count - 1; slot >= 0 && amountToDelete > 0; slot--)
        {
            Item? item = Game1.player.Items[slot];
            if (item?.QualifiedItemId != itemId)
                continue;

            int removed = Math.Min(item.Stack, amountToDelete);
            item.Stack -= removed;
            amountToDelete -= removed;
            if (item.Stack <= 0)
                Game1.player.Items[slot] = null;
        }
    }

    /// <summary>
    /// Apply queued catch deletions. A caught item shows up in the inventory (or in an overflow
    /// ItemGrabMenu when the inventory is full) only after the player clicks through the hold-up
    /// pose, so this sweeps both places until the catch has been collected and destroyed.
    /// </summary>
    private void UpdatePendingCatchDeletion()
    {
        if (_pendingCatchDeletions.Count == 0)
            return;

        if (!Context.IsWorldReady)
        {
            _pendingCatchDeletions.Clear();
            return;
        }

        for (int i = _pendingCatchDeletions.Count - 1; i >= 0; i--)
        {
            PendingCatchDeletion pending = _pendingCatchDeletions[i];

            if (Game1.activeClickableMenu is ItemGrabMenu { context: FishingRod } grabMenu)
                RemoveAutoDestroyItemFromGrabMenu(grabMenu, pending.ItemId);

            // Also sweep overflow menus that are queued but haven't become active yet (treasure and
            // multi-item catches can queue an overflow menu before it's shown).
            foreach (IClickableMenu queuedMenu in Game1.nextClickableMenu)
            {
                if (queuedMenu is ItemGrabMenu { context: FishingRod } queuedGrabMenu)
                    RemoveAutoDestroyItemFromGrabMenu(queuedGrabMenu, pending.ItemId);
            }

            // Reduce the inventory down to the pre-catch count for this item, destroying whatever
            // the catch added. Once we're back at (or below) the baseline, the deletion is done.
            int currentCount = CountInventoryItem(pending.ItemId);
            if (currentCount > pending.PreviousCount)
            {
                Monitor.Log($"Auto-destroy: removing {currentCount - pending.PreviousCount}x {pending.ItemId} from inventory (catch landed).", LogLevel.Trace);
                DeleteInventoryIncrease(pending.ItemId, pending.PreviousCount);
                currentCount = CountInventoryItem(pending.ItemId);
            }

            if (currentCount <= pending.PreviousCount)
            {
                // Fully handled (or the catch never made it to inventory), so stop tracking it.
                _pendingCatchDeletions.RemoveAt(i);
            }
            else if (Game1.ticks - pending.SinceTick > CatchDeletionTimeoutTicks && !IsCatchStillHeld(pending.ItemId))
            {
                // Safety net: stop sweeping a stale entry that never resolved.
                _pendingCatchDeletions.RemoveAt(i);
            }
        }
    }

    /// <summary>Whether the player is still holding the given catch overhead (not yet collected).</summary>
    private static bool IsCatchStillHeld(string itemId)
    {
        return Game1.player.CurrentTool is FishingRod rod
            && rod.fishCaught
            && rod.whichFish?.QualifiedItemId == itemId;
    }

    /// <summary>Remove all instances of the auto-destroy item from a fishing overflow/treasure menu, closing the menu if nothing else remains.</summary>
    private static void RemoveAutoDestroyItemFromGrabMenu(ItemGrabMenu menu, string itemId)
    {
        IList<Item> items = menu.ItemsToGrabMenu.actualInventory;
        bool removedAny = false;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i]?.QualifiedItemId == itemId)
            {
                items[i] = null!;
                removedAny = true;
            }
        }

        // If the menu only held the destroyed item, skip the "place in inventory" prompt entirely.
        if (removedAny && menu.areAllItemsTaken())
        {
            menu.setEssential(false);
            menu.exitThisMenu(playSound: false);
        }
    }

    /// <summary>Open the auto-destroy items editor once the config menu that requested it has closed (and saved).</summary>
    private void OpenAutoDestroyMenuWhenReady()
    {
        if (!_autoDestroyMenuRequested)
            return;

        if (!Context.IsWorldReady)
        {
            _autoDestroyMenuRequested = false;
            return;
        }

        if (Game1.activeClickableMenu is not null)
        {
            Game1.exitActiveMenu();
            return;
        }

        Game1.activeClickableMenu = new AutoDestroyItemsMenu(_config.AutoDestroyItemIds, () => Helper.WriteConfig(_config));
        _autoDestroyMenuRequested = false;
    }

    /// <summary>Harmony postfix for EideeEasyFishing.ModEntry.UpdateAutoRecast().</summary>
    private static void AfterEideeUpdateAutoRecast(object __instance)
    {
        if (Instance is not null)
        {
            Instance._eideeInstance = __instance;
            Instance.ObserveAutoRecastState();
        }
    }

    /// <summary>Harmony prefix for AutoEat.ModEntry.EatFood(...).</summary>
    private static void BeforeAutoEatEatFood()
    {
        Instance?.RememberInterruptedAutoRecast();
    }

    private void RememberInterruptedAutoRecast()
    {
        if (!_integrationReady || !Context.IsWorldReady || _eideeInstance is null || _autoRecastRodField is null)
            return;

        Farmer player = Game1.player;
        if (player is null || !player.IsLocalPlayer)
            return;

        FishingRod? armedRod = _autoRecastRodField.GetValue(_eideeInstance) as FishingRod;
        if (armedRod is null || !ReferenceEquals(player.CurrentTool, armedRod))
            return;

        _pendingRod = armedRod;
        _pendingSinceTick = Game1.ticks;
        _readyTicks = 0;

        Monitor.Log(
            "Auto-Eat started eating during Eidee Auto Recast; preserving the recast session.",
            LogLevel.Trace
        );
    }

    // Runs before the vanilla game update, unlike UpdateTicked; suppressing here (rather than
    // after the fact) is what actually stops a held click from being read as a cast each tick.
    private void OnUpdateTicking(object? sender, UpdateTickingEventArgs e)
    {
        if (_draggingTracker)
            Helper.Input.Suppress(SButton.MouseLeft);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!_configMenuRegistered)
            RegisterConfigMenu();

        UpdatePendingCatchDeletion();
        OpenAutoDestroyMenuWhenReady();
        UpdateFishingTracker();
        UpdateTrackerDrag();
        RefreshComputedStats();

        if (!_integrationReady || _pendingRod is null)
            return;

        if (!Context.IsWorldReady || _eideeInstance is null)
        {
            ClearPending();
            return;
        }

        long age = Game1.ticks - _pendingSinceTick;
        if (age > PendingTimeoutTicks)
        {
            Monitor.Log("Timed out waiting to resume Eidee Auto Recast after Auto-Eat.", LogLevel.Warn);
            ClearPending();
            return;
        }

        Farmer player = Game1.player;
        if (!player.IsLocalPlayer)
        {
            ClearPending();
            return;
        }

        // Auto-Eat deliberately restores the selected hotbar slot. If the player has changed
        // away from this rod anyway, treat that as a manual cancellation and do nothing.
        if (!ReferenceEquals(player.CurrentTool, _pendingRod))
        {
            Monitor.Log("Not resuming Auto Recast because the player changed tools.", LogLevel.Trace);
            ClearPending();
            return;
        }

        // If Eidee recovered on its own, don't touch anything. This also makes the bridge safe if
        // a future version of either mod fixes the compatibility issue itself.
        FishingRod? currentArmedRod = _autoRecastRodField?.GetValue(_eideeInstance) as FishingRod;
        bool dispatched = ReadBool(_autoRecastDispatchedField, _eideeInstance);
        if (ReferenceEquals(currentArmedRod, _pendingRod) && !dispatched && !_pendingRod.inUse() && !IsAutoEatBusy())
        {
            ClearPending();
            return;
        }

        if (IsAutoEatBusy() || !Context.IsPlayerFree || Game1.eventUp || player.UsingTool ||
            player.freezePause > 0 || Game1.activeClickableMenu is not null || _pendingRod.inUse())
        {
            _readyTicks = 0;
            return;
        }

        // Require two clean frames so we don't fight the tail end of eatObject's animation/state reset.
        _readyTicks++;
        if (_readyTicks < ReadyTicksRequired)
            return;

        RepairEideeAutoRecast(_pendingRod);
        ClearPending();
    }

    private void UpdateFishingTracker()
    {
        if (!Context.IsWorldReady)
        {
            _trackingFishing = false;
            return;
        }

        ObserveAutoRecastState();
    }

    private void ObserveAutoRecastState()
    {
        if (!Context.IsWorldReady || Game1.player.CurrentTool is not FishingRod rod)
        {
            StopFishingTracker();
            return;
        }

        bool isFishing = IsAutoRecastArmed(rod);
        if (isFishing && !_trackingFishing)
        {
            _trackingFishing = true;
            _fishingStartedTime = Game1.timeOfDay;
            _fishingStoppedTime = 0;
            _fishingStartedRealSeconds = GetRealGameSeconds();
            _fishingStoppedRealSeconds = 0;
            _trackerHiddenSinceTick = 0;
            _fishCaughtAtSessionStart = GetTotalFishCaught();
            _fishCaughtThisSession = 0;
            _sessionGoldEarned = 0;
        }
        else if (!isFishing && _trackingFishing)
            StopFishingTracker();
    }

    private void StopFishingTracker()
    {
        if (!_trackingFishing)
            return;

        _fishCaughtThisSession = Math.Max(0, GetTotalFishCaught() - _fishCaughtAtSessionStart);
        _trackingFishing = false;
        _fishingStoppedTime = Game1.timeOfDay;
        _fishingStoppedRealSeconds = GetRealGameSeconds();
        _fishingStoppedGameMinutes = GetContinuousElapsedGameMinutes();
        _trackerHiddenSinceTick = Game1.ticks;
        _lastFishPerHour = GetCurrentFishPerHour(_fishingStoppedRealSeconds);
        _mostFishPerHour = Math.Max(_mostFishPerHour, _lastFishPerHour);
        _lastGoldPerHour = GetCurrentGoldPerHour(_fishingStoppedRealSeconds);
        _cachedFishPerHour = _lastFishPerHour;
        _cachedGoldPerHourSession = _lastGoldPerHour;
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        _fishCaughtAtDayStart = GetTotalFishCaught();
        _dayGoldEarned = 0;
        _pendingCatchDeletions.Clear();
    }

    private int GetTotalFishCaught()
    {
        int total = 0;
        foreach (KeyValuePair<string, int[]> entry in Game1.player.fishCaught.Pairs)
        {
            if (entry.Value.Length > 0)
                total += entry.Value[0];
        }

        return total;
    }

    private int GetFishCaughtToday()
    {
        return Math.Max(0, GetTotalFishCaught() - _fishCaughtAtDayStart);
    }

    // Fires once per real fish caught (not garbage), with the exact species/quality/count of that catch.
    private void RecordFishCatch(string fishId, int fishQuality, int numCaught)
    {
        if (!Context.IsWorldReady || !Game1.player.IsLocalPlayer)
            return;

        Item? fish;
        try
        {
            fish = ItemRegistry.Create(fishId, 1, fishQuality, allowNull: true);
        }
        catch
        {
            return;
        }

        if (fish is not StardewValley.Object caughtFish || caughtFish.Category != StardewValley.Object.FishCategory)
            return;

        int value = caughtFish.sellToStorePrice(Game1.player.UniqueMultiplayerID) * Math.Max(1, numCaught);

        _sessionGoldEarned += value;
        _dayGoldEarned += value;
    }

    private void RefreshComputedStats()
    {
        double now = GetRealGameSeconds();
        if (_lastStatsRefreshRealSeconds != 0 && now - _lastStatsRefreshRealSeconds < StatsRefreshIntervalSeconds)
            return;

        _lastStatsRefreshRealSeconds = now;

        if (_trackingFishing)
        {
            _cachedFishPerHour = GetCurrentFishPerHour(now);
            _cachedGoldPerHourSession = GetCurrentGoldPerHour(now);
        }
    }

    private double GetCurrentGoldPerHour(double currentRealSeconds)
    {
        double elapsedRealSeconds = currentRealSeconds - _fishingStartedRealSeconds;
        if (elapsedRealSeconds <= 0)
            return 0;

        double elapsedGameMinutes = _trackingFishing ? GetContinuousElapsedGameMinutes() : _fishingStoppedGameMinutes;
        if (elapsedGameMinutes <= 0)
            return 0;

        return _sessionGoldEarned * 60d / elapsedGameMinutes;
    }

    private bool IsAutoRecastArmed(FishingRod rod)
    {
        if (_eideeInstance is null || _autoRecastRodField is null)
            return false;

        try
        {
            return _autoRecastRodField.GetValue(_eideeInstance) is FishingRod armedRod &&
                ReferenceEquals(armedRod, rod) &&
                !ReadBool(_autoRecastStopPendingField, _eideeInstance);
        }
        catch (TargetException)
        {
            _eideeInstance = null;
            return false;
        }
    }

    private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
    {
        if (!_config.ShowTracker || !Context.IsWorldReady)
            return;

        // Visible whenever the rod is out (manual or autocast fishing), plus a short delay after autocast stops.
        bool rodOut = Game1.player.CurrentTool is FishingRod;
        if (!rodOut && Game1.ticks - _trackerHiddenSinceTick > TrackerHideDelayTicks)
            return;

        if (_trackingFishing)
            _fishCaughtThisSession = Math.Max(0, GetTotalFishCaught() - _fishCaughtAtSessionStart);

        string[] lines = BuildTrackerLines();
        UpdateTrackerSize(lines);

        int x = GetTrackerX(_trackerWidth);
        int y = GetTrackerY(_trackerHeight);
        IClickableMenu.drawTextureBox(e.SpriteBatch, Game1.menuTexture, new Rectangle(0, 256, 60, 60), x, y, _trackerWidth, _trackerHeight, Color.White);
        for (int index = 0; index < lines.Length; index++)
            e.SpriteBatch.DrawString(Game1.smallFont, lines[index], new Vector2(x + TrackerPadding, y + TrackerPadding - 4 + index * 22), Color.Black);
    }

    private string[] BuildTrackerLines()
    {
        double fishPerHour = _trackingFishing ? _cachedFishPerHour : _lastFishPerHour;
        double goldPerHour = _trackingFishing ? _cachedGoldPerHourSession : _lastGoldPerHour;
        return new[]
        {
            $"Rate (session):   {fishPerHour:0.0}/hr",
            $"Gold (session):   {goldPerHour:0}g/hr",
            $"Caught (session): {_fishCaughtThisSession}",
            $"Caught (today):   {GetFishCaughtToday()}",
            $"Last session:     {_lastFishPerHour:0.0}/hr",
            $"Best session:     {Math.Max(_mostFishPerHour, fishPerHour):0.0}/hr",
            $"Gold (today):     {_dayGoldEarned}g"
        };
    }

    // Grows the box to fit the widest line so text never renders past its right edge.
    private void UpdateTrackerSize(string[] lines)
    {
        float maxLineWidth = 0f;
        foreach (string line in lines)
            maxLineWidth = Math.Max(maxLineWidth, Game1.smallFont.MeasureString(line).X);

        _trackerWidth = Math.Max(TrackerMinWidth, (int)Math.Ceiling(maxLineWidth) + TrackerPadding * 2);
        _trackerHeight = Math.Max(TrackerMinHeight, lines.Length * 22 + TrackerPadding * 2 - 4);
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (_config.ToggleTracker.JustPressed())
        {
            _config.ShowTracker = !_config.ShowTracker;
            Helper.WriteConfig(_config);
        }

        // Only treat the click as a button press if GMCM drew the button very recently (i.e. its
        // menu is open and the button is actually on screen). A small window covers the fact that
        // input events fire during the update phase while the draw callback runs during render.
        if (e.Button == SButton.MouseLeft && Game1.ticks - _autoDestroyButtonDrawnTick <= 2 &&
            _autoDestroyButtonBounds.Contains(Game1.getMouseX(ui_scale: true), Game1.getMouseY(ui_scale: true)))
        {
            Game1.playSound("smallSelect");
            _autoDestroyMenuRequested = true;
            Helper.Input.Suppress(e.Button);
            return;
        }

        if (e.Button != SButton.MouseLeft || _draggingTracker || !_config.ShowTracker || !Context.IsWorldReady)
            return;

        bool shiftHeld = Keyboard.GetState().IsKeyDown(Keys.LeftShift) || Keyboard.GetState().IsKeyDown(Keys.RightShift);
        if (!shiftHeld)
            return;

        int mouseX = Game1.getMouseX(ui_scale: true);
        int mouseY = Game1.getMouseY(ui_scale: true);
        int x = GetTrackerX(_trackerWidth);
        int y = GetTrackerY(_trackerHeight);
        if (!new Rectangle(x, y, _trackerWidth, _trackerHeight).Contains(mouseX, mouseY))
            return;

        _draggingTracker = true;
        _dragOffset = new Point(mouseX - x, mouseY - y);

        // Stop this click from also reaching the fishing rod (which would otherwise start casting).
        Helper.Input.Suppress(e.Button);
    }

    private void UpdateTrackerDrag()
    {
        if (!_draggingTracker)
            return;

        bool shiftHeld = Keyboard.GetState().IsKeyDown(Keys.LeftShift) || Keyboard.GetState().IsKeyDown(Keys.RightShift);
        bool leftPressed = Mouse.GetState().LeftButton == ButtonState.Pressed;

        if (leftPressed && shiftHeld)
        {
            int mouseX = Game1.getMouseX(ui_scale: true);
            int mouseY = Game1.getMouseY(ui_scale: true);
            _config.TrackerX = Math.Clamp(mouseX - _dragOffset.X, 0, Math.Max(0, Game1.uiViewport.Width - _trackerWidth));
            _config.TrackerY = Math.Clamp(mouseY - _dragOffset.Y, 0, Math.Max(0, Game1.uiViewport.Height - _trackerHeight));
        }
        else
        {
            _draggingTracker = false;
            Helper.WriteConfig(_config);
        }
    }

    private int GetTrackerX(int width)
    {
        int x = _config.TrackerX < 0 ? Game1.uiViewport.Width - width - 16 : _config.TrackerX;
        return Math.Clamp(x, 0, Math.Max(0, Game1.uiViewport.Width - width));
    }

    private int GetTrackerY(int height)
    {
        return Math.Clamp(_config.TrackerY < 0 ? 150 : _config.TrackerY, 0, Math.Max(0, Game1.uiViewport.Height - height));
    }

    private static int GetElapsedGameMinutes(int startTime, int endTime)
    {
        int startMinutes = (startTime / 100) * 60 + startTime % 100;
        int endMinutes = (endTime / 100) * 60 + endTime % 100;
        return Math.Max(0, endMinutes - startMinutes);
    }

    private double GetCurrentFishPerHour(double currentRealSeconds)
    {
        double elapsedRealSeconds = currentRealSeconds - _fishingStartedRealSeconds;
        if (elapsedRealSeconds <= 0)
            return 0;

        double elapsedGameMinutes = _trackingFishing ? GetContinuousElapsedGameMinutes() : _fishingStoppedGameMinutes;
        if (elapsedGameMinutes <= 0)
            return 0;

        return _fishCaughtThisSession * 60d / elapsedGameMinutes;
    }

    private double GetContinuousElapsedGameMinutes()
    {
        int elapsedWholeMinutes = GetElapsedGameMinutes(_fishingStartedTime, Game1.timeOfDay);
        double intervalProgress = Game1.gameTimeInterval / (double)Game1.realMilliSecondsPerGameTenMinutes * 10d;
        return elapsedWholeMinutes + Math.Clamp(intervalProgress, 0, 10);
    }

    private static double GetRealGameSeconds()
    {
        return Game1.currentGameTime.TotalGameTime.TotalSeconds;
    }

    // 1x base, +1x per fishing level (0-10), +1x for each of the other four skills at level 10 (0-4),
    // +1x per mastery level (0-5). Uses raw skill levels (not buff-boosted) since this reflects
    // permanent progression. Maxes out at 20x once fishing and all other skills are level 10 and every
    // mastery has been claimed.
    private static float GetFishingAnimationMultiplier()
    {
        if (!Context.IsWorldReady)
            return 1f;

        Farmer player = Game1.player;
        int fishingLevel = Math.Clamp(player.fishingLevel.Value, 0, 10);

        int otherSkillsAtMax = 0;
        if (player.farmingLevel.Value >= 10)
            otherSkillsAtMax++;
        if (player.miningLevel.Value >= 10)
            otherSkillsAtMax++;
        if (player.foragingLevel.Value >= 10)
            otherSkillsAtMax++;
        if (player.combatLevel.Value >= 10)
            otherSkillsAtMax++;
        otherSkillsAtMax = Math.Min(otherSkillsAtMax, MaxOtherSkillsAtMax);

        int masteryLevel = Math.Clamp(MasteryTrackerMenu.getCurrentMasteryLevel(), 0, MaxMasteryLevel);

        return 1 + fishingLevel + otherSkillsAtMax + masteryLevel;
    }

    /// <summary>Keep Fast Animations' fishing speed multiplier in sync with fishing/mastery level.</summary>
    private void UpdateFishingAnimationSync()
    {
        if (!_fastAnimationsIntegrationReady || _fastAnimationsInstance is null || _fastAnimationsConfigField is null || _fastAnimationsUpdateConfigMethod is null)
            return;

        object? config = _fastAnimationsConfigField.GetValue(_fastAnimationsInstance);
        if (config is null)
            return;

        _fastAnimationsFishingSpeedProperty ??= AccessTools.Property(config.GetType(), "FishingSpeed");
        if (_fastAnimationsFishingSpeedProperty is null)
            return;

        _fastAnimationsBaselineFishingSpeed ??= (float)_fastAnimationsFishingSpeedProperty.GetValue(config)!;

        float targetSpeed = _config.SyncFishingSpeedWithLevel
            ? GetFishingAnimationMultiplier()
            : _fastAnimationsBaselineFishingSpeed.Value;

        if (_lastAppliedFishingAnimationSpeed.HasValue && Math.Abs(_lastAppliedFishingAnimationSpeed.Value - targetSpeed) < SpeedEpsilon)
            return;

        _fastAnimationsFishingSpeedProperty.SetValue(config, targetSpeed);
        _fastAnimationsUpdateConfigMethod.Invoke(_fastAnimationsInstance, null);
        _lastAppliedFishingAnimationSpeed = targetSpeed;
    }

    /// <summary>Restore Fast Animations' original fishing speed multiplier, e.g. when returning to the title screen.</summary>
    private void RestoreFastAnimationsBaseline()
    {
        if (_fastAnimationsInstance is null || _fastAnimationsConfigField is null || _fastAnimationsUpdateConfigMethod is null ||
            _fastAnimationsFishingSpeedProperty is null || _fastAnimationsBaselineFishingSpeed is null)
            return;

        object? config = _fastAnimationsConfigField.GetValue(_fastAnimationsInstance);
        if (config is null)
            return;

        _fastAnimationsFishingSpeedProperty.SetValue(config, _fastAnimationsBaselineFishingSpeed.Value);
        _fastAnimationsUpdateConfigMethod.Invoke(_fastAnimationsInstance, null);
    }

    /// <summary>Keep TimeSpeed's flow of time in sync with the current fishing animation speed while fishing.</summary>
    private void UpdateTimeSpeedSync()
    {
        if (!_timeSpeedIntegrationReady || _timeSpeedInstance is null || _timeSpeedConfigField is null || _timeSpeedUpdateSettingsMethod is null)
            return;

        // TimeSpeed lets farmhands control time too when enabled, so don't restrict this to the host.
        if (!Context.IsWorldReady)
            return;

        object? config = _timeSpeedConfigField.GetValue(_timeSpeedInstance);
        if (config is null)
            return;

        _timeSpeedSecondsPerMinuteProperty ??= AccessTools.Property(config.GetType(), "SecondsPerMinute");
        object? secondsPerMinuteConfig = _timeSpeedSecondsPerMinuteProperty?.GetValue(config);
        if (secondsPerMinuteConfig is null)
            return;

        Type spmType = secondsPerMinuteConfig.GetType();
        _timeSpeedOutdoorsProperty ??= AccessTools.Property(spmType, "Outdoors");
        _timeSpeedIndoorsProperty ??= AccessTools.Property(spmType, "Indoors");
        _timeSpeedMinesProperty ??= AccessTools.Property(spmType, "Mines");
        _timeSpeedSkullCavernProperty ??= AccessTools.Property(spmType, "SkullCavern");
        _timeSpeedVolcanoDungeonProperty ??= AccessTools.Property(spmType, "VolcanoDungeon");
        _timeSpeedByLocationNameProperty ??= AccessTools.Property(spmType, "ByLocationName");

        if (_timeSpeedOutdoorsProperty is null || _timeSpeedIndoorsProperty is null ||
            _timeSpeedMinesProperty is null || _timeSpeedSkullCavernProperty is null || _timeSpeedVolcanoDungeonProperty is null)
            return;

        if (_timeSpeedBaseline is null)
        {
            Dictionary<string, double> byLocationName = new(StringComparer.OrdinalIgnoreCase);
            if (_timeSpeedByLocationNameProperty?.GetValue(secondsPerMinuteConfig) is System.Collections.IDictionary sourceDictionary)
            {
                foreach (System.Collections.DictionaryEntry entry in sourceDictionary)
                    byLocationName[(string)entry.Key] = Convert.ToDouble(entry.Value);
            }

            _timeSpeedBaseline = new TimeSpeedBaseline(
                (double)_timeSpeedOutdoorsProperty.GetValue(secondsPerMinuteConfig)!,
                (double)_timeSpeedIndoorsProperty.GetValue(secondsPerMinuteConfig)!,
                (double)_timeSpeedMinesProperty.GetValue(secondsPerMinuteConfig)!,
                (double)_timeSpeedSkullCavernProperty.GetValue(secondsPerMinuteConfig)!,
                (double)_timeSpeedVolcanoDungeonProperty.GetValue(secondsPerMinuteConfig)!,
                byLocationName
            );
        }

        // Only slow time while a cast is actually happening: either the rod is in use (cast through
        // catch), or Eidee's Auto Recast is armed so another cast is coming. Simply holding the rod
        // shouldn't affect the flow of time.
        // In multiplayer the flow of time is shared, so scale the effect by the share of players who
        // are fishing: e.g. a 4x multiplier with one of two players fishing becomes 2x.
        double fishingShare = GetFishingPlayerShare();
        bool shouldSync = _config.SyncTimeSpeedWithFishingSpeed && fishingShare > 0;

        double divisor = 1d;
        if (shouldSync)
        {
            double multiplier = GetFishingAnimationMultiplier() * fishingShare;
            double strength = Math.Clamp(_config.TimeSpeedSyncStrengthPercent / 100d, 0, 1);
            divisor = Math.Max(1d, 1 + (multiplier - 1) * strength);
        }

        if (_lastAppliedTimeSpeedDivisor.HasValue && Math.Abs(_lastAppliedTimeSpeedDivisor.Value - divisor) < DivisorEpsilon)
            return;

        TimeSpeedBaseline baseline = _timeSpeedBaseline.Value;
        _timeSpeedOutdoorsProperty.SetValue(secondsPerMinuteConfig, baseline.Outdoors / divisor);
        _timeSpeedIndoorsProperty.SetValue(secondsPerMinuteConfig, baseline.Indoors / divisor);
        _timeSpeedMinesProperty.SetValue(secondsPerMinuteConfig, baseline.Mines / divisor);
        _timeSpeedSkullCavernProperty.SetValue(secondsPerMinuteConfig, baseline.SkullCavern / divisor);
        _timeSpeedVolcanoDungeonProperty.SetValue(secondsPerMinuteConfig, baseline.VolcanoDungeon / divisor);

        if (_timeSpeedByLocationNameProperty?.GetValue(secondsPerMinuteConfig) is System.Collections.IDictionary targetDictionary)
        {
            foreach (KeyValuePair<string, double> entry in baseline.ByLocationName)
                targetDictionary[entry.Key] = entry.Value / divisor;
        }

        _timeSpeedUpdateSettingsMethod.Invoke(_timeSpeedInstance, new object?[] { Game1.currentLocation });
        _lastAppliedTimeSpeedDivisor = divisor;
    }

    /// <summary>Get the share of online players currently casting (0 when nobody is fishing, 1 when everyone is).</summary>
    private double GetFishingPlayerShare()
    {
        int total = 0;
        int fishing = 0;

        foreach (Farmer farmer in Game1.getOnlineFarmers())
        {
            total++;
            if (IsPlayerCasting(farmer))
                fishing++;
        }

        if (total == 0)
            return 0;

        return (double)fishing / total;
    }

    /// <summary>Whether the given player is mid-cast, or has an armed auto-recast that will cast again shortly.</summary>
    private bool IsPlayerCasting(Farmer farmer)
    {
        if (farmer.CurrentTool is not FishingRod rod)
            return false;

        // Eidee's state is only readable for the local player; remote players are judged by synced net fields.
        if (farmer.IsLocalPlayer)
        {
            // While a menu is open (e.g. the "inventory full" overflow after a catch) the player
            // isn't actively fishing and vanilla would pause time here. Don't keep time racing at
            // the fishing multiplier; TimeSpeed resumes its normal speed until the menu closes.
            return Game1.activeClickableMenu is null && (rod.inUse() || IsAutoRecastArmed(rod));
        }

        return farmer.UsingTool || rod.isFishing || rod.castedButBobberStillInAir || rod.isCasting || rod.isReeling;
    }

    /// <summary>Restore TimeSpeed's original seconds-per-minute settings, e.g. when returning to the title screen.</summary>
    private void RestoreTimeSpeedBaseline()
    {
        if (_timeSpeedInstance is null || _timeSpeedConfigField is null || _timeSpeedUpdateSettingsMethod is null || _timeSpeedBaseline is null ||
            _timeSpeedOutdoorsProperty is null || _timeSpeedIndoorsProperty is null ||
            _timeSpeedMinesProperty is null || _timeSpeedSkullCavernProperty is null || _timeSpeedVolcanoDungeonProperty is null)
            return;

        object? config = _timeSpeedConfigField.GetValue(_timeSpeedInstance);
        object? secondsPerMinuteConfig = _timeSpeedSecondsPerMinuteProperty?.GetValue(config);
        if (secondsPerMinuteConfig is null)
            return;

        TimeSpeedBaseline baseline = _timeSpeedBaseline.Value;
        _timeSpeedOutdoorsProperty.SetValue(secondsPerMinuteConfig, baseline.Outdoors);
        _timeSpeedIndoorsProperty.SetValue(secondsPerMinuteConfig, baseline.Indoors);
        _timeSpeedMinesProperty.SetValue(secondsPerMinuteConfig, baseline.Mines);
        _timeSpeedSkullCavernProperty.SetValue(secondsPerMinuteConfig, baseline.SkullCavern);
        _timeSpeedVolcanoDungeonProperty.SetValue(secondsPerMinuteConfig, baseline.VolcanoDungeon);

        if (_timeSpeedByLocationNameProperty?.GetValue(secondsPerMinuteConfig) is System.Collections.IDictionary targetDictionary)
        {
            foreach (KeyValuePair<string, double> entry in baseline.ByLocationName)
                targetDictionary[entry.Key] = entry.Value;
        }

        if (Context.IsWorldReady)
            _timeSpeedUpdateSettingsMethod.Invoke(_timeSpeedInstance, new object?[] { Game1.currentLocation });
    }

    /// <summary>The height reserved for the "Manage auto-destroy items" button row in GMCM.</summary>
    private const int AutoDestroyButtonHeight = 56;

    /// <summary>Draw the "Manage auto-destroy items" button inside GMCM and record its bounds for click detection.</summary>
    private void DrawAutoDestroyButton(SpriteBatch b, Vector2 position)
    {
        const string label = "Manage auto-destroy items...";
        int textWidth = (int)Game1.smallFont.MeasureString(label).X;
        int buttonWidth = textWidth + 48;
        int buttonHeight = AutoDestroyButtonHeight - 8;
        int x = (int)position.X;
        int y = (int)position.Y + 4;

        _autoDestroyButtonBounds = new Rectangle(x, y, buttonWidth, buttonHeight);
        _autoDestroyButtonDrawnTick = Game1.ticks;

        bool hovered = _autoDestroyButtonBounds.Contains(Game1.getMouseX(ui_scale: true), Game1.getMouseY(ui_scale: true));
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), x, y, buttonWidth, buttonHeight, hovered ? Color.Wheat : Color.White);
        b.DrawString(Game1.smallFont, label, new Vector2(x + 24, y + (buttonHeight - Game1.smallFont.LineSpacing) / 2), Game1.textColor);
    }

    private void RegisterConfigMenu()
    {
        if (_configMenuRegistered)
            return;

        IGenericModConfigMenuApi? api = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>(GenericModConfigMenuId);
        if (api is null)
            return;

        api.Register(
            ModManifest,
            reset: () => _config = new ModConfig(),
            save: () => Helper.WriteConfig(_config)
        );
        api.AddKeybindList(
            ModManifest,
            getValue: () => _config.ToggleTracker,
            setValue: value => _config.ToggleTracker = value,
            name: () => "Toggle tracker"
        );
        api.AddBoolOption(
            ModManifest,
            getValue: () => _config.ShowTracker,
            setValue: value => _config.ShowTracker = value,
            name: () => "Show tracker"
        );
        api.AddBoolOption(
            ModManifest,
            getValue: () => _config.DeleteFishingTrash,
            setValue: value => _config.DeleteFishingTrash = value,
            name: () => "Automatically delete fishing trash",
            tooltip: () => "Delete trash caught while fishing instead of keeping it in your inventory. Algae and seaweed are preserved."
        );
        // A real button: the draw callback renders it and records its on-screen bounds, and
        // OnButtonPressed opens the editor when those bounds are clicked.
        api.AddComplexOption(
            ModManifest,
            name: () => "Auto-destroy items",
            tooltip: () => "Extra items that are destroyed automatically when caught while fishing. Click to manage the list.",
            draw: DrawAutoDestroyButton,
            height: () => AutoDestroyButtonHeight
        );
        api.AddBoolOption(
            ModManifest,
            getValue: () => _config.SyncFishingSpeedWithLevel,
            setValue: value => _config.SyncFishingSpeedWithLevel = value,
            name: () => "Sync fishing animation speed with level/mastery",
            tooltip: () => "Requires Fast Animations. Sets its fishing animation speed multiplier from your fishing level (1x-10x), plus 1x for each of the other four skills at level 10 (up to +4x), plus 1x per mastery level (up to +5x), for a maximum of 20x."
        );
        api.AddBoolOption(
            ModManifest,
            getValue: () => _config.SyncTimeSpeedWithFishingSpeed,
            setValue: value => _config.SyncTimeSpeedWithFishingSpeed = value,
            name: () => "Sync time speed with fishing animation",
            tooltip: () => "Requires TimeSpeed. While you're actually casting (or autocast is running), divides TimeSpeed's seconds-per-minute settings by the current fishing animation speed multiplier, so faster fishing doesn't also grant extra time in the day. In multiplayer the effect is scaled by how many players are fishing (e.g. 4x with 1 of 2 players fishing acts as 2x)."
        );
        api.AddNumberOption(
            ModManifest,
            getValue: () => _config.TimeSpeedSyncStrengthPercent,
            setValue: value => _config.TimeSpeedSyncStrengthPercent = value,
            name: () => "Time speed sync strength",
            tooltip: () => "How closely TimeSpeed's flow of time follows the fishing animation speed. 100% fully ties them together (e.g. 2x animation speed halves seconds-per-minute); 0% leaves TimeSpeed unaffected.",
            min: 0,
            max: 100,
            interval: 5
        );
        _configMenuRegistered = true;
    }

    private void RepairEideeAutoRecast(FishingRod rod)
    {
        if (_eideeInstance is null || _autoRecastRodField is null ||
            _autoRecastStopPendingField is null || _autoRecastDispatchedField is null ||
            _autoRecastForcePowerField is null || _prevRodInUseField is null ||
            _autoAdvanceCatchAttemptsField is null || _autoAdvanceCatchCooldownTicksField is null)
            return;

        // Re-arm the existing Eidee session rather than issuing our own cast. On Eidee's next
        // UpdateAutoRecast call it will run all of its normal checks and call BeginUsingTool itself.
        _autoRecastRodField.SetValue(_eideeInstance, rod);
        _autoRecastStopPendingField.SetValue(_eideeInstance, false);
        _autoRecastDispatchedField.SetValue(_eideeInstance, false);
        _autoRecastForcePowerField.SetValue(_eideeInstance, false);
        _prevRodInUseField.SetValue(_eideeInstance, false);
        _autoAdvanceCatchAttemptsField.SetValue(_eideeInstance, 0);
        _autoAdvanceCatchCooldownTicksField.SetValue(_eideeInstance, 0);

        // This field exists in Eidee 1.4.0, but it isn't essential enough to disable the bridge
        // if a later compatible version renames/removes it.
        _castOwnedByAutoRecastField?.SetValue(_eideeInstance, false);

        Monitor.Log("Re-armed Eidee Auto Recast after Auto-Eat finished eating.", LogLevel.Debug);
    }

    private bool IsAutoEatBusy()
    {
        if (Game1.player.isEating)
            return true;

        if (_autoEatEatingFoodField?.GetValue(null) is bool eatingFood && eatingFood)
            return true;

        return false;
    }

    private static bool ReadBool(FieldInfo? field, object instance)
    {
        return field?.GetValue(instance) is bool value && value;
    }

    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        if (e.IsLocalPlayer)
            ClearPending();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        _eideeInstance = null;
        ClearPending();
        _pendingCatchDeletions.Clear();
        _autoDestroyMenuRequested = false;
        _trackingFishing = false;
        _fishingStartedTime = 0;
        _fishingStoppedTime = 0;
        _fishingStoppedGameMinutes = 0;
        _fishingStartedRealSeconds = 0;
        _fishingStoppedRealSeconds = 0;
        _trackerHiddenSinceTick = 0;
        _fishCaughtThisSession = 0;
        _lastFishPerHour = 0;
        _mostFishPerHour = 0;
        _fishCaughtAtDayStart = 0;
        _lastStatsRefreshRealSeconds = 0;
        _cachedFishPerHour = 0;
        _cachedGoldPerHourSession = 0;
        _lastGoldPerHour = 0;
        _sessionGoldEarned = 0;
        _dayGoldEarned = 0;

        RestoreFastAnimationsBaseline();
        RestoreTimeSpeedBaseline();
        _fastAnimationsBaselineFishingSpeed = null;
        _lastAppliedFishingAnimationSpeed = null;
        _timeSpeedBaseline = null;
        _lastAppliedTimeSpeedDivisor = null;
    }

    private void ClearPending()
    {
        _pendingRod = null;
        _pendingSinceTick = 0;
        _readyTicks = 0;
    }

    private void DisableIntegration(string reason)
    {
        _integrationReady = false;
        Monitor.Log($"Auto-Eat/Eidee bridge disabled: {reason}", LogLevel.Error);
    }
}

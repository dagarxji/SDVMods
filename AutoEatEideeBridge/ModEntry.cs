using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
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
    private readonly record struct CatchInventoryState(string ItemId, int PreviousCount, bool IsTrash);

    private const string EideeTypeName = "EideeEasyFishing.ModEntry";
    private const string AutoEatTypeName = "AutoEat.ModEntry";
    private const string FastAnimationsTypeName = "Pathoschild.Stardew.FastAnimations.ModEntry";
    private const string TimeSpeedTypeName = "TimeSpeed.ModEntry";

    // Don't leave a stale pending resume around indefinitely if another mod changes state.
    private const long PendingTimeoutTicks = 600; // ~10 seconds at Stardew's normal 60 ticks/sec.
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

    /// <summary>Harmony prefix used to distinguish the newly caught stack from trash already carried.</summary>
    private static void BeforePlayerCaughtFish(FishingRod __instance, out CatchInventoryState __state)
    {
        string itemId = __instance.whichFish.QualifiedItemId;
        bool isTrash = Instance?._config.DeleteFishingTrash == true && IsFishingTrash(itemId);
        int previousCount = isTrash ? CountInventoryItem(itemId) : 0;
        __state = new CatchInventoryState(itemId, previousCount, isTrash);
    }

    /// <summary>Harmony postfix after Stardew finalizes a catch and applies perfect-catch quality upgrades.</summary>
    private static void AfterPlayerCaughtFish(FishingRod __instance, CatchInventoryState __state)
    {
        if (__state.IsTrash)
            DeleteInventoryIncrease(__state.ItemId, __state.PreviousCount);

        if (!Game1.isFestival())
            Instance?.RecordFishCatch(__instance.whichFish.QualifiedItemId, __instance.fishQuality, __instance.numberOfFishCaught);
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

        // Only the host actually drives the flow of time; touching TimeSpeed's config elsewhere would have no effect.
        if (!Context.IsWorldReady || !Context.IsMainPlayer)
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

        bool isFishing = Game1.player.CurrentTool is FishingRod;
        bool shouldSync = _config.SyncTimeSpeedWithFishingSpeed && isFishing;

        double divisor = 1d;
        if (shouldSync)
        {
            float multiplier = GetFishingAnimationMultiplier();
            double strength = Math.Clamp(_config.TimeSpeedSyncStrengthPercent / 100d, 0, 1);
            divisor = 1 + (multiplier - 1) * strength;
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
            tooltip: () => "Requires TimeSpeed. While you have a fishing rod out, divides TimeSpeed's seconds-per-minute settings by the current fishing animation speed multiplier, so faster fishing doesn't also grant extra time in the day."
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

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
using StardewValley.GameData.Objects;
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
/// It also tracks fishing rate/gold stats and enforces an optional daily fish catch
/// cap: once reached, it stops re-arming Auto Recast after Auto-Eat interrupts it.
/// </summary>
internal sealed class ModEntry : Mod
{
    private readonly record struct CatchInventoryState(string ItemId, int PreviousCount, bool IsTrash);

    private const string EideeTypeName = "EideeEasyFishing.ModEntry";
    private const string AutoEatTypeName = "AutoEat.ModEntry";

    // Don't leave a stale pending resume around indefinitely if another mod changes state.
    private const long PendingTimeoutTicks = 600; // ~10 seconds at Stardew's normal 60 ticks/sec.
    private const int ReadyTicksRequired = 2;
    private const int TrackerHideDelayTicks = 300;
    private const int TrackerMinWidth = 300;
    private const int TrackerMinHeight = 196;
    private const int TrackerPadding = 22;
    private const double StatsRefreshIntervalSeconds = 5d;
    private const string GenericModConfigMenuId = "spacechase0.GenericModConfigMenu";
    private const string CapRemovalItemId = "Local.AutoEatEideeBridge_AnglersSeal";
    private const string QualifiedCapRemovalItemId = "(O)" + CapRemovalItemId;
    private const string CapRemovedModDataKey = "Local.AutoEatEideeBridge/DailyCapRemoved";
    private const double CapRemovalItemDropChance = 0.001d;

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
    private bool _dailyCapNotified;
    private double _lastStatsRefreshRealSeconds;
    private double _cachedFishPerHour;
    private double _cachedGoldPerHourSession;
    private double _lastGoldPerHour;
    private int _sessionGoldEarned;
    private int _dayGoldEarned;

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
        helper.Events.Content.AssetRequested += OnAssetRequested;
    }

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (!e.NameWithoutLocale.IsEquivalentTo("Data/Objects"))
            return;

        e.Edit(asset =>
        {
            asset.AsDictionary<string, ObjectData>().Data[CapRemovalItemId] = new ObjectData
            {
                Name = "Angler's Seal",
                DisplayName = "Angler's Seal",
                Description = "A rare seal earned through mastery of fishing. Use it to remove the daily fish catch cap.",
                Type = "Basic",
                Category = StardewValley.Object.junkCategory,
                Price = 0,
                Edibility = -300,
                Texture = "Maps/springobjects",
                SpriteIndex = 74
            };
        });
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
            _harmony.Patch(
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

        int dailyFishCatchCap = GetDailyFishCatchCap();
        if (!IsDailyCapRemoved() && _config.DailyFishCatchCap > 0 && GetFishCaughtToday() >= dailyFishCatchCap)
        {
            if (!_dailyCapNotified)
            {
                Monitor.Log($"Daily fish catch cap ({dailyFishCatchCap}) reached; leaving Auto Recast paused after eating instead of resuming it.", LogLevel.Info);
                _dailyCapNotified = true;
            }

            ClearPending();
            return;
        }

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
        _dailyCapNotified = false;
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

    private int GetDailyFishCatchCap()
    {
        int fishingLevel = Math.Clamp(Game1.player.FishingLevel, 0, 10);
        return _config.DailyFishCatchCap / 10 * fishingLevel;
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

        TryDropCapRemovalItem(numCaught);

        int value = caughtFish.sellToStorePrice(Game1.player.UniqueMultiplayerID) * Math.Max(1, numCaught);

        _sessionGoldEarned += value;
        _dayGoldEarned += value;
    }

    private void TryDropCapRemovalItem(int numCaught)
    {
        if (IsDailyCapRemoved() || Game1.player.fishingLevel.Value < 10 || PlayerHasCapRemovalItem())
            return;

        bool dropped = false;
        for (int fishIndex = 0; fishIndex < Math.Max(1, numCaught); fishIndex++)
        {
            if (Game1.random.NextDouble() < CapRemovalItemDropChance)
            {
                dropped = true;
                break;
            }
        }

        if (!dropped)
            return;

        Item seal = ItemRegistry.Create(QualifiedCapRemovalItemId);
        bool addedToInventory = Game1.player.addItemToInventoryBool(seal);
        if (!addedToInventory)
            Game1.createItemDebris(seal, Game1.player.getStandingPosition(), -1, Game1.currentLocation);

        Game1.playSound("discoverMineral");
        string message = addedToInventory
            ? "You found an Angler's Seal!"
            : "An Angler's Seal dropped at your feet!";
        Game1.addHUDMessage(new HUDMessage(message, HUDMessage.newQuest_type));
    }

    private static bool PlayerHasCapRemovalItem()
    {
        foreach (Item? item in Game1.player.Items)
        {
            if (item?.QualifiedItemId == QualifiedCapRemovalItemId)
                return true;
        }

        return false;
    }

    private static bool IsDailyCapRemoved()
    {
        return Game1.player.modData.TryGetValue(CapRemovedModDataKey, out string? value) && value == "true";
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
        if (TryUseCapRemovalItem(e.Button))
            return;

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

    private bool TryUseCapRemovalItem(SButton button)
    {
        if (!Context.IsWorldReady || !Context.IsPlayerFree ||
            (!button.IsUseToolButton() && !button.IsActionButton()) ||
            Game1.player.ActiveObject?.QualifiedItemId != QualifiedCapRemovalItemId)
        {
            return false;
        }

        Game1.player.modData[CapRemovedModDataKey] = "true";
        Game1.player.reduceActiveItemByOne();
        Helper.Input.Suppress(button);
        Game1.playSound("reward");
        Game1.addHUDMessage(new HUDMessage("The daily fish catch cap has been permanently removed.", HUDMessage.newQuest_type));
        Monitor.Log("The player used an Angler's Seal; the daily fish catch cap is now permanently removed.", LogLevel.Info);
        return true;
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
        api.AddNumberOption(
            ModManifest,
            getValue: () => _config.DailyFishCatchCap,
            setValue: value => _config.DailyFishCatchCap = value,
            name: () => "Maximum daily fish catch cap",
            tooltip: () => "The daily cap at fishing level 10. Lower levels receive one tenth of this amount per level. Once reached, the bridge stops re-arming Auto Recast after Auto-Eat interrupts it. Set to 0 to disable the cap.",
            min: 0,
            max: 2000,
            interval: 10
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
        _dailyCapNotified = false;
        _lastStatsRefreshRealSeconds = 0;
        _cachedFishPerHour = 0;
        _cachedGoldPerHourSession = 0;
        _lastGoldPerHour = 0;
        _sessionGoldEarned = 0;
        _dayGoldEarned = 0;
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

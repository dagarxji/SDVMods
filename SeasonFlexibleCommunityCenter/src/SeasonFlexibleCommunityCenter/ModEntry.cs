using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SeasonFlexibleCommunityCenter.Framework;
using SeasonFlexibleCommunityCenter.Menus;
using SeasonFlexibleCommunityCenter.Models;
using SeasonFlexibleCommunityCenter.Services;
using SeasonFlexibleCommunityCenter.Utilities;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;

namespace SeasonFlexibleCommunityCenter;

public sealed class ModEntry : Mod
{
    private const string SaveDataKey = "settings";
    private const string MsgSettingsRequest = "SettingsRequest";
    private const string MsgSettingsSync = "SettingsSync";

    private ModConfig Config = new();
    private SeasonCatalog Catalog = null!;
    private SubstitutionEngine Engine = null!;
    private bool PendingFirstSetup;
    private SaveSettings? PendingNewFarmSettings;
    private bool JunimoReflectionFailureLogged;

    internal SaveSettings Settings { get; private set; } = new();

    public override void Entry(IModHelper helper)
    {
        Config = helper.ReadConfig<ModConfig>();
        Config.DefaultSettings ??= new SaveSettings();
        Config.DefaultSettings.Validate();

        Catalog = new SeasonCatalog(helper, Monitor);
        Engine = new SubstitutionEngine(Catalog, () => Settings, Monitor);

        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.Display.RenderedActiveMenu += OnRenderedActiveMenu;
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.Multiplayer.PeerConnected += OnPeerConnected;
        helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;

        helper.ConsoleCommands.Add("sfc_setup", "Open this farm's Season-Flexible Community Center setup screen.", OnSetupCommand);
        helper.ConsoleCommands.Add("sfc_rebuild_catalog", "Re-scan seasonal item data after adding or changing content packs.", OnRebuildCatalogCommand);
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        IGenericModConfigMenuApi? gmcm = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (gmcm is null)
            return;

        gmcm.Register(ModManifest, ResetEditableSettings, SaveEditableSettings);
        gmcm.AddParagraph(ModManifest, () =>
            "These values are defaults on the title screen. While a farm is loaded, they edit that farm's settings. " +
            "In multiplayer, the host's per-farm settings are authoritative.");

        gmcm.AddSectionTitle(ModManifest, () => "Difficulty");
        gmcm.AddNumberOption(ModManifest,
            () => EditableSettings.SeasonPenaltyPercent,
            value => MutateEditable(s => s.SeasonPenaltyPercent = value),
            () => "Season penalty",
            () => "Multiplier applied once for each season the requirement is ahead. 200% means ×2 per season: one season ×2, two seasons ×4, three seasons ×8.",
            100, 400, 25,
            value => $"×{value / 100d:0.##} / season");

        gmcm.AddNumberOption(ModManifest,
            () => EditableSettings.ValueScalingPercent,
            value => MutateEditable(s => s.ValueScalingPercent = value),
            () => "Value scaling",
            () => "How strongly the sell-value difference between the original requirement and substitute changes the quantity. 0% ignores value; 100% uses the full ratio.",
            0, 100, 5,
            value => $"{value}%");

        gmcm.AddNumberOption(ModManifest,
            () => EditableSettings.QualityCreditPercent,
            value => MutateEditable(s => s.QualityCreditPercent = value),
            () => "Quality credit",
            () => "How much silver/gold/iridium quality can reduce the number of substitute items needed.",
            0, 100, 5,
            value => $"{value}%");

        gmcm.AddNumberOption(ModManifest,
            () => EditableSettings.MinimumQuantity,
            value => MutateEditable(s => s.MinimumQuantity = value),
            () => "Minimum exchange quantity",
            () => "Lower clamp after all scaling.",
            1, 99, 1);

        gmcm.AddNumberOption(ModManifest,
            () => EditableSettings.MaximumQuantity,
            value => MutateEditable(s => s.MaximumQuantity = value),
            () => "Maximum exchange quantity",
            () => "Upper clamp after all scaling.",
            1, 999, 1);

        gmcm.AddSectionTitle(ModManifest, () => "Categories");
        gmcm.AddBoolOption(ModManifest, () => EditableSettings.EnableCrops,
            value => MutateEditable(s => s.EnableCrops = value), () => "Crop substitutions");
        gmcm.AddBoolOption(ModManifest, () => EditableSettings.EnableFish,
            value => MutateEditable(s => s.EnableFish = value), () => "Fish substitutions");
        gmcm.AddBoolOption(ModManifest, () => EditableSettings.EnableForage,
            value => MutateEditable(s => s.EnableForage = value), () => "Forage substitutions");
        gmcm.AddBoolOption(ModManifest, () => EditableSettings.EnableFruit,
            value => MutateEditable(s => s.EnableFruit = value), () => "Fruit-tree substitutions");
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        Catalog.Rebuild();
        PendingFirstSetup = false;
        JunimoReflectionFailureLogged = false;

        if (Context.IsMultiplayer && !Context.IsMainPlayer)
        {
            Settings = Config.DefaultSettings.Clone();
            Settings.SetupComplete = true;
            RequestHostSettings();
            return;
        }

        SaveSettings? saved = Helper.Data.ReadSaveData<SaveSettings>(SaveDataKey);
        if (saved is not null)
        {
            saved.Validate();
            Settings = saved;
            PendingFirstSetup = !saved.SetupComplete;
            return;
        }

        bool brandNewFarm = Game1.year == 1
            && Game1.dayOfMonth == 1
            && string.Equals(Game1.currentSeason, "spring", StringComparison.OrdinalIgnoreCase);

        if (brandNewFarm && PendingNewFarmSettings is not null)
        {
            Settings = PendingNewFarmSettings.Clone();
            Settings.SetupComplete = true;
            PendingNewFarmSettings = null;
            SaveSettingsForFarm();
            BroadcastSettings();
            return;
        }

        Settings = Config.DefaultSettings.Clone();
        Settings.SetupComplete = !brandNewFarm;
        PendingFirstSetup = brandNewFarm;
        if (!brandNewFarm)
            SaveSettingsForFarm();
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        // Content Patcher may invalidate seasonal assets as conditions change. Re-reading once per day
        // is cheap and keeps custom/expansion content accurate without requiring hard dependencies.
        Catalog.Rebuild();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!PendingFirstSetup || !Context.IsWorldReady || !Context.IsMainPlayer)
            return;
        if (Game1.activeClickableMenu is not null || Game1.eventUp || Game1.currentLocation is null)
            return;

        PendingFirstSetup = false;
        Game1.activeClickableMenu = new NewSaveSetupMenu(this, Settings, isFirstSetup: true);
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        PendingFirstSetup = false;
        PendingNewFarmSettings = null;
        JunimoReflectionFailureLogged = false;
        Settings = Config.DefaultSettings.Clone();
    }

    private void OnRenderedActiveMenu(object? sender, RenderedActiveMenuEventArgs e)
    {
        if (TryGetNewGameCustomization(out CharacterCustomization? customization))
        {
            Rectangle creationBounds = GetCreationSettingsButtonBounds(customization);
            Ui.DrawBox(e.SpriteBatch, creationBounds, Color.White, false);
            Ui.DrawCentered(e.SpriteBatch, "Season Exchange Settings", Game1.smallFont, creationBounds, Game1.textColor);
            customization.drawMouse(e.SpriteBatch);
            return;
        }

        if (!TryGetExchangeContext(out JunimoNoteMenu? menu, out Bundle? bundle))
            return;

        Rectangle bounds = GetExchangeButtonBounds(menu);
        Ui.DrawBox(e.SpriteBatch, bounds, Color.White, false);
        Ui.DrawCentered(e.SpriteBatch, "Season Exchange", Game1.smallFont, bounds, Game1.textColor);

        // RenderedActiveMenu fires after the vanilla menu has already drawn its cursor.
        menu.drawMouse(e.SpriteBatch);
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (e.Button != SButton.MouseLeft)
            return;

        Vector2 cursorPixels = e.Cursor.ScreenPixels;
        Point cursor = new((int)cursorPixels.X, (int)cursorPixels.Y);
        if (TryGetNewGameCustomization(out CharacterCustomization? customization))
        {
            Rectangle creationBounds = GetCreationSettingsButtonBounds(customization);
            if (!creationBounds.Contains(cursor))
                return;

            Helper.Input.Suppress(e.Button);
            Game1.playSound("smallSelect");
            SaveSettings initial = (PendingNewFarmSettings ?? Config.DefaultSettings).Clone();
            Game1.activeClickableMenu = new NewSaveSetupMenu(
                this,
                initial,
                isFirstSetup: true,
                returnMenu: customization,
                saveHandler: ApplyNewFarmCreationSettings
            );
            return;
        }

        if (!TryGetExchangeContext(out JunimoNoteMenu? menu, out Bundle? bundle))
            return;

        Rectangle bounds = GetExchangeButtonBounds(menu);
        if (!bounds.Contains(cursor))
            return;

        Helper.Input.Suppress(e.Button);
        Game1.playSound("smallSelect");
        Game1.activeClickableMenu = new SubstitutionMenu(this, menu, bundle, Engine);
    }

    private static bool TryGetNewGameCustomization(out CharacterCustomization menu)
    {
        menu = null!;
        if (Context.IsWorldReady || Game1.activeClickableMenu is not CharacterCustomization customization)
            return false;
        if (customization.source is not CharacterCustomization.Source.NewGame
            and not CharacterCustomization.Source.HostNewFarm)
            return false;

        menu = customization;
        return true;
    }

    private static Rectangle GetCreationSettingsButtonBounds(CharacterCustomization menu)
    {
        const int buttonWidth = 220;
        const int buttonHeight = 44;
        const int edgePadding = 16;

        // CharacterCustomization is taller than the visible UI viewport at common resolutions,
        // so its nominal top edge can be off-screen. Anchor beside the vanilla Advanced Options
        // control near the bottom instead, where Stardew already reserves visible space.
        Rectangle? advanced = menu.advancedOptionsButton?.bounds;
        int y = advanced?.Y - buttonHeight - 8 ?? menu.yPositionOnScreen + menu.height - buttonHeight - 100;
        int x = Math.Max(edgePadding, menu.xPositionOnScreen - buttonWidth - 12);
        y = Math.Clamp(y, edgePadding, Math.Max(edgePadding, Game1.uiViewport.Height - buttonHeight - edgePadding));

        return new Rectangle(x, y, buttonWidth, buttonHeight);
    }

    private bool TryGetExchangeContext(out JunimoNoteMenu menu, out Bundle bundle)
    {
        menu = null!;
        bundle = null!;

        if (!Context.IsWorldReady || Game1.currentLocation is not CommunityCenter || Game1.activeClickableMenu is not JunimoNoteMenu note)
            return false;

        if (!TryReadJunimoNoteState(note, out bool specificBundlePage, out Bundle? current, out Item? heldItem, out Item? partialDonationItem, out int whichArea))
            return false;
        if (!specificBundlePage || current is null)
            return false;
        if (current.complete || !current.depositsAllowed)
            return false; // remote bundle menu isn't a donation location.
        if (heldItem is not null || partialDonationItem is not null)
            return false;
        if (whichArea == 4)
            return false; // Vault is money-based, not seasonal.
        if (!Engine.HasFutureTarget(current))
            return false;

        menu = note;
        bundle = current;
        return true;
    }

    private static Rectangle GetExchangeButtonBounds(JunimoNoteMenu menu)
    {
        int buttonWidth = 240;
        int buttonHeight = 44;
        return new Rectangle(
            menu.xPositionOnScreen + menu.width - buttonWidth - 42,
            menu.yPositionOnScreen + menu.height - buttonHeight - 24,
            buttonWidth,
            buttonHeight
        );
    }

    internal void RefreshBundleMenu(JunimoNoteMenu previous, int bundleIndex, bool bundleCompleted)
    {
        CommunityCenter? cc = Game1.getLocationFromName("CommunityCenter") as CommunityCenter;
        if (cc is null)
        {
            Game1.activeClickableMenu = null;
            return;
        }

        // Reconstructing the vanilla area page forces Bundle objects to be rebuilt from the synchronized
        // Community Center NetFields. This avoids mutating readonly BundleIngredientDescription fields and
        // remains resilient across game patches/custom bundle layouts.
        if (!TryGetJunimoNoteArea(previous, out int whichArea))
        {
            Game1.activeClickableMenu = null;
            return;
        }

        Game1.activeClickableMenu = new JunimoNoteMenu(whichArea, cc.bundlesDict());
        Game1.addHUDMessage(new HUDMessage(bundleCompleted ? "Bundle completed through Season Exchange!" : "Season Exchange accepted.", HUDMessage.newQuest_type));
    }

    private bool TryReadJunimoNoteState(
        JunimoNoteMenu menu,
        out bool specificBundlePage,
        out Bundle? currentBundle,
        out Item? heldItem,
        out Item? partialDonationItem,
        out int whichArea
    )
    {
        specificBundlePage = false;
        currentBundle = null;
        heldItem = null;
        partialDonationItem = null;
        whichArea = -1;

        try
        {
            // These menu-state fields have changed visibility across Stardew builds. SMAPI's
            // reflection helper keeps access centralized and avoids a Harmony dependency.
            specificBundlePage = Helper.Reflection.GetField<bool>(menu, "specificBundlePage").GetValue();
            currentBundle = Helper.Reflection.GetField<Bundle>(menu, "currentPageBundle").GetValue();
            heldItem = Helper.Reflection.GetField<Item>(menu, "heldItem").GetValue();
            partialDonationItem = Helper.Reflection.GetField<Item>(menu, "partialDonationItem").GetValue();
            whichArea = Helper.Reflection.GetField<int>(menu, "whichArea").GetValue();
            return true;
        }
        catch (Exception ex)
        {
            if (!JunimoReflectionFailureLogged)
            {
                JunimoReflectionFailureLogged = true;
                Monitor.Log($"Couldn't read Junimo Note menu state; Season Exchange UI is disabled for this session. {ex.Message}", LogLevel.Error);
            }
            return false;
        }
    }

    private bool TryGetJunimoNoteArea(JunimoNoteMenu menu, out int whichArea)
    {
        whichArea = -1;
        try
        {
            whichArea = Helper.Reflection.GetField<int>(menu, "whichArea").GetValue();
            return whichArea >= 0;
        }
        catch (Exception ex)
        {
            if (!JunimoReflectionFailureLogged)
            {
                JunimoReflectionFailureLogged = true;
                Monitor.Log($"Couldn't read Junimo Note area. {ex.Message}", LogLevel.Error);
            }
            return false;
        }
    }

    internal void ApplySaveSettings(SaveSettings settings)
    {
        settings.Validate();
        settings.SetupComplete = true;
        Settings = settings.Clone();
        PendingFirstSetup = false;
        SaveSettingsForFarm();
        BroadcastSettings();
    }

    private void ApplyNewFarmCreationSettings(SaveSettings settings)
    {
        settings.Validate();
        settings.SetupComplete = true;
        PendingNewFarmSettings = settings.Clone();
    }

    private SaveSettings EditableSettings => Context.IsWorldReady ? Settings : Config.DefaultSettings;

    private void MutateEditable(Action<SaveSettings> mutate)
    {
        if (Context.IsWorldReady && Context.IsMultiplayer && !Context.IsMainPlayer)
            return;

        mutate(EditableSettings);
        EditableSettings.Validate();
    }

    private void ResetEditableSettings()
    {
        if (Context.IsWorldReady)
        {
            if (Context.IsMultiplayer && !Context.IsMainPlayer)
                return;
            Settings = Config.DefaultSettings.Clone();
            Settings.SetupComplete = true;
        }
        else
        {
            Config = new ModConfig();
        }
    }

    private void SaveEditableSettings()
    {
        if (Context.IsWorldReady)
        {
            if (Context.IsMultiplayer && !Context.IsMainPlayer)
                return;
            Settings.Validate();
            Settings.SetupComplete = true;
            SaveSettingsForFarm();
            BroadcastSettings();
        }
        else
        {
            Config.DefaultSettings.Validate();
            Helper.WriteConfig(Config);
        }
    }

    private void SaveSettingsForFarm()
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer)
            return;
        Helper.Data.WriteSaveData(SaveDataKey, Settings);
    }

    private void OnPeerConnected(object? sender, PeerConnectedEventArgs e)
    {
        if (Context.IsMainPlayer && Context.IsWorldReady)
            SendSettingsTo(e.Peer.PlayerID);
    }

    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        if (!string.Equals(e.FromModID, ModManifest.UniqueID, StringComparison.Ordinal))
            return;

        if (e.Type == MsgSettingsRequest && Context.IsMainPlayer)
        {
            SendSettingsTo(e.FromPlayerID);
            return;
        }

        if (e.Type == MsgSettingsSync && !Context.IsMainPlayer)
        {
            SaveSettings synced = e.ReadAs<SaveSettings>();
            synced.Validate();
            synced.SetupComplete = true;
            Settings = synced;
        }
    }

    private void RequestHostSettings()
    {
        Helper.Multiplayer.SendMessage(
            new SettingsRequest(),
            MsgSettingsRequest,
            modIDs: new[] { ModManifest.UniqueID }
        );
    }

    private void BroadcastSettings()
    {
        if (!Context.IsMultiplayer || !Context.IsMainPlayer)
            return;
        Helper.Multiplayer.SendMessage(
            Settings,
            MsgSettingsSync,
            modIDs: new[] { ModManifest.UniqueID }
        );
    }

    private void SendSettingsTo(long playerId)
    {
        Helper.Multiplayer.SendMessage(
            Settings,
            MsgSettingsSync,
            modIDs: new[] { ModManifest.UniqueID },
            playerIDs: new[] { playerId }
        );
    }

    private void OnSetupCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady)
        {
            Monitor.Log("Load a farm before using sfc_setup.", LogLevel.Info);
            return;
        }
        if (Context.IsMultiplayer && !Context.IsMainPlayer)
        {
            Monitor.Log("Only the multiplayer host can change per-farm Season Exchange settings.", LogLevel.Info);
            return;
        }

        Game1.activeClickableMenu = new NewSaveSetupMenu(this, Settings, isFirstSetup: false);
    }

    private void OnRebuildCatalogCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady)
        {
            Monitor.Log("Load a farm before rebuilding the seasonal catalog.", LogLevel.Info);
            return;
        }
        Catalog.Rebuild();
        Monitor.Log("Seasonal item catalog rebuilt.", LogLevel.Info);
    }

    private sealed record SettingsRequest;
}

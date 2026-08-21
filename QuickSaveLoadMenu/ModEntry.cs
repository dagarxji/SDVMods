using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace QuickSaveLoadMenu;

public sealed class ModEntry : Mod
{
    private const string QuickSaveModId = "DLX.QuickSave";
    private const string QuickSaveFileName = "Quicksave";
    private const int ButtonWidth = 62;
    private const int ButtonHeight = 48;

    private static ModEntry Instance = null!;

    private readonly Dictionary<LoadGameMenu.SaveFileSlot, SlotState> slotStates = new();

    private IQuickSaveApi? quickSaveApi;
    private bool pendingQuickLoad;
    private bool waitingForVanillaLoad;
    private int waitTicks;

    public override void Entry(IModHelper helper)
    {
        Instance = this;

        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
        helper.Events.Display.MenuChanged += this.OnMenuChanged;

        Harmony harmony = new(this.ModManifest.UniqueID);

        MethodInfo? drawMethod = AccessTools.Method(
            typeof(LoadGameMenu.SaveFileSlot),
            nameof(LoadGameMenu.SaveFileSlot.Draw),
            new[] { typeof(SpriteBatch), typeof(int) }
        );

        if (drawMethod is null)
        {
            this.Monitor.Log("Couldn't find LoadGameMenu.SaveFileSlot.Draw(SpriteBatch, int); the QuickSave buttons can't be drawn.", LogLevel.Error);
            return;
        }

        harmony.Patch(
            original: drawMethod,
            postfix: new HarmonyMethod(typeof(ModEntry), nameof(SaveFileSlot_Draw_Postfix))
        );

        MethodInfo? clickMethod = AccessTools.Method(
            typeof(LoadGameMenu),
            nameof(LoadGameMenu.receiveLeftClick),
            new[] { typeof(int), typeof(int), typeof(bool) }
        ) ?? AccessTools.Method(
            typeof(LoadGameMenu),
            nameof(LoadGameMenu.receiveLeftClick),
            new[] { typeof(int), typeof(int) }
        );

        if (clickMethod is null)
        {
            this.Monitor.Log("Couldn't find LoadGameMenu.receiveLeftClick; the QuickSave buttons can't receive clicks.", LogLevel.Error);
            return;
        }

        harmony.Patch(
            original: clickMethod,
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(LoadGameMenu_ReceiveLeftClick_Prefix))
        );
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        this.quickSaveApi = this.Helper.ModRegistry.GetApi<IQuickSaveApi>(QuickSaveModId);
        if (this.quickSaveApi is null)
            this.Monitor.Log("QuickSave is installed but its API couldn't be loaded. QuickSave Load Menu will stay disabled.", LogLevel.Error);
    }

    private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        // SaveFileSlot instances are recreated when the load menu is reopened, so old
        // geometry/path cache entries are no longer useful.
        bool oldWasLoadMenu = IsLoadMenu(e.OldMenu);
        bool newIsLoadMenu = IsLoadMenu(e.NewMenu);
        if (oldWasLoadMenu && !newIsLoadMenu)
            this.slotStates.Clear();
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        // The first SaveLoaded is the vanilla morning save we intentionally selected.
        // Once it has initialized far enough for QuickSave's normal guard conditions,
        // OnUpdateTicked invokes QuickSave.TryLoad().
        if (!this.pendingQuickLoad)
            return;

        this.waitingForVanillaLoad = true;
        this.waitTicks = 0;
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!this.pendingQuickLoad || !this.waitingForVanillaLoad)
            return;

        this.waitTicks++;

        if (this.quickSaveApi is null)
        {
            this.FailPendingLoad("QuickSave's API is unavailable.");
            return;
        }

        // Mirror QuickSave's CanSaveOrLoad checks. Waiting for these here prevents us
        // from spamming TryLoad while the vanilla save is still finishing startup.
        if (!Context.IsWorldReady
            || !Context.IsMainPlayer
            || !Context.IsPlayerFree
            || Context.HasRemotePlayers
            || Context.IsSplitScreen
            || Game1.player is null
            || !Game1.player.CanMove
            || Game1.farmEvent is not null
            || Game1.isWarping
            || Game1.isFestival()
            || this.quickSaveApi.IsLoading
            || this.quickSaveApi.IsSaving)
        {
            // Don't leave an invisible pending action around forever if another mod or
            // event keeps the player unavailable after the vanilla save finishes.
            if (this.waitTicks > 900)
                this.FailPendingLoad("The vanilla save loaded, but QuickSave never became available to perform the midday load.");
            return;
        }

        this.pendingQuickLoad = false;
        this.waitingForVanillaLoad = false;

        bool started;
        try
        {
            started = this.quickSaveApi.TryLoad(this.ModManifest, QuickSaveFileName);
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"QuickSave.TryLoad threw an exception:\n{ex}", LogLevel.Error);
            Game1.showRedMessage("Couldn't load the midday QuickSave. See the SMAPI console.");
            return;
        }

        if (!started)
        {
            this.Monitor.Log("QuickSave.TryLoad returned false after the vanilla save finished loading.", LogLevel.Warn);
            Game1.showRedMessage("QuickSave couldn't load the midday save.");
            return;
        }

        this.Monitor.Log("Vanilla save initialized; QuickSave midday load started.", LogLevel.Debug);
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        this.pendingQuickLoad = false;
        this.waitingForVanillaLoad = false;
        this.waitTicks = 0;
        this.slotStates.Clear();
    }

    private void FailPendingLoad(string reason)
    {
        this.Monitor.Log(reason, LogLevel.Warn);
        this.pendingQuickLoad = false;
        this.waitingForVanillaLoad = false;
        this.waitTicks = 0;

        if (Context.IsWorldReady)
            Game1.showRedMessage("Couldn't load the midday QuickSave.");
    }

    private static void SaveFileSlot_Draw_Postfix(LoadGameMenu.SaveFileSlot __instance, SpriteBatch b, int i)
    {
        try
        {
            Instance.DrawQuickSaveButton(__instance, b, i);
        }
        catch (Exception ex)
        {
            Instance.Monitor.LogOnce($"Error drawing a QuickSave load button:\n{ex}", LogLevel.Error);
        }
    }

    private static bool LoadGameMenu_ReceiveLeftClick_Prefix(LoadGameMenu __instance, int x, int y)
    {
        try
        {
            SlotState? state = Instance.slotStates.Values.FirstOrDefault(p => ReferenceEquals(p.Menu, __instance) && p.ButtonBounds.Contains(x, y));
            if (state is null)
            {
                // Any ordinary load-menu click cancels a previously armed QuickSave
                // request. This prevents an aborted/ignored QS click from carrying over
                // to a later normal save selection.
                Instance.pendingQuickLoad = false;
                Instance.waitingForVanillaLoad = false;
                Instance.waitTicks = 0;
                return true;
            }

            if (!state.HasQuickSave)
            {
                Instance.pendingQuickLoad = false;
                Instance.waitingForVanillaLoad = false;
                Instance.waitTicks = 0;
                Game1.playSound("cancel");
                return false;
            }

            Instance.pendingQuickLoad = true;
            Instance.waitingForVanillaLoad = false;
            Instance.waitTicks = 0;

            Game1.playSound("smallSelect");
            Instance.Monitor.Log($"QuickSave button selected for '{state.SaveFolderName}'. Loading the vanilla save first.", LogLevel.Debug);

            // The button sits inside the vanilla save-row clickable bounds, so letting
            // the original receiveLeftClick run starts the normal save load. We then
            // call QuickSave.TryLoad as soon as that save is initialized.
            return true;
        }
        catch (Exception ex)
        {
            Instance.Monitor.Log($"Error handling a QuickSave load-menu click:\n{ex}", LogLevel.Error);
            return true;
        }
    }

    private void DrawQuickSaveButton(LoadGameMenu.SaveFileSlot slot, SpriteBatch b, int index)
    {
        LoadGameMenu? menu = GetCurrentLoadMenu();
        if (menu is null || slot.Farmer is null)
            return;

        Rectangle? rowBounds = GetClickableBounds(menu, "slotButtons", index);
        if (rowBounds is null)
            return;

        Rectangle? deleteBounds = GetClickableBounds(menu, "deleteButtons", index);

        int x = deleteBounds is not null
            ? deleteBounds.Value.Left - ButtonWidth - 12
            : rowBounds.Value.Right - ButtonWidth - 74;

        int y = rowBounds.Value.Y + (rowBounds.Value.Height - ButtonHeight) / 2;
        Rectangle bounds = new(x, y, ButtonWidth, ButtonHeight);

        if (!this.slotStates.TryGetValue(slot, out SlotState? state))
        {
            string? saveFolder = ResolveSaveFolder(slot.Farmer);
            state = new SlotState(slot, menu, bounds, saveFolder);
            this.slotStates[slot] = state;
        }
        else
        {
            state.Menu = menu;
            state.ButtonBounds = bounds;
        }

        state.HasQuickSave = this.quickSaveApi is not null
            && state.SaveFolder is not null
            && File.Exists(Path.Combine(state.SaveFolder, QuickSaveFileName));

        bool hover = bounds.Contains(Game1.getMouseX(true), Game1.getMouseY(true));
        Color boxColor = state.HasQuickSave
            ? (hover ? Color.White * 0.9f : Color.White)
            : Color.Gray * 0.75f;

        IClickableMenu.drawTextureBox(b, bounds.X, bounds.Y, bounds.Width, bounds.Height, boxColor);

        const string label = "QS";
        Vector2 size = Game1.smallFont.MeasureString(label);
        Vector2 textPos = new(
            bounds.X + (bounds.Width - size.X) / 2f,
            bounds.Y + (bounds.Height - size.Y) / 2f - 1f
        );

        b.DrawString(
            Game1.smallFont,
            label,
            textPos,
            state.HasQuickSave ? Game1.textColor : Color.DarkGray
        );

        if (hover)
        {
            string tooltip = state.HasQuickSave
                ? "Load midday QuickSave"
                : this.quickSaveApi is null
                    ? "QuickSave API unavailable"
                    : "No midday QuickSave found";

            IClickableMenu.drawHoverText(b, tooltip, Game1.smallFont);
        }
    }

    private static LoadGameMenu? GetCurrentLoadMenu()
    {
        if (TitleMenu.subMenu is LoadGameMenu titleLoadMenu)
            return titleLoadMenu;

        if (Game1.activeClickableMenu is LoadGameMenu directLoadMenu)
            return directLoadMenu;

        return null;
    }

    private static bool IsLoadMenu(IClickableMenu? menu)
    {
        if (menu is LoadGameMenu)
            return true;

        return menu is TitleMenu && TitleMenu.subMenu is LoadGameMenu;
    }

    private static Rectangle? GetClickableBounds(LoadGameMenu menu, string fieldName, int index)
    {
        FieldInfo? field = AccessTools.Field(typeof(LoadGameMenu), fieldName);
        if (field?.GetValue(menu) is not IList list || index < 0 || index >= list.Count)
            return null;

        return list[index] is ClickableComponent component
            ? component.bounds
            : null;
    }

    private static string? ResolveSaveFolder(Farmer farmer)
    {
        try
        {
            string savesRoot = StardewValley.Program.GetSavesFolder();
            if (!Directory.Exists(savesRoot))
                return null;

            List<string> candidates = Directory
                .EnumerateDirectories(savesRoot, "*", SearchOption.TopDirectoryOnly)
                .Where(path => File.Exists(Path.Combine(path, "SaveGameInfo")))
                .ToList();

            if (candidates.Count == 0)
                return null;

            if (candidates.Count == 1)
                return candidates[0];

            // Save-folder names aren't guaranteed to start with the farmer name, so
            // match SaveGameInfo metadata against the load-screen Farmer object.
            string farmerName = farmer.Name ?? string.Empty;
            string farmName = farmer.farmName.Value ?? string.Empty;
            ulong millisecondsPlayed = farmer.millisecondsPlayed;

            int bestScore = -1;
            string? bestPath = null;
            bool tied = false;

            foreach (string candidate in candidates)
            {
                int score = ScoreSaveGameInfo(candidate, farmerName, farmName, millisecondsPlayed);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPath = candidate;
                    tied = false;
                }
                else if (score == bestScore)
                {
                    tied = true;
                }
            }

            return bestScore > 0 && !tied ? bestPath : null;
        }
        catch (Exception ex)
        {
            Instance.Monitor.LogOnce($"Couldn't resolve a save folder for a Load Game slot: {ex.Message}", LogLevel.Warn);
            return null;
        }
    }

    private static int ScoreSaveGameInfo(string saveFolder, string farmerName, string farmName, ulong millisecondsPlayed)
    {
        try
        {
            XDocument doc = XDocument.Load(Path.Combine(saveFolder, "SaveGameInfo"), LoadOptions.None);

            string? xmlName = GetElementValue(doc, "name");
            string? xmlFarmName = GetElementValue(doc, "farmName");
            string? xmlMilliseconds = GetElementValue(doc, "millisecondsPlayed");

            int score = 0;
            if (string.Equals(xmlName, farmerName, StringComparison.Ordinal))
                score += 4;
            if (string.Equals(xmlFarmName, farmName, StringComparison.Ordinal))
                score += 3;
            if (ulong.TryParse(xmlMilliseconds, out ulong parsedMs) && parsedMs == millisecondsPlayed)
                score += 6;

            return score;
        }
        catch
        {
            return -1;
        }
    }

    private static string? GetElementValue(XDocument doc, string localName)
    {
        return doc.Descendants()
            .FirstOrDefault(p => string.Equals(p.Name.LocalName, localName, StringComparison.Ordinal))
            ?.Value;
    }

    private sealed class SlotState
    {
        public SlotState(LoadGameMenu.SaveFileSlot slot, LoadGameMenu menu, Rectangle buttonBounds, string? saveFolder)
        {
            this.Slot = slot;
            this.Menu = menu;
            this.ButtonBounds = buttonBounds;
            this.SaveFolder = saveFolder;
        }

        public LoadGameMenu.SaveFileSlot Slot { get; }
        public LoadGameMenu Menu { get; set; }
        public Rectangle ButtonBounds { get; set; }
        public string? SaveFolder { get; }
        public string SaveFolderName => this.SaveFolder is null ? "unknown save" : Path.GetFileName(this.SaveFolder);
        public bool HasQuickSave { get; set; }
    }
}

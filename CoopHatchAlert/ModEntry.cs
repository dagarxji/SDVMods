using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Machines;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace CoopHatchAlert;

internal enum HatchState
{
    None,
    Incubating,
    Ready,
    ReadyButFull
}

internal sealed class IncubatorInfo
{
    public SObject Machine { get; }
    public HatchState State { get; }
    public string EggName { get; }
    public SObject? Egg { get; }

    public IncubatorInfo(SObject machine, HatchState state, string eggName, SObject? egg)
    {
        this.Machine = machine;
        this.State = state;
        this.EggName = eggName;
        this.Egg = egg;
    }
}

public sealed class ModEntry : Mod
{
    private readonly Dictionary<Guid, HatchState> LastBuildingStates = new();
    private ModConfig Config = new();

    public override void Entry(IModHelper helper)
    {
        this.Config = helper.ReadConfig<ModConfig>();
        this.Config.MarkerScale = Math.Clamp(this.Config.MarkerScale, 1f, 6f);

        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.Events.GameLoop.TimeChanged += this.OnTimeChanged;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
        helper.Events.Display.RenderedWorld += this.OnRenderedWorld;
        helper.Events.Display.RenderedHud += this.OnRenderedHud;
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.LastBuildingStates.Clear();

        // Establish a baseline so loading a save doesn't replay old alerts.
        this.ScanAllCoops(showNotifications: false);
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        this.ScanAllCoops(showNotifications: true);
    }

    private void OnTimeChanged(object? sender, TimeChangedEventArgs e)
    {
        this.ScanAllCoops(showNotifications: true);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        // This catches non-clock changes, such as selling/moving an animal and freeing a slot.
        if (Context.IsWorldReady && e.IsMultipleOf(60))
            this.ScanAllCoops(showNotifications: true);
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        this.LastBuildingStates.Clear();
    }

    private void ScanAllCoops(bool showNotifications)
    {
        if (!Context.IsWorldReady)
            return;

        HashSet<Guid> seen = new();

        Utility.ForEachBuilding(building =>
        {
            if (building.GetIndoors() is not AnimalHouse house)
                return true;

            List<IncubatorInfo> incubators = this.GetIncubators(house);
            if (incubators.Count == 0)
                return true;

            Guid id = building.id.Value;
            seen.Add(id);

            HatchState state = this.GetBuildingState(incubators);
            this.LastBuildingStates.TryGetValue(id, out HatchState previous);

            if (showNotifications && state != previous)
                this.NotifyForTransition(building, incubators, previous, state);

            this.LastBuildingStates[id] = state;
            return true;
        });

        foreach (Guid stale in this.LastBuildingStates.Keys.Where(id => !seen.Contains(id)).ToArray())
            this.LastBuildingStates.Remove(stale);
    }

    private void NotifyForTransition(Building building, List<IncubatorInfo> incubators, HatchState previous, HatchState current)
    {
        if (!this.Config.EnableNotifications)
            return;

        string buildingName = this.GetBuildingName(building);
        IncubatorInfo? ready = incubators.FirstOrDefault(p => p.State is HatchState.Ready or HatchState.ReadyButFull);
        string eggName = ready?.EggName ?? "Egg";

        if (current == HatchState.Ready && previous != HatchState.Ready)
        {
            Game1.addHUDMessage(new HUDMessage($"{eggName} is ready to hatch in {buildingName}!", HUDMessage.newQuest_type));
        }
        else if (current == HatchState.ReadyButFull && previous != HatchState.ReadyButFull && this.Config.NotifyWhenCoopIsFull)
        {
            Game1.addHUDMessage(new HUDMessage($"{eggName} finished incubating in {buildingName}, but the coop is full.", HUDMessage.error_type));
        }
    }

    private List<IncubatorInfo> GetIncubators(AnimalHouse house)
    {
        List<IncubatorInfo> result = new();

        foreach (SObject machine in house.objects.Values)
        {
            if (!machine.bigCraftable.Value)
                continue;

            MachineData? data = machine.GetMachineData();
            if (data?.IsIncubator != true)
                continue;

            SObject? egg = machine.heldObject.Value;
            HatchState state;

            if (egg is null)
                state = HatchState.None;
            else if (machine.MinutesUntilReady > 0)
                state = HatchState.Incubating;
            else
                state = house.isFull() ? HatchState.ReadyButFull : HatchState.Ready;

            result.Add(new IncubatorInfo(machine, state, egg?.DisplayName ?? "Egg", egg));
        }

        return result;
    }

    private HatchState GetBuildingState(List<IncubatorInfo> incubators)
    {
        if (incubators.Any(p => p.State == HatchState.Ready))
            return HatchState.Ready;
        if (incubators.Any(p => p.State == HatchState.ReadyButFull))
            return HatchState.ReadyButFull;
        if (incubators.Any(p => p.State == HatchState.Incubating))
            return HatchState.Incubating;
        return HatchState.None;
    }

    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        if (!Context.IsWorldReady || !this.Config.EnableExteriorMarker)
            return;

        foreach (Building building in Game1.currentLocation.buildings)
        {
            if (building.GetIndoors() is not AnimalHouse house)
                continue;

            List<IncubatorInfo> incubators = this.GetIncubators(house);
            HatchState state = this.GetBuildingState(incubators);
            if (state is not HatchState.Ready and not HatchState.ReadyButFull)
                continue;

            SObject? egg = incubators.FirstOrDefault(p => p.State == state)?.Egg;
            if (egg is null)
                continue;

            ParsedItemData eggData = ItemRegistry.GetDataOrErrorItem(egg.QualifiedItemId);
            Texture2D texture = eggData.GetTexture();
            Rectangle sourceRect = eggData.GetSourceRect();

            float bob = (float)Math.Sin(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 220d) * 4f;
            Vector2 worldPosition = new(
                (building.tileX.Value + building.tilesWide.Value / 2f) * Game1.tileSize,
                building.tileY.Value * Game1.tileSize - 14f + bob
            );
            Vector2 screenPosition = Game1.GlobalToLocal(Game1.viewport, worldPosition);

            Color tint = state == HatchState.Ready ? Color.White : Color.LightGray;
            Vector2 origin = new(sourceRect.Width / 2f, sourceRect.Height);

            e.SpriteBatch.Draw(
                texture,
                screenPosition,
                sourceRect,
                tint,
                0f,
                origin,
                this.Config.MarkerScale,
                SpriteEffects.None,
                1f
            );
        }
    }

    private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.activeClickableMenu is not null)
            return;

        if (Game1.currentLocation is AnimalHouse house)
            this.DrawIncubatorTooltip(e, house);
        else
            this.DrawExteriorCoopTooltip(e);
    }

    private void DrawIncubatorTooltip(RenderedHudEventArgs e, AnimalHouse house)
    {
        if (!this.Config.EnableIncubatorTooltip)
            return;

        // Use the raw cursor tile (not GrabTile, which the game clamps to the player's reach) so this works from anywhere on screen.
        Vector2 tile = this.Helper.Input.GetCursorPosition().Tile;
        if (!house.objects.TryGetValue(tile, out SObject? machine) || machine is null || !machine.bigCraftable.Value)
            return;

        MachineData? data = machine.GetMachineData();
        if (data?.IsIncubator != true)
            return;

        SObject? egg = machine.heldObject.Value;
        string text;

        if (egg is null)
        {
            if (!this.Config.ShowEmptyIncubatorTooltip)
                return;

            text = "Empty";
        }
        else if (machine.MinutesUntilReady <= 0)
        {
            text = house.isFull()
                ? $"{egg.DisplayName}\nReady — coop is full"
                : $"{egg.DisplayName}\nReady to hatch!";
        }
        else
        {
            text = $"{egg.DisplayName}\n{this.FormatRemainingTime(machine.MinutesUntilReady)} remaining";
        }

        Item icon = egg is not null ? egg : machine;
        this.DrawIconTooltip(e.SpriteBatch, "Incubator", text, icon);
    }

    private void DrawExteriorCoopTooltip(RenderedHudEventArgs e)
    {
        if (!this.Config.EnableExteriorTooltip)
            return;

        // Use the raw cursor tile (not GrabTile, which the game clamps to the player's reach) so this works from anywhere on screen.
        Vector2 tile = this.Helper.Input.GetCursorPosition().Tile;

        foreach (Building building in Game1.currentLocation.buildings)
        {
            if (building.GetIndoors() is not AnimalHouse house)
                continue;

            // Pad above the collision footprint so hovering the roof (not just the door tile) counts.
            const int padTop = 3;
            Rectangle footprint = new(
                (int)building.tileX.Value,
                (int)building.tileY.Value - padTop,
                (int)building.tilesWide.Value,
                (int)building.tilesHigh.Value + padTop
            );
            if (!footprint.Contains((int)tile.X, (int)tile.Y))
                continue;

            List<IncubatorInfo> incubators = this.GetIncubators(house);
            IncubatorInfo? highlight = incubators.FirstOrDefault(p => p.State is HatchState.Ready or HatchState.ReadyButFull)
                ?? incubators.Where(p => p.State == HatchState.Incubating).OrderBy(p => p.Machine.MinutesUntilReady).FirstOrDefault();

            if (highlight?.Egg is not SObject egg)
                return;

            string text = highlight.State switch
            {
                HatchState.Ready => $"{egg.DisplayName}\nReady to hatch!",
                HatchState.ReadyButFull => $"{egg.DisplayName}\nReady — coop is full",
                _ => $"{egg.DisplayName}\n{this.FormatRemainingTime(highlight.Machine.MinutesUntilReady)} remaining"
            };

            this.DrawIconTooltip(e.SpriteBatch, building.buildingType.Value, text, egg);
            return;
        }
    }

    private void DrawIconTooltip(SpriteBatch b, string title, string text, Item icon)
    {
        ParsedItemData iconData = ItemRegistry.GetDataOrErrorItem(icon.QualifiedItemId);
        Texture2D texture = iconData.GetTexture();
        Rectangle sourceRect = iconData.GetSourceRect();

        // Build our own box (icon + text together) instead of drawHoverText/drawToolTip, so the icon stays
        // attached to this tooltip and the whole thing can fan out to the side instead of covering other UIs.
        string fullText = $"{title}\n{text}";
        Vector2 textSize = Game1.smallFont.MeasureString(fullText);

        const int iconSize = 64;
        const int padding = 16;
        const int gap = 12;
        const int cursorMargin = 24;

        int boxWidth = iconSize + gap + (int)textSize.X + padding * 2;
        int boxHeight = Math.Max(iconSize, (int)textSize.Y) + padding * 2;

        Point mouse = Game1.getMousePosition();

        // Fan out to the left of the cursor so this doesn't sit on top of other mods' hover tooltips.
        int x = mouse.X - boxWidth - cursorMargin;
        int y = mouse.Y - boxHeight / 2;

        x = Math.Clamp(x, 8, Math.Max(8, Game1.uiViewport.Width - boxWidth - 8));
        y = Math.Clamp(y, 8, Math.Max(8, Game1.uiViewport.Height - boxHeight - 8));

        IClickableMenu.drawTextureBox(b, x, y, boxWidth, boxHeight, Color.White);

        Rectangle iconDest = new(x + padding, y + (boxHeight - iconSize) / 2, iconSize, iconSize);
        b.Draw(texture, iconDest, sourceRect, Color.White);

        Vector2 textPos = new(x + padding + iconSize + gap, y + (boxHeight - textSize.Y) / 2);
        b.DrawString(Game1.smallFont, fullText, textPos, Game1.textColor);
    }

    private string FormatRemainingTime(int minutes)
    {
        minutes = Math.Max(0, minutes);

        // Stardew machines count a full overnight/day cycle as 1600 machine-minutes.
        int days = minutes / 1600;
        int remainder = minutes % 1600;
        int hours = remainder / 60;
        int mins = remainder % 60;

        List<string> parts = new();
        if (days > 0)
            parts.Add(days == 1 ? "1 day" : $"{days} days");
        if (hours > 0)
            parts.Add(hours == 1 ? "1 hour" : $"{hours} hours");
        if (mins > 0 || parts.Count == 0)
            parts.Add(mins == 1 ? "1 minute" : $"{mins} minutes");

        return string.Join(", ", parts);
    }

    private string GetBuildingName(Building building)
    {
        string raw = building.buildingType.Value;
        return string.IsNullOrWhiteSpace(raw) ? "your coop" : $"your {raw}";
    }
}

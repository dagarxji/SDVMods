using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using System;
using System.Collections.Generic;

namespace AutoEatEideeBridge;

public interface IGenericModConfigMenuApi
{
    void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);
    void AddKeybindList(IManifest mod, Func<KeybindList> getValue, Action<KeybindList> setValue, Func<string> name, Func<string>? tooltip = null, string? fieldId = null);
    void AddBoolOption(IManifest mod, Func<bool> getValue, Action<bool> setValue, Func<string> name, Func<string>? tooltip = null, string? fieldId = null);
    void AddNumberOption(IManifest mod, Func<int> getValue, Action<int> setValue, Func<string> name, Func<string>? tooltip = null, int? min = null, int? max = null, int? interval = null, Func<int, string>? formatValue = null, string? fieldId = null);
    void AddComplexOption(IManifest mod, Func<string> name, Action<SpriteBatch, Vector2> draw, Func<string>? tooltip = null, Action? beforeMenuOpened = null, Action? beforeSave = null, Action? afterSave = null, Action? beforeReset = null, Action? afterReset = null, Action? beforeMenuClosed = null, Func<int>? height = null, string? fieldId = null);
}

internal sealed class ModConfig
{
    public bool ShowTracker { get; set; } = true;
    public KeybindList ToggleTracker { get; set; } = new(SButton.F8);
    public int TrackerX { get; set; } = -1;
    public int TrackerY { get; set; } = -1;
    public bool DeleteFishingTrash { get; set; } = false;

    // Qualified item IDs (e.g. "(O)167") the user picked to destroy automatically when caught while
    // fishing. Edited through the in-game auto-destroy items menu.
    public List<string> AutoDestroyItemIds { get; set; } = new();

    // Keeps Fast Animations' fishing speed multiplier in sync with fishing/mastery progress (1x-20x).
    public bool SyncFishingSpeedWithLevel { get; set; } = true;

    // Speeds up TimeSpeed's flow of time to match the current fishing animation speed while fishing.
    public bool SyncTimeSpeedWithFishingSpeed { get; set; } = true;

    // How closely TimeSpeed's flow of time follows the fishing animation speed, from 0 (no effect) to
    // 100 (fully tied, e.g. 2x animation speed halves seconds-per-minute, 20x divides it by 20).
    public int TimeSpeedSyncStrengthPercent { get; set; } = 100;
}

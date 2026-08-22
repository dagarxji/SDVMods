using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using System;

namespace AutoEatEideeBridge;

public interface IGenericModConfigMenuApi
{
    void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);
    void AddKeybindList(IManifest mod, Func<KeybindList> getValue, Action<KeybindList> setValue, Func<string> name, Func<string>? tooltip = null, string? fieldId = null);
    void AddBoolOption(IManifest mod, Func<bool> getValue, Action<bool> setValue, Func<string> name, Func<string>? tooltip = null, string? fieldId = null);
    void AddNumberOption(IManifest mod, Func<int> getValue, Action<int> setValue, Func<string> name, Func<string>? tooltip = null, int? min = null, int? max = null, int? interval = null, Func<int, string>? formatValue = null, string? fieldId = null);
}

internal sealed class ModConfig
{
    public bool ShowTracker { get; set; } = true;
    public KeybindList ToggleTracker { get; set; } = new(SButton.F8);
    public int TrackerX { get; set; } = -1;
    public int TrackerY { get; set; } = -1;
    public bool DeleteFishingTrash { get; set; } = false;

    // The level-10 daily cap. Each fishing level receives one tenth of this amount; 0 disables the cap.
    public int DailyFishCatchCap { get; set; } = 500;
}

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

    // Once this many fish are caught in a day, the bridge stops re-arming Auto Recast after Auto-Eat interrupts it. 0 disables the cap.
    public int DailyFishCatchCap { get; set; } = 500;
}

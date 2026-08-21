using StardewModdingAPI.Utilities;

namespace FishingForecast;

internal sealed class ModConfig
{
    public KeybindList OpenMenu { get; set; } = KeybindList.Parse("P");

    /// <summary>Monte-Carlo fish selections per sampled hour and candidate fishing tile.</summary>
    public int SamplesPerHour { get; set; } = 24;

    /// <summary>How many tiles to skip while scanning a map for representative fishing tiles.</summary>
    public int TileScanStride { get; set; } = 2;

    /// <summary>Maximum representative fishing tiles evaluated for each location.</summary>
    public int MaxTilesPerLocation { get; set; } = 6;

    /// <summary>Approximate real-time milliseconds spent casting, hooking, and resetting between catches.</summary>
    public int CastOverheadMilliseconds { get; set; } = 2600;

    /// <summary>Approximate real-time milliseconds represented by one in-game minute while the clock is running.</summary>
    public int RealMillisecondsPerGameMinute { get; set; } = 700;

    /// <summary>Use World Navigator for reachability when its API is available.</summary>
    public bool UseWorldNavigatorWhenAvailable { get; set; } = true;
}

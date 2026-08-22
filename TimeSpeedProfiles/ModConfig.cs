using System.Globalization;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;

namespace TimeSpeedProfiles;

/// <summary>The companion mod's configuration.</summary>
internal sealed class ModConfig
{
    /// <summary>The complete TimeSpeed profile used outside multiplayer.</summary>
    public TimeSpeedProfile SinglePlayer { get; set; } = new();

    /// <summary>The complete TimeSpeed profile used in multiplayer and split-screen.</summary>
    public TimeSpeedProfile Multiplayer { get; set; } = new();

    public void Normalize()
    {
        this.SinglePlayer ??= new TimeSpeedProfile();
        this.Multiplayer ??= new TimeSpeedProfile();
        this.SinglePlayer.Normalize();
        this.Multiplayer.Normalize();
    }
}

/// <summary>
/// A complete mirror of TimeSpeed 2.8.1's ModConfig schema.
/// Property names intentionally match TimeSpeed's config.json exactly so a profile can be written directly to it.
/// </summary>
internal sealed class TimeSpeedProfile
{
    public bool EnableOnFestivalDays { get; set; } = true;
    public bool LocationNotify { get; set; } = false;
    public SecondsPerMinuteConfig SecondsPerMinute { get; set; } = new();
    public FreezeTimeConfig FreezeTime { get; set; } = new();
    public bool LetFarmhandsManageTime { get; set; } = true;
    public ControlsConfig Keys { get; set; } = new();

    public void Normalize()
    {
        this.SecondsPerMinute ??= new SecondsPerMinuteConfig();
        this.FreezeTime ??= new FreezeTimeConfig();
        this.Keys ??= new ControlsConfig();
        this.SecondsPerMinute.Normalize();
        this.FreezeTime.Normalize();
        this.Keys.Normalize();
    }

    public TimeSpeedProfile Clone()
    {
        this.Normalize();

        return new TimeSpeedProfile
        {
            EnableOnFestivalDays = this.EnableOnFestivalDays,
            LocationNotify = this.LocationNotify,
            LetFarmhandsManageTime = this.LetFarmhandsManageTime,
            SecondsPerMinute = this.SecondsPerMinute.Clone(),
            FreezeTime = this.FreezeTime.Clone(),
            Keys = this.Keys.Clone()
        };
    }
}

internal sealed class SecondsPerMinuteConfig
{
    public double Indoors { get; set; } = 1.4;
    public double Outdoors { get; set; } = 0.7;
    public double Mines { get; set; } = 0.7;
    public double SkullCavern { get; set; } = 0.9;
    public double VolcanoDungeon { get; set; } = 0.9;
    public Dictionary<string, double> ByLocationName { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Normalize()
    {
        this.ByLocationName = new Dictionary<string, double>(
            this.ByLocationName ?? new Dictionary<string, double>(),
            StringComparer.OrdinalIgnoreCase
        );
    }

    public SecondsPerMinuteConfig Clone()
    {
        this.Normalize();
        return new SecondsPerMinuteConfig
        {
            Indoors = this.Indoors,
            Outdoors = this.Outdoors,
            Mines = this.Mines,
            SkullCavern = this.SkullCavern,
            VolcanoDungeon = this.VolcanoDungeon,
            ByLocationName = new Dictionary<string, double>(this.ByLocationName, StringComparer.OrdinalIgnoreCase)
        };
    }

    public string FormatLocationOverrides()
    {
        this.Normalize();
        return string.Join(", ", this.ByLocationName
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => $"{p.Key}={p.Value.ToString("0.###", CultureInfo.InvariantCulture)}"));
    }

    public bool TrySetLocationOverrides(string? raw)
    {
        var parsed = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (string item in (raw ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string entry = item.Trim();
            int separator = entry.LastIndexOf('=');
            if (separator <= 0 || separator >= entry.Length - 1)
                return false;

            string location = entry[..separator].Trim();
            string valueText = entry[(separator + 1)..].Trim();
            if (location.Length == 0
                || !double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || value < 0.1
                || value > 15)
            {
                return false;
            }

            parsed[location] = Math.Round(value, 3);
        }

        this.ByLocationName = parsed;
        return true;
    }
}

internal sealed class FreezeTimeConfig
{
    public int? AnywhereAtTime { get; set; }
    public bool PassOut { get; set; } = false;
    public bool Indoors { get; set; } = false;
    public bool Outdoors { get; set; } = false;
    public bool Mines { get; set; } = false;
    public bool SkullCavern { get; set; } = false;
    public bool VolcanoDungeon { get; set; } = false;
    public HashSet<string> ByLocationName { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExceptLocationNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Normalize()
    {
        this.ByLocationName = new HashSet<string>(
            (this.ByLocationName ?? new HashSet<string>()).Where(p => !string.IsNullOrWhiteSpace(p)),
            StringComparer.OrdinalIgnoreCase
        );
        this.ExceptLocationNames = new HashSet<string>(
            (this.ExceptLocationNames ?? new HashSet<string>()).Where(p => !string.IsNullOrWhiteSpace(p)),
            StringComparer.OrdinalIgnoreCase
        );
    }

    public FreezeTimeConfig Clone()
    {
        this.Normalize();
        return new FreezeTimeConfig
        {
            AnywhereAtTime = this.AnywhereAtTime,
            PassOut = this.PassOut,
            Indoors = this.Indoors,
            Outdoors = this.Outdoors,
            Mines = this.Mines,
            SkullCavern = this.SkullCavern,
            VolcanoDungeon = this.VolcanoDungeon,
            ByLocationName = new HashSet<string>(this.ByLocationName, StringComparer.OrdinalIgnoreCase),
            ExceptLocationNames = new HashSet<string>(this.ExceptLocationNames, StringComparer.OrdinalIgnoreCase)
        };
    }

    public static HashSet<string> ParseLocationList(string? raw)
    {
        return new HashSet<string>(
            (raw ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0),
            StringComparer.OrdinalIgnoreCase
        );
    }
}

internal sealed class ControlsConfig
{
    public KeybindList FreezeTime { get; set; } = new(SButton.N);
    public KeybindList IncreaseTickInterval { get; set; } = new(SButton.OemPeriod);
    public KeybindList DecreaseTickInterval { get; set; } = new(SButton.OemComma);
    public KeybindList ReloadConfig { get; set; } = new(SButton.B);

    public void Normalize()
    {
        this.FreezeTime ??= new KeybindList();
        this.IncreaseTickInterval ??= new KeybindList();
        this.DecreaseTickInterval ??= new KeybindList();
        this.ReloadConfig ??= new KeybindList();
    }

    public ControlsConfig Clone()
    {
        this.Normalize();
        return new ControlsConfig
        {
            FreezeTime = CloneKeybind(this.FreezeTime),
            IncreaseTickInterval = CloneKeybind(this.IncreaseTickInterval),
            DecreaseTickInterval = CloneKeybind(this.DecreaseTickInterval),
            ReloadConfig = CloneKeybind(this.ReloadConfig)
        };
    }

    private static KeybindList CloneKeybind(KeybindList keybind)
    {
        return KeybindList.Parse(keybind.ToString());
    }
}

using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace TimeSpeedProfiles;

/// <summary>Automatically selects and applies a full TimeSpeed config profile.</summary>
internal sealed class ModEntry : Mod
{
    private const string TimeSpeedId = "cantorsdust.TimeSpeed";
    private const string GmcmId = "spacechase0.GenericModConfigMenu";
    private const string ImportTempFile = "__timespeed_import.tmp.json";
    private const string ActiveTempFile = "__timespeed_active.tmp.json";

    private static ModEntry? Instance;

    private ModConfig Config = new();
    private bool WasConfigPresentAtStartup;
    private object? TimeSpeedInstance;
    private Type? TimeSpeedType;
    private MethodInfo? TimeSpeedReloadConfig;
    private string? TimeSpeedDirectory;
    private IManifest? TimeSpeedManifest;
    private IGenericModConfigMenuApi? Gmcm;
    private bool? LastAppliedMultiplayer;

    public override void Entry(IModHelper helper)
    {
        Instance = this;

        this.WasConfigPresentAtStartup = File.Exists(Path.Combine(helper.DirectoryPath, "config.json"));
        this.Config = helper.ReadConfig<ModConfig>();
        this.Config.Normalize();

        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        var timeSpeedInfo = this.Helper.ModRegistry.Get(TimeSpeedId);
        if (timeSpeedInfo is null)
        {
            this.Monitor.Log("TimeSpeed isn't loaded, so profiles can't be applied.", LogLevel.Error);
            return;
        }

        this.TimeSpeedManifest = timeSpeedInfo.Manifest;
        this.TimeSpeedType = AccessTools.TypeByName("TimeSpeed.ModEntry");
        if (this.TimeSpeedType is null)
        {
            this.Monitor.Log("Couldn't find TimeSpeed.ModEntry. This companion targets TimeSpeed 2.8.1.", LogLevel.Error);
            return;
        }

        this.TimeSpeedDirectory = Path.GetDirectoryName(this.TimeSpeedType.Assembly.Location);
        this.TimeSpeedReloadConfig = AccessTools.Method(this.TimeSpeedType, "ReloadConfig");

        if (string.IsNullOrWhiteSpace(this.TimeSpeedDirectory) || this.TimeSpeedReloadConfig is null)
        {
            this.Monitor.Log("Couldn't locate TimeSpeed's config/reload implementation. This companion targets TimeSpeed 2.8.1.", LogLevel.Error);
            return;
        }

        if (!this.WasConfigPresentAtStartup)
            this.ImportExistingTimeSpeedConfig();

        this.PatchTimeSpeedSaveLoaded();
        this.RegisterConfigMenu();
        this.HideOriginalTimeSpeedConfigMenu();

        if (timeSpeedInfo.Manifest.Version.IsNewerThan("2.8.1"))
        {
            this.Monitor.Log(
                $"TimeSpeed {timeSpeedInfo.Manifest.Version} is newer than the 2.8.1 schema this companion mirrors. " +
                "Existing 2.8.1 settings will work, but review this companion after TimeSpeed adds new config options.",
                LogLevel.Warn
            );
        }
    }

    private void PatchTimeSpeedSaveLoaded()
    {
        if (this.TimeSpeedType is null)
            return;

        MethodInfo? target = AccessTools.Method(this.TimeSpeedType, "OnSaveLoaded");
        MethodInfo? postfix = AccessTools.Method(typeof(ModEntry), nameof(AfterTimeSpeedSaveLoaded));
        if (target is null || postfix is null)
        {
            this.Monitor.Log("Couldn't hook TimeSpeed's SaveLoaded handler.", LogLevel.Error);
            return;
        }

        var harmony = new Harmony(this.ModManifest.UniqueID);
        harmony.Patch(target, postfix: new HarmonyMethod(postfix));

        // TimeSpeed re-registers its own GMCM menu at launch/save-load. Hide it after every registration
        // so users edit the authoritative profile pages instead of the generated active copy.
        MethodInfo? registerMenu = AccessTools.Method(this.TimeSpeedType, "RegisterConfigMenu");
        MethodInfo? registerMenuPostfix = AccessTools.Method(typeof(ModEntry), nameof(AfterTimeSpeedRegisterConfigMenu));
        if (registerMenu is not null && registerMenuPostfix is not null)
            harmony.Patch(registerMenu, postfix: new HarmonyMethod(registerMenuPostfix));
    }

    private static void AfterTimeSpeedRegisterConfigMenu()
    {
        Instance?.HideOriginalTimeSpeedConfigMenu();
    }

    /// <summary>Runs immediately after TimeSpeed's own SaveLoaded handler.</summary>
    private static void AfterTimeSpeedSaveLoaded(object __instance)
    {
        Instance?.OnTimeSpeedSaveLoaded(__instance);
    }

    private void OnTimeSpeedSaveLoaded(object timeSpeedInstance)
    {
        this.TimeSpeedInstance = timeSpeedInstance;
        this.ApplyActiveProfile(force: true);
        this.HideOriginalTimeSpeedConfigMenu();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!e.IsOneSecond || !Context.IsWorldReady || this.TimeSpeedInstance is null)
            return;

        // This catches transitions into/out of co-op and split-screen even if no remote peer event fires.
        bool isMultiplayer = Context.IsMultiplayer;
        if (this.LastAppliedMultiplayer != isMultiplayer)
            this.ApplyActiveProfile(force: true);
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        this.TimeSpeedInstance = null;
        this.LastAppliedMultiplayer = null;
        this.HideOriginalTimeSpeedConfigMenu();
    }

    private void ImportExistingTimeSpeedConfig()
    {
        if (this.TimeSpeedDirectory is null)
            return;

        string sourcePath = Path.Combine(this.TimeSpeedDirectory, "config.json");
        if (!File.Exists(sourcePath))
        {
            this.Helper.WriteConfig(this.Config);
            return;
        }

        string tempPath = Path.Combine(this.Helper.DirectoryPath, ImportTempFile);
        try
        {
            File.Copy(sourcePath, tempPath, overwrite: true);
            TimeSpeedProfile? imported = this.Helper.Data.ReadJsonFile<TimeSpeedProfile>(ImportTempFile);
            if (imported is null)
                return;

            imported.Normalize();
            this.Config.SinglePlayer = imported.Clone();
            this.Config.Multiplayer = imported.Clone();
            this.Config.Normalize();
            this.Helper.WriteConfig(this.Config);
            this.Monitor.Log("Imported the existing TimeSpeed config into both profiles.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Couldn't import the existing TimeSpeed config. Defaults will be used instead.\n{ex}", LogLevel.Warn);
            this.Helper.WriteConfig(this.Config);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private void ApplyActiveProfile(bool force = false)
    {
        if (this.TimeSpeedInstance is null || this.TimeSpeedDirectory is null || this.TimeSpeedReloadConfig is null)
            return;

        bool isMultiplayer = Context.IsMultiplayer;
        if (!force && this.LastAppliedMultiplayer == isMultiplayer)
            return;

        this.Config.Normalize();
        TimeSpeedProfile profile = isMultiplayer ? this.Config.Multiplayer : this.Config.SinglePlayer;
        profile.Normalize();

        string tempPath = Path.Combine(this.Helper.DirectoryPath, ActiveTempFile);
        string targetPath = Path.Combine(this.TimeSpeedDirectory, "config.json");

        try
        {
            // Use SMAPI's JSON writer so KeybindList values use the same config serialization TimeSpeed expects.
            this.Helper.Data.WriteJsonFile(ActiveTempFile, profile);
            File.Copy(tempPath, targetPath, overwrite: true);

            this.TimeSpeedReloadConfig.Invoke(this.TimeSpeedInstance, null);
            this.LastAppliedMultiplayer = isMultiplayer;

            this.Monitor.Log(
                $"Applied {(isMultiplayer ? "multiplayer/co-op" : "single-player")} TimeSpeed profile.",
                LogLevel.Trace
            );
        }
        catch (TargetInvocationException ex)
        {
            this.Monitor.Log($"TimeSpeed failed while reloading the selected profile.\n{ex.InnerException ?? ex}", LogLevel.Error);
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Couldn't apply the selected TimeSpeed profile.\n{ex}", LogLevel.Error);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private void RegisterConfigMenu()
    {
        this.Gmcm = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>(GmcmId);
        if (this.Gmcm is null)
            return;

        this.Gmcm.Unregister(this.ModManifest);
        this.Gmcm.Register(
            this.ModManifest,
            reset: () =>
            {
                this.Config = new ModConfig();
                this.Config.Normalize();
            },
            save: () =>
            {
                this.Config.Normalize();
                this.Helper.WriteConfig(this.Config);
                if (Context.IsWorldReady && this.TimeSpeedInstance is not null)
                    this.ApplyActiveProfile(force: true);
                this.HideOriginalTimeSpeedConfigMenu();
            }
        );

        this.Gmcm.AddParagraph(
            this.ModManifest,
            () => "TimeSpeed Profiles keeps two complete TimeSpeed configs and automatically uses the multiplayer profile whenever Context.IsMultiplayer is true (including split-screen)."
        );
        this.Gmcm.AddParagraph(
            this.ModManifest,
            () => "TimeSpeed's original GMCM entry is hidden while this companion is installed so direct edits there aren't overwritten by a profile."
        );

        this.Gmcm.AddPageLink(
            this.ModManifest,
            "single-player",
            () => "Single Player Profile",
            () => "Edit the complete TimeSpeed configuration used for normal single-player saves."
        );
        this.Gmcm.AddPageLink(
            this.ModManifest,
            "multiplayer",
            () => "Multiplayer / Co-op Profile",
            () => "Edit the complete TimeSpeed configuration used in multiplayer and split-screen."
        );

        this.Gmcm.AddPage(this.ModManifest, "single-player", () => "Single Player Profile");
        this.AddProfileOptions(() => this.Config.SinglePlayer, "sp");

        this.Gmcm.AddPage(this.ModManifest, "multiplayer", () => "Multiplayer / Co-op Profile");
        this.AddProfileOptions(() => this.Config.Multiplayer, "mp");
    }

    private void AddProfileOptions(Func<TimeSpeedProfile> getProfile, string fieldPrefix)
    {
        if (this.Gmcm is null)
            return;

        const float minSecondsPerMinute = 0.1f;
        const float maxSecondsPerMinute = 15f;

        this.Gmcm.AddSectionTitle(this.ModManifest, () => "General");
        this.Gmcm.AddBoolOption(
            this.ModManifest,
            getValue: () => getProfile().EnableOnFestivalDays,
            setValue: value => getProfile().EnableOnFestivalDays = value,
            name: () => "Enable on festival days",
            tooltip: () => "Whether TimeSpeed changes tick length on festival days.",
            fieldId: $"{fieldPrefix}.EnableOnFestivalDays"
        );
        this.Gmcm.AddBoolOption(
            this.ModManifest,
            getValue: () => getProfile().LocationNotify,
            setValue: value => getProfile().LocationNotify = value,
            name: () => "Location notifications",
            tooltip: () => "Show a message about the active time settings when entering a location.",
            fieldId: $"{fieldPrefix}.LocationNotify"
        );
        this.Gmcm.AddBoolOption(
            this.ModManifest,
            getValue: () => getProfile().LetFarmhandsManageTime,
            setValue: value => getProfile().LetFarmhandsManageTime = value,
            name: () => "Let farmhands manage time",
            tooltip: () => "When hosting multiplayer, allow farmhands to freeze/unfreeze or change time speed. This option is retained in both profiles because it is part of TimeSpeed's full config.",
            fieldId: $"{fieldPrefix}.LetFarmhandsManageTime"
        );

        this.Gmcm.AddSectionTitle(this.ModManifest, () => "Seconds per in-game minute");
        this.AddSpeedOption(getProfile, p => p.SecondsPerMinute.Indoors, (p, v) => p.SecondsPerMinute.Indoors = v, "Indoors", "Default speed for indoor locations.", $"{fieldPrefix}.SecondsPerMinute.Indoors", minSecondsPerMinute, maxSecondsPerMinute);
        this.AddSpeedOption(getProfile, p => p.SecondsPerMinute.Outdoors, (p, v) => p.SecondsPerMinute.Outdoors = v, "Outdoors", "Default speed for outdoor locations.", $"{fieldPrefix}.SecondsPerMinute.Outdoors", minSecondsPerMinute, maxSecondsPerMinute);
        this.AddSpeedOption(getProfile, p => p.SecondsPerMinute.Mines, (p, v) => p.SecondsPerMinute.Mines = v, "Mines", "Speed in mine levels 1-120.", $"{fieldPrefix}.SecondsPerMinute.Mines", minSecondsPerMinute, maxSecondsPerMinute);
        this.AddSpeedOption(getProfile, p => p.SecondsPerMinute.SkullCavern, (p, v) => p.SecondsPerMinute.SkullCavern = v, "Skull Cavern", "Speed in Skull Cavern.", $"{fieldPrefix}.SecondsPerMinute.SkullCavern", minSecondsPerMinute, maxSecondsPerMinute);
        this.AddSpeedOption(getProfile, p => p.SecondsPerMinute.VolcanoDungeon, (p, v) => p.SecondsPerMinute.VolcanoDungeon = v, "Volcano Dungeon", "Speed in the Volcano Dungeon.", $"{fieldPrefix}.SecondsPerMinute.VolcanoDungeon", minSecondsPerMinute, maxSecondsPerMinute);

        this.Gmcm.AddTextOption(
            this.ModManifest,
            getValue: () => getProfile().SecondsPerMinute.FormatLocationOverrides(),
            setValue: value =>
            {
                if (!getProfile().SecondsPerMinute.TrySetLocationOverrides(value))
                {
                    this.Monitor.Log(
                        $"Ignored invalid custom location speed list for {fieldPrefix}. Use entries like Farm=0.9, FarmHouse=2.0 with values from 0.1 to 15.",
                        LogLevel.Trace
                    );
                }
            },
            name: () => "Custom location speeds",
            tooltip: () => "TimeSpeed's SecondsPerMinute.ByLocationName setting. Format: Farm=0.9, FarmHouse=2.0. These overrides are checked before Mines/Skull Cavern/Volcano and Indoors/Outdoors.",
            fieldId: $"{fieldPrefix}.SecondsPerMinute.ByLocationName"
        );

        this.Gmcm.AddSectionTitle(this.ModManifest, () => "Freeze time");
        this.Gmcm.AddNumberOption(
            this.ModManifest,
            getValue: () => Utility.ConvertTimeToMinutes(getProfile().FreezeTime.AnywhereAtTime ?? 2600),
            setValue: value =>
            {
                int time = Utility.ConvertMinutesToTime(value);
                getProfile().FreezeTime.AnywhereAtTime = time == 2600 ? null : time;
            },
            name: () => "Freeze everywhere at time",
            tooltip: () => "Freeze time everywhere at this time of day. Setting this to 2:00 AM means disabled, matching TimeSpeed's own GMCM behavior.",
            min: Utility.ConvertTimeToMinutes(600),
            max: Utility.ConvertTimeToMinutes(2600),
            interval: Utility.ConvertTimeToMinutes(10),
            formatValue: value => Game1.getTimeOfDayString(Utility.ConvertMinutesToTime(value)),
            fieldId: $"{fieldPrefix}.FreezeTime.AnywhereAtTime"
        );
        this.Gmcm.AddBoolOption(
            this.ModManifest,
            getValue: () => getProfile().FreezeTime.PassOut,
            setValue: value => getProfile().FreezeTime.PassOut = value,
            name: () => "Freeze before passing out",
            tooltip: () => "Freeze time at 1:50 AM so the player doesn't pass out from the 2:00 AM limit.",
            fieldId: $"{fieldPrefix}.FreezeTime.PassOut"
        );
        this.Gmcm.AddBoolOption(this.ModManifest, () => getProfile().FreezeTime.Indoors, v => getProfile().FreezeTime.Indoors = v, () => "Freeze indoors", () => "Automatically freeze time in indoor locations.", $"{fieldPrefix}.FreezeTime.Indoors");
        this.Gmcm.AddBoolOption(this.ModManifest, () => getProfile().FreezeTime.Outdoors, v => getProfile().FreezeTime.Outdoors = v, () => "Freeze outdoors", () => "Automatically freeze time in outdoor locations.", $"{fieldPrefix}.FreezeTime.Outdoors");
        this.Gmcm.AddBoolOption(this.ModManifest, () => getProfile().FreezeTime.Mines, v => getProfile().FreezeTime.Mines = v, () => "Freeze in mines", () => "Automatically freeze time in mine levels 1-120.", $"{fieldPrefix}.FreezeTime.Mines");
        this.Gmcm.AddBoolOption(this.ModManifest, () => getProfile().FreezeTime.SkullCavern, v => getProfile().FreezeTime.SkullCavern = v, () => "Freeze in Skull Cavern", () => "Automatically freeze time in Skull Cavern.", $"{fieldPrefix}.FreezeTime.SkullCavern");
        this.Gmcm.AddBoolOption(this.ModManifest, () => getProfile().FreezeTime.VolcanoDungeon, v => getProfile().FreezeTime.VolcanoDungeon = v, () => "Freeze in Volcano Dungeon", () => "Automatically freeze time in the Volcano Dungeon.", $"{fieldPrefix}.FreezeTime.VolcanoDungeon");
        this.Gmcm.AddTextOption(
            this.ModManifest,
            getValue: () => string.Join(", ", getProfile().FreezeTime.ByLocationName.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)),
            setValue: value => getProfile().FreezeTime.ByLocationName = FreezeTimeConfig.ParseLocationList(value),
            name: () => "Always freeze location names",
            tooltip: () => "TimeSpeed's FreezeTime.ByLocationName list. Enter internal location names separated by commas.",
            fieldId: $"{fieldPrefix}.FreezeTime.ByLocationName"
        );
        this.Gmcm.AddTextOption(
            this.ModManifest,
            getValue: () => string.Join(", ", getProfile().FreezeTime.ExceptLocationNames.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)),
            setValue: value => getProfile().FreezeTime.ExceptLocationNames = FreezeTimeConfig.ParseLocationList(value),
            name: () => "Never freeze location names",
            tooltip: () => "TimeSpeed's FreezeTime.ExceptLocationNames list. These locations remain unfrozen even when another freeze rule would match.",
            fieldId: $"{fieldPrefix}.FreezeTime.ExceptLocationNames"
        );

        this.Gmcm.AddSectionTitle(this.ModManifest, () => "Controls");
        this.Gmcm.AddKeybindList(
            this.ModManifest,
            getValue: () => getProfile().Keys.FreezeTime,
            setValue: value => getProfile().Keys.FreezeTime = value,
            name: () => "Freeze / unfreeze",
            tooltip: () => "Manually toggle time freezing. TimeSpeed default: N.",
            fieldId: $"{fieldPrefix}.Keys.FreezeTime"
        );
        this.Gmcm.AddKeybindList(
            this.ModManifest,
            getValue: () => getProfile().Keys.IncreaseTickInterval,
            setValue: value => getProfile().Keys.IncreaseTickInterval = value,
            name: () => "Slow time",
            tooltip: () => "Increase TimeSpeed's tick interval. TimeSpeed default: period (.).",
            fieldId: $"{fieldPrefix}.Keys.IncreaseTickInterval"
        );
        this.Gmcm.AddKeybindList(
            this.ModManifest,
            getValue: () => getProfile().Keys.DecreaseTickInterval,
            setValue: value => getProfile().Keys.DecreaseTickInterval = value,
            name: () => "Speed up time",
            tooltip: () => "Decrease TimeSpeed's tick interval. TimeSpeed default: comma (,).",
            fieldId: $"{fieldPrefix}.Keys.DecreaseTickInterval"
        );
        this.Gmcm.AddKeybindList(
            this.ModManifest,
            getValue: () => getProfile().Keys.ReloadConfig,
            setValue: value => getProfile().Keys.ReloadConfig = value,
            name: () => "Reload TimeSpeed config",
            tooltip: () => "Reload TimeSpeed's active generated config. TimeSpeed default: B.",
            fieldId: $"{fieldPrefix}.Keys.ReloadConfig"
        );
    }

    private void AddSpeedOption(
        Func<TimeSpeedProfile> getProfile,
        Func<TimeSpeedProfile, double> getter,
        Action<TimeSpeedProfile, double> setter,
        string name,
        string tooltip,
        string fieldId,
        float min,
        float max
    )
    {
        this.Gmcm!.AddNumberOption(
            this.ModManifest,
            getValue: () => (float)getter(getProfile()),
            setValue: value => setter(getProfile(), Math.Round(value, 2)),
            name: () => name,
            tooltip: () => tooltip,
            min: min,
            max: max,
            interval: 0.1f,
            fieldId: fieldId
        );
    }

    private void HideOriginalTimeSpeedConfigMenu()
    {
        if (this.Gmcm is null || this.TimeSpeedManifest is null)
            return;

        try
        {
            this.Gmcm.Unregister(this.TimeSpeedManifest);
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Couldn't hide TimeSpeed's original GMCM entry.\n{ex}", LogLevel.Trace);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A leftover temp file is harmless; don't fail profile application for cleanup.
        }
    }
}

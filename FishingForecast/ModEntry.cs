using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace FishingForecast;

public sealed class ModEntry : Mod
{
    private ModConfig config = null!;
    private ForecastCalculator? calculator;

    private bool forecastQueued;
    private bool forecastTickSubscribed;
    private int forecastDelayTicks;

    // Manual area choices are UI preferences, so keep them while the player opens,
    // closes, and refreshes the forecast. Newly discovered areas remain enabled by
    // default because only exclusions are stored.
    private readonly HashSet<string> excludedLocations = new(StringComparer.OrdinalIgnoreCase);

    public override void Entry(IModHelper helper)
    {
        this.config = helper.ReadConfig<ModConfig>();

        // The calculator remains lazy so title/save-selection screens don't trigger
        // any location scans or World Navigator work.
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
        helper.ConsoleCommands.Add("fish_forecast", "Open/recalculate the Fishing Forecast window.", this.OnConsoleCommand);
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady || !this.config.OpenMenu.JustPressed())
            return;

        if (Game1.activeClickableMenu is FishingForecastMenu or ForecastLoadingMenu)
        {
            this.CancelQueuedForecast();
            Game1.exitActiveMenu();
            this.Helper.Input.Suppress(e.Button);
            return;
        }

        // Don't replace inventory/dialogue/etc. Press P again after closing the other menu.
        if (Game1.activeClickableMenu is not null)
            return;

        this.Helper.Input.Suppress(e.Button);
        this.QueueForecast(initialDelayTicks: 2);
    }

    private void OnConsoleCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady)
        {
            this.Monitor.Log("Load a save first.", LogLevel.Info);
            return;
        }

        if (Game1.activeClickableMenu is not null
            && Game1.activeClickableMenu is not FishingForecastMenu
            && Game1.activeClickableMenu is not ForecastLoadingMenu)
        {
            this.Monitor.Log("Close the current menu before opening Fishing Forecast.", LogLevel.Info);
            return;
        }

        this.QueueForecast(initialDelayTicks: 2);
    }

    /// <summary>
    /// Open a lightweight menu immediately, then calculate after a short delay.
    /// World Navigator requires callers to already be in a menu, and after a new
    /// day starts its daily graph can need a few extra ticks before it is complete.
    /// </summary>
    private void QueueForecast(int initialDelayTicks)
    {
        this.forecastQueued = true;
        this.forecastDelayTicks = Math.Max(0, initialDelayTicks);
        Game1.activeClickableMenu = new ForecastLoadingMenu();
        this.EnsureForecastTickSubscription();
    }

    private void EnsureForecastTickSubscription()
    {
        if (this.forecastTickSubscribed)
            return;

        this.Helper.Events.GameLoop.UpdateTicked += this.OnForecastTick;
        this.forecastTickSubscribed = true;
    }

    private void RemoveForecastTickSubscription()
    {
        if (!this.forecastTickSubscribed)
            return;

        this.Helper.Events.GameLoop.UpdateTicked -= this.OnForecastTick;
        this.forecastTickSubscribed = false;
    }

    private void OnForecastTick(object? sender, UpdateTickedEventArgs e)
    {
        if (!this.forecastQueued)
        {
            this.RemoveForecastTickSubscription();
            return;
        }

        if (!Context.IsWorldReady || Game1.activeClickableMenu is not ForecastLoadingMenu)
        {
            this.CancelQueuedForecast();
            return;
        }

        if (this.forecastDelayTicks > 0)
        {
            this.forecastDelayTicks--;
            return;
        }

        this.forecastQueued = false;

        try
        {
            this.calculator ??= new ForecastCalculator(this.config, this.Helper, this.Monitor);
            ForecastReport report = this.calculator.Calculate();

            Game1.activeClickableMenu = new FishingForecastMenu(
                report,
                this.RefreshForecast,
                this.excludedLocations,
                this.SetLocationIncluded
            );

            this.RemoveForecastTickSubscription();
        }
        catch (WorldNavigatorNotReadyException ex)
        {
            // Don't fall back to a raw warp scan and don't cache the one-location
            // result. Keep the loading menu visible while World Navigator finishes
            // its daily scan, then retry automatically.
            this.Monitor.Log($"{ex.Message} Retrying shortly.", LogLevel.Trace);
            this.forecastQueued = true;
            this.forecastDelayTicks = 30;
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Couldn't calculate Fishing Forecast:\n{ex}", LogLevel.Error);
            this.RemoveForecastTickSubscription();

            if (Game1.activeClickableMenu is ForecastLoadingMenu)
                Game1.exitActiveMenu();

            Game1.addHUDMessage(new HUDMessage(
                "Fishing Forecast couldn't calculate. Check the SMAPI console.",
                HUDMessage.error_type
            ));
        }
    }

    private void RefreshForecast()
    {
        this.calculator?.InvalidateReachabilityCache();
        this.QueueForecast(initialDelayTicks: 2);
    }

    private void SetLocationIncluded(string locationName, bool included)
    {
        if (included)
            this.excludedLocations.Remove(locationName);
        else
            this.excludedLocations.Add(locationName);
    }

    private void CancelQueuedForecast()
    {
        this.forecastQueued = false;
        this.forecastDelayTicks = 0;
        this.RemoveForecastTickSubscription();
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        // Explicitly discard every per-day cache. This also prevents a partial
        // World Navigator result from the first moments of a new day from surviving.
        this.calculator?.InvalidateAllCaches();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        this.CancelQueuedForecast();
        this.calculator = null;
    }

    /// <summary>A lightweight visible menu used while World Navigator/the forecast are calculating.</summary>
    private sealed class ForecastLoadingMenu : IClickableMenu
    {
        public ForecastLoadingMenu()
        {
            this.width = Math.Min(720, Game1.uiViewport.Width - 64);
            this.height = 180;
            this.xPositionOnScreen = (Game1.uiViewport.Width - this.width) / 2;
            this.yPositionOnScreen = (Game1.uiViewport.Height - this.height) / 2;
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(
                Game1.fadeToBlackRect,
                new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height),
                Color.Black * 0.68f
            );

            drawTextureBox(
                b,
                this.xPositionOnScreen,
                this.yPositionOnScreen,
                this.width,
                this.height,
                Color.White
            );

            Utility.drawTextWithShadow(
                b,
                "Fishing Forecast",
                Game1.dialogueFont,
                new Vector2(this.xPositionOnScreen + 28, this.yPositionOnScreen + 26),
                Game1.textColor
            );

            b.DrawString(
                Game1.smallFont,
                "Calculating reachable fishing locations…",
                new Vector2(this.xPositionOnScreen + 30, this.yPositionOnScreen + 92),
                Game1.textColor * 0.85f
            );

            this.drawMouse(b);
        }
    }
}

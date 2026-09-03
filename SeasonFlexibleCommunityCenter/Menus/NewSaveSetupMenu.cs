using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SeasonFlexibleCommunityCenter.Models;
using SeasonFlexibleCommunityCenter.Utilities;
using StardewValley;
using StardewValley.Menus;

namespace SeasonFlexibleCommunityCenter.Menus;

internal sealed class NewSaveSetupMenu : IClickableMenu
{
    private readonly ModEntry Mod;
    private readonly SaveSettings Working;
    private readonly bool IsFirstSetup;
    private readonly IClickableMenu? ReturnMenu;
    private readonly Action<SaveSettings>? SaveHandler;

    private int DraggingSlider = -1;

    public NewSaveSetupMenu(
        ModEntry mod,
        SaveSettings settings,
        bool isFirstSetup,
        IClickableMenu? returnMenu = null,
        Action<SaveSettings>? saveHandler = null
    )
        : base(
            (Game1.uiViewport.Width - Math.Min(900, Game1.uiViewport.Width - 70)) / 2,
            (Game1.uiViewport.Height - Math.Min(700, Game1.uiViewport.Height - 70)) / 2,
            Math.Min(900, Game1.uiViewport.Width - 70),
            Math.Min(700, Game1.uiViewport.Height - 70)
        )
    {
        Mod = mod;
        Working = settings.Clone();
        IsFirstSetup = isFirstSetup;
        ReturnMenu = returnMenu;
        SaveHandler = saveHandler;
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (RelaxedButton.Contains(x, y))
        {
            Working.ApplyPreset("relaxed");
            Game1.playSound("smallSelect");
            return;
        }
        if (BalancedButton.Contains(x, y))
        {
            Working.ApplyPreset("balanced");
            Game1.playSound("smallSelect");
            return;
        }
        if (ChallengingButton.Contains(x, y))
        {
            Working.ApplyPreset("challenging");
            Game1.playSound("smallSelect");
            return;
        }

        for (int i = 0; i < 3; i++)
        {
            if (SliderBounds(i).Contains(x, y))
            {
                DraggingSlider = i;
                SetSliderFromX(i, x);
                Game1.playSound("smallSelect");
                return;
            }
        }

        if (CropButton.Contains(x, y)) { Working.EnableCrops = !Working.EnableCrops; Game1.playSound("drumkit6"); return; }
        if (FishButton.Contains(x, y)) { Working.EnableFish = !Working.EnableFish; Game1.playSound("drumkit6"); return; }
        if (ForageButton.Contains(x, y)) { Working.EnableForage = !Working.EnableForage; Game1.playSound("drumkit6"); return; }
        if (FruitButton.Contains(x, y)) { Working.EnableFruit = !Working.EnableFruit; Game1.playSound("drumkit6"); return; }

        if (SaveButton.Contains(x, y))
        {
            SaveAndClose();
            return;
        }
    }

    public override void leftClickHeld(int x, int y)
    {
        if (DraggingSlider >= 0)
            SetSliderFromX(DraggingSlider, x);
        base.leftClickHeld(x, y);
    }

    public override void releaseLeftClick(int x, int y)
    {
        DraggingSlider = -1;
        base.releaseLeftClick(x, y);
    }

    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape)
        {
            // First-save setup must leave behind deterministic settings, so Escape saves the current values.
            SaveAndClose();
            return;
        }
        base.receiveKeyPress(key);
    }

    public override void receiveGamePadButton(Buttons button)
    {
        if (button == Buttons.B)
        {
            SaveAndClose();
            return;
        }
        base.receiveGamePadButton(button);
    }

    public override void draw(SpriteBatch b)
    {
        Rectangle viewport = new(Game1.uiViewport.X, Game1.uiViewport.Y, Game1.uiViewport.Width, Game1.uiViewport.Height);
        b.Draw(Game1.fadeToBlackRect, viewport, Color.Black * 0.72f);
        Ui.DrawBox(b, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height));

        string title = IsFirstSetup ? "Season-Flexible Community Center" : "Season Exchange Settings";
        Utility.drawTextWithShadow(b, title, Game1.dialogueFont,
            new Vector2(xPositionOnScreen + 28, yPositionOnScreen + 20), Game1.textColor);

        string description = IsFirstSetup
            ? "Choose how expensive it should be to complete future-season bundle requirements early. These settings are saved per farm."
            : "Adjust this farm's exchange difficulty. Generic Mod Config Menu exposes the same controls during play.";
        DrawWrapped(b, description, new Rectangle(xPositionOnScreen + 30, yPositionOnScreen + 70, width - 60, 70));

        Utility.drawTextWithShadow(b, "Presets", Game1.smallFont, new Vector2(xPositionOnScreen + 34, yPositionOnScreen + 128), Game1.textColor);
        DrawButton(b, RelaxedButton, "Relaxed");
        DrawButton(b, BalancedButton, "Balanced");
        DrawButton(b, ChallengingButton, "Challenging");

        DrawSlider(b, 0, "Season penalty per season", Working.SeasonPenaltyPercent, 100, 2000,
            $"×{Working.SeasonPenaltyPercent / 100d:0.##}");
        DrawSlider(b, 1, "Value scaling", Working.ValueScalingPercent, 0, 100, $"{Working.ValueScalingPercent}%");
        DrawSlider(b, 2, "Quality credit", Working.QualityCreditPercent, 0, 100, $"{Working.QualityCreditPercent}%");

        int categoryY = yPositionOnScreen + 478;
        Utility.drawTextWithShadow(b, "Allowed categories", Game1.smallFont,
            new Vector2(xPositionOnScreen + 34, categoryY - 30), Game1.textColor);
        DrawToggle(b, CropButton, "Crops", Working.EnableCrops);
        DrawToggle(b, FishButton, "Fish", Working.EnableFish);
        DrawToggle(b, ForageButton, "Forage", Working.EnableForage);
        DrawToggle(b, FruitButton, "Tree fruit", Working.EnableFruit);

        DrawButton(b, SaveButton, IsFirstSetup ? "Start with these settings" : "Save settings");

        string example = $"Example: an item 2 seasons ahead has a base season multiplier of ×{Math.Pow(Working.SeasonPenaltyPercent / 100d, 2):0.##} before value/quality adjustment.";
        DrawWrapped(b, example, new Rectangle(xPositionOnScreen + 34, yPositionOnScreen + height - 80, width - 68, 42), Color.DimGray);

        drawMouse(b);
    }

    private void SaveAndClose()
    {
        Working.SetupComplete = true;
        Working.Validate();
        if (SaveHandler is not null)
            SaveHandler(Working);
        else
            Mod.ApplySaveSettings(Working);

        Game1.playSound("bigSelect");
        Game1.activeClickableMenu = ReturnMenu;
    }

    private void DrawSlider(SpriteBatch b, int index, string name, int value, int min, int max, string formatted)
    {
        Rectangle bounds = SliderBounds(index);
        Utility.drawTextWithShadow(b, name, Game1.smallFont, new Vector2(bounds.X, bounds.Y - 27), Game1.textColor);
        Vector2 valueSize = Game1.smallFont.MeasureString(formatted);
        Utility.drawTextWithShadow(b, formatted, Game1.smallFont,
            new Vector2(bounds.Right - valueSize.X, bounds.Y - 27), Game1.textColor);

        Rectangle track = new(bounds.X, bounds.Center.Y - 2, bounds.Width, 4);
        b.Draw(Game1.fadeToBlackRect, track, Color.DimGray * 0.8f);
        float t = (value - min) / (float)(max - min);
        int knobX = bounds.X + (int)Math.Round(t * bounds.Width);
        Rectangle knob = new(knobX - 9, bounds.Center.Y - 12, 18, 24);
        b.Draw(Game1.fadeToBlackRect, knob, Color.SaddleBrown * 0.95f);
    }

    private void SetSliderFromX(int index, int x)
    {
        Rectangle bounds = SliderBounds(index);
        double t = Math.Clamp((x - bounds.X) / (double)bounds.Width, 0d, 1d);
        if (index == 0)
            Working.SeasonPenaltyPercent = RoundStep(100 + t * 1900, 25);
        else if (index == 1)
            Working.ValueScalingPercent = RoundStep(t * 100, 5);
        else
            Working.QualityCreditPercent = RoundStep(t * 100, 5);
    }

    private static int RoundStep(double value, int step) => (int)(Math.Round(value / step) * step);

    private static void DrawButton(SpriteBatch b, Rectangle bounds, string text)
    {
        Ui.DrawBox(b, bounds, Color.White, false);
        Ui.DrawCentered(b, text, Game1.smallFont, bounds, Game1.textColor);
    }

    private static void DrawToggle(SpriteBatch b, Rectangle bounds, string label, bool enabled)
    {
        Ui.DrawBox(b, bounds, enabled ? Color.White : Color.Gray * 0.85f, false);
        Ui.DrawCentered(b, $"{label}: {(enabled ? "On" : "Off")}", Game1.smallFont, bounds, enabled ? Game1.textColor : Color.DimGray);
    }

    private static void DrawWrapped(SpriteBatch b, string text, Rectangle bounds, Color? color = null)
    {
        string wrapped = Game1.parseText(text, Game1.smallFont, bounds.Width);
        Utility.drawTextWithShadow(b, wrapped, Game1.smallFont, new Vector2(bounds.X, bounds.Y), color ?? Game1.textColor);
    }

    private Rectangle RelaxedButton => new(xPositionOnScreen + 34, yPositionOnScreen + 157, 180, 45);
    private Rectangle BalancedButton => new(xPositionOnScreen + width / 2 - 90, yPositionOnScreen + 157, 180, 45);
    private Rectangle ChallengingButton => new(xPositionOnScreen + width - 214, yPositionOnScreen + 157, 180, 45);

    private Rectangle SliderBounds(int index) => new(xPositionOnScreen + 58, yPositionOnScreen + 246 + index * 74, width - 116, 30);

    private Rectangle CropButton => new(xPositionOnScreen + 34, yPositionOnScreen + 478, (width - 92) / 4, 44);
    private Rectangle FishButton => new(CropButton.Right + 8, CropButton.Y, CropButton.Width, CropButton.Height);
    private Rectangle ForageButton => new(FishButton.Right + 8, CropButton.Y, CropButton.Width, CropButton.Height);
    private Rectangle FruitButton => new(ForageButton.Right + 8, CropButton.Y, CropButton.Width, CropButton.Height);

    private Rectangle SaveButton => new(xPositionOnScreen + width / 2 - 150, yPositionOnScreen + 545, 300, 52);
}

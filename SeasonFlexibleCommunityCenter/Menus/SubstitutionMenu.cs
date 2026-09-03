using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SeasonFlexibleCommunityCenter.Models;
using SeasonFlexibleCommunityCenter.Services;
using SeasonFlexibleCommunityCenter.Utilities;
using StardewValley;
using StardewValley.Menus;

namespace SeasonFlexibleCommunityCenter.Menus;

internal sealed class SubstitutionMenu : IClickableMenu
{
    private const int VisibleRows = 7;
    private const int RowHeight = 58;

    private readonly ModEntry Mod;
    private readonly JunimoNoteMenu PreviousMenu;
    private readonly Bundle Bundle;
    private readonly SubstitutionEngine Engine;
    private readonly List<TargetOption> Targets;

    private List<CandidateOption> Candidates = new();
    private int SelectedTarget;
    private int TargetScroll;
    private int CandidateScroll;
    private string Status = "Choose a future-season requirement, then choose what to trade.";

    private readonly Rectangle CloseButton;

    public SubstitutionMenu(ModEntry mod, JunimoNoteMenu previousMenu, Bundle bundle, SubstitutionEngine engine)
        : base(
            (Game1.uiViewport.Width - Math.Min(1060, Game1.uiViewport.Width - 80)) / 2,
            (Game1.uiViewport.Height - Math.Min(720, Game1.uiViewport.Height - 80)) / 2,
            Math.Min(1060, Game1.uiViewport.Width - 80),
            Math.Min(720, Game1.uiViewport.Height - 80)
        )
    {
        Mod = mod;
        PreviousMenu = previousMenu;
        Bundle = bundle;
        Engine = engine;
        Targets = Engine.GetTargets(bundle);
        if (Targets.Count > 0)
            Candidates = Engine.GetCandidates(Targets[0]);

        CloseButton = new Rectangle(xPositionOnScreen + width - 56, yPositionOnScreen + 18, 38, 38);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (CloseButton.Contains(x, y))
        {
            Game1.playSound("bigDeSelect");
            ReturnToBundle();
            return;
        }

        Rectangle targetPanel = TargetPanel;
        Rectangle candidatePanel = CandidatePanel;

        for (int row = 0; row < VisibleRows; row++)
        {
            int index = TargetScroll + row;
            if (index >= Targets.Count)
                break;
            Rectangle bounds = new(targetPanel.X + 10, targetPanel.Y + 48 + row * RowHeight, targetPanel.Width - 20, RowHeight - 4);
            if (bounds.Contains(x, y))
            {
                SelectedTarget = index;
                CandidateScroll = 0;
                Candidates = Engine.GetCandidates(Targets[SelectedTarget]);
                Status = Candidates.Count == 0
                    ? "No same-category items from the current season are in your backpack."
                    : "Choose a substitute. The required amount includes season, value, and quality scaling.";
                Game1.playSound("smallSelect");
                return;
            }
        }

        if (Targets.Count == 0 || SelectedTarget >= Targets.Count)
            return;

        for (int row = 0; row < VisibleRows; row++)
        {
            int index = CandidateScroll + row;
            if (index >= Candidates.Count)
                break;
            Rectangle bounds = new(candidatePanel.X + 10, candidatePanel.Y + 48 + row * RowHeight, candidatePanel.Width - 20, RowHeight - 4);
            if (!bounds.Contains(x, y))
                continue;

            CandidateOption candidate = Candidates[index];
            if (candidate.Have < candidate.Need)
            {
                Status = $"Need {candidate.Need}; you currently have {candidate.Have}.";
                Game1.playSound("cancel");
                return;
            }

            ExchangeResult result = Engine.TryExchange(Bundle, Targets[SelectedTarget], candidate);
            Status = result.Message;
            if (!result.Success)
            {
                Game1.playSound("cancel");
                Candidates = Engine.GetCandidates(Targets[SelectedTarget]);
                return;
            }

            Game1.playSound("newArtifact");
            Mod.RefreshBundleMenu(PreviousMenu, Bundle.bundleIndex, result.BundleCompleted);
            return;
        }
    }

    public override void receiveScrollWheelAction(int direction)
    {
        Point mouse = Game1.getMousePosition(true);
        if (TargetPanel.Contains(mouse))
            TargetScroll = ClampScroll(TargetScroll + (direction < 0 ? 1 : -1), Targets.Count);
        else if (CandidatePanel.Contains(mouse))
            CandidateScroll = ClampScroll(CandidateScroll + (direction < 0 ? 1 : -1), Candidates.Count);
        base.receiveScrollWheelAction(direction);
    }

    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape)
        {
            Game1.playSound("bigDeSelect");
            ReturnToBundle();
            return;
        }
        base.receiveKeyPress(key);
    }

    public override void receiveGamePadButton(Buttons button)
    {
        if (button == Buttons.B)
        {
            Game1.playSound("bigDeSelect");
            ReturnToBundle();
            return;
        }
        base.receiveGamePadButton(button);
    }

    public override void draw(SpriteBatch b)
    {
        Rectangle viewport = new(Game1.uiViewport.X, Game1.uiViewport.Y, Game1.uiViewport.Width, Game1.uiViewport.Height);
        b.Draw(Game1.fadeToBlackRect, viewport, Color.Black * 0.72f);
        Ui.DrawBox(b, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height));

        Utility.drawTextWithShadow(b, "Season Exchange", Game1.dialogueFont,
            new Vector2(xPositionOnScreen + 28, yPositionOnScreen + 20), Game1.textColor);
        Utility.drawTextWithShadow(b, Status, Game1.smallFont,
            new Vector2(xPositionOnScreen + 30, yPositionOnScreen + 70), Game1.textColor * 0.9f);

        Ui.DrawBox(b, CloseButton, Color.White, false);
        Ui.DrawCentered(b, "X", Game1.smallFont, CloseButton, Game1.textColor);

        DrawTargetPanel(b);
        DrawCandidatePanel(b);
        DrawFooter(b);
        drawMouse(b);
    }

    private void DrawTargetPanel(SpriteBatch b)
    {
        Rectangle panel = TargetPanel;
        Ui.DrawBox(b, panel, Color.White, false);
        Utility.drawTextWithShadow(b, "Future requirement", Game1.smallFont,
            new Vector2(panel.X + 12, panel.Y + 12), Game1.textColor);

        if (Targets.Count == 0)
        {
            DrawWrapped(b,
                "This bundle has no incomplete requirement whose normal season is still ahead of the current season.",
                new Rectangle(panel.X + 18, panel.Y + 65, panel.Width - 36, panel.Height - 80));
            return;
        }

        for (int row = 0; row < VisibleRows; row++)
        {
            int index = TargetScroll + row;
            if (index >= Targets.Count)
                break;
            TargetOption target = Targets[index];
            Rectangle bounds = new(panel.X + 10, panel.Y + 48 + row * RowHeight, panel.Width - 20, RowHeight - 4);

            if (index == SelectedTarget)
                b.Draw(Game1.fadeToBlackRect, bounds, Color.Wheat * 0.32f);

            target.DisplayItem.drawInMenu(b, new Vector2(bounds.X + 4, bounds.Y + 4), 0.72f);
            string line = $"{target.DisplayItem.DisplayName}  ×{Math.Max(1, target.Ingredient.stack)}";
            Utility.drawTextWithShadow(b, line, Game1.smallFont, new Vector2(bounds.X + 58, bounds.Y + 6), Game1.textColor);
            Utility.drawTextWithShadow(b, $"{target.SeasonGap} season{(target.SeasonGap == 1 ? "" : "s")} away",
                Game1.smallFont, new Vector2(bounds.X + 58, bounds.Y + 29), Color.DimGray);
        }
    }

    private void DrawCandidatePanel(SpriteBatch b)
    {
        Rectangle panel = CandidatePanel;
        Ui.DrawBox(b, panel, Color.White, false);
        Utility.drawTextWithShadow(b, "Current-season substitute", Game1.smallFont,
            new Vector2(panel.X + 12, panel.Y + 12), Game1.textColor);

        if (Targets.Count == 0)
            return;
        if (Candidates.Count == 0)
        {
            DrawWrapped(b,
                "Put a same-category item from the current season in your backpack, then reopen this screen.",
                new Rectangle(panel.X + 18, panel.Y + 65, panel.Width - 36, panel.Height - 80));
            return;
        }

        for (int row = 0; row < VisibleRows; row++)
        {
            int index = CandidateScroll + row;
            if (index >= Candidates.Count)
                break;
            CandidateOption candidate = Candidates[index];
            Rectangle bounds = new(panel.X + 10, panel.Y + 48 + row * RowHeight, panel.Width - 20, RowHeight - 4);
            bool affordable = candidate.Have >= candidate.Need;
            Color text = affordable ? Game1.textColor : Color.Gray;

            if (bounds.Contains(Game1.getMousePosition(true)))
                b.Draw(Game1.fadeToBlackRect, bounds, (affordable ? Color.Wheat : Color.Gray) * 0.22f);

            candidate.Sample.drawInMenu(b, new Vector2(bounds.X + 4, bounds.Y + 4), 0.72f, 1f, 0.9f, StackDrawType.Hide);
            Utility.drawTextWithShadow(b, candidate.Sample.DisplayName, Game1.smallFont,
                new Vector2(bounds.X + 58, bounds.Y + 5), text);

            string quality = candidate.Quality > 0 ? $" • {Ui.QualityName(candidate.Quality)}" : "";
            string amount = $"Need {candidate.Need} • Have {candidate.Have}{quality}";
            Utility.drawTextWithShadow(b, amount, Game1.smallFont,
                new Vector2(bounds.X + 58, bounds.Y + 29), affordable ? Color.DarkGreen : Color.DarkRed);
        }
    }

    private void DrawFooter(SpriteBatch b)
    {
        SaveSettings s = Mod.Settings;
        string factor = (s.SeasonPenaltyPercent / 100d).ToString("0.##");
        string footer = $"Season penalty ×{factor} per season • Value scaling {s.ValueScalingPercent}% • Quality credit {s.QualityCreditPercent}%";
        Vector2 size = Game1.smallFont.MeasureString(footer);
        Utility.drawTextWithShadow(b, footer, Game1.smallFont,
            new Vector2(xPositionOnScreen + width / 2f - size.X / 2f, yPositionOnScreen + height - 38), Color.DimGray);
    }

    private void ReturnToBundle() => Game1.activeClickableMenu = PreviousMenu;

    private Rectangle TargetPanel => new(xPositionOnScreen + 22, yPositionOnScreen + 108, (width - 58) / 2, height - 170);
    private Rectangle CandidatePanel => new(TargetPanel.Right + 14, TargetPanel.Y, width - TargetPanel.Width - 58, TargetPanel.Height);

    private static int ClampScroll(int value, int count) => Math.Clamp(value, 0, Math.Max(0, count - VisibleRows));

    private static void DrawWrapped(SpriteBatch b, string text, Rectangle bounds)
    {
        string wrapped = Game1.parseText(text, Game1.smallFont, bounds.Width);
        Utility.drawTextWithShadow(b, wrapped, Game1.smallFont, new Vector2(bounds.X, bounds.Y), Color.DimGray);
    }
}

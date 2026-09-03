using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace SeasonFlexibleCommunityCenter.Utilities;

internal static class Ui
{
    public static void DrawBox(SpriteBatch b, Rectangle bounds, Color? tint = null, bool shadow = true)
    {
        IClickableMenu.drawTextureBox(
            b,
            Game1.menuTexture,
            new Rectangle(0, 256, 60, 60),
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            tint ?? Color.White,
            1f,
            shadow
        );
    }

    public static void DrawCentered(SpriteBatch b, string text, SpriteFont font, Rectangle bounds, Color color)
    {
        Vector2 size = font.MeasureString(text);
        Vector2 pos = new(bounds.Center.X - size.X / 2f, bounds.Center.Y - size.Y / 2f);
        Utility.drawTextWithShadow(b, text, font, pos, color);
    }

    public static string QualityName(int quality) => quality switch
    {
        1 => "Silver",
        2 => "Gold",
        4 => "Iridium",
        _ => "Normal"
    };
}

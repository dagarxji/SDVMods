using System.Reflection;
using System.Text;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace InventoryInsight;

[HarmonyPatch]
internal static class TooltipPatch
{
    /// <summary>
    /// Patch only the deepest StringBuilder drawHoverText overload. The string overload and drawToolTip funnel into it,
    /// so this avoids duplicate panels while still covering standard inventory/chest/shop item tooltips.
    /// </summary>
    private static MethodBase TargetMethod()
    {
        MethodBase? target = AccessTools.GetDeclaredMethods(typeof(IClickableMenu))
            .FirstOrDefault(method =>
            {
                if (method.Name != nameof(IClickableMenu.drawHoverText))
                    return false;

                ParameterInfo[] p = method.GetParameters();
                return p.Length > 2
                    && p[0].ParameterType == typeof(SpriteBatch)
                    && p[1].ParameterType == typeof(StringBuilder)
                    && p.Any(arg => arg.Name == "hoveredItem" && arg.ParameterType == typeof(Item));
            });

        return target ?? throw new InvalidOperationException("Couldn't locate Stardew's item drawHoverText overload.");
    }

    private static void Postfix(SpriteBatch b, Item? hoveredItem)
    {
        ModEntry.Instance?.Renderer.Draw(b, hoveredItem);
    }
}

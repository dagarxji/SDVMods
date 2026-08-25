namespace InventoryInsight;

internal sealed class ModConfig
{
    // Both views are intentionally fixed-size. Compact rows are forced onto one line so inventory scanning
    // never causes the panel to grow/shrink based on item contents.
    public int CompactWidth { get; set; } = 540;
    public int CompactHeight { get; set; } = 335;
    public int ExpandedWidth { get; set; } = 820;
    public int ExpandedHeight { get; set; } = 730;
    public int CompactLoveLimit { get; set; } = 5;
    public int ExpandedLoveLimit { get; set; } = 20;
    public int ExpandedRecipeLimit { get; set; } = 8;
    public int ExpandedMachineLimit { get; set; } = 4;

    /// <summary>When true, loved gifts make Safe to sell = No.</summary>
    public bool GiftsPreventSafeSell { get; set; } = true;

    /// <summary>When true, any profitable deterministic machine route makes Safe to sell = No.</summary>
    public bool ProfitableMachinesPreventSafeSell { get; set; } = true;
}

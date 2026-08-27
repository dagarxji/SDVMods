namespace InventoryInsight;

internal sealed record CraftingUse(string RecipeName, string OutputItemId, int RequiredCount);

internal sealed record QuestUse(string Name, string Kind);

internal sealed record QualityValue(int Quality, int RawValue, int OutputValue);

internal sealed record ConsumedItem(string ItemId, int Count, int Value);

internal sealed record MachineRoute(
    string MachineItemId,
    string MachineName,
    string OutputItemId,
    string OutputName,
    int RequiredInputCount,
    IReadOnlyList<ConsumedItem> AdditionalInputs,
    IReadOnlyList<QualityValue> Values
)
{
    public int AdditionalInputCost => AdditionalInputs.Sum(p => p.Value);
    public bool IsProfitable => Values.Any(p => p.OutputValue > p.RawValue + AdditionalInputCost);
}

internal sealed class ItemInsight
{
    public string ItemId { get; init; } = null!;
    public string DisplayName { get; init; } = null!;
    public IReadOnlyList<string> LovedBy { get; init; } = null!;
    public bool CommunityCenterNeeded { get; init; }
    public bool MuseumNeeded { get; init; }
    public IReadOnlyList<QuestUse> QuestUses { get; init; } = null!;
    public IReadOnlyList<CraftingUse> CraftingUses { get; init; } = null!;
    public int SellPrice { get; init; }
    public IReadOnlyList<MachineRoute> MachineRoutes { get; init; } = null!;
    public bool SafeToSell { get; init; }
}

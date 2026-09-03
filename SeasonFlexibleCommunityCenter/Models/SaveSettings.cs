namespace SeasonFlexibleCommunityCenter.Models;

public sealed class SaveSettings
{
    public bool SetupComplete { get; set; }

    /// <summary>100 = no seasonal penalty, 200 = x2 per season early, 300 = x3 per season early.</summary>
    public int SeasonPenaltyPercent { get; set; } = 200;

    /// <summary>How strongly sell-price differences affect the exchange quantity.</summary>
    public int ValueScalingPercent { get; set; } = 100;

    /// <summary>How much a substitute's silver/gold/iridium value reduces the quantity needed.</summary>
    public int QualityCreditPercent { get; set; } = 100;

    public int MinimumQuantity { get; set; } = 1;
    public int MaximumQuantity { get; set; } = 999;

    public bool EnableCrops { get; set; } = true;
    public bool EnableFish { get; set; } = true;
    public bool EnableForage { get; set; } = true;
    public bool EnableFruit { get; set; } = true;

    public SaveSettings Clone() => new()
    {
        SetupComplete = SetupComplete,
        SeasonPenaltyPercent = SeasonPenaltyPercent,
        ValueScalingPercent = ValueScalingPercent,
        QualityCreditPercent = QualityCreditPercent,
        MinimumQuantity = MinimumQuantity,
        MaximumQuantity = MaximumQuantity,
        EnableCrops = EnableCrops,
        EnableFish = EnableFish,
        EnableForage = EnableForage,
        EnableFruit = EnableFruit
    };

    public void Validate()
    {
        SeasonPenaltyPercent = Math.Clamp(SeasonPenaltyPercent, 100, 400);
        ValueScalingPercent = Math.Clamp(ValueScalingPercent, 0, 100);
        QualityCreditPercent = Math.Clamp(QualityCreditPercent, 0, 100);
        MinimumQuantity = Math.Clamp(MinimumQuantity, 1, 999);
        MaximumQuantity = Math.Clamp(MaximumQuantity, MinimumQuantity, 999);
    }

    public void ApplyPreset(string preset)
    {
        switch (preset.ToLowerInvariant())
        {
            case "relaxed":
                SeasonPenaltyPercent = 150;
                ValueScalingPercent = 70;
                QualityCreditPercent = 100;
                break;
            case "challenging":
                SeasonPenaltyPercent = 250;
                ValueScalingPercent = 100;
                QualityCreditPercent = 75;
                break;
            default:
                SeasonPenaltyPercent = 200;
                ValueScalingPercent = 100;
                QualityCreditPercent = 100;
                break;
        }
    }
}

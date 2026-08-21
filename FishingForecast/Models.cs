using Microsoft.Xna.Framework;

namespace FishingForecast;

internal sealed record ForecastReport(
    IReadOnlyList<ForecastSlot> Slots,
    string ReachabilitySource,
    string EquipmentSummary,
    int FishingLevel,
    string WeatherSummary,
    int ReachableLocationCount,
    int EvaluatedLocationCount
);

internal sealed record ForecastSlot(
    string Label,
    int StartTime,
    int EndTime,
    IReadOnlyList<LocationForecast> Locations
);

internal sealed record LocationForecast(
    string LocationName,
    string DisplayName,
    double GoldPerHour,
    double FourHourGross,
    double TravelAdjustedGold,
    int EstimatedTravelMinutes,
    string BestCatchName,
    double BestCatchShare,
    Vector2 BestBobberTile,
    int WaterDepth,
    double ExpectedCatches,
    double TravelAdjustedExpectedCatches,
    IReadOnlyList<FishCatchForecast> Fish
);

/// <summary>
/// Expected contribution of one fish species to a four-hour forecast block.
/// ShareOfCatchSlots is deliberately the finite-block share after catch limits,
/// not the raw per-hook probability before a one-time fish is removed.
/// </summary>
internal sealed record FishCatchForecast(
    string QualifiedItemId,
    string DisplayName,
    double ExpectedCount,
    double ShareOfCatchSlots,
    double ExpectedGold,
    int SaleValue,
    int RemainingCatchLimit
);

internal sealed record CandidateFishingTile(Vector2 Tile, int WaterDepth, Vector2 PlayerTile);

internal sealed class CatchAggregate
{
    public int Count { get; set; }
    public double TotalValue { get; set; }
    public string Name { get; set; } = "";
    public string QualifiedItemId { get; set; } = "";
    public bool IsFish { get; set; }
}

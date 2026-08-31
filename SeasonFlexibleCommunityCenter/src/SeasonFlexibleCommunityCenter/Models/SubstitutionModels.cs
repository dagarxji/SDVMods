using StardewValley;
using StardewValley.Menus;

namespace SeasonFlexibleCommunityCenter.Models;

internal sealed record TargetOption(
    int IngredientIndex,
    BundleIngredientDescription Ingredient,
    Item DisplayItem,
    ItemKind Kind,
    IReadOnlyCollection<string> TargetSeasons,
    int SeasonGap
);

internal sealed record CandidateOption(
    string QualifiedItemId,
    int Quality,
    Item Sample,
    int Have,
    int Need
);

internal sealed record ExchangeResult(bool Success, bool BundleCompleted, string Message);

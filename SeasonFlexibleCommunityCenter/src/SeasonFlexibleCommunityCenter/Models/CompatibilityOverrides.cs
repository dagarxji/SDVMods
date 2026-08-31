namespace SeasonFlexibleCommunityCenter.Models;

public sealed class CompatibilityOverrides
{
    public Dictionary<string, CompatibilityItemRule> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CompatibilityItemRule
{
    public string Kind { get; set; } = "";
    public List<string> Seasons { get; set; } = new();
}

namespace SeasonFlexibleCommunityCenter.Models;

[Flags]
internal enum ItemKind
{
    None = 0,
    Crop = 1,
    Fish = 2,
    Forage = 4,
    Fruit = 8
}

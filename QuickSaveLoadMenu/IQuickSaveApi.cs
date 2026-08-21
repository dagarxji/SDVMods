using StardewModdingAPI;

namespace QuickSaveLoadMenu;

/// <summary>
/// The subset of DLX.QuickSave's public API used by this mod.
/// This must be a public interface for SMAPI's API proxy mapper.
/// </summary>
public interface IQuickSaveApi
{
    bool IsSaving { get; }
    bool IsLoading { get; }

    bool TryLoad(IManifest requester, string? saveFileName = null);
}

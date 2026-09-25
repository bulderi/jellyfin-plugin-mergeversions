using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.MergeVersions;

internal sealed record IncrementalMergeTarget(
    string Key,
    BaseItemKind ItemType,
    string ProviderName = null,
    string ProviderValue = null,
    string SeriesPresentationUniqueKey = null,
    string ItemName = null,
    int? ParentIndexNumber = null,
    int? IndexNumber = null);

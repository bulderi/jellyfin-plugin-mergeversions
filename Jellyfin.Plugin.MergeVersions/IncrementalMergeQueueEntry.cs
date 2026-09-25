namespace Jellyfin.Plugin.MergeVersions;

internal sealed record IncrementalMergeQueueEntry(
    long Revision,
    IncrementalMergeTarget Target);

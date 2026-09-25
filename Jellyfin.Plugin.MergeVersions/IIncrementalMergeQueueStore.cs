using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MergeVersions;

internal interface IIncrementalMergeQueueStore : IDisposable
{
    IReadOnlyCollection<IncrementalMergeQueueEntry> Load();

    void Enqueue(IncrementalMergeQueueEntry entry);

    void Complete(IncrementalMergeQueueEntry entry);
}

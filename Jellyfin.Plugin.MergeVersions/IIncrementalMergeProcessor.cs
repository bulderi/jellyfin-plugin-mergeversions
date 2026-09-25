using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.MergeVersions;

internal interface IIncrementalMergeProcessor
{
    bool TryCreateIncrementalTarget(BaseItem item, out IncrementalMergeTarget target);

    Task<int> MergeIncrementalBatchAsync(
        IReadOnlyCollection<IncrementalMergeTarget> targets,
        CancellationToken cancellationToken);
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MergeVersions;

internal sealed class IncrementalMergeService : BackgroundService
{
    private const int BatchSize = 256;
    private static readonly ItemUpdateType RelevantUpdateTypes =
        ItemUpdateType.MetadataDownload | ItemUpdateType.MetadataImport;

    private readonly ILibraryManager _libraryManager;
    private readonly IIncrementalMergeProcessor _mergeProcessor;
    private readonly IIncrementalMergeQueueStore _queueStore;
    private readonly ILogger<IncrementalMergeService> _logger;
    private readonly ConcurrentDictionary<string, IncrementalMergeQueueEntry> _pending =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<byte> _wakeUp = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    private long _nextRevision;

    internal TimeSpan DebounceDelay { get; set; } = TimeSpan.FromSeconds(60);

    public IncrementalMergeService(
        ILibraryManager libraryManager,
        IIncrementalMergeProcessor mergeProcessor,
        IIncrementalMergeQueueStore queueStore,
        ILogger<IncrementalMergeService> logger)
    {
        _libraryManager = libraryManager;
        _mergeProcessor = mergeProcessor;
        _queueStore = queueStore;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        RestorePendingTargets();
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _libraryManager.ItemRemoved += OnItemRemoved;
        _logger.LogInformation("Incremental version merging is enabled");
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        _wakeUp.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _wakeUp.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                DrainWakeUps();
                await Task.Delay(DebounceDelay, stoppingToken).ConfigureAwait(false);
                DrainWakeUps();

                while (!_pending.IsEmpty)
                {
                    var batch = TakeBatch();
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    try
                    {
                        var mergedGroups = await _mergeProcessor
                            .MergeIncrementalBatchAsync(
                                batch.Select(entry => entry.Target).ToArray(),
                                stoppingToken)
                            .ConfigureAwait(false);
                        CompleteBatch(batch);
                        _logger.LogInformation(
                            "Processed {TargetCount} changed version groups; merged {MergedGroupCount}",
                            batch.Count,
                            mergedGroups);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "Incremental version merge failed for a batch of {TargetCount} groups",
                            batch.Count);
                        await Task.Delay(DebounceDelay, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal service shutdown.
        }
    }

    private void OnItemAdded(object sender, ItemChangeEventArgs eventArgs)
        => Queue(eventArgs.Item);

    private void OnItemRemoved(object sender, ItemChangeEventArgs eventArgs)
        => Queue(eventArgs.Item);

    private void OnItemUpdated(object sender, ItemChangeEventArgs eventArgs)
    {
        if ((eventArgs.UpdateReason & RelevantUpdateTypes) != 0)
        {
            Queue(eventArgs.Item);
        }
    }

    private void Queue(BaseItem item)
    {
        if (item is not (Movie or Episode) || item.IsVirtualItem)
        {
            return;
        }

        if (!_mergeProcessor.TryCreateIncrementalTarget(item, out var target) || target is null)
        {
            return;
        }

        var entry = new IncrementalMergeQueueEntry(
            Interlocked.Increment(ref _nextRevision),
            target);

        try
        {
            _queueStore.Enqueue(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Unable to persist an incremental version merge target");
        }

        _pending.AddOrUpdate(
            target.Key,
            entry,
            (_, current) => current.Revision < entry.Revision ? entry : current);
        _wakeUp.Writer.TryWrite(0);
    }

    private List<IncrementalMergeQueueEntry> TakeBatch()
    {
        return _pending.Values.Take(BatchSize).ToList();
    }

    private void CompleteBatch(IEnumerable<IncrementalMergeQueueEntry> batch)
    {
        foreach (var entry in batch)
        {
            if (!_pending.TryRemove(
                    new KeyValuePair<string, IncrementalMergeQueueEntry>(entry.Target.Key, entry)))
            {
                continue;
            }

            try
            {
                _queueStore.Complete(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Unable to persist completion of an incremental version merge target");
            }
        }
    }

    private void RestorePendingTargets()
    {
        IReadOnlyCollection<IncrementalMergeQueueEntry> restored;
        try
        {
            restored = _queueStore.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Unable to restore the incremental version merge queue");
            return;
        }

        foreach (var entry in restored)
        {
            _pending.AddOrUpdate(
                entry.Target.Key,
                entry,
                (_, current) => current.Revision < entry.Revision ? entry : current);
            _nextRevision = Math.Max(_nextRevision, entry.Revision);
        }

        if (!_pending.IsEmpty)
        {
            _wakeUp.Writer.TryWrite(0);
        }
    }

    private void DrainWakeUps()
    {
        while (_wakeUp.Reader.TryRead(out _))
        {
        }
    }
}

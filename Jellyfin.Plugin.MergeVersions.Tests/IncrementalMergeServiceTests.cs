using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MergeVersions.Tests;

public class IncrementalMergeServiceTests
{
    [Fact]
    public async Task ChangeDuringActiveBatchIsProcessedInNextBatch()
    {
        var library = new Mock<ILibraryManager>();
        var processor = new BlockingProcessor();
        using var service = new IncrementalMergeService(
            library.Object,
            processor,
            new InMemoryQueueStore(),
            NullLogger<IncrementalMergeService>.Instance)
        {
            DebounceDelay = TimeSpan.FromMilliseconds(10)
        };

        await service.StartAsync(default);
        try
        {
            RaiseAdded(library, "first");
            await processor.FirstBatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            RaiseAdded(library, "second");
            processor.ReleaseFirstBatch.SetResult();

            await processor.SecondBatchCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(["first", "second"], processor.ProcessedKeys);
        }
        finally
        {
            await service.StopAsync(default);
        }
    }

    [Fact]
    public async Task MetadataEditDoesNotFeedMergeBackIntoQueue()
    {
        var library = new Mock<ILibraryManager>();
        var processor = new RecordingProcessor();
        using var service = new IncrementalMergeService(
            library.Object,
            processor,
            new InMemoryQueueStore(),
            NullLogger<IncrementalMergeService>.Instance)
        {
            DebounceDelay = TimeSpan.FromMilliseconds(10)
        };

        await service.StartAsync(default);
        try
        {
            var episode = CreateEpisode("ignored");
            library.Raise(
                manager => manager.ItemUpdated += null,
                library.Object,
                new ItemChangeEventArgs
                {
                    Item = episode,
                    UpdateReason = ItemUpdateType.MetadataEdit
                });

            await Task.Delay(50);
            Assert.Empty(processor.ProcessedKeys);
        }
        finally
        {
            await service.StopAsync(default);
        }
    }

    [Fact]
    public void PersistentQueueRetainsLatestTargetUntilItCompletes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mergeversions-tests", Guid.NewGuid().ToString("N"));
        var queuePath = Path.Combine(directory, "queue.jsonl");
        var first = new IncrementalMergeQueueEntry(
            1,
            new IncrementalMergeTarget("same", BaseItemKind.Episode, "Tvdb", "1"));
        var latest = new IncrementalMergeQueueEntry(
            2,
            new IncrementalMergeTarget("same", BaseItemKind.Episode, "Tvdb", "2"));

        try
        {
            using (var queue = CreatePersistentQueue(queuePath))
            {
                queue.Enqueue(first);
                queue.Enqueue(latest);
                queue.Complete(first);
            }

            using (var restoredQueue = CreatePersistentQueue(queuePath))
            {
                var restored = Assert.Single(restoredQueue.Load());
                Assert.Equal(latest, restored);
                restoredQueue.Complete(latest);
            }

            using var emptyQueue = CreatePersistentQueue(queuePath);
            Assert.Empty(emptyQueue.Load());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RestoredTargetIsProcessedAfterServiceStarts()
    {
        var library = new Mock<ILibraryManager>();
        var processor = new RecordingProcessor();
        var queue = new InMemoryQueueStore();
        queue.Enqueue(
            new IncrementalMergeQueueEntry(
                10,
                new IncrementalMergeTarget("restored", BaseItemKind.Episode, "Tvdb", "restored")));
        using var service = new IncrementalMergeService(
            library.Object,
            processor,
            queue,
            NullLogger<IncrementalMergeService>.Instance)
        {
            DebounceDelay = TimeSpan.FromMilliseconds(10)
        };

        await service.StartAsync(default);
        try
        {
            await processor.BatchCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(["restored"], processor.ProcessedKeys);
            Assert.Empty(queue.Load());
        }
        finally
        {
            await service.StopAsync(default);
        }
    }

    [Fact]
    public async Task FailedBatchRemainsQueuedAndIsRetried()
    {
        var library = new Mock<ILibraryManager>();
        var processor = new FlakyProcessor();
        var queue = new InMemoryQueueStore();
        using var service = new IncrementalMergeService(
            library.Object,
            processor,
            queue,
            NullLogger<IncrementalMergeService>.Instance)
        {
            DebounceDelay = TimeSpan.FromMilliseconds(10)
        };

        await service.StartAsync(default);
        try
        {
            RaiseAdded(library, "retry");
            await processor.BatchCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, processor.Attempts);
            Assert.Equal(["retry"], processor.ProcessedKeys);
            Assert.Empty(queue.Load());
        }
        finally
        {
            await service.StopAsync(default);
        }
    }

    [Fact]
    public void PersistentQueueCompactsSupersededRecords()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mergeversions-tests", Guid.NewGuid().ToString("N"));
        var queuePath = Path.Combine(directory, "queue.jsonl");

        try
        {
            using (var queue = CreatePersistentQueue(queuePath))
            {
                for (var revision = 1; revision <= 4100; revision++)
                {
                    queue.Enqueue(
                        new IncrementalMergeQueueEntry(
                            revision,
                            new IncrementalMergeTarget(
                                "same",
                                BaseItemKind.Episode,
                                "Tvdb",
                                revision.ToString())));
                }
            }

            using var restoredQueue = CreatePersistentQueue(queuePath);
            var restored = Assert.Single(restoredQueue.Load());
            Assert.Equal(4100, restored.Revision);
            Assert.True(File.ReadLines(queuePath).Count() < 10);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static PersistentIncrementalMergeQueueStore CreatePersistentQueue(string path)
        => new(path, NullLogger<PersistentIncrementalMergeQueueStore>.Instance);

    private static void RaiseAdded(Mock<ILibraryManager> library, string key)
    {
        library.Raise(
            manager => manager.ItemAdded += null,
            library.Object,
            new ItemChangeEventArgs { Item = CreateEpisode(key) });
    }

    private static Episode CreateEpisode(string key)
    {
        var episode = new Episode { Id = Guid.NewGuid(), Name = key };
        episode.ProviderIds["Tvdb"] = key;
        return episode;
    }

    private class RecordingProcessor : IIncrementalMergeProcessor
    {
        public ConcurrentQueue<string> ProcessedKeys { get; } = new();

        public TaskCompletionSource BatchCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryCreateIncrementalTarget(BaseItem item, out IncrementalMergeTarget target)
        {
            target = new IncrementalMergeTarget(
                item.Name,
                BaseItemKind.Episode,
                "Tvdb",
                item.Name);
            return true;
        }

        public virtual Task<int> MergeIncrementalBatchAsync(
            IReadOnlyCollection<IncrementalMergeTarget> targets,
            CancellationToken cancellationToken)
        {
            foreach (var target in targets)
            {
                ProcessedKeys.Enqueue(target.Key);
            }

            BatchCompleted.TrySetResult();
            return Task.FromResult(targets.Count);
        }
    }

    private sealed class BlockingProcessor : RecordingProcessor
    {
        private int _batchCount;

        public TaskCompletionSource FirstBatchStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstBatch { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondBatchCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<int> MergeIncrementalBatchAsync(
            IReadOnlyCollection<IncrementalMergeTarget> targets,
            CancellationToken cancellationToken)
        {
            var batch = Interlocked.Increment(ref _batchCount);
            if (batch == 1)
            {
                FirstBatchStarted.SetResult();
                await ReleaseFirstBatch.Task.WaitAsync(cancellationToken);
            }

            var result = await base.MergeIncrementalBatchAsync(targets, cancellationToken);
            if (batch == 2)
            {
                SecondBatchCompleted.SetResult();
            }

            return result;
        }
    }

    private sealed class FlakyProcessor : RecordingProcessor
    {
        private int _attempts;

        public int Attempts => _attempts;

        public override Task<int> MergeIncrementalBatchAsync(
            IReadOnlyCollection<IncrementalMergeTarget> targets,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                throw new InvalidOperationException("Expected test failure");
            }

            return base.MergeIncrementalBatchAsync(targets, cancellationToken);
        }
    }

    private sealed class InMemoryQueueStore : IIncrementalMergeQueueStore
    {
        private readonly Dictionary<string, IncrementalMergeQueueEntry> _entries =
            new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<IncrementalMergeQueueEntry> Load()
            => _entries.Values.ToArray();

        public void Enqueue(IncrementalMergeQueueEntry entry)
        {
            _entries[entry.Target.Key] = entry;
        }

        public void Complete(IncrementalMergeQueueEntry entry)
        {
            if (_entries.TryGetValue(entry.Target.Key, out var current) && current == entry)
            {
                _entries.Remove(entry.Target.Key);
            }
        }

        public void Dispose()
        {
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MergeVersions;

internal sealed class PersistentIncrementalMergeQueueStore : IIncrementalMergeQueueStore
{
    private const int MinimumRecordsBeforeCompaction = 4096;
    private const int CompactionRatio = 4;
    private const string QueueFileName = "mergeversions.queue.jsonl";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly object _sync = new();
    private readonly string _queuePath;
    private readonly ILogger<PersistentIncrementalMergeQueueStore> _logger;
    private readonly Dictionary<string, IncrementalMergeQueueEntry> _current =
        new(StringComparer.OrdinalIgnoreCase);

    private FileStream _appendStream;
    private int _recordCount;
    private bool _loaded;

    public PersistentIncrementalMergeQueueStore(
        IServerApplicationPaths applicationPaths,
        ILogger<PersistentIncrementalMergeQueueStore> logger)
        : this(Path.Combine(applicationPaths.PluginConfigurationsPath, QueueFileName), logger)
    {
    }

    internal PersistentIncrementalMergeQueueStore(
        string queuePath,
        ILogger<PersistentIncrementalMergeQueueStore> logger)
    {
        _queuePath = queuePath;
        _logger = logger;
    }

    public IReadOnlyCollection<IncrementalMergeQueueEntry> Load()
    {
        lock (_sync)
        {
            EnsureLoaded();
            return _current.Values.ToArray();
        }
    }

    public void Enqueue(IncrementalMergeQueueEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_sync)
        {
            EnsureLoaded();
            Append(new QueueJournalRecord(
                QueueJournalOperation.Enqueue,
                entry.Revision,
                entry.Target.Key,
                entry.Target));

            if (!_current.TryGetValue(entry.Target.Key, out var current) ||
                current.Revision < entry.Revision)
            {
                _current[entry.Target.Key] = entry;
            }

            CompactIfNeeded();
        }
    }

    public void Complete(IncrementalMergeQueueEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_sync)
        {
            EnsureLoaded();
            Append(new QueueJournalRecord(
                QueueJournalOperation.Complete,
                entry.Revision,
                entry.Target.Key));

            if (_current.TryGetValue(entry.Target.Key, out var current) &&
                current.Revision == entry.Revision)
            {
                _current.Remove(entry.Target.Key);
            }

            CompactIfNeeded();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _appendStream?.Dispose();
            _appendStream = null;
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_queuePath)
            ?? throw new InvalidOperationException("The incremental merge queue path has no directory.");
        Directory.CreateDirectory(directory);

        if (File.Exists(_queuePath))
        {
            foreach (var line in File.ReadLines(_queuePath))
            {
                _recordCount++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                QueueJournalRecord record;
                try
                {
                    record = JsonSerializer.Deserialize<QueueJournalRecord>(line, SerializerOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Ignoring an invalid incremental merge queue record");
                    continue;
                }

                Apply(record);
            }
        }

        _appendStream = OpenAppendStream();
        RestrictFilePermissions(_queuePath);
        _loaded = true;

        if (_current.Count > 0)
        {
            _logger.LogInformation(
                "Restored {TargetCount} pending incremental version groups",
                _current.Count);
        }
    }

    private void Apply(QueueJournalRecord record)
    {
        if (record is null || string.IsNullOrWhiteSpace(record.Key))
        {
            _logger.LogWarning("Ignoring an incomplete incremental merge queue record");
            return;
        }

        if (record.Operation == QueueJournalOperation.Enqueue)
        {
            if (record.Target is null)
            {
                _logger.LogWarning("Ignoring an incremental merge queue record without a target");
                return;
            }

            if (!_current.TryGetValue(record.Key, out var current) ||
                current.Revision < record.Revision)
            {
                _current[record.Key] = new IncrementalMergeQueueEntry(record.Revision, record.Target);
            }

            return;
        }

        if (record.Operation == QueueJournalOperation.Complete &&
            _current.TryGetValue(record.Key, out var pending) &&
            pending.Revision == record.Revision)
        {
            _current.Remove(record.Key);
        }
    }

    private void Append(QueueJournalRecord record)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(record, SerializerOptions);
        _appendStream.Write(payload);
        _appendStream.WriteByte((byte)'\n');
        _appendStream.Flush(flushToDisk: false);
        _recordCount++;
    }

    private void CompactIfNeeded()
    {
        if (_recordCount < MinimumRecordsBeforeCompaction ||
            _recordCount <= Math.Max(MinimumRecordsBeforeCompaction, _current.Count * CompactionRatio))
        {
            return;
        }

        var temporaryPath = _queuePath + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.SequentialScan))
            {
                foreach (var entry in _current.Values)
                {
                    var record = new QueueJournalRecord(
                        QueueJournalOperation.Enqueue,
                        entry.Revision,
                        entry.Target.Key,
                        entry.Target);
                    var payload = JsonSerializer.SerializeToUtf8Bytes(record, SerializerOptions);
                    stream.Write(payload);
                    stream.WriteByte((byte)'\n');
                }

                stream.Flush(flushToDisk: true);
            }

            RestrictFilePermissions(temporaryPath);
            _appendStream.Dispose();
            _appendStream = null;
            File.Move(temporaryPath, _queuePath, overwrite: true);
            _appendStream = OpenAppendStream();
            _recordCount = _current.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Unable to compact the incremental merge queue journal");
            _appendStream ??= OpenAppendStream();
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // A later compaction can reuse or replace the temporary file.
            }
        }
    }

    private FileStream OpenAppendStream()
    {
        var stream = new FileStream(
            _queuePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);

        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != (byte)'\n')
            {
                stream.Seek(0, SeekOrigin.End);
                stream.WriteByte((byte)'\n');
            }
        }

        stream.Seek(0, SeekOrigin.End);
        return stream;
    }

    private void RestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _logger.LogWarning(ex, "Unable to restrict permissions on the incremental merge queue journal");
        }
    }

    private enum QueueJournalOperation
    {
        Enqueue,
        Complete
    }

    private sealed record QueueJournalRecord(
        QueueJournalOperation Operation,
        long Revision,
        string Key,
        IncrementalMergeTarget Target = null);
}

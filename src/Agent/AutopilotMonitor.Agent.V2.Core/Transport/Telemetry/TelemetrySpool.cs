#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// Disk-backed <see cref="ITelemetrySpool"/> — append-only JSONL + separater Cursor-File.
    /// Plan §2.7a / L.12 Sofort-Flush.
    /// <para>
    /// <b>Layout auf Disk</b>:
    /// <list type="bullet">
    ///   <item><c>&lt;dir&gt;/spool.jsonl</c> — eine <see cref="TelemetryItem"/>-Zeile pro enqueue, append-only</item>
    ///   <item><c>&lt;dir&gt;/upload-cursor.json</c> — <c>{ "LastUploadedItemId": N }</c>, atomar geschrieben</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Recovery</b>: beim Konstruktor werden beide Files gescannt. <c>LastAssignedItemId</c> = max
    /// <c>TelemetryItemId</c> in spool.jsonl (ab der letzten parsbaren Zeile); <c>LastUploadedItemId</c>
    /// = Wert aus cursor-File (oder -1 bei Fehler). Items mit <c>Id &gt; LastUploadedItemId</c> werden
    /// in den in-memory Tail-Cache <c>_pending</c> rehydriert.
    /// </para>
    /// <para>
    /// <b>Performance — in-memory tail cache</b>: <see cref="Peek"/> liest aus dem in-memory
    /// <see cref="LinkedList{T}"/> <c>_pending</c>, nicht aus der Datei. Disk wird nur beim
    /// <see cref="Enqueue"/> (append) und einmal im ctor (rehydrate) berührt. Die JSONL bleibt
    /// die Source-of-Truth für Crash-Recovery + Diagnostics-ZIP-Forensik; der RAM-Cache
    /// existiert ausschliesslich um O(N)-File-Reads pro Drain zu vermeiden.
    /// </para>
    /// <para>
    /// <b>Performance — conditional fsync</b>: nur Items mit
    /// <see cref="TelemetryItemDraft.RequiresImmediateFlush"/> = <c>true</c> erzwingen einen
    /// <c>FileOptions.WriteThrough</c> + <c>Flush(true)</c>. Nicht-immediate Items (typisch:
    /// periodische Snapshots, Hello-Snapshot-Flag-Updates) gehen ohne fsync in den OS-Cache;
    /// das spart auf langsamen Provisioning-VMs ~5-20 ms pro Item. Trade-off: bei einem
    /// Crash zwischen Append und OS-Flush gehen genau die nicht-immediate Items verloren,
    /// die noch nicht hochgeladen wurden — Backend dedupt eh via (PartitionKey, RowKey),
    /// ein paar verlorene Snapshots sind akzeptabel.
    /// </para>
    /// </summary>
    public sealed class TelemetrySpool : ITelemetrySpool
    {
        private readonly string _spoolPath;
        private readonly UploadCursorPersistence _cursor;
        private readonly IClock _clock;
        private readonly Logging.AgentLogger? _logger;
        private readonly object _lock = new object();

        private long _lastAssignedItemId = -1;
        private long _lastUploadedItemId = -1;

        // In-memory tail cache: holds every item with TelemetryItemId > _lastUploadedItemId.
        // Populated from disk in the ctor and kept in sync by Enqueue / MarkUploaded. Peek
        // reads from here so the periodic drain loop is O(batchSize) instead of O(N) per
        // call. Disk JSONL stays the source of truth — the cache is rebuilt on every
        // process start. Each entry carries its on-disk line length (incl. trailing \n) so
        // MarkUploaded can decrement _pendingBytes exactly.
        private readonly LinkedList<PendingEntry> _pending = new LinkedList<PendingEntry>();
        private int _peakPendingCount;
        private long _pendingBytes;

        private readonly struct PendingEntry
        {
            public PendingEntry(TelemetryItem item, int byteLength)
            {
                Item = item;
                ByteLength = byteLength;
            }

            public TelemetryItem Item { get; }
            public int ByteLength { get; }
        }

        public TelemetrySpool(string directoryPath, IClock clock, Logging.AgentLogger? logger = null)
        {
            if (string.IsNullOrEmpty(directoryPath))
            {
                throw new ArgumentException("DirectoryPath is mandatory.", nameof(directoryPath));
            }

            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger;

            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            _spoolPath = Path.Combine(directoryPath, "spool.jsonl");
            _cursor = new UploadCursorPersistence(Path.Combine(directoryPath, "upload-cursor.json"));

            // Cursor must be loaded BEFORE we scan the file so we know which items still
            // count as "pending" and need to be rehydrated into _pending.
            _lastUploadedItemId = _cursor.Load();
            _lastAssignedItemId = ScanSpoolAndRehydratePending();
            _peakPendingCount = _pending.Count;
        }

        public long LastAssignedItemId
        {
            get { lock (_lock) return _lastAssignedItemId; }
        }

        public long LastUploadedItemId
        {
            get { lock (_lock) return _lastUploadedItemId; }
        }

        public int PendingItemCount
        {
            get { lock (_lock) return _pending.Count; }
        }

        public int PeakPendingItemCount
        {
            get { lock (_lock) return _peakPendingCount; }
        }

        public long PendingBytes
        {
            get { lock (_lock) return _pendingBytes; }
        }

        public long SpoolFileSizeBytes
        {
            get
            {
                try
                {
                    var info = new FileInfo(_spoolPath);
                    return info.Exists ? info.Length : 0L;
                }
                catch
                {
                    return 0L;
                }
            }
        }

        public event EventHandler? ImmediateFlushRequested;

        public TelemetryItem Enqueue(TelemetryItemDraft draft)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));

            TelemetryItem item;
            lock (_lock)
            {
                var itemId = _lastAssignedItemId + 1;
                long? traceOrdinal = draft.IsSessionScoped ? itemId : (long?)null;

                item = new TelemetryItem(
                    kind: draft.Kind,
                    partitionKey: draft.PartitionKey,
                    rowKey: draft.RowKey,
                    telemetryItemId: itemId,
                    sessionTraceOrdinal: traceOrdinal,
                    payloadJson: draft.PayloadJson,
                    requiresImmediateFlush: draft.RequiresImmediateFlush,
                    enqueuedAtUtc: _clock.UtcNow);

                var line = TelemetryItemSerializer.Serialize(item);
                var bytes = Encoding.UTF8.GetBytes(line + "\n");

                // Conditional fsync (P2 Step 1): immediate-flush items get the strong
                // crash-durability guarantee; periodic / batched items go to OS cache only.
                // FileStream.Dispose at the end of the using-block always Flushes the user
                // buffer to the OS, so the data is visible to in-process readers immediately;
                // only the fsync-to-disk barrier differs.
                var options = draft.RequiresImmediateFlush
                    ? FileOptions.WriteThrough
                    : FileOptions.None;

                using (var fs = new FileStream(
                    _spoolPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    options: options))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    if (draft.RequiresImmediateFlush)
                    {
                        fs.Flush(flushToDisk: true);
                    }
                }

                _lastAssignedItemId = itemId;
                _pending.AddLast(new PendingEntry(item, bytes.Length));
                _pendingBytes += bytes.Length;
                if (_pending.Count > _peakPendingCount)
                    _peakPendingCount = _pending.Count;

                // Plan §5 Fix 5 — spool-cadence logging. PR3-A2: VERBOSE (was DEBUG)
                // because per-item logging hits ~600 lines per session and floods the log
                // at troubleshoot level. Activate only at micro-repro level.
                _logger?.Verbose(
                    $"TelemetrySpool: enqueued itemId={itemId} kind={item.Kind} immediate={item.RequiresImmediateFlush} pending={_pending.Count}.");
            }

            // Fire outside the lock — subscribers must not be able to re-enter Enqueue or
            // hold the write path while waking the drain loop. Swallow handler exceptions:
            // a buggy subscriber must not break telemetry persistence.
            if (draft.RequiresImmediateFlush)
            {
                try { ImmediateFlushRequested?.Invoke(this, EventArgs.Empty); }
                catch (Exception ex)
                {
                    _logger?.Warning($"TelemetrySpool: ImmediateFlushRequested handler threw: {ex.Message}");
                }
            }

            return item;
        }

        public IReadOnlyList<TelemetryItem> Peek(int max)
        {
            if (max <= 0) return Array.Empty<TelemetryItem>();

            lock (_lock)
            {
                if (_pending.Count == 0) return Array.Empty<TelemetryItem>();

                var batch = new List<TelemetryItem>(Math.Min(max, _pending.Count));
                var node = _pending.First;
                while (node != null && batch.Count < max)
                {
                    batch.Add(node.Value.Item);
                    node = node.Next;
                }
                return batch;
            }
        }

        public void MarkUploaded(long upToItemIdInclusive)
        {
            lock (_lock)
            {
                if (upToItemIdInclusive <= _lastUploadedItemId) return;   // idempotency — never regress
                if (upToItemIdInclusive > _lastAssignedItemId)
                {
                    throw new InvalidOperationException(
                        $"Cannot mark uploaded beyond last-assigned id (requested={upToItemIdInclusive}, lastAssigned={_lastAssignedItemId}).");
                }

                _cursor.Save(upToItemIdInclusive);
                _lastUploadedItemId = upToItemIdInclusive;

                // Trim the in-memory tail. The list is in TelemetryItemId-monotonic order
                // (Enqueue only appends), so we can drop from the front until the first
                // remaining item is past the cursor.
                while (_pending.First != null && _pending.First.Value.Item.TelemetryItemId <= upToItemIdInclusive)
                {
                    _pendingBytes -= _pending.First.Value.ByteLength;
                    _pending.RemoveFirst();
                }
            }
        }

        /// <summary>
        /// Single-pass streaming scan of <c>spool.jsonl</c> (<c>File.ReadLines</c> — never
        /// materializes the whole file): returns the highest <c>TelemetryItemId</c> found AND
        /// populates <see cref="_pending"/> / <see cref="_pendingBytes"/> with every item past
        /// <see cref="_lastUploadedItemId"/>. Called once from the ctor.
        /// </summary>
        private long ScanSpoolAndRehydratePending()
        {
            if (!File.Exists(_spoolPath)) return -1;

            long highest = -1;
            foreach (var line in File.ReadLines(_spoolPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                TelemetryItem item;
                try
                {
                    item = TelemetryItemSerializer.Deserialize(line);
                }
                catch
                {
                    // Corrupt tail (crash mid-append) — stop reading, the items before are
                    // still valid. Keeps current behaviour: orchestrator drains the parsable
                    // prefix; the unparsable tail stays in the file but is never returned.
                    break;
                }

                if (item.TelemetryItemId > highest) highest = item.TelemetryItemId;
                if (item.TelemetryItemId > _lastUploadedItemId)
                {
                    // +1 = the trailing \n Enqueue writes after every line, so the rehydrated
                    // byte count matches what Enqueue would have accumulated.
                    var byteLength = Encoding.UTF8.GetByteCount(line) + 1;
                    _pending.AddLast(new PendingEntry(item, byteLength));
                    _pendingBytes += byteLength;
                }
            }
            return highest;
        }
    }
}

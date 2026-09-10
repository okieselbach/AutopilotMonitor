using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// Partial: overwrite-aware tailing of the two IME logs that several processes append to.
    /// <para>
    /// IME's trace listener opens each log once per process with FileMode.Append and
    /// FileShare.ReadWrite and writes at its own stream position afterwards. AgentExecutor.exe
    /// (two processes per user-context script, one per detection or remediation script) has such
    /// a listener on AgentExecutor.log AND on IntuneManagementExtension.log. Whenever one process
    /// resumes writing after another one appended, it overwrites those bytes: the final file
    /// holds the last writer, while a tailer that trusts its bookmark read the first version and
    /// never sees the second (session 46749560: 6 of 16 script results, 4 of 16 exit blocks).
    /// </para>
    /// <para>
    /// So the bytes behind the bookmark are not immutable. The tracker keeps a fingerprint ledger
    /// of every entry it read from these files, re-verifies it while script invocations are in
    /// flight or right after a signal that a stale writer just wrote, and on the first changed
    /// entry rewinds the bookmark — processing only entries it has not processed before.
    /// </para>
    /// </summary>
    public partial class ImeLogTracker
    {
        /// <summary>Live logs with more than one writing process; archived rotations are closed files.</summary>
        internal static readonly string[] MultiWriterLogFiles = { "AgentExecutor.log", "IntuneManagementExtension.log" };

        /// <summary>Ledger depth behind the bookmark while no script is in flight.</summary>
        internal const int LedgerRetentionBytes = 1024 * 1024;

        /// <summary>Hard cap on the ledger depth, whatever is in flight.</summary>
        internal const int LedgerMaxBytes = 4 * 1024 * 1024;

        /// <summary>Entries this close to the bookmark are re-checked on every pass that found growth (the file is open anyway).</summary>
        internal const int OverwriteGuardBytes = 4 * 1024;

        /// <summary>After a restart the ledger is seeded from this many bytes before the persisted bookmark.</summary>
        internal const int RestartSeedBytes = 64 * 1024;

        /// <summary>Verification work per second while in contention — a deep ledger is checked less often, never skipped.</summary>
        internal const long VerifyBudgetBytesPerSecond = 256 * 1024;

        internal static readonly TimeSpan VerifyMinInterval = TimeSpan.FromSeconds(1);

        /// <summary>How long an observed invocation keeps the files in contention on its own (detection executors have no pending slot).</summary>
        internal static readonly TimeSpan InvocationContentionWindow = TimeSpan.FromSeconds(120);

        /// <summary>A pending script older than this no longer holds the files in contention.</summary>
        internal static readonly TimeSpan MaxPendingScriptContention = TimeSpan.FromHours(2);

        private const int MaxInvocationMarkersPerFile = 512;
        private const string TestSourceFileName = "TEST.log";
        private const string LogOpenTag = "<![LOG[";

        /// <summary>One processed entry: where it starts, how far it reaches, what its bytes hashed to.</summary>
        internal struct LedgerEntry : IEquatable<LedgerEntry>
        {
            public long Offset;
            public int Length;
            public ulong Hash;

            // Identity is (offset, hash): a tail first read unterminated and later completed
            // keeps its hash but grows by its terminator.
            public bool Equals(LedgerEntry other) => Offset == other.Offset && Hash == other.Hash;
            public override bool Equals(object obj) => obj is LedgerEntry other && Equals(other);
            public override int GetHashCode() => unchecked(((int)Offset * 397) ^ (int)Hash ^ (int)(Hash >> 32));
        }

        internal sealed class FileLedger
        {
            public readonly List<LedgerEntry> Entries = new List<LedgerEntry>();
            public bool VerifyRequested;
            public DateTime NextCadenceVerifyUtc = DateTime.MinValue;
            public long LastFragmentOffset = -1;
            public bool Seeded;
            public bool RewindLogged;

            public long StartOffset => Entries.Count > 0 ? Entries[0].Offset : -1;
            public long EndOffset => Entries.Count > 0 ? Entries[Entries.Count - 1].Offset + Entries[Entries.Count - 1].Length : -1;

            public int IndexOfFirstAtOrAfter(long offset)
            {
                int lo = 0, hi = Entries.Count;
                while (lo < hi)
                {
                    var mid = (lo + hi) >> 1;
                    if (Entries[mid].Offset < offset) lo = mid + 1; else hi = mid;
                }
                return lo;
            }

            /// <summary>Entry offset at or after <paramref name="offset"/>, or -1.</summary>
            public long OffsetOfEntryAtOrAfter(long offset)
            {
                var idx = IndexOfFirstAtOrAfter(offset);
                return idx < Entries.Count ? Entries[idx].Offset : -1;
            }

            public void Record(AssembledEntry e)
            {
                var idx = IndexOfFirstAtOrAfter(e.Offset);
                if (idx < Entries.Count) Entries.RemoveRange(idx, Entries.Count - idx);
                Entries.Add(new LedgerEntry { Offset = e.Offset, Length = e.Length, Hash = e.Hash });
            }

            /// <summary>Drops everything from <paramref name="offset"/> on and returns it — the entries a rewind must not process twice.</summary>
            public HashSet<LedgerEntry> TruncateFrom(long offset)
            {
                var idx = IndexOfFirstAtOrAfter(offset);
                var removed = new HashSet<LedgerEntry>();
                for (var i = idx; i < Entries.Count; i++) removed.Add(Entries[i]);
                if (idx < Entries.Count) Entries.RemoveRange(idx, Entries.Count - idx);
                return removed;
            }

            public void TrimBelow(long floor)
            {
                var idx = IndexOfFirstAtOrAfter(floor);
                if (idx > 0) Entries.RemoveRange(0, idx);
            }

            public void Clear()
            {
                Entries.Clear();
                LastFragmentOffset = -1;
            }
        }

        // Keyed by file name (unique within the log folder). Poll thread only.
        private readonly Dictionary<string, FileLedger> _ledgers = new Dictionary<string, FileLedger>(StringComparer.OrdinalIgnoreCase);
        private DateTime _contentionUntilUtc = DateTime.MinValue;

        // Cumulative, restart-safe like the other health counters.
        private long _overwriteRewinds;
        private long _overwriteBytesReprocessed;
        private long _verifyPasses;
        private long _verifiedBytes;

        internal static bool IsMultiWriterLogFile(string fileName)
        {
            foreach (var f in MultiWriterLogFiles)
                if (string.Equals(f, fileName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private FileLedger GetLedger(string fileName)
        {
            FileLedger ledger;
            if (!_ledgers.TryGetValue(fileName, out ledger))
            {
                ledger = new FileLedger();
                _ledgers[fileName] = ledger;
            }
            return ledger;
        }

        /// <summary>A stale writer writes exactly when its script ends: every end signal asks both files for a check on the next pass.</summary>
        private void RequestOverwriteCheck()
        {
            foreach (var ledger in _ledgers.Values) ledger.VerifyRequested = true;
        }

        private void NoteInvocationActivity()
        {
            _contentionUntilUtc = UtcNowProvider() + InvocationContentionWindow;
        }

        private bool InContention(DateTime nowUtc)
        {
            if (nowUtc < _contentionUntilUtc || _pendingHealthScript != null) return true;
            foreach (var script in _pendingPlatformScripts.Values)
            {
                if (script.Result != null) continue;
                if (!script.StartedAtUtc.HasValue || nowUtc - script.StartedAtUtc.Value < MaxPendingScriptContention) return true;
            }
            return false;
        }

        /// <summary>
        /// Runs before a multi-writer file is read: rollover reset, restart seeding, then the
        /// overwrite check when one is due. Returns the offset to rewind to, or -1.
        /// </summary>
        private async Task<long> PrepareLedgerAsync(string filePath, string fileName, FileLedger ledger, long bookmark, DateTime nowUtc, CancellationToken token)
        {
            if (ledger.Entries.Count > 0 && bookmark < ledger.EndOffset)
            {
                // The bookmark moved below what was read: the file was replaced (rollover).
                ledger.Clear();
                InvalidateInvocationMarkersFrom(fileName, 0);
            }

            if (!ledger.Seeded)
            {
                ledger.Seeded = true;
                if (ledger.Entries.Count == 0 && bookmark > 0)
                    await SeedLedgerAsync(filePath, ledger, bookmark, token).ConfigureAwait(false);
            }

            var due = ledger.VerifyRequested || (InContention(nowUtc) && nowUtc >= ledger.NextCadenceVerifyUtc);
            if (!due || ledger.Entries.Count == 0) return -1;

            var from = ledger.StartOffset;
            var bytes = bookmark - from;
            if (bytes <= 0)
            {
                ledger.VerifyRequested = false;
                return -1;
            }

            long divergence;
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                divergence = await FindOverwriteAsync(stream, ledger, from, bookmark, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return -1;

            ledger.VerifyRequested = false;
            var paceSeconds = Math.Max(VerifyMinInterval.TotalSeconds, (double)bytes / VerifyBudgetBytesPerSecond);
            ledger.NextCadenceVerifyUtc = nowUtc.AddSeconds(paceSeconds);
            _verifyPasses++;
            _verifiedBytes += bytes;
            return divergence;
        }

        /// <summary>
        /// Re-reads [fromOffset, toOffset) with the read loop's assembler and compares against the
        /// ledger. Returns the offset of the first entry whose bytes changed — a ledger entry not
        /// reproduced, or an entry the ledger never had — or -1 when everything reads as before.
        /// </summary>
        private async Task<long> FindOverwriteAsync(FileStream stream, FileLedger ledger, long fromOffset, long toOffset, CancellationToken token)
        {
            var entries = ledger.Entries;
            var idx = ledger.IndexOfFirstAtOrAfter(fromOffset);
            if (idx >= entries.Count) return -1;

            stream.Seek(entries[idx].Offset, SeekOrigin.Begin);
            var reader = new BoundedLineReader(stream, MaxEntryBytes, hashLines: true);
            var assembler = new CmTraceEntryAssembler(MaxMultiLineBufferLines, MaxEntryBytes);

            while (reader.Position < toOffset)
            {
                var line = await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
                if (line == null) break;
                if (token.IsCancellationRequested) return -1;

                AssembledEntry e;
                if (assembler.Feed(line, reader.LastLineStart, reader.Position, reader.LastLineTruncated, reader.LastLineHash, out e)
                    != CmTraceEntryAssembler.Outcome.Entry)
                    continue;
                if (e.Offset >= toOffset) break;

                if (idx < entries.Count && entries[idx].Offset < e.Offset) return entries[idx].Offset;
                if (idx < entries.Count && entries[idx].Offset == e.Offset)
                {
                    if (entries[idx].Hash != e.Hash) return e.Offset;
                    idx++;
                    continue;
                }
                return e.Offset; // an entry the ledger never had: the bytes changed here
            }

            return idx < entries.Count && entries[idx].Offset < toOffset ? entries[idx].Offset : -1;
        }

        /// <summary>After a restart the ledger is empty: fingerprint the entries just behind the persisted bookmark without processing them.</summary>
        private async Task SeedLedgerAsync(string filePath, FileLedger ledger, long bookmark, CancellationToken token)
        {
            var from = Math.Max(0, bookmark - RestartSeedBytes);
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(from, SeekOrigin.Begin);
                var reader = new BoundedLineReader(stream, MaxEntryBytes, hashLines: true);
                var assembler = new CmTraceEntryAssembler(MaxMultiLineBufferLines, MaxEntryBytes);
                var aligned = from == 0;
                while (reader.Position < bookmark)
                {
                    var line = await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
                    if (line == null || token.IsCancellationRequested) break;
                    if (!aligned)
                    {
                        // The seek may have landed mid-entry: wait for an entry boundary.
                        if (!line.StartsWith(LogOpenTag)) continue;
                        aligned = true;
                    }
                    AssembledEntry e;
                    if (assembler.Feed(line, reader.LastLineStart, reader.Position, reader.LastLineTruncated, reader.LastLineHash, out e)
                            == CmTraceEntryAssembler.Outcome.Entry
                        && e.Offset + e.Length <= bookmark)
                        ledger.Record(e);
                }
            }
        }

        /// <summary>Moves the bookmark back to the first changed entry; returns the entries to skip if they still read unchanged behind it.</summary>
        private HashSet<LedgerEntry> ApplyRewind(string filePath, string fileName, FileLedger ledger, long rewindTo, long bookmark)
        {
            var removed = ledger.TruncateFrom(rewindTo);
            _positionTracker.SetPosition(filePath, rewindTo);
            _heldTails.Remove(filePath);
            InvalidateInvocationMarkersFrom(fileName, rewindTo);
            _overwriteRewinds++;
            _overwriteBytesReprocessed += bookmark - rewindTo;

            var message = $"ImeLogTracker: {fileName} changed behind the bookmark at offset {rewindTo} " +
                          $"(a concurrent writer overwrote already-read bytes) — rewinding {bookmark - rewindTo} bytes";
            if (!ledger.RewindLogged)
            {
                ledger.RewindLogged = true;
                _logger.Info(message);
            }
            else
            {
                _logger.Debug(message);
            }
            return removed;
        }

        private long RetentionFloor(string fileName, long bookmark)
        {
            var floor = bookmark - LedgerRetentionBytes;
            var oldest = OldestPendingScriptMarkerOffset(fileName);
            if (oldest >= 0 && oldest < floor) floor = oldest;
            var hardFloor = bookmark - LedgerMaxBytes;
            return floor < hardFloor ? hardFloor : floor;
        }

        // -----------------------------------------------------------------------
        // Invocation markers: who owns the policyId-less executor lines at a file position
        // -----------------------------------------------------------------------
        //
        // AgentExecutor.log carries "Powershell exit code is N" / "write output done" without a
        // policy id. Their owner is decided by FILE POSITION, not by the order lines were read:
        // an executor writes its end block at its own stream position, i.e. directly after its
        // own start block in the final bytes, so the nearest preceding invocation marker names
        // it — and stays right after a rewind re-reads that region. A marker is a platform start
        // (policy id), some other invocation (detection, remediation, requirement: null) or a
        // close ("gets invoked" banner, "Agent executor completed").

        private sealed class InvocationMarker
        {
            public long Offset;
            public string PolicyId;
            public bool IsClose;
        }

        private readonly Dictionary<string, List<InvocationMarker>> _invocationMarkers =
            new Dictionary<string, List<InvocationMarker>>(StringComparer.OrdinalIgnoreCase);
        private InvocationMarker _lastPlatformMarker;
        private long _testEntryOrdinal;

        private void RecordInvocationMarker(string policyId, bool isClose)
        {
            if (_currentEntryOffset < 0) return;
            var file = _currentSourceFileName ?? TestSourceFileName;
            List<InvocationMarker> list;
            if (!_invocationMarkers.TryGetValue(file, out list))
            {
                list = new List<InvocationMarker>();
                _invocationMarkers[file] = list;
            }

            if (list.Count > 0 && list[list.Count - 1].Offset == _currentEntryOffset)
            {
                // Two patterns on one line: the platform start outranks the generic argument line.
                var last = list[list.Count - 1];
                if (!isClose && policyId != null)
                {
                    last.PolicyId = policyId;
                    last.IsClose = false;
                    _lastPlatformMarker = last;
                }
                return;
            }

            if (list.Count >= MaxInvocationMarkersPerFile) list.RemoveAt(0);
            var marker = new InvocationMarker { Offset = _currentEntryOffset, PolicyId = policyId, IsClose = isClose };
            list.Add(marker);
            if (!isClose && policyId != null) _lastPlatformMarker = marker;
        }

        /// <summary>The pending platform script that owns the entry being processed, or null.</summary>
        private ScriptExecutionState ResolvePlatformScriptForCurrentEntry()
        {
            InvocationMarker marker = null;
            List<InvocationMarker> list = null;
            if (_currentSourceFileName != null && _currentEntryOffset >= 0 && _invocationMarkers.TryGetValue(_currentSourceFileName, out list))
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].Offset <= _currentEntryOffset)
                    {
                        marker = list[i];
                        break;
                    }
                }
            }

            // A file without any marker (a context line after a rotation): the last platform start wins.
            if (marker == null && (list == null || list.Count == 0)) marker = _lastPlatformMarker;
            if (marker == null || marker.IsClose || marker.PolicyId == null) return null;

            ScriptExecutionState state;
            return _pendingPlatformScripts.TryGetValue(marker.PolicyId, out state) ? state : null;
        }

        private void InvalidateInvocationMarkersFrom(string fileName, long offset)
        {
            List<InvocationMarker> list;
            if (!_invocationMarkers.TryGetValue(fileName, out list) || list.Count == 0) return;
            var removedLast = false;
            for (var i = list.Count - 1; i >= 0 && list[i].Offset >= offset; i--)
            {
                if (ReferenceEquals(list[i], _lastPlatformMarker)) removedLast = true;
                list.RemoveAt(i);
            }
            if (removedLast)
            {
                _lastPlatformMarker = null;
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (!list[i].IsClose && list[i].PolicyId != null)
                    {
                        _lastPlatformMarker = list[i];
                        break;
                    }
                }
            }
        }

        private long OldestPendingScriptMarkerOffset(string fileName)
        {
            List<InvocationMarker> list;
            if (!_invocationMarkers.TryGetValue(fileName, out list)) return -1;
            long oldest = -1;
            foreach (var marker in list)
            {
                if (marker.IsClose || marker.PolicyId == null || !_pendingPlatformScripts.ContainsKey(marker.PolicyId)) continue;
                if (oldest < 0 || marker.Offset < oldest) oldest = marker.Offset;
            }
            return oldest;
        }

        // -----------------------------------------------------------------------
        // Test seams
        // -----------------------------------------------------------------------

        /// <summary>Same effect as an end signal: both multi-writer ledgers are verified on the next pass.</summary>
        internal void RequestOverwriteCheckForTest() => RequestOverwriteCheck();

        internal int LedgerEntryCountForTest(string fileName)
        {
            FileLedger ledger;
            return _ledgers.TryGetValue(fileName, out ledger) ? ledger.Entries.Count : 0;
        }

        /// <summary>Gives a seam-fed message a file position of its own so positional attribution behaves like in a real pass.</summary>
        internal void NextTestEntry()
        {
            if (_currentSourceFileName == null) _currentSourceFileName = TestSourceFileName;
            _currentEntryOffset = ++_testEntryOrdinal * 4096;
        }
    }
}

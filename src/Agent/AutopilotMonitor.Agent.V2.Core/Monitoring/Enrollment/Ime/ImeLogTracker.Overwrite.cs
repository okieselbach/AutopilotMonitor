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
            /// <summary>
            /// When these bytes were last seen as they are (read, seeded or verified unchanged): bytes found different
            /// at this position later were written after it. Not part of the identity.
            /// </summary>
            public DateTime SeenUtc;

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

            public void Record(AssembledEntry e, DateTime seenUtc)
            {
                var idx = IndexOfFirstAtOrAfter(e.Offset);
                if (idx < Entries.Count) Entries.RemoveRange(idx, Entries.Count - idx);
                Entries.Add(new LedgerEntry { Offset = e.Offset, Length = e.Length, Hash = e.Hash, SeenUtc = seenUtc });
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
                    await SeedLedgerAsync(filePath, ledger, bookmark, nowUtc, token).ConfigureAwait(false);
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
                divergence = await FindOverwriteAsync(stream, ledger, from, bookmark, nowUtc, token).ConfigureAwait(false);
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
        /// Every entry found unchanged is stamped seen at <paramref name="nowUtc"/>.
        /// </summary>
        private async Task<long> FindOverwriteAsync(FileStream stream, FileLedger ledger, long fromOffset, long toOffset, DateTime nowUtc, CancellationToken token)
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
                    var confirmed = entries[idx];
                    confirmed.SeenUtc = nowUtc;
                    entries[idx] = confirmed;
                    idx++;
                    continue;
                }
                return e.Offset; // an entry the ledger never had: the bytes changed here
            }

            return idx < entries.Count && entries[idx].Offset < toOffset ? entries[idx].Offset : -1;
        }

        /// <summary>After a restart the ledger is empty: fingerprint the entries just behind the persisted bookmark without processing them.</summary>
        private async Task SeedLedgerAsync(string filePath, FileLedger ledger, long bookmark, DateTime nowUtc, CancellationToken token)
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
                        ledger.Record(e, nowUtc);
                }
            }
        }

        /// <summary>
        /// What one rewind pass carries: the entries to skip if they still read unchanged behind the rewind point, and
        /// the invocation markers set aside there until their entries prove unchanged. Pass-local by design — whatever
        /// is not given back dies with the pass on every exit path.
        /// </summary>
        private sealed class RewindState
        {
            public RewindState(string fileName, HashSet<LedgerEntry> unchanged, List<InvocationMarker> markers, InvocationMarker lastPlatformMarker, long rewindTo)
            {
                FileName = fileName;
                Unchanged = unchanged;
                Markers = markers;
                LastPlatformMarker = lastPlatformMarker;
                foreach (var replaced in unchanged)
                {
                    if (replaced.Offset == rewindTo) EntryAtRewindPointReplaced = true;
                    if (!OldestReplacedSeenUtc.HasValue || replaced.SeenUtc < OldestReplacedSeenUtc.Value) OldestReplacedSeenUtc = replaced.SeenUtc;
                }
            }

            public string FileName { get; }
            public HashSet<LedgerEntry> Unchanged { get; }
            /// <summary>Set aside at the rewind, ascending by offset.</summary>
            public List<InvocationMarker> Markers { get; }
            /// <summary>The tracker-wide last platform marker, when it was among the set-aside ones.</summary>
            public InvocationMarker LastPlatformMarker { get; }
            public int Next { get; set; }
            /// <summary>The ledger held the entry at the rewind point: the changed bytes lie where something was seen before.</summary>
            public bool EntryAtRewindPointReplaced { get; }
            /// <summary>The earliest time any of the replaced bytes was last seen as it was: every changed byte was written after it.</summary>
            public DateTime? OldestReplacedSeenUtc { get; }

            public bool IsUnchanged(AssembledEntry entry) => Unchanged.Contains(new LedgerEntry { Offset = entry.Offset, Hash = entry.Hash });
        }

        /// <summary>
        /// Whether an entry a rewind re-processes (it reads differently than before) is fresh. Its bytes were written after
        /// the bytes it replaced were last seen as they were — IME stamps a line when it writes it — so when that was at
        /// most <see cref="FreshLineMaxAge"/> ago the entry's age is bounded like an appended line's and it may anchor
        /// itself (session d29e27e3: recovered result lines fell back to the agent's stale zone and lost their duration).
        /// Not fresh when the pass is not (the first pass after a restart never anchors), when the divergence lies in bytes
        /// the ledger never held (nothing dates them), or when the clock stepped back since.
        /// </summary>
        internal static bool RewoundEntryIsFresh(bool passLinesAreFresh, bool entryAtRewindPointReplaced, DateTime? oldestReplacedSeenUtc, DateTime passNowUtc)
        {
            if (!passLinesAreFresh || !entryAtRewindPointReplaced || !oldestReplacedSeenUtc.HasValue) return false;
            var sinceSeen = passNowUtc - oldestReplacedSeenUtc.Value;
            return sinceSeen >= TimeSpan.Zero && sinceSeen <= FreshLineMaxAge;
        }

        /// <summary>Moves the bookmark back to the first changed entry; returns what the re-read needs to skip and to give back.</summary>
        private RewindState ApplyRewind(string filePath, string fileName, FileLedger ledger, long rewindTo, long bookmark)
        {
            var removed = ledger.TruncateFrom(rewindTo);
            _positionTracker.SetPosition(filePath, rewindTo);
            _heldTails.Remove(filePath);
            var lastPlatformMarker = _lastPlatformMarker;
            var markers = TakeInvocationMarkersFrom(fileName, rewindTo, out var tookLastPlatformMarker);
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
            return new RewindState(fileName, removed, markers, tookLastPlatformMarker ? lastPlatformMarker : null, rewindTo);
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
        // policy id. Their owner is decided by FILE POSITION: the nearest preceding marker — a
        // platform start (policy id), some other invocation (detection, remediation, requirement:
        // null), a close ("gets invoked" banner, "Agent executor completed") or a writer boundary.
        //
        // Every executor writes its lines contiguously from its own stream position, so its end
        // block follows its own start block — but that start block may be gone. Two executors that
        // open the file at the same end write their start blocks over each other, and the end block
        // of the one that lost then sits behind the OTHER executor's start block (session 756970cf:
        // a detection script's exit code and output on a platform script). Where one writer's bytes
        // meet another's, the cut leaves an entry that is not exactly one CMTrace record — a
        // fragment, a merged line or an empty line — unless the line boundaries of both writers
        // coincide byte-exactly. That writer boundary closes the platform invocation in front of it.
        // AgentExecutor.log only: in the IME log the service writes the markers and the lines they
        // own itself, and its fragments are executor telemetry lines.
        //
        // A marker lives exactly as long as the bytes of its entry: a rewind sets the markers from
        // the rewind point on aside, and an entry that reads unchanged gets its markers back.

        private sealed class InvocationMarker
        {
            public long Offset;
            public string PolicyId;
            /// <summary>
            /// The platform run the start line belongs to: the lines behind it go to that run while it is pending or
            /// waits for its IME result, and nowhere once it was emitted — never to a later run of the same policy.
            /// Null only on seeded test markers, which resolve by policy.
            /// </summary>
            public string RunId;
            public bool IsClose;
            /// <summary>Set on a writer boundary: the platform invocation it closed.</summary>
            public string ClosedPolicyId;
        }

        /// <summary>Run id of a start line whose run was emitted already (a late line): owns its lines, matches no live run.</summary>
        private const string EmittedRunId = "emitted";

        private const string ExecutorLogFileName = "AgentExecutor.log";

        private readonly Dictionary<string, List<InvocationMarker>> _invocationMarkers =
            new Dictionary<string, List<InvocationMarker>>(StringComparer.OrdinalIgnoreCase);
        private InvocationMarker _lastPlatformMarker;
        // Files a platform run has opened in (PS-AGENT-SCRIPT-START in AgentExecutor.log,
        // PS-SCRIPT-GENERATED in IntuneManagementExtension.log). Survives a rollover of the
        // file's markers: the fallback below is meant for exactly that file.
        private readonly HashSet<string> _platformMarkerFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private long _testEntryOrdinal;

        /// <summary>
        /// The executor log, live or rotated (<c>AgentExecutor-yyyyMMdd-HHmmss.log</c>): its invocations are one process
        /// each, so a writer boundary ends an invocation — in the final bytes of a rotation as much as in the live file.
        /// </summary>
        internal static bool IsWriterBoundaryLogFile(string fileName)
            => string.Equals(fileName, ExecutorLogFileName, StringComparison.OrdinalIgnoreCase)
            || (fileName != null
                && fileName.StartsWith("AgentExecutor-", StringComparison.OrdinalIgnoreCase)
                && fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// True when the entry is exactly one CMTrace record: it opens with <c>&lt;![LOG[</c> (culture-sensitive like
        /// <c>CmTraceLogParser.TryParseLine</c>, so a BOM is ignorable) and holds no second one — the parser takes the
        /// message up to the LAST trailer, so a line cut anywhere, its trailer included, would swallow the record
        /// written over it. An empty entry is the terminator of a cut line. A script whose own output contains the
        /// open tag loses that output line: the bytes cannot tell it from a merged line.
        /// </summary>
        internal static bool IsSingleCmTraceRecord(string text)
        {
            if (string.IsNullOrEmpty(text) || !text.StartsWith(LogOpenTag)) return false;
            var first = text.IndexOf(LogOpenTag, StringComparison.Ordinal);
            return first >= 0 && text.IndexOf(LogOpenTag, first + LogOpenTag.Length, StringComparison.Ordinal) < 0;
        }

        private List<InvocationMarker> GetOrCreateMarkerList(string file)
        {
            List<InvocationMarker> list;
            if (!_invocationMarkers.TryGetValue(file, out list))
            {
                list = new List<InvocationMarker>();
                _invocationMarkers[file] = list;
            }
            return list;
        }

        private static void AppendInvocationMarker(List<InvocationMarker> list, InvocationMarker marker)
        {
            if (list.Count >= MaxInvocationMarkersPerFile) list.RemoveAt(0);
            list.Add(marker);
        }

        private void RecordInvocationMarker(string policyId, bool isClose, string runId = null)
        {
            if (_currentEntryOffset < 0) return;
            var file = _currentSourceFileName ?? TestSourceFileName;
            var list = GetOrCreateMarkerList(file);

            if (list.Count > 0 && list[list.Count - 1].Offset == _currentEntryOffset)
            {
                // Two patterns on one line: the platform start outranks the generic argument line.
                // A writer boundary is never upgraded.
                var last = list[list.Count - 1];
                if (!isClose && policyId != null && last.ClosedPolicyId == null)
                {
                    last.PolicyId = policyId;
                    last.RunId = runId;
                    last.IsClose = false;
                    _lastPlatformMarker = last;
                    _platformMarkerFiles.Add(file);
                }
                return;
            }

            var marker = new InvocationMarker { Offset = _currentEntryOffset, PolicyId = policyId, RunId = runId, IsClose = isClose };
            AppendInvocationMarker(list, marker);
            if (!isClose && policyId != null)
            {
                _lastPlatformMarker = marker;
                _platformMarkerFiles.Add(file);
            }
        }

        /// <summary>
        /// An entry of <paramref name="fileName"/> at <paramref name="offset"/> that is not exactly one CMTrace record:
        /// the bytes behind it were written by another process than the bytes in front of it. Closes the platform
        /// invocation that owns the position; records nothing when none is open there (a cut multi-line output yields
        /// one fragment per line). Idempotent at the same offset.
        /// </summary>
        private void CloseAtWriterBoundary(string fileName, long offset, string text)
        {
            var owner = FindOwningMarker(fileName, offset);
            if (owner == null || owner.IsClose || owner.PolicyId == null) return;

            var list = GetOrCreateMarkerList(fileName);
            if (list.Count > 0 && list[list.Count - 1].Offset >= offset) return;
            AppendInvocationMarker(list, new InvocationMarker { Offset = offset, IsClose = true, ClosedPolicyId = owner.PolicyId });

            var kind = string.IsNullOrEmpty(text) ? "empty line" : text.StartsWith(LogOpenTag) ? "merged line" : "fragment";
            _logger.Debug($"ImeLogTracker: writer boundary in {fileName} at offset {offset} ({kind}, {text?.Length ?? 0} chars) closes platform script {owner.PolicyId} (opened at {owner.Offset}) — the bytes behind it belong to another executor");
        }

        /// <summary>The marker that owns <paramref name="offset"/> in <paramref name="file"/>, or null.</summary>
        private InvocationMarker FindOwningMarker(string file, long offset)
        {
            InvocationMarker marker = null;
            List<InvocationMarker> list = null;
            if (offset >= 0 && _invocationMarkers.TryGetValue(file, out list))
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].Offset <= offset)
                    {
                        marker = list[i];
                        break;
                    }
                }
            }

            // A file without any marker resolves to the platform run in flight only when platform
            // runs open in that file at all — IntuneManagementExtension.log right after a rollover
            // cleared its markers, AgentExecutor.log likewise. HealthScripts.log and the app logs
            // never carry a platform marker: their launch and exit lines belong to health scripts
            // and detection scripts, never to the platform script that happens to be pending
            // (session 24dc69d1: the health-script worker's exit line pre-filled a platform slot's
            // exit code, its launch line the slot's context).
            if (marker == null && (list == null || list.Count == 0) && _platformMarkerFiles.Contains(file)) marker = _lastPlatformMarker;
            return marker;
        }

        /// <summary>
        /// The pending platform script that owns the entry being processed, or null.
        /// <paramref name="ownerPolicyId"/> names the owning platform script even when its slot is
        /// gone (completion already emitted); null when no platform invocation owns the entry.
        /// </summary>
        private ScriptExecutionState ResolvePlatformScriptForCurrentEntry(out string ownerPolicyId)
        {
            ownerPolicyId = null;
            var marker = FindOwningMarker(_currentSourceFileName ?? TestSourceFileName, _currentEntryOffset);
            if (marker == null || marker.IsClose || marker.PolicyId == null) return null;

            ownerPolicyId = marker.PolicyId;
            if (marker.RunId != null) return FindLivePlatformRun(marker.PolicyId, marker.RunId);
            ScriptExecutionState state;
            return _pendingPlatformScripts.TryGetValue(marker.PolicyId, out state) ? state : null;
        }

        /// <summary>Debug trace for an executor line that a writer boundary kept away from a platform script.</summary>
        private void LogLineBehindWriterBoundary(string what)
        {
            var marker = FindOwningMarker(_currentSourceFileName ?? TestSourceFileName, _currentEntryOffset);
            if (marker?.ClosedPolicyId == null) return;
            _logger.Debug($"ImeLogTracker: {what} at offset {_currentEntryOffset} lies behind the writer boundary at {marker.Offset} that closed platform script {marker.ClosedPolicyId} — another executor's end block, not attributed");
        }

        /// <summary>Removes the markers from <paramref name="offset"/> on and returns them in offset order.</summary>
        private List<InvocationMarker> TakeInvocationMarkersFrom(string fileName, long offset, out bool tookLastPlatformMarker)
        {
            tookLastPlatformMarker = false;
            var taken = new List<InvocationMarker>();
            List<InvocationMarker> list;
            if (!_invocationMarkers.TryGetValue(fileName, out list) || list.Count == 0) return taken;

            var from = list.Count;
            while (from > 0 && list[from - 1].Offset >= offset) from--;
            for (var i = from; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], _lastPlatformMarker)) tookLastPlatformMarker = true;
                taken.Add(list[i]);
            }
            list.RemoveRange(from, list.Count - from);

            if (tookLastPlatformMarker)
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
            return taken;
        }

        private void InvalidateInvocationMarkersFrom(string fileName, long offset) => TakeInvocationMarkersFrom(fileName, offset, out _);

        /// <summary>
        /// An entry that reads unchanged after a rewind keeps its markers: gives back the set-aside markers at exactly
        /// <paramref name="entryOffset"/>. Set-aside markers in front of it belonged to entries that changed and stay
        /// gone with their bytes.
        /// </summary>
        private void RestoreInvocationMarkers(RewindState rewind, long entryOffset)
        {
            var markers = rewind.Markers;
            while (rewind.Next < markers.Count && markers[rewind.Next].Offset < entryOffset) rewind.Next++;
            while (rewind.Next < markers.Count && markers[rewind.Next].Offset == entryOffset)
            {
                var marker = markers[rewind.Next++];
                var list = GetOrCreateMarkerList(rewind.FileName);
                if (list.Count > 0 && list[list.Count - 1].Offset >= marker.Offset) continue;
                AppendInvocationMarker(list, marker);
                if (ReferenceEquals(marker, rewind.LastPlatformMarker)) _lastPlatformMarker = marker;
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

        /// <summary>What a rollover of the file does to its invocation markers.</summary>
        internal void ClearInvocationMarkersForTest(string fileName) => InvalidateInvocationMarkersFrom(fileName, 0);

        internal int InvocationMarkerCountForTest(string fileName)
            => _invocationMarkers.TryGetValue(fileName, out var list) ? list.Count : 0;

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

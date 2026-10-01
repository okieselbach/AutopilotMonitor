using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather.Collectors;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather
{
    /// <summary>
    /// Gather-rule state of one enrollment session that survives agent restarts: where each
    /// logparser rule stopped reading each file, and the on_change value each rule last emitted.
    /// <para>
    /// Positions are kept per (rule, file) — two rules parsing the same log each read all of it.
    /// Every position carries a fingerprint of the file's first bytes, so a file replaced under
    /// the same name is read from the beginning even when it is already longer than the position.
    /// </para>
    /// <para>
    /// Every commit is written to disk synchronously, AFTER the events it covers were handed on:
    /// a process that dies in between repeats that one pass after the restart instead of losing it.
    /// The lock guards memory and file write together and is never taken under the executor's
    /// scope lock. Without a persistence (diagnostic mode, tests) the state lives in memory only.
    /// </para>
    /// </summary>
    public sealed class GatherRuleSessionState
    {
        /// <summary>
        /// Files remembered per rule. A run reads at most <see cref="LogParserCollector.MaxFilesPerRun"/>
        /// files; the least recently touched entry beyond the cap is dropped, and its file — should it
        /// come back — is simply read again.
        /// </summary>
        internal const int MaxLogFilesPerRule = 5 * LogParserCollector.MaxFilesPerRun;

        private static readonly char[] WildcardChars = { '*', '?' };

        private readonly object _gate = new object();
        private readonly string _sessionId;
        private readonly GatherRuleStatePersistence _persistence;
        private readonly AgentLogger _logger;
        private readonly Dictionary<string, Dictionary<string, Entry>> _logPositions =
            new Dictionary<string, Dictionary<string, Entry>>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _emittedHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<KeyValuePair<string, string>> _restoredDetails = new List<KeyValuePair<string, string>>();
        private long _touchCounter;
        private bool _unsaved;
        private bool _saveFailureLogged;
        private bool _closed;

        private sealed class Entry
        {
            public long Position;
            public long LineNumber;
            public int HeadLength;
            public string HeadHash;
            public bool FromPreviousRun;
            public long HeldTailStart = -1;
            public long LastTouched;
        }

        public GatherRuleSessionState(string sessionId, GatherRuleStatePersistence persistence, AgentLogger logger)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _persistence = persistence;
            RestoreSummary = Restore();
        }

        /// <summary>One line on what the constructor found on disk — for the agent log and the debug trace.</summary>
        public string RestoreSummary { get; }

        /// <summary>(ruleId, message) per restored position and on_change value — for the debug trace.</summary>
        internal IReadOnlyList<KeyValuePair<string, string>> RestoredDetails => _restoredDetails;

        // ===== logparser positions =====

        /// <summary>The recorded position of <paramref name="filePath"/> for the rule, or null when it has none.</summary>
        internal LogPositionSnapshot PeekLogPosition(string ruleId, string filePath)
        {
            lock (_gate)
            {
                Dictionary<string, Entry> files;
                Entry entry;
                if (!_logPositions.TryGetValue(ruleId, out files) || !files.TryGetValue(filePath, out entry))
                    return null;
                entry.LastTouched = ++_touchCounter;
                return new LogPositionSnapshot(entry.Position, entry.LineNumber, entry.HeadLength, entry.HeadHash,
                    entry.FromPreviousRun, entry.HeldTailStart);
            }
        }

        /// <summary>
        /// Records where the rule stopped in the file and saves the state when it changed.
        /// <paramref name="heldTailStart"/> (in memory only) marks an unfinished last line left for
        /// the next run; -1 when none.
        /// </summary>
        internal void CommitLogPosition(string ruleId, string filePath, long position, long lineNumber,
            LogFileHead head, long heldTailStart)
        {
            var headLength = head.Length;
            var headHash = headLength > 0 ? head.Hash(headLength) : null;

            lock (_gate)
            {
                Dictionary<string, Entry> files;
                if (!_logPositions.TryGetValue(ruleId, out files))
                {
                    files = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                    _logPositions[ruleId] = files;
                }

                Entry entry;
                var changed = false;
                if (!files.TryGetValue(filePath, out entry))
                {
                    entry = new Entry();
                    files[filePath] = entry;
                    changed = true;
                }

                if (changed || entry.Position != position || entry.LineNumber != lineNumber
                    || entry.HeadLength != headLength || !string.Equals(entry.HeadHash, headHash, StringComparison.Ordinal))
                {
                    entry.Position = position;
                    entry.LineNumber = lineNumber;
                    entry.HeadLength = headLength;
                    entry.HeadHash = headHash;
                    entry.FromPreviousRun = false;
                    changed = true;
                }
                entry.HeldTailStart = heldTailStart;
                entry.LastTouched = ++_touchCounter;

                if (files.Count > MaxLogFilesPerRule)
                    EvictLeastRecentlyTouched(ruleId, files);

                if (changed || _unsaved)
                    SaveLocked();
            }
        }

        /// <summary>
        /// Where to continue reading: the recorded position while the file is still the one it was
        /// recorded for, otherwise 0 with the reason.
        /// </summary>
        internal static long ResolveStart(LogPositionSnapshot known, long fileLength, LogFileHead head, out LogReadStart start)
        {
            if (known == null)
            {
                start = LogReadStart.FirstRead;
                return 0;
            }
            if (fileLength < known.Position)
            {
                start = LogReadStart.Shrunk;
                return 0;
            }
            if (known.HeadLength > 0
                && (head.Length < known.HeadLength
                    || !string.Equals(head.Hash(known.HeadLength), known.HeadHash, StringComparison.Ordinal)))
            {
                start = LogReadStart.Replaced;
                return 0;
            }
            start = LogReadStart.Continue;
            return known.Position;
        }

        // ===== on_change values =====

        /// <summary>Rule id → hash of the result the rule last emitted.</summary>
        internal Dictionary<string, string> GetEmittedHashes()
        {
            lock (_gate)
                return new Dictionary<string, string>(_emittedHashes, StringComparer.Ordinal);
        }

        /// <summary>Records the hash of a result that was just emitted and saves the state when it changed.</summary>
        internal void CommitEmittedHash(string ruleId, string hash)
        {
            lock (_gate)
            {
                string current;
                var changed = !_emittedHashes.TryGetValue(ruleId, out current)
                              || !string.Equals(current, hash, StringComparison.Ordinal);
                _emittedHashes[ruleId] = hash;
                if (changed || _unsaved)
                    SaveLocked();
            }
        }

        /// <summary>Retries a save that failed earlier, then stops writing (executor disposed).</summary>
        internal void Close()
        {
            lock (_gate)
            {
                if (_unsaved)
                    SaveLocked();
                _closed = true;
            }
        }

        // ===== persistence =====

        private void SaveLocked()
        {
            if (_persistence == null || _closed)
            {
                _unsaved = false;
                return;
            }

            var data = new GatherRuleStateData
            {
                SessionId = _sessionId,
                SavedAtUtc = DateTime.UtcNow,
                LogPositions = _logPositions
                    .SelectMany(rule => rule.Value.Select(file => new { RuleId = rule.Key, Path = file.Key, Entry = file.Value }))
                    .OrderBy(x => x.Entry.LastTouched)
                    .Select(x => new GatherLogPositionData
                    {
                        RuleId = x.RuleId,
                        Path = x.Path,
                        Position = x.Entry.Position,
                        LineNumber = x.Entry.LineNumber,
                        HeadLength = x.Entry.HeadLength,
                        HeadHash = x.Entry.HeadHash,
                    })
                    .ToList(),
                OnChangeHashes = new Dictionary<string, string>(_emittedHashes, StringComparer.Ordinal),
            };

            if (_persistence.Save(data))
            {
                _unsaved = false;
                _saveFailureLogged = false;
                return;
            }

            _unsaved = true;
            if (!_saveFailureLogged)
            {
                _saveFailureLogged = true;
                _logger.Warning($"GatherRuleSessionState: saving {_persistence.StateFilePath} failed — kept in memory, retried at the next change");
            }
        }

        private string Restore()
        {
            if (_persistence == null)
                return "persistence off (no state directory) — positions and on_change values live in memory only";

            string discardReason;
            var data = _persistence.Load(_sessionId, out discardReason);
            if (data == null)
            {
                return discardReason == null
                    ? "no saved state — first agent run of this session"
                    : $"saved state discarded ({discardReason}) — starting fresh";
            }

            // The file is input like any other: whatever it holds, the executor must come up. A
            // failure here would otherwise stop every collector host, at every restart of the session.
            try
            {
                return RestoreFrom(data);
            }
            catch (Exception ex)
            {
                _logPositions.Clear();
                _emittedHashes.Clear();
                _restoredDetails.Clear();
                _logger.Warning($"GatherRuleSessionState: restoring {_persistence.StateFilePath} failed, starting fresh: {ex.Message}");
                return $"saved state discarded (restore failed: {ex.Message}) — starting fresh";
            }
        }

        private string RestoreFrom(GatherRuleStateData data)
        {
            var dropped = 0;
            foreach (var position in data.LogPositions ?? new List<GatherLogPositionData>())
            {
                if (!IsValid(position))
                {
                    dropped++;
                    continue;
                }

                Dictionary<string, Entry> files;
                if (!_logPositions.TryGetValue(position.RuleId, out files))
                {
                    files = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                    _logPositions[position.RuleId] = files;
                }
                files[position.Path] = new Entry
                {
                    Position = position.Position,
                    LineNumber = position.LineNumber,
                    HeadLength = position.HeadLength,
                    HeadHash = position.HeadLength > 0 ? position.HeadHash : null,
                    FromPreviousRun = true,
                    LastTouched = ++_touchCounter,
                };
                if (files.Count > MaxLogFilesPerRule)
                    EvictLeastRecentlyTouched(position.RuleId, files);
            }

            foreach (var hash in data.OnChangeHashes ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrEmpty(hash.Key) || string.IsNullOrEmpty(hash.Value))
                {
                    dropped++;
                    continue;
                }
                _emittedHashes[hash.Key] = hash.Value;
            }

            foreach (var rule in _logPositions)
            {
                foreach (var file in rule.Value)
                {
                    _restoredDetails.Add(new KeyValuePair<string, string>(rule.Key,
                        $"logparser position restored: {Path.GetFileName(file.Key)} at byte {file.Value.Position}, after line {file.Value.LineNumber}"));
                }
            }
            foreach (var hash in _emittedHashes)
            {
                _restoredDetails.Add(new KeyValuePair<string, string>(hash.Key,
                    $"on_change value restored (hash {Prefix(hash.Value)}) — an unchanged result stays silent"));
            }

            var positionCount = _logPositions.Sum(rule => rule.Value.Count);
            return $"restored {positionCount} logparser position(s) for {_logPositions.Count} rule(s) and {_emittedHashes.Count} on_change value(s), " +
                   $"saved {data.SavedAtUtc:yyyy-MM-ddTHH:mm:ssZ} by a previous agent run of this session" +
                   (dropped > 0 ? $"; {dropped} invalid entr{(dropped == 1 ? "y" : "ies")} dropped" : "") +
                   " — logparser rules continue after their saved position, on_change rules stay silent until their result changes";
        }

        private static bool IsValid(GatherLogPositionData position)
        {
            if (position == null || string.IsNullOrEmpty(position.RuleId) || string.IsNullOrEmpty(position.Path))
                return false;
            // A real resolved path never carries these; Path APIs throw on them under .NET Framework.
            if (position.Path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || position.Path.IndexOfAny(WildcardChars) >= 0)
                return false;
            if (position.Position < 0 || position.LineNumber < 0)
                return false;
            if (position.HeadLength < 0 || position.HeadLength > LogFileHead.MaxBytes)
                return false;
            if (position.HeadLength > 0 && string.IsNullOrEmpty(position.HeadHash))
                return false;
            // Reading any byte records a non-empty head; a position without one cannot be verified.
            return position.Position == 0 || position.HeadLength > 0;
        }

        private void EvictLeastRecentlyTouched(string ruleId, Dictionary<string, Entry> files)
        {
            var oldest = files.OrderBy(file => file.Value.LastTouched).First();
            files.Remove(oldest.Key);
            _logger.Info($"GatherRuleSessionState: rule {ruleId} tracks more than {MaxLogFilesPerRule} files — forgot the position of {Path.GetFileName(oldest.Key)}");
        }

        private static string Prefix(string hash) => hash.Length > 8 ? hash.Substring(0, 8) : hash;
    }

    /// <summary>Immutable copy of a recorded logparser position.</summary>
    internal sealed class LogPositionSnapshot
    {
        public LogPositionSnapshot(long position, long lineNumber, int headLength, string headHash,
            bool fromPreviousRun, long heldTailStart)
        {
            Position = position;
            LineNumber = lineNumber;
            HeadLength = headLength;
            HeadHash = headHash;
            FromPreviousRun = fromPreviousRun;
            HeldTailStart = heldTailStart;
        }

        public long Position { get; }
        public long LineNumber { get; }
        public int HeadLength { get; }
        public string HeadHash { get; }

        /// <summary>Restored from disk and not advanced by this process yet.</summary>
        public bool FromPreviousRun { get; }

        /// <summary>Start of an unfinished last line left for this run by the previous one; -1 when none.</summary>
        public long HeldTailStart { get; }
    }

    /// <summary>How <see cref="GatherRuleSessionState.ResolveStart"/> decided where reading starts.</summary>
    internal enum LogReadStart
    {
        FirstRead,
        Continue,
        Shrunk,
        Replaced,
    }
}

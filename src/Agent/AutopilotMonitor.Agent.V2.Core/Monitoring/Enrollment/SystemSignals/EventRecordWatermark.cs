using System;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Cross-restart dedup for event-log watchers that backfill a channel on start. RecordIds are
    /// per channel, so every watched channel owns one instance with its own state file.
    /// <para>
    /// Two boundaries, deliberately separate: the restart watermark is the highest RecordId a
    /// PRIOR run emitted (loaded once, immutable for this run); the seen-set holds what THIS run
    /// processed. A single high-water mark would be wrong because the live watcher is armed before
    /// the backfill runs: a live record with a higher RecordId can arrive first and would then
    /// make the backfill skip every older, never-emitted record.
    /// </para>
    /// </summary>
    internal sealed class EventRecordWatermark
    {
        private readonly string _stateFilePath;
        private readonly AgentLogger _logger;
        private readonly string _label;
        private readonly object _lock = new object();
        private readonly HashSet<long> _seenThisRun = new HashSet<long>();
        private long _restartWatermark = -1;
        private long _maxEmittedRecordId = -1;

        /// <param name="stateDirectory">Directory for the state file; null keeps the dedup in memory only.</param>
        /// <param name="stateFileName">File name inside <paramref name="stateDirectory"/>.</param>
        /// <param name="label">Watcher name for log lines.</param>
        public EventRecordWatermark(string stateDirectory, string stateFileName, AgentLogger logger, string label)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _label = label ?? "EventLog";
            _stateFilePath = string.IsNullOrEmpty(stateDirectory) || string.IsNullOrEmpty(stateFileName)
                ? null
                : Path.Combine(Environment.ExpandEnvironmentVariables(stateDirectory), stateFileName);
        }

        /// <summary>Highest RecordId emitted by a prior run; -1 when none was persisted.</summary>
        public long RestartWatermark
        {
            get { lock (_lock) return _restartWatermark; }
        }

        /// <summary>True when the record was already emitted, by a prior run or by this one.</summary>
        public bool IsAlreadyProcessed(long recordId)
        {
            if (recordId < 0) return false; // no RecordId → cannot dedup, let it through
            lock (_lock)
            {
                return recordId <= _restartWatermark || _seenThisRun.Contains(recordId);
            }
        }

        /// <summary>
        /// Atomically claims <paramref name="recordId"/> for this run. True on first sight (the
        /// caller processes it), false when it was already processed. Records without a RecordId
        /// (-1) are always claimed, never tracked. Claiming persists nothing — only
        /// <see cref="MarkEmitted"/> does, so a record that is counted instead of emitted costs no
        /// state-file write (CBS maintenance writes hundreds of servicing records in seconds).
        /// </summary>
        public bool TryClaim(long recordId)
        {
            if (recordId < 0) return true;
            lock (_lock)
            {
                return recordId > _restartWatermark && _seenThisRun.Add(recordId);
            }
        }

        /// <summary>
        /// Persists <paramref name="recordId"/> when it raises the emitted maximum, so a restarted
        /// agent's backfill skips it. Called after the record's event and engine signal were
        /// handed to the ingress: a crash before that re-emits the record instead of losing it.
        /// The ingress makes them durable only when its worker appends them to the SignalLog —
        /// normally milliseconds later; a process killed inside that window (e.g. by a reboot)
        /// loses the record, and the backfill does not bring it back. Counted records above the
        /// persisted maximum are counted again after a restart, which only touches the activity
        /// summary.
        /// </summary>
        public void MarkEmitted(long recordId)
        {
            if (recordId < 0) return;

            long toPersist = -1;
            lock (_lock)
            {
                if (recordId > _maxEmittedRecordId)
                {
                    _maxEmittedRecordId = recordId;
                    toPersist = recordId;
                }
            }
            if (toPersist >= 0) Persist(toPersist);
        }

        public void Load()
        {
            if (_stateFilePath == null || !File.Exists(_stateFilePath)) return;

            try
            {
                var state = JsonConvert.DeserializeObject<WatermarkState>(File.ReadAllText(_stateFilePath));
                if (state == null) return;
                lock (_lock)
                {
                    _restartWatermark = state.LastRecordId;
                    _maxEmittedRecordId = state.LastRecordId;
                }
                _logger.Info($"{_label} watermark loaded: lastRecordId={state.LastRecordId} (persisted {state.PersistedUtc:O})");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to load {_label} watermark: {ex.Message}");
            }
        }

        private void Persist(long recordId)
        {
            if (_stateFilePath == null) return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath));
                var json = JsonConvert.SerializeObject(
                    new WatermarkState { LastRecordId = recordId, PersistedUtc = DateTime.UtcNow },
                    Formatting.Indented);
                var tempPath = _stateFilePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Copy(tempPath, _stateFilePath, overwrite: true);
                try { File.Delete(tempPath); } catch { }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to persist {_label} watermark: {ex.Message}");
            }
        }

        internal sealed class WatermarkState
        {
            public long LastRecordId { get; set; }
            public DateTime PersistedUtc { get; set; }
        }
    }
}

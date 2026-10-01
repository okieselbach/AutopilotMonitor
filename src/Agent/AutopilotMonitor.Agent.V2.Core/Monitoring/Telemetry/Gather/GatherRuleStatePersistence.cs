using System;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather
{
    /// <summary>
    /// Persists <see cref="GatherRuleSessionState"/> in the agent's state directory so one
    /// enrollment's logparser positions and on_change values survive agent restarts, reboots and
    /// the WhiteGlove Part 2 resume.
    /// <para>
    /// The state is only resumed for the SAME session id: the state directory is one fixed folder
    /// that a session rotation does not clear, and the new session starts empty in the backend, so
    /// it must read its logs from the beginning.
    /// </para>
    /// Fail-soft like <c>BandwidthStatePersistence</c>: I/O errors never throw; a missing, corrupt
    /// or foreign file loads as null (fresh start — the pre-persistence behavior).
    /// </summary>
    public class GatherRuleStatePersistence
    {
        public const string FileName = "gather-rule-state.json";

        private const string SourceName = "GatherRuleStatePersistence";

        private readonly string _stateDirectory;
        private readonly string _stateFilePath;
        private readonly AgentLogger _logger;

        public GatherRuleStatePersistence(string stateDirectory, AgentLogger logger)
        {
            _stateDirectory = Environment.ExpandEnvironmentVariables(stateDirectory);
            _stateFilePath = Path.Combine(_stateDirectory, FileName);
            _logger = logger;
        }

        public string StateFilePath => _stateFilePath;

        /// <summary>
        /// Loads the persisted state, or null when absent, unreadable, or written for a different
        /// session. <paramref name="discardReason"/> is null when there simply was no file.
        /// </summary>
        public GatherRuleStateData Load(string expectedSessionId, out string discardReason)
        {
            discardReason = null;
            if (!File.Exists(_stateFilePath)) return null;
            try
            {
                var json = File.ReadAllText(_stateFilePath);
                var state = JsonConvert.DeserializeObject<GatherRuleStateData>(json);
                if (state == null || string.IsNullOrEmpty(state.SessionId))
                {
                    discardReason = "state file empty or invalid";
                    _logger.Warning($"[{SourceName}] persisted state file was empty or invalid — starting fresh");
                    return null;
                }
                if (!string.Equals(state.SessionId, expectedSessionId, StringComparison.OrdinalIgnoreCase))
                {
                    discardReason = $"saved for session {state.SessionId}, current session is {expectedSessionId}";
                    _logger.Warning($"[{SourceName}] persisted state belongs to session {state.SessionId}, not {expectedSessionId} — starting fresh");
                    return null;
                }
                return state;
            }
            catch (Exception ex)
            {
                discardReason = $"state file unreadable: {ex.Message}";
                _logger.Warning($"[{SourceName}] failed to load persisted state, starting fresh: {ex.Message}");
                return null;
            }
        }

        /// <summary>Persists the state (atomic write via temp file). Never throws; false when the write failed.</summary>
        public bool Save(GatherRuleStateData state)
        {
            try
            {
                Directory.CreateDirectory(_stateDirectory);
                var tempPath = _stateFilePath + ".tmp";
                File.WriteAllText(tempPath, JsonConvert.SerializeObject(state));
                if (File.Exists(_stateFilePath))
                {
                    File.Replace(tempPath, _stateFilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, _stateFilePath);
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.Debug($"[{SourceName}] failed to save state: {ex.Message}");
                return false;
            }
        }
    }

    // ---- DTOs for state serialization ----

    public class GatherRuleStateData
    {
        public string SessionId { get; set; }
        public DateTime SavedAtUtc { get; set; }

        /// <summary>Least recently used first, so a restore keeps the eviction order.</summary>
        public List<GatherLogPositionData> LogPositions { get; set; }

        /// <summary>Rule id → canonical hash of the result the rule last emitted (on_change).</summary>
        public Dictionary<string, string> OnChangeHashes { get; set; }
    }

    public class GatherLogPositionData
    {
        public string RuleId { get; set; }
        public string Path { get; set; }

        /// <summary>File offset right behind the last line the rule processed.</summary>
        public long Position { get; set; }

        /// <summary>Lines before <see cref="Position"/> — the base for <c>logLineNumber</c>.</summary>
        public long LineNumber { get; set; }

        /// <summary>Bytes from the start of the file the fingerprint covers (0..4096).</summary>
        public int HeadLength { get; set; }

        /// <summary>SHA-256 of those bytes, Base64; null when <see cref="HeadLength"/> is 0.</summary>
        public string HeadHash { get; set; }
    }
}

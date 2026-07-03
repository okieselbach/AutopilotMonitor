using System;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// Handles persistence of ImeLogTracker state to disk (JSON serialization).
    /// Extracted from ImeLogTracker to separate I/O concerns from log parsing.
    /// </summary>
    public class ImeTrackerStatePersistence
    {
        private readonly string _stateDirectory;
        private readonly string _stateFilePath;
        private readonly AgentLogger _logger;

        public ImeTrackerStatePersistence(string stateDirectory, AgentLogger logger)
        {
            _stateDirectory = Environment.ExpandEnvironmentVariables(stateDirectory);
            _stateFilePath = Path.Combine(_stateDirectory, "ime-tracker-state.json");
            _logger = logger;
        }

        public string StateFilePath => _stateFilePath;

        /// <summary>
        /// Loads persisted state from disk. Returns null if no state file exists or on error.
        /// </summary>
        public ImeTrackerStateData Load()
        {
            if (!File.Exists(_stateFilePath))
            {
                _logger.Info("ImeLogTracker: no persisted state found (fresh enrollment)");
                return null;
            }

            try
            {
                var json = File.ReadAllText(_stateFilePath);
                var state = JsonConvert.DeserializeObject<ImeTrackerStateData>(json);
                if (state == null)
                {
                    _logger.Warning("ImeLogTracker: persisted state file was empty or invalid");
                    return null;
                }
                return state;
            }
            catch (Exception ex)
            {
                _logger.Warning($"ImeLogTracker: failed to load persisted state, starting fresh: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Persists tracker state to disk as JSON (atomic write via temp file).
        /// </summary>
        public void Save(ImeTrackerStateData state)
        {
            try
            {
                Directory.CreateDirectory(_stateDirectory);
                var tempPath = _stateFilePath + ".tmp";
                // M1: Formatting.None (was Indented — this state is machine-only, ~halves the
                // bytes for 30+ packages) and an atomic File.Replace/Move instead of File.Copy
                // (which read+wrote the whole file again AND was non-atomic).
                File.WriteAllText(tempPath, JsonConvert.SerializeObject(state));
                if (File.Exists(_stateFilePath))
                {
                    File.Replace(tempPath, _stateFilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, _stateFilePath);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"ImeLogTracker: failed to save state: {ex.Message}");
            }
        }

        /// <summary>
        /// Deletes persisted state file.
        /// </summary>
        public void Delete()
        {
            try
            {
                if (File.Exists(_stateFilePath))
                {
                    File.Delete(_stateFilePath);
                    _logger.Info("ImeLogTracker: persisted state deleted");
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"ImeLogTracker: failed to delete state file: {ex.Message}");
            }
        }
    }

    // ---- DTOs for state serialization ----

    public class ImeTrackerStateData
    {
        public int CurrentPhaseOrder { get; set; }
        public string LastEspPhaseDetected { get; set; }
        public bool AllAppsCompletedFired { get; set; }
        public bool LogPhaseIsCurrentPhase { get; set; }
        public List<string> SeenAppIds { get; set; }
        public List<string> IgnoreList { get; set; }
        public string CurrentPackageId { get; set; }
        public List<PackageStateData> Packages { get; set; }
        public Dictionary<string, FilePositionData> FilePositions { get; set; }

        // Codex follow-up (882fef64 PR3-PR5 review): per-phase package snapshots that the
        // tracker captures just before _packageStates.Clear() on the next-phase transition.
        // Without persistence, an agent restart between phases (common on hybrid-join +
        // multi-reboot enrollments) loses the earlier-phase apps from the union returned by
        // GetAllKnownPackageStates, which in turn drops them from FinalStatus + the
        // app_tracking_summary terminal event. Null on agents from before this field was
        // added — LoadState handles that as "no snapshots" and the in-memory dict stays empty
        // (degrades gracefully back to the live _packageStates view).
        public Dictionary<string, List<PackageStateData>> PhasePackageSnapshots { get; set; }

        // H1 (delta review 2026-07-02): platform-script completion dedup must survive agent
        // restarts. The shutdown pass force-flushes fallback completions from AgentExecutor exit
        // codes; IME's authoritative PS-SCRIPT-RESULT line is often written AFTER that flush and
        // lies beyond the persisted file position, so the restarted tracker parses it fresh —
        // without the persisted marker set it would emit a duplicate script_completed. The
        // pending buffer rides along so a script whose exit code was seen but not yet flushed
        // continues its grace window after the restart instead of being silently dropped.
        // ScriptExecutionState is a plain DTO, serialized directly (no mapping layer needed).
        // All three are null on state files from before this fix — LoadState treats null as empty.
        public List<string> PlatformScriptResultEmitted { get; set; }
        public List<ScriptExecutionState> PendingPlatformScripts { get; set; }
        public List<string> ScriptTimeoutSuspectedPosted { get; set; }
    }

    public class PackageStateData
    {
        public string Id { get; set; }
        public int ListPos { get; set; }
        public string Name { get; set; }
        public int RunAs { get; set; }
        public int Intent { get; set; }
        public int Targeted { get; set; }
        public List<string> DependsOn { get; set; }
        public int InstallationState { get; set; }
        public bool DownloadingOrInstallingSeen { get; set; }
        public int? ProgressPercent { get; set; }
        public long BytesDownloaded { get; set; }
        public long BytesTotal { get; set; }

        // Error context
        public string ErrorPatternId { get; set; }
        public string ErrorDetail { get; set; }
        public string ErrorCode { get; set; }

        // Installer result codes
        public string ExitCode { get; set; }
        public string HResultFromWin32 { get; set; }

        // Delivery Optimization telemetry
        public long DoFileSize { get; set; }
        public long DoTotalBytesDownloaded { get; set; }
        public long DoBytesFromPeers { get; set; }
        public int DoPercentPeerCaching { get; set; }
        public long DoBytesFromLanPeers { get; set; }
        public long DoBytesFromGroupPeers { get; set; }
        public long DoBytesFromInternetPeers { get; set; }
        // Newer DO breakdown fields — null-defaulting deserialization handles older state files
        // (machines that crashed mid-enrollment before this addition will deserialize these as 0/null).
        public long DoBytesFromLinkLocalPeers { get; set; }
        public long DoBytesFromCacheServer { get; set; }
        public string DoCacheHost { get; set; }
        public int DoDownloadMode { get; set; } = -1;
        public string DoDownloadDuration { get; set; }
        public long DoBytesFromHttp { get; set; }
        public bool HasDoTelemetry { get; set; }

        // App metadata (captured from IME logs for App Dashboard reporting)
        public string AppVersion { get; set; }
        public string AppType { get; set; }
        public int AttemptNumber { get; set; }
        public string DetectionResult { get; set; }
    }

    public class FilePositionData
    {
        public long Position { get; set; }
        public long LastKnownSize { get; set; }
    }
}

#nullable enable
using System;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office
{
    /// <summary>
    /// Persists the <see cref="OfficeInstallDetector"/> lifecycle state across agent restarts
    /// (one enrollment commonly spans several reboots). Without it every restart re-armed the
    /// detector from Idle and all three start triggers fire falsely on an already-installed
    /// Office: the <c>Scenario\INSTALL</c> registry key persists post-install, OfficeC2RClient.exe
    /// runs again for update checks, and Office updates stream from the same CDN — each restart
    /// then emitted a duplicate started+completed pair (~0s duration).
    /// <para>
    /// Semantics: <c>Active</c> means "started was emitted, no terminal yet" — the next run resumes
    /// the open lifecycle (no second started; a missed completion is delivered with the original
    /// start time). <c>Completed</c>/<c>Failed</c> mean the terminal event went out — the next run
    /// does not arm the detector at all. <see cref="OfficeInstallDetector.AbandonSilently"/> is
    /// deliberately NOT persisted as terminal: it fires on every dispose/shutdown and would
    /// otherwise block the resume that catches the completion after a mid-install reboot.
    /// </para>
    /// Fail-soft like <c>ImeTrackerStatePersistence</c>: I/O errors never throw; a missing or
    /// corrupt file loads as null (fresh start — worst case is today's duplicate-pair behavior).
    /// </summary>
    public class OfficeInstallStatePersistence
    {
        private readonly string _stateDirectory;
        private readonly string _stateFilePath;
        private readonly AgentLogger _logger;

        public OfficeInstallStatePersistence(string stateDirectory, AgentLogger logger)
        {
            _stateDirectory = Environment.ExpandEnvironmentVariables(stateDirectory);
            _stateFilePath = Path.Combine(_stateDirectory, "office-install-state.json");
            _logger = logger;
        }

        public string StateFilePath => _stateFilePath;

        /// <summary>Loads the persisted lifecycle state, or null when absent/unreadable.</summary>
        public OfficeInstallStateData? Load()
        {
            if (!File.Exists(_stateFilePath)) return null;
            try
            {
                var json = File.ReadAllText(_stateFilePath);
                var state = JsonConvert.DeserializeObject<OfficeInstallStateData>(json);
                if (state == null || string.IsNullOrEmpty(state.State))
                {
                    _logger.Warning($"[{OfficeInstallDetector.SourceName}] persisted state file was empty or invalid — starting fresh");
                    return null;
                }
                return state;
            }
            catch (Exception ex)
            {
                _logger.Warning($"[{OfficeInstallDetector.SourceName}] failed to load persisted state, starting fresh: {ex.Message}");
                return null;
            }
        }

        /// <summary>Persists the lifecycle state (atomic write via temp file). Never throws.</summary>
        public void Save(OfficeInstallStateData state)
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
            }
            catch (Exception ex)
            {
                _logger.Debug($"[{OfficeInstallDetector.SourceName}] failed to save state: {ex.Message}");
            }
        }
    }

    // ---- DTOs for state serialization ----

    public class OfficeInstallStateData
    {
        public const string StateActive = "Active";
        public const string StateCompleted = "Completed";
        public const string StateFailed = "Failed";
        // The C2R activity was on an already-resident Office (OEM/consumer inbox) — a
        // office_preinstalled_detected went out, no install lifecycle was opened. Persisted ONLY to
        // suppress a duplicate emit across reboots; deliberately NOT terminal (see IsTerminal), because
        // an enrollment commonly uninstalls the inbox Office and lays down a fresh one afterwards — the
        // next run must re-arm the detector to catch that real install.
        public const string StatePreinstalled = "Preinstalled";

        /// <summary><see cref="StateActive"/>, <see cref="StateCompleted"/>, <see cref="StateFailed"/> or <see cref="StatePreinstalled"/>.</summary>
        public string? State { get; set; }

        public DateTime? StartedAtUtc { get; set; }
        public string? StartedTrigger { get; set; }

        /// <summary>Stamped only on the two terminal persists (Completed/Failed) — basis for the
        /// summary dialog's Office row duration. Null on Active/Preinstalled and on state files
        /// written by older agents (fail-soft deserialization).</summary>
        public DateTime? CompletedAtUtc { get; set; }

        /// <summary>Office build reached at the terminal transition (snapshot's VersionToReport).
        /// Null when the snapshot had none or on older state files.</summary>
        public string? VersionReached { get; set; }

        /// <summary>Highest-bytes DO sample seen — basis for the doSummary on a resumed completion.</summary>
        public OfficeDoPeakData? PeakDo { get; set; }

        // Preinstalled is intentionally excluded — it is persisted for emit-once dedup but must NOT
        // block the next run from arming the detector (a later fresh install must still be detected).
        [JsonIgnore]
        public bool IsTerminal => State == StateCompleted || State == StateFailed;
    }

    /// <summary>Persisted mirror of the <see cref="OfficeDoSample"/> fields the completed doSummary needs.</summary>
    public class OfficeDoPeakData
    {
        public int JobCount { get; set; }
        public long FileSize { get; set; }
        public long TotalBytesDownloaded { get; set; }
        public long BytesFromPeers { get; set; }
        public long BytesFromHttp { get; set; }
        public long BytesFromCacheServer { get; set; }
        public int DownloadMode { get; set; }

        public static OfficeDoPeakData? FromSample(OfficeDoSample? sample)
        {
            if (sample == null) return null;
            return new OfficeDoPeakData
            {
                JobCount = sample.JobCount,
                FileSize = sample.FileSize,
                TotalBytesDownloaded = sample.TotalBytesDownloaded,
                BytesFromPeers = sample.BytesFromPeers,
                BytesFromHttp = sample.BytesFromHttp,
                BytesFromCacheServer = sample.BytesFromCacheServer,
                DownloadMode = sample.DownloadMode,
            };
        }

        public OfficeDoSample ToSample() => new OfficeDoSample
        {
            JobCount = JobCount,
            FileSize = FileSize,
            TotalBytesDownloaded = TotalBytesDownloaded,
            BytesFromPeers = BytesFromPeers,
            BytesFromHttp = BytesFromHttp,
            BytesFromCacheServer = BytesFromCacheServer,
            DownloadMode = DownloadMode,
        };
    }
}

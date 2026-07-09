#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic
{
    /// <summary>
    /// Persists the <see cref="BandwidthEstimator"/> accumulator across agent restarts (one
    /// enrollment commonly spans several reboots). Without it, a reboot before the interim
    /// snapshot — e.g. a Win32 app forcing a restart mid-DeviceSetup — silently discards every
    /// rate sample collected so far; if little downloads after the reboot, the session ends
    /// with a weak or missing estimate although gigabytes were observed.
    /// <para>
    /// Save points are cheap and event-driven (never per-poll): when the collector goes
    /// dormant (all downloads done — which is exactly the moment an app-forced reboot becomes
    /// possible), after the interim emission, and at collector stop. <c>InterimEmitted</c>
    /// rides in the same file so the device_setup_end snapshot stays once-per-SESSION rather
    /// than once-per-process.
    /// </para>
    /// The state is only resumed for the SAME session id — the state directory has a
    /// per-enrollment lifecycle, but the check makes a stale file from an aborted run harmless.
    /// Fail-soft like <c>OfficeInstallStatePersistence</c>: I/O errors never throw; a missing
    /// or corrupt file loads as null (fresh start — worst case is the pre-persistence behavior).
    /// </summary>
    public class BandwidthStatePersistence
    {
        private const string SourceName = "BandwidthStatePersistence";

        private readonly string _stateDirectory;
        private readonly string _stateFilePath;
        private readonly AgentLogger _logger;

        public BandwidthStatePersistence(string stateDirectory, AgentLogger logger)
        {
            _stateDirectory = Environment.ExpandEnvironmentVariables(stateDirectory);
            _stateFilePath = Path.Combine(_stateDirectory, "bandwidth-state.json");
            _logger = logger;
        }

        public string StateFilePath => _stateFilePath;

        /// <summary>
        /// Loads the persisted state, or null when absent, unreadable, or belonging to a
        /// different session.
        /// </summary>
        public BandwidthStateData? Load(string expectedSessionId)
        {
            if (!File.Exists(_stateFilePath)) return null;
            try
            {
                var json = File.ReadAllText(_stateFilePath);
                var state = JsonConvert.DeserializeObject<BandwidthStateData>(json);
                if (state == null || string.IsNullOrEmpty(state.SessionId))
                {
                    _logger.Warning($"[{SourceName}] persisted state file was empty or invalid — starting fresh");
                    return null;
                }
                if (!string.Equals(state.SessionId, expectedSessionId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warning($"[{SourceName}] persisted state belongs to session {state.SessionId}, not {expectedSessionId} — starting fresh");
                    return null;
                }
                return state;
            }
            catch (Exception ex)
            {
                _logger.Warning($"[{SourceName}] failed to load persisted state, starting fresh: {ex.Message}");
                return null;
            }
        }

        /// <summary>Persists the state (atomic write via temp file). Never throws.</summary>
        public void Save(BandwidthStateData state)
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
                _logger.Debug($"[{SourceName}] failed to save state: {ex.Message}");
            }
        }
    }

    /// <summary>Persisted mirror of the estimator accumulator plus the interim once-guard.</summary>
    public class BandwidthStateData
    {
        public string? SessionId { get; set; }
        public DateTime SavedAtUtc { get; set; }

        /// <summary>True once the device_setup_end interim snapshot went out (this session, any process).</summary>
        public bool InterimEmitted { get; set; }

        public List<double>? WanSamplesMbps { get; set; }
        public List<double>? LanSamplesMbps { get; set; }
        public long WanBytesObserved { get; set; }
        public long LanBytesObserved { get; set; }

        public BandwidthEstimatorState ToEstimatorState() => new BandwidthEstimatorState
        {
            WanSamplesMbps = WanSamplesMbps,
            LanSamplesMbps = LanSamplesMbps,
            WanBytesObserved = WanBytesObserved,
            LanBytesObserved = LanBytesObserved,
        };
    }
}

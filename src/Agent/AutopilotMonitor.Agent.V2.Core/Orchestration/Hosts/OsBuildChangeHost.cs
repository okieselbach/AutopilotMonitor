#nullable enable
using System;
using System.IO;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Deterministic Windows-Update corroboration (session 7443317c, 2026-07-10): persists the
    /// OS build (<c>CurrentBuild.UBR</c>) across agent restarts and emits a one-shot
    /// <c>os_build_changed</c> event when it differs — an update provably installed during the
    /// enrollment, no matter which servicing path it took. This closes the WindowsUpdateTracker
    /// blind spot deterministically: in 7443317c the build jumped 26200.8037→26200.8655 across a
    /// mid-OOBE reboot while the WU channel carried none of the targeted EventIDs. The comparison
    /// happens AFTER the reboot by construction, so unlike event-channel capture it cannot be
    /// lost to timing, watermarks, or an unexpected logging path.
    /// <para>
    /// <see cref="BuildChanged"/> feeds the <see cref="WindowsUpdateWatcherHost"/> channel census
    /// (start-order: this host runs first) so a captured-nothing run can self-report the EventID
    /// histogram of the update channels.
    /// </para>
    /// <para>
    /// First run seeds the state file silently. Reads use the 64-bit registry view — the agent is
    /// a 32-bit process on x64, and the default view would redirect to WOW6432Node.
    /// </para>
    /// </summary>
    internal sealed class OsBuildChangeHost : ICollectorHost
    {
        internal const string StateFileName = "os-build-state.json";
        private const string RegKeyCurrentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

        public string Name => "OsBuildChangeDetector";

        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly AgentLogger _logger;
        private readonly InformationalEventPost _post;
        private readonly string? _stateDirectory;
        private readonly Func<string?> _buildReader;
        private int _disposed;

        /// <summary>True once <see cref="Start"/> found a build different from the persisted one.</summary>
        public bool BuildChanged { get; private set; }

        public OsBuildChangeHost(
            string sessionId,
            string tenantId,
            AgentLogger logger,
            ISignalIngressSink ingress,
            IClock clock,
            string? stateDirectory,
            Func<string?>? buildReader = null)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _post = new InformationalEventPost(ingress, clock, logger);
            _stateDirectory = stateDirectory != null ? Environment.ExpandEnvironmentVariables(stateDirectory) : null;
            _buildReader = buildReader ?? ReadOsBuildFromRegistry;
        }

        public void Start()
        {
            try
            {
                DetectAndEmit();
            }
            catch (Exception ex)
            {
                // One-shot observability — never let it break the host pipeline.
                _logger.Warning($"OsBuildChangeDetector: detection failed: {ex.Message}");
            }
        }

        public void Stop() { }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Stop();
        }

        internal void DetectAndEmit()
        {
            var currentBuild = _buildReader();
            if (string.IsNullOrEmpty(currentBuild))
            {
                _logger.Warning("OsBuildChangeDetector: could not read the current OS build — skipping");
                return;
            }

            if (string.IsNullOrEmpty(_stateDirectory))
            {
                _logger.Debug("OsBuildChangeDetector: no state directory configured — skipping");
                return;
            }

            var state = LoadState();
            var previousBuild = state?.OsBuild;
            if (state == null || string.IsNullOrEmpty(previousBuild))
            {
                // First run of this enrollment: seed silently — there is no "previous" to compare.
                PersistState(new OsBuildState { OsBuild = currentBuild, CapturedUtc = DateTime.UtcNow });
                _logger.Info($"OsBuildChangeDetector: seeded OS build {currentBuild}");
                return;
            }

            if (string.Equals(previousBuild, currentBuild, StringComparison.Ordinal))
            {
                _logger.Debug($"OsBuildChangeDetector: OS build unchanged ({currentBuild})");
                return;
            }

            BuildChanged = true;
            _logger.Info(
                $"OsBuildChangeDetector: OS build changed across restart {previousBuild} -> {currentBuild} " +
                $"(previous captured {state.CapturedUtc:o})");

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = Constants.EventTypes.OsBuildChanged,
                Severity = EventSeverity.Info,
                Source = Name,
                Phase = EnrollmentPhase.Unknown,
                Message = $"OS build changed across restart: {previousBuild} -> {currentBuild} — a Windows update was likely installed during enrollment",
                Data = new System.Collections.Generic.Dictionary<string, object>
                {
                    { "previousBuild", previousBuild! },
                    { "currentBuild", currentBuild! },
                    { "previousCapturedUtc", state.CapturedUtc.ToString("o") },
                },
                ImmediateUpload = true,
            });

            PersistState(new OsBuildState { OsBuild = currentBuild, CapturedUtc = DateTime.UtcNow });
        }

        /// <summary>
        /// <c>CurrentBuild.UBR</c> (e.g. "26200.8655") from the 64-bit HKLM view, or null when
        /// either value is unreadable.
        /// </summary>
        internal static string? ReadOsBuildFromRegistry()
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = baseKey.OpenSubKey(RegKeyCurrentVersion))
                {
                    if (key == null) return null;
                    var currentBuild = key.GetValue("CurrentBuild")?.ToString();
                    var ubr = key.GetValue("UBR")?.ToString();
                    if (string.IsNullOrEmpty(currentBuild) || string.IsNullOrEmpty(ubr)) return null;
                    return $"{currentBuild}.{ubr}";
                }
            }
            catch
            {
                return null;
            }
        }

        private OsBuildState? LoadState()
        {
            try
            {
                var path = Path.Combine(_stateDirectory!, StateFileName);
                if (!File.Exists(path)) return null;
                return JsonConvert.DeserializeObject<OsBuildState>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                // Corrupt state → treat as first run (re-seed); losing one comparison beats crashing.
                _logger.Warning($"OsBuildChangeDetector: state file unreadable, re-seeding: {ex.Message}");
                return null;
            }
        }

        private void PersistState(OsBuildState state)
        {
            try
            {
                Directory.CreateDirectory(_stateDirectory!);
                File.WriteAllText(
                    Path.Combine(_stateDirectory!, StateFileName),
                    JsonConvert.SerializeObject(state));
            }
            catch (Exception ex)
            {
                _logger.Warning($"OsBuildChangeDetector: failed to persist state: {ex.Message}");
            }
        }

        internal sealed class OsBuildState
        {
            public string? OsBuild { get; set; }
            public DateTime CapturedUtc { get; set; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// Parses IME (Intune Management Extension) log files using regex patterns from the backend.
    /// Tracks app installation state transitions and emits strategic events.
    /// Split into partial classes: Core, LogProcessing, Handlers.
    ///
    /// Key design: Regex patterns are NOT hardcoded - they come from backend config via ImeLogPattern list.
    /// This allows updating patterns without agent rebuild when Microsoft changes IME log output.
    /// </summary>
    public partial class ImeLogTracker : IDisposable
    {
        private readonly string _logFolder;
        private readonly AgentLogger _logger;
        private readonly LogFilePositionTracker _positionTracker = new LogFilePositionTracker();
        private readonly AppPackageStateList _packageStates;
        private readonly int _pollingIntervalMs;
        private readonly string _matchLogPath; // Optional: path to write every matched raw line
        private readonly object _matchLogLock = new object();

        // Compiled pattern matchers grouped by category
        private List<CompiledPattern> _patternsAlways = new List<CompiledPattern>();
        private List<CompiledPattern> _patternsCurrentPhase = new List<CompiledPattern>();
        private List<CompiledPattern> _patternsOtherPhases = new List<CompiledPattern>();

        // Active matchers (changes based on ESP phase)
        private List<CompiledPattern> _activePatterns = new List<CompiledPattern>();
        private bool _logPhaseIsCurrentPhase = false;

        // Phase isolation: track ALL app IDs seen during pattern matching in the current phase.
        // On phase change, these are added to the ignore list to prevent device-phase apps from
        // bleeding into AccountSetup. This is more comprehensive than only ignoring packageStates
        // because setcurrentapp, esptrackstatus etc. see IDs that never enter packageStates.
        private readonly HashSet<string> _seenAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Forward-only phase progression: prevents accidental phase bounce (e.g. DeviceSetup
        // re-detected during AccountSetup if IME re-evaluates device apps and logs the old phase).
        private static readonly Dictionary<string, int> EspPhaseOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "DeviceSetup", 1 },
            { "AccountSetup", 2 }
        };
        private int _currentPhaseOrder = 0;

        // Snapshots of AppPackageState lists from completed ESP phases (e.g. DeviceSetup apps
        // before AccountSetup starts). Keyed by phase name ("DeviceSetup", "AccountSetup").
        // Captured as live AppPackageState references just before _packageStates.Clear() on
        // phase transition — those references survive the clear (List<T>.Clear only removes
        // them from the source list) and remain readable for the termination summary path.
        private readonly Dictionary<string, List<AppPackageState>> _phasePackageSnapshots =
            new Dictionary<string, List<AppPackageState>>(StringComparer.OrdinalIgnoreCase);

        // Simulation mode
        public bool SimulationMode { get; set; }
        public double SpeedFactor { get; set; } = 50;
        private DateTime _lastLogTimestamp = DateTime.MinValue;

        // Background task
        private Task _pollingTask;
        private CancellationTokenSource _cts;
        private bool _allAppsCompletedFired;

        // State persistence: saves tracker state to disk so agent restart continues
        // from the exact log position without re-parsing or re-building ignore lists.
        private readonly ImeTrackerStatePersistence _statePersistence;
        private bool _stateDirty;

        // Standard GUID capture pattern used as {GUID} placeholder in patterns
        private const string GuidPattern = @"(?<id>[a-z0-9]{8}-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{12})";

        // Log files to monitor
        private static readonly string[] LogFilePatterns = new[]
        {
            "IntuneManagementExtension.log",
            "_IntuneManagementExtension.log",
            "IntuneManagementExtension-????????-??????.log",
            "AppWorkload.log",
            "AppWorkload-????????-??????.log",
            // PowerShell script tracking
            "AgentExecutor.log",
            "AgentExecutor-????????-??????.log",
            "HealthScripts.log",
            "HealthScripts-????????-??????.log"
        };

        // Platform-script multi-line state accumulator (per-policy dict because PS-AGENT-* lines
        // arrive across multiple log entries and IME may interleave platform scripts).
        private readonly Dictionary<string, ScriptExecutionState> _pendingPlatformScripts =
            new Dictionary<string, ScriptExecutionState>(StringComparer.OrdinalIgnoreCase);
        private string _lastPlatformScriptPolicyId;

        // Health-script (remediation) line-by-line accumulator — single slot because IME
        // executes health scripts SEQUENTIALLY within a session (verified across multiple
        // diagnostic captures: ProcessScript → context → exit → stdout → stderr → compliance,
        // one policy at a time). The slot fills as IME emits HS-RUN-CONTEXT / HS-EXITCODE /
        // HS-STDOUT / HS-STDERR; HS-COMPLIANCE merges + emits + clears the slot. The full
        // payload (RemediationStatus, TargetType, ErrorCode, plus the actual remediation
        // phase's data) arrives later via HS-NEW-RESULT and replaces this entry through the
        // UI dedupe (dataCompleteness scoring).
        private ScriptExecutionState _pendingHealthScript;
        private const int MaxScriptOutputLength = 2048;
        private const int MaxMultiLineBufferLines = 100;

        // Counter for HS-NEW-RESULT JSON parse failures — visible to operators via tracker
        // metrics so we can detect IME log-format drift in production.
        internal int HealthScriptResultParseFailures;

        // Set synchronously during HandlePatternMatch so callbacks can read it.
        // Setters are `internal` so V2.Core.Tests can drive the source-timestamp path
        // through the adapter's TriggerXxxFromTest seams without spinning up a real log file.
        public string LastMatchedPatternId { get; internal set; }
        public DateTime? LastMatchedLogTimestamp { get; internal set; }

        // Callbacks to EnrollmentTracker
        public Action<string> OnEspPhaseChanged { get; set; }
        public Action<string> OnImeAgentVersion { get; set; }
        public Action OnImeStarted { get; set; }
        public Action<AppPackageState, AppInstallationState, AppInstallationState> OnAppStateChanged { get; set; }
        public Action<string> OnPoliciesDiscovered { get; set; }
        public Action OnAllAppsCompleted { get; set; }
        public Action OnUserSessionCompleted { get; set; }
        public Action<string> OnImeSessionChange { get; set; }
        public Action<AppPackageState> OnDoTelemetryReceived { get; set; }
        public Action<ScriptExecutionState> OnScriptCompleted { get; set; }

        /// <summary>
        /// Fires when IME logs the start of a health script (HS-SCRIPT-START pattern).
        /// Drives the live "running" indicator in the UI before the consolidated final result
        /// (HS-NEW-RESULT) arrives — the latency between start and result is typically
        /// 30 s – 3 min depending on script duration and IME's batched reporting cycle.
        /// </summary>
        public Action<ScriptStartedInfo> OnScriptStarted { get; set; }

        /// <summary>
        /// Fires on every pattern match with the matched <c>PatternId</c>. Plan §4.x M4.4.4.
        /// Invoked by <c>HandlePatternMatch</c> after <see cref="LastMatchedPatternId"/> is
        /// set, before action-specific callbacks — callers can read <see cref="LastMatchedPatternId"/>
        /// / <see cref="LastMatchedLogTimestamp"/> synchronously inside the handler.
        /// Added to enable the <c>WhiteGloveSealingPatternDetected</c> signal path in
        /// <c>ImeLogTrackerAdapter</c> without the legacy polling-on-LastMatchedPatternId anti-pattern.
        /// </summary>
        public Action<string> OnPatternMatched { get; set; }

        /// <summary>
        /// Access to the tracked package states
        /// </summary>
        public AppPackageStateList PackageStates => _packageStates;

        /// <summary>
        /// Snapshots of <see cref="AppPackageState"/> lists from completed ESP phases (keyed
        /// by phase name, e.g. <c>"DeviceSetup"</c>). Captured immediately before package
        /// states are cleared on phase transition.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<AppPackageState>> PhasePackageSnapshots
        {
            get
            {
                var copy = new Dictionary<string, IReadOnlyList<AppPackageState>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in _phasePackageSnapshots)
                    copy[kv.Key] = kv.Value;
                return copy;
            }
        }

        /// <summary>
        /// Test-only seam: lets unit tests populate <c>_phasePackageSnapshots</c> without
        /// driving a full ESP phase-transition via the regex patterns. Production code never
        /// calls this — phase snapshots are captured from <see cref="HandleEspPhaseChanged"/>
        /// at the real transition boundary.
        /// </summary>
        internal void SeedPhaseSnapshotForTesting(string phaseName, IReadOnlyCollection<AppPackageState> apps)
        {
            if (string.IsNullOrEmpty(phaseName)) throw new ArgumentNullException(nameof(phaseName));
            if (apps == null) throw new ArgumentNullException(nameof(apps));
            _phasePackageSnapshots[phaseName] = new List<AppPackageState>(apps);
        }

        /// <summary>
        /// Returns a deduped union of phase-snapshotted apps plus the live
        /// <see cref="PackageStates"/> (current phase). Live entries win on Id collision.
        /// Used by the termination summary path so that DeviceSetup apps cleared from
        /// <c>_packageStates</c> on the AccountSetup transition still appear in the
        /// final-status JSON and the <c>app_tracking_summary</c> event (V1-parity for
        /// per-phase app counts).
        /// </summary>
        public IReadOnlyList<AppPackageState> GetAllKnownPackageStates()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<AppPackageState>();
            foreach (var pkg in _packageStates)
            {
                if (pkg != null && pkg.Id != null && seen.Add(pkg.Id))
                    result.Add(pkg);
            }
            foreach (var snapshot in _phasePackageSnapshots.Values)
            {
                foreach (var pkg in snapshot)
                {
                    if (pkg != null && pkg.Id != null && seen.Add(pkg.Id))
                        result.Add(pkg);
                }
            }
            return result;
        }

        public ImeLogTracker(string logFolder, List<ImeLogPattern> patterns, AgentLogger logger, int pollingIntervalMs = 100, string matchLogPath = null, string stateDirectory = null)
        {
            _logFolder = Environment.ExpandEnvironmentVariables(logFolder);
            _logger = logger;
            _pollingIntervalMs = pollingIntervalMs;
            _matchLogPath = matchLogPath;
            _packageStates = new AppPackageStateList(logger);

            // State persistence setup
            if (!string.IsNullOrEmpty(stateDirectory))
            {
                _statePersistence = new ImeTrackerStatePersistence(stateDirectory, logger);
            }

            if (!string.IsNullOrEmpty(_matchLogPath))
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(_matchLogPath)); } catch { }
                _logger.Info($"ImeLogTracker: match log enabled -> {_matchLogPath}");
            }

            CompilePatterns(patterns);
            ActivatePatterns(logPhaseIsCurrentPhase: false, force: true);
        }

        /// <summary>
        /// Compiles ImeLogPattern list from backend into Regex objects grouped by category.
        /// </summary>
        public void CompilePatterns(List<ImeLogPattern> patterns)
        {
            _patternsAlways = new List<CompiledPattern>();
            _patternsCurrentPhase = new List<CompiledPattern>();
            _patternsOtherPhases = new List<CompiledPattern>();

            if (patterns == null) return;

            foreach (var pattern in patterns.Where(p => p.Enabled))
            {
                try
                {
                    // Replace {GUID} placeholder with actual GUID capture regex
                    var regexStr = pattern.Pattern.Replace("{GUID}", GuidPattern);
                    // Singleline: make '.' match newlines too — required because multiline CMTrace
                    // entries are reassembled with '\n' chars (e.g. DO TEL JSON in IME >= 1.101)
                    var regex = new Regex(regexStr, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline, TimeSpan.FromSeconds(1));

                    var compiled = new CompiledPattern
                    {
                        PatternId = pattern.PatternId,
                        Regex = regex,
                        Action = pattern.Action,
                        Parameters = pattern.Parameters ?? new Dictionary<string, string>()
                    };

                    switch (pattern.Category?.ToLower())
                    {
                        case "always":
                            _patternsAlways.Add(compiled);
                            break;
                        case "currentphase":
                            _patternsCurrentPhase.Add(compiled);
                            break;
                        case "otherphases":
                            _patternsOtherPhases.Add(compiled);
                            break;
                        default:
                            _patternsAlways.Add(compiled); // Default to always
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to compile IME pattern {pattern.PatternId}: {ex.Message}");
                }
            }

            _logger.Info($"ImeLogTracker: compiled {_patternsAlways.Count} always, {_patternsCurrentPhase.Count} currentPhase, {_patternsOtherPhases.Count} otherPhases patterns");

            // Re-activate with current phase state
            ActivatePatterns(_logPhaseIsCurrentPhase, force: true);
        }

        /// <summary>
        /// Starts the background polling task
        /// </summary>
        public void Start()
        {
            if (_pollingTask != null) return;

            // Restore persisted state from previous agent lifetime (handles agent restart mid-enrollment).
            // This recovers phase order, ignore list, app states, and log file positions so we continue
            // exactly where we left off — no re-parsing, no device-phase bleeding.
            LoadState();

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _pollingTask = Task.Run(async () =>
            {
                _logger.Info("ImeLogTracker: polling started");

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await CheckLogFilesAsync(token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.Warning($"ImeLogTracker: error during log check: {ex.Message}");
                        try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; }
                    }

                    // Persist state after each polling cycle so agent restart continues from here
                    if (_stateDirty)
                    {
                        SaveState();
                        _stateDirty = false;
                    }

                    try { await Task.Delay(_pollingIntervalMs, token); } catch (OperationCanceledException) { break; }
                }

                // Final state save on shutdown
                if (_stateDirty)
                {
                    SaveState();
                    _stateDirty = false;
                }

                _logger.Info("ImeLogTracker: polling stopped");
            }, token);
        }

        /// <summary>
        /// Stops the background polling task
        /// </summary>
        public void Stop()
        {
            _cts?.Cancel();
            try { _pollingTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
            _pollingTask = null;
        }

        #region State Persistence

        /// <summary>
        /// Loads persisted state from disk. Called on Start() to restore tracker state
        /// after an agent restart, so parsing continues exactly where it left off.
        /// </summary>
        private void LoadState()
        {
            if (_statePersistence == null) return;

            var state = _statePersistence.Load();
            if (state == null) return;

            // Restore phase tracking
            _currentPhaseOrder = state.CurrentPhaseOrder;
            _lastEspPhaseDetected = state.LastEspPhaseDetected;
            _allAppsCompletedFired = state.AllAppsCompletedFired;
            _logPhaseIsCurrentPhase = state.LogPhaseIsCurrentPhase;

            // Restore seen app IDs
            _seenAppIds.Clear();
            if (state.SeenAppIds != null)
            {
                foreach (var id in state.SeenAppIds)
                    _seenAppIds.Add(id);
            }

            // Restore ignore list
            if (state.IgnoreList != null)
            {
                foreach (var id in state.IgnoreList)
                    _packageStates.AddToIgnoreList(id);
            }

            // Restore current package ID
            _packageStates.CurrentPackageId = state.CurrentPackageId;

            // Restore package states
            if (state.Packages != null)
            {
                _packageStates.Clear();
                foreach (var p in state.Packages)
                {
                    _packageStates.Add(FromPackageStateData(p));
                }
            }

            // Codex follow-up (882fef64 PR3-PR5 review): restore per-phase snapshots so
            // GetAllKnownPackageStates() still sees DeviceSetup apps after a mid-AccountSetup
            // restart. Old state files that pre-date the field have null here — leave the
            // dict empty in that case (degrades to live-only view, no crash).
            _phasePackageSnapshots.Clear();
            if (state.PhasePackageSnapshots != null)
            {
                foreach (var kv in state.PhasePackageSnapshots)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                    var restored = new List<AppPackageState>(kv.Value.Count);
                    foreach (var p in kv.Value)
                    {
                        try { restored.Add(FromPackageStateData(p)); }
                        catch (Exception ex)
                        {
                            _logger.Warning($"ImeLogTracker: failed to restore phase-snapshot package '{p?.Id ?? "(null)"}' for phase '{kv.Key}': {ex.Message}");
                        }
                    }
                    _phasePackageSnapshots[kv.Key] = restored;
                }
            }

            // Restore file positions
            if (state.FilePositions != null)
            {
                foreach (var kvp in state.FilePositions)
                {
                    var fullPath = Path.Combine(_logFolder, kvp.Key);
                    _positionTracker.RestorePosition(fullPath, kvp.Value.Position, kvp.Value.LastKnownSize);
                }
            }

            // Re-activate patterns based on restored phase state
            ActivatePatterns(_logPhaseIsCurrentPhase, force: true);

            _logger.Info($"ImeLogTracker: state restored - phase: {_lastEspPhaseDetected ?? "(none)"} (order: {_currentPhaseOrder}), " +
                         $"ignore list: {_packageStates.IgnoreList.Count}, packages: {_packageStates.Count}, " +
                         $"file positions: {state.FilePositions?.Count ?? 0}");
        }

        /// <summary>
        /// Persists current tracker state to disk as JSON.
        /// Called after each polling cycle when state has changed.
        /// </summary>
        private void SaveState()
        {
            if (_statePersistence == null) return;

            // Build state DTO
            var state = new ImeTrackerStateData
            {
                CurrentPhaseOrder = _currentPhaseOrder,
                LastEspPhaseDetected = _lastEspPhaseDetected,
                AllAppsCompletedFired = _allAppsCompletedFired,
                LogPhaseIsCurrentPhase = _logPhaseIsCurrentPhase,
                SeenAppIds = _seenAppIds.ToList(),
                IgnoreList = _packageStates.IgnoreList.ToList(),
                CurrentPackageId = _packageStates.CurrentPackageId,
                Packages = _packageStates.Select(ToPackageStateData).ToList(),
                FilePositions = new Dictionary<string, FilePositionData>(),

                // Codex follow-up (882fef64 PR3-PR5 review): persist per-phase snapshots so
                // hybrid-join / multi-reboot enrollments don't lose DeviceSetup apps from
                // FinalStatus + app_tracking_summary across an agent restart. The dict key is
                // the ESP phase name (e.g. "DeviceSetup"); the value is the dehydrated snapshot
                // captured on _packageStates.Clear() during the next-phase transition.
                PhasePackageSnapshots = _phasePackageSnapshots.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Select(ToPackageStateData).ToList(),
                    StringComparer.Ordinal),
            };

            // Store file positions by filename only (log folder is known)
            foreach (var kvp in _positionTracker.GetAllPositions())
            {
                var fileName = Path.GetFileName(kvp.Key);
                state.FilePositions[fileName] = new FilePositionData
                {
                    Position = kvp.Value.Position,
                    LastKnownSize = kvp.Value.LastKnownSize
                };
            }

            _statePersistence.Save(state);
        }

        // Codex follow-up (882fef64 PR3-PR5 review): single source of truth for AppPackageState
        // <-> PackageStateData mapping. Used both for the live _packageStates list and for the
        // _phasePackageSnapshots restore path so the field set never drifts between the two.
        private static PackageStateData ToPackageStateData(AppPackageState p) =>
            new PackageStateData
            {
                Id = p.Id,
                ListPos = p.ListPos,
                Name = p.Name,
                RunAs = (int)p.RunAs,
                Intent = (int)p.Intent,
                Targeted = (int)p.Targeted,
                DependsOn = p.DependsOn?.ToList() ?? new List<string>(),
                InstallationState = (int)p.InstallationState,
                DownloadingOrInstallingSeen = p.DownloadingOrInstallingSeen,
                ProgressPercent = p.ProgressPercent,
                BytesDownloaded = p.BytesDownloaded,
                BytesTotal = p.BytesTotal,
                ErrorPatternId = p.ErrorPatternId,
                ErrorDetail = p.ErrorDetail,
                ErrorCode = p.ErrorCode,
                ExitCode = p.ExitCode,
                HResultFromWin32 = p.HResultFromWin32,
                DoFileSize = p.DoFileSize,
                DoTotalBytesDownloaded = p.DoTotalBytesDownloaded,
                DoBytesFromPeers = p.DoBytesFromPeers,
                DoPercentPeerCaching = p.DoPercentPeerCaching,
                DoBytesFromLanPeers = p.DoBytesFromLanPeers,
                DoBytesFromGroupPeers = p.DoBytesFromGroupPeers,
                DoBytesFromInternetPeers = p.DoBytesFromInternetPeers,
                DoBytesFromLinkLocalPeers = p.DoBytesFromLinkLocalPeers,
                DoBytesFromCacheServer = p.DoBytesFromCacheServer,
                DoCacheHost = p.DoCacheHost,
                DoDownloadMode = p.DoDownloadMode,
                DoDownloadDuration = p.DoDownloadDuration,
                DoBytesFromHttp = p.DoBytesFromHttp,
                HasDoTelemetry = p.HasDoTelemetry,
                AppVersion = p.AppVersion,
                AppType = p.AppType,
                AttemptNumber = p.AttemptNumber,
                DetectionResult = p.DetectionResult,
            };

        private static AppPackageState FromPackageStateData(PackageStateData p) =>
            AppPackageState.Restore(
                p.Id, p.ListPos, p.Name,
                (AppRunAs)p.RunAs, (AppIntent)p.Intent, (AppTargeted)p.Targeted,
                p.DependsOn != null ? new HashSet<string>(p.DependsOn) : new HashSet<string>(),
                (AppInstallationState)p.InstallationState, p.DownloadingOrInstallingSeen,
                p.ProgressPercent, p.BytesDownloaded, p.BytesTotal,
                errorPatternId: p.ErrorPatternId, errorDetail: p.ErrorDetail, errorCode: p.ErrorCode,
                exitCode: p.ExitCode, hresultFromWin32: p.HResultFromWin32,
                doFileSize: p.DoFileSize, doTotalBytesDownloaded: p.DoTotalBytesDownloaded,
                doBytesFromPeers: p.DoBytesFromPeers, doPercentPeerCaching: p.DoPercentPeerCaching,
                doBytesFromLanPeers: p.DoBytesFromLanPeers, doBytesFromGroupPeers: p.DoBytesFromGroupPeers,
                doBytesFromInternetPeers: p.DoBytesFromInternetPeers, doDownloadMode: p.DoDownloadMode,
                doDownloadDuration: p.DoDownloadDuration, doBytesFromHttp: p.DoBytesFromHttp,
                hasDoTelemetry: p.HasDoTelemetry,
                doBytesFromLinkLocalPeers: p.DoBytesFromLinkLocalPeers,
                doBytesFromCacheServer: p.DoBytesFromCacheServer,
                doCacheHost: p.DoCacheHost,
                appVersion: p.AppVersion, appType: p.AppType,
                attemptNumber: p.AttemptNumber, detectionResult: p.DetectionResult);

        /// <summary>
        /// Deletes persisted state file. Called on enrollment complete to ensure
        /// a fresh state on the next enrollment cycle.
        /// </summary>
        public void DeleteState()
        {
            _statePersistence?.Delete();
        }

        // Test-only seams: exercise the persistence round-trip without driving Start() (which
        // also spins up file watchers). Codex follow-up (882fef64 PR3-PR5 review) needs these
        // to verify PhasePackageSnapshots survive a simulated agent restart.
        internal void SaveStateForTest() => SaveState();
        internal void LoadStateForTest() => LoadState();

        #endregion

    }
}

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

        // M1: the state file is a read-position bookmark + package-state cache, NOT a source of
        // truth (the IME logs themselves are written by Windows and always on disk). Saving it on
        // every 100 ms poll while IME streams progress lines hammers the disk during the most
        // contended phase of enrollment. Throttle to at most once per this interval; a save that
        // is throttled away leaves _stateDirty set so the next eligible cycle (or the unconditional
        // final save on shutdown) persists it. Worst case on restart: ~this-many-seconds of already
        // processed log lines are re-read and re-emitted (deduped downstream) — no data loss.
        private static readonly TimeSpan StateSaveMinInterval = TimeSpan.FromSeconds(2);
        private DateTime _lastStateSaveUtc = DateTime.MinValue;

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

        // Cycle start timestamps for health (remediation) scripts, keyed by policyId. Captured
        // from the HS-SCRIPT-START line ([HS] ProcessScript) so the consolidated HS-NEW-RESULT
        // emit can surface the cycle's total run duration (start → result) on its phase events —
        // the remediation-script analog of the platform-script StartedAtUtc timing. Kept in a
        // dedicated map (not on _pendingHealthScript) because the early-signal HS-COMPLIANCE
        // handler clears that slot before HS-NEW-RESULT arrives. Consumed + removed on result emit.
        private readonly Dictionary<string, DateTime> _healthScriptStartTimes =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // Restart-safe dedup for the adapter's one-shot script_timeout_suspected Warning.
        // Owned by the tracker (not the adapter) solely so it rides the persisted state file —
        // an in-memory set re-emits the Warning after every stall restart once the position
        // bookmark is lost and the failing script's log tail is reprocessed.
        private readonly HashSet<string> _scriptTimeoutSuspectedPosted =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Claims the one-shot <c>script_timeout_suspected</c> emission for a policy. Returns
        /// false when a prior claim exists — including one restored from persisted state after
        /// an agent restart, which is the whole point of routing the adapter's dedup through
        /// the tracker.
        /// </summary>
        public bool TryClaimScriptTimeoutSuspected(string policyId)
        {
            if (string.IsNullOrEmpty(policyId)) return false;
            if (!_scriptTimeoutSuspectedPosted.Add(policyId)) return false;
            _stateDirty = true;
            return true;
        }

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
        /// Test-only seam: sets the tracker's current detected ESP phase without driving the
        /// IME-ESP-PHASE regex pattern through a real log file. Production code never calls
        /// this — the phase is set by <c>HandleEspPhaseDetected</c> from live pattern matches
        /// (or restored from persisted state). Used by <c>AreUserEspAppsSettled</c> tests.
        /// </summary>
        internal void SeedCurrentPhaseForTesting(string phaseName)
        {
            _lastEspPhaseDetected = phaseName;
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

        /// <summary>
        /// Session caa6cf50 gate-starvation fix (2026-06-11): returns <c>true</c> when the
        /// tracker's current ESP phase is AccountSetup AND every tracked user-phase app has
        /// reached a terminal state (Installed / Skipped / Postponed) with zero errors.
        /// <para>
        /// Used by <c>EspAndHelloTracker</c> as alternative evidence for the strong
        /// post-AccountSetup completion gate: when a user-ESP app is policy-skipped, Windows
        /// never flips the registry's Apps subcategory to <c>succeeded</c> and never writes
        /// <c>AccountSetupCategory.Status.categorySucceeded</c> before tearing down the ESP
        /// page — the registry-driven <c>AccountSetupProvisioningComplete</c> (and its
        /// all-subcategories fallback) can then never fire. IME's own app tracking is the
        /// independent evidence that AccountSetup app enforcement actually finished.
        /// </para>
        /// <para>
        /// Conservative by construction: requires at least one required (Install/Uninstall
        /// intent) app in the live current-phase list — an empty list (phase just cleared, or
        /// apps not yet surfaced) or intent-unknown-only tracking returns <c>false</c>. Any
        /// app in <see cref="AppInstallationState.Error"/> returns <c>false</c>. May be called
        /// from the Shell-Core event-log watcher thread while the polling thread mutates the
        /// list — any race-induced exception is swallowed and reported as "not settled".
        /// </para>
        /// </summary>
        public bool AreUserEspAppsSettled()
        {
            try
            {
                if (!string.Equals(_lastEspPhaseDetected, "AccountSetup", StringComparison.OrdinalIgnoreCase))
                    return false;

                var apps = new List<AppPackageState>(_packageStates);
                if (apps.Count == 0) return false;

                var sawRequired = false;
                foreach (var pkg in apps)
                {
                    if (pkg == null) continue;
                    if (pkg.IsError) return false;
                    if (!pkg.IsRequired) continue;
                    sawRequired = true;
                    if (!pkg.IsCompleted) return false;
                }
                return sawRequired;
            }
            catch (Exception ex)
            {
                _logger.Debug($"ImeLogTracker: AreUserEspAppsSettled probe threw: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Liveness plan PR3 — returns the required user-ESP apps that are STARVING the
        /// AccountSetup apps gate: tracked in the current AccountSetup phase, Install intent
        /// only, no download/install activity ever observed, not terminal and not failed.
        /// These are the apps whose enforcement IME never started (e.g. a user-targeted app
        /// stuck on "pending") — the actual blocker behind the gate-starvation dead-ends.
        /// <para>
        /// Session a4537c36 fix (2026-07-10): Uninstall-intent apps are excluded — the ESP
        /// apps gate does not block on uninstalls, and "never started installing" is not a
        /// meaningful claim for them. <see cref="AreUserEspAppsSettled"/> deliberately keeps
        /// counting Uninstall-intent apps (unproven whether the ESP ever waits on them —
        /// completion behaviour stays conservative until field data says otherwise).
        /// </para>
        /// <para>
        /// Deliberately excludes apps that are alive (<see cref="AppPackageState.DownloadingOrInstallingSeen"/>
        /// covers Downloading/Installing and anything that ever progressed) and error-state apps
        /// (the failure path already names those). Same thread-safety contract as
        /// <see cref="AreUserEspAppsSettled"/>: snapshot copy, any race-induced exception
        /// returns an empty list.
        /// </para>
        /// </summary>
        public IReadOnlyList<AppPackageState> GetStarvedUserEspApps()
        {
            try
            {
                if (!string.Equals(_lastEspPhaseDetected, "AccountSetup", StringComparison.OrdinalIgnoreCase))
                    return Array.Empty<AppPackageState>();

                var apps = new List<AppPackageState>(_packageStates);
                if (apps.Count == 0) return Array.Empty<AppPackageState>();

                var starved = new List<AppPackageState>();
                foreach (var pkg in apps)
                {
                    if (pkg == null || pkg.Intent != AppIntent.Install) continue;
                    if (pkg.IsCompleted || pkg.IsError) continue;
                    if (pkg.DownloadingOrInstallingSeen) continue;
                    starved.Add(pkg);
                }
                return starved;
            }
            catch (Exception ex)
            {
                _logger.Debug($"ImeLogTracker: GetStarvedUserEspApps probe threw: {ex.Message}");
                return Array.Empty<AppPackageState>();
            }
        }

        /// <summary>
        /// Promotes every app currently in <see cref="AppInstallationState.Installing"/> to
        /// <see cref="AppInstallationState.Error"/>, stamping <paramref name="failureType"/>
        /// onto <see cref="AppPackageState.ErrorPatternId"/> and <paramref name="message"/>
        /// onto <see cref="AppPackageState.ErrorDetail"/>, and fires
        /// <see cref="OnAppStateChanged"/> so the adapter emits a regular
        /// <c>app_install_failed</c> event for each promoted app.
        /// <para>
        /// Used by the V2 EnrollmentTerminationHandler on the terminal-ESP-Apps-failure path
        /// (when the ESP gave up while these apps were still installing). Only
        /// <see cref="AppInstallationState.Installing"/> is targeted — <c>Downloading</c>,
        /// <c>InProgress</c>, <c>Postponed</c> and pending states are left alone because the
        /// agent cannot make a confident "likely stuck" claim about them.
        /// </para>
        /// <para>
        /// Returns the list of <see cref="AppPackageState.Id"/> values that were promoted —
        /// caller uses this for logging only; the actual events are emitted via the standard
        /// <see cref="OnAppStateChanged"/> path so the adapter sees them as ordinary error-
        /// state transitions and the DecisionEngine receives <c>AppInstallFailed</c> signals
        /// (idempotent at the engine — promoted apps are post-terminal).
        /// </para>
        /// </summary>
        public IReadOnlyList<string> PromoteActiveInstallsToStuck(string failureType, string message, string errorCode = null)
        {
            if (string.IsNullOrEmpty(failureType))
                throw new ArgumentException("failureType is mandatory.", nameof(failureType));

            // Snapshot the candidates first — UpdateState mutates the underlying list ordering
            // (SortErrorsToTop), and we want a stable iteration target.
            var candidates = _packageStates
                .Where(p => p != null && p.InstallationState == AppInstallationState.Installing)
                .ToList();

            var promoted = new List<string>(candidates.Count);
            foreach (var pkg in candidates)
            {
                var oldState = pkg.InstallationState;
                pkg.SetErrorContext(failureType, message ?? string.Empty, errorCode);
                if (pkg.UpdateState(AppInstallationState.Error))
                {
                    promoted.Add(pkg.Id);
                    var ecSuffix = string.IsNullOrEmpty(errorCode) ? string.Empty : $", errorCode={errorCode}";
                    _logger?.Info($"ImeLogTracker: promoted '{pkg.Name ?? pkg.Id}' Installing -> Error ({failureType}{ecSuffix}).");
                    OnAppStateChanged?.Invoke(pkg, oldState, AppInstallationState.Error);
                }
            }

            return promoted;
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

                        // Safety net: emit completions for platform scripts whose AgentExecutor
                        // exit code we already have but whose authoritative IME PS-SCRIPT-RESULT
                        // line never arrived within the grace period. Runs on this same loop
                        // thread (no locking) so it observes the buffer right after parsing.
                        FlushStalePlatformScriptResults(DateTime.UtcNow);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.Warning($"ImeLogTracker: error during log check: {ex.Message}");
                        try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; }
                    }

                    // Persist state after each polling cycle so agent restart continues from here.
                    // M1: throttled to at most once per StateSaveMinInterval — a throttled-away save
                    // keeps _stateDirty set for the next eligible cycle; the final save on shutdown
                    // (below) is unconditional so nothing pending is lost on a clean exit.
                    if (_stateDirty && (DateTime.UtcNow - _lastStateSaveUtc) >= StateSaveMinInterval)
                    {
                        SaveState();
                        _stateDirty = false;
                        _lastStateSaveUtc = DateTime.UtcNow;
                    }

                    try { await Task.Delay(_pollingIntervalMs, token); } catch (OperationCanceledException) { break; }
                }

                // Final pass: force-flush any platform script that completed within the last grace
                // window before shutdown (exit code known, IME result still pending). Best-effort —
                // emits only while callbacks are still wired (i.e. the adapter hasn't been disposed).
                try { FlushStalePlatformScriptResults(DateTime.UtcNow, force: true); }
                catch (Exception ex) { _logger.Warning($"ImeLogTracker: shutdown script flush failed: {ex.Message}"); }

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

            // H1 (delta review 2026-07-02): restore platform-script emission markers + pending
            // buffer. Without these, the shutdown force-flush emits a fallback completion, the
            // restarted tracker resumes past the persisted position, and IME's later-written
            // PS-SCRIPT-RESULT line for the same execution emits a SECOND script_completed
            // (the in-memory marker set started empty). Old state files have null here —
            // degrades to the pre-fix behavior, no crash.
            _platformScriptResultEmitted.Clear();
            if (state.PlatformScriptResultEmitted != null)
            {
                foreach (var id in state.PlatformScriptResultEmitted)
                    _platformScriptResultEmitted.Add(id);
            }

            _pendingPlatformScripts.Clear();
            if (state.PendingPlatformScripts != null)
            {
                foreach (var pending in state.PendingPlatformScripts)
                {
                    if (!string.IsNullOrEmpty(pending?.PolicyId))
                        _pendingPlatformScripts[pending.PolicyId] = pending;
                }
            }

            _scriptTimeoutSuspectedPosted.Clear();
            if (state.ScriptTimeoutSuspectedPosted != null)
            {
                foreach (var id in state.ScriptTimeoutSuspectedPosted)
                    _scriptTimeoutSuspectedPosted.Add(id);
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

                // H1 (delta review 2026-07-02): platform-script dedup markers + pending buffer
                // must survive restarts — see the LoadState comment for the duplicate scenario.
                PlatformScriptResultEmitted = _platformScriptResultEmitted.ToList(),
                PendingPlatformScripts = _pendingPlatformScripts.Values.ToList(),
                ScriptTimeoutSuspectedPosted = _scriptTimeoutSuspectedPosted.ToList(),
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

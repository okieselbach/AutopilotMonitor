using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Response from the agent configuration endpoint
    /// Contains collector toggles and active gather rules for the tenant
    /// </summary>
    public class AgentConfigResponse
    {
        /// <summary>
        /// Semantic config version from backend.
        /// Used for debugging and future schema evolution.
        /// </summary>
        public int ConfigVersion { get; set; }

        /// <summary>
        /// Event upload debounce interval in seconds.
        /// </summary>
        public int UploadIntervalSeconds { get; set; } = 30;

        /// <summary>
        /// Whether to self-destruct after enrollment completion (remove Scheduled Task and all files).
        /// </summary>
        public bool SelfDestructOnComplete { get; set; } = true;

        /// <summary>
        /// Preserve logs during self-destruct.
        /// </summary>
        public bool KeepLogFile { get; set; } = false;

        /// <summary>
        /// Whether to enable geo-location detection.
        /// </summary>
        public bool EnableGeoLocation { get; set; } = true;

        /// <summary>
        /// Whether to write a log of every IME log line matched by a pattern.
        /// When true, the default path is used: Constants.ImeMatchLogPath.
        /// </summary>
        public bool EnableImeMatchLog { get; set; } = false;

        public CollectorConfiguration Collectors { get; set; } = default!;

        /// <summary>
        /// User-defined ad-hoc gather rules (minimal set, not for IME log parsing)
        /// </summary>
        public List<GatherRule> GatherRules { get; set; } = new List<GatherRule>();

        /// <summary>
        /// IME log regex patterns for smart enrollment tracking.
        /// Delivered from backend so patterns can be updated without agent rebuild.
        /// </summary>
        public List<ImeLogPattern> ImeLogPatterns { get; set; } = new List<ImeLogPattern>();

        /// <summary>
        /// Pattern-IDs (aus <see cref="ImeLogPatterns"/>), deren Match vom V2-Agent als
        /// <c>WhiteGloveSealingPatternDetected</c>-DecisionSignal re-emittiert wird
        /// (zusätzlich zum normalen <c>ime_pattern_match</c>-Event). Plan §4.x M4.4.4 / M4.4.5.e.
        /// <para>
        /// Default <c>null</c>/leer = Feature off (M3-kompatibel, kein Regressions-Risiko).
        /// Nur Pattern-IDs in dieser Liste werden als Sealing-Signal gewertet — alle anderen
        /// IME-Pattern-Matches laufen den normalen Event-Pfad.
        /// </para>
        /// </summary>
        public List<string> WhiteGloveSealingPatternIds { get; set; } = new List<string>();

        /// <summary>
        /// Maximum consecutive authentication failures (401/403) before the agent shuts down.
        /// Prevents endless retry traffic when the device is not authorized.
        /// 0 = disabled (retry forever). Default: 5.
        /// </summary>
        public int MaxAuthFailures { get; set; } = 5;

        /// <summary>
        /// Maximum time in minutes the agent keeps retrying after the first auth failure.
        /// 0 = disabled (no time limit, only MaxAuthFailures applies). Default: 0.
        /// </summary>
        public int AuthFailureTimeoutMinutes { get; set; } = 0;

        /// <summary>
        /// Log verbosity level for the agent.
        /// "Info" = normal messages, "Debug" = component state/decisions, "Verbose" = per-event tracing, "Trace" = full diagnostic output.
        /// Default: "Info"
        /// </summary>
        public string LogLevel { get; set; } = "Info";

        /// <summary>
        /// Whether to reboot the device after enrollment completes (and cleanup/self-destruct).
        /// Default: false
        /// </summary>
        public bool RebootOnComplete { get; set; } = false;

        /// <summary>
        /// Delay in seconds before the reboot is initiated (shutdown.exe /r /t X).
        /// Gives the user a short window to see what is happening.
        /// Default: 10 seconds
        /// </summary>
        public int RebootDelaySeconds { get; set; } = 10;

        /// <summary>
        /// Whether to show a visual enrollment summary dialog to the end user
        /// after enrollment completes (success or failure).
        /// Default: false (opt-in)
        /// </summary>
        public bool ShowEnrollmentSummary { get; set; } = false;

        /// <summary>
        /// Auto-close timeout in seconds for the enrollment summary dialog.
        /// 0 = no auto-close. Default: 60
        /// </summary>
        public int EnrollmentSummaryTimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// Optional URL to a branding image displayed as a banner at the top of the enrollment summary dialog.
        /// Expected size: 540 x 80 px. Larger images will be center-cropped.
        /// null = no banner.
        /// </summary>
        public string EnrollmentSummaryBrandingImageUrl { get; set; } = default!;

        /// <summary>
        /// Maximum time in seconds the agent retries launching the enrollment summary dialog
        /// when the user's desktop is locked by a credential UI (e.g. Windows Hello).
        /// 0 = no retry (single attempt). Default: 120 (2 minutes).
        /// </summary>
        public int EnrollmentSummaryLaunchRetrySeconds { get; set; } = 120;

        /// <summary>
        /// Maximum number of events per upload batch.
        /// Default: 100
        /// </summary>
        public int MaxBatchSize { get; set; } = 100;

        /// <summary>
        /// Whether diagnostics upload is configured for this tenant.
        /// When true, the agent should request a short-lived upload URL via the API just before uploading.
        /// The SAS URL itself is never included in this config response — it is fetched on-demand.
        /// </summary>
        public bool DiagnosticsUploadEnabled { get; set; } = false;

        /// <summary>
        /// When to upload diagnostics packages: "Off", "Always", "OnFailure".
        /// Default: "Off"
        /// </summary>
        public string DiagnosticsUploadMode { get; set; } = "Off";

        /// <summary>
        /// Merged list of log paths/wildcards to include in the diagnostics ZIP package.
        /// Global entries (IsBuiltIn=true) come first, followed by tenant-specific additions.
        /// The agent validates each path against DiagnosticsPathGuards before collection.
        /// </summary>
        public List<DiagnosticsLogPath> DiagnosticsLogPaths { get; set; } = new List<DiagnosticsLogPath>();

        /// <summary>
        /// Configuration for agent-side security and configuration analyzers.
        /// Controls which analyzers run and their per-analyzer parameters.
        /// </summary>
        public AnalyzerConfiguration Analyzers { get; set; } = new AnalyzerConfiguration();

        /// <summary>
        /// Whether the agent should send Trace-severity events to the backend.
        /// Trace events capture key agent decisions (e.g. "AccountSetup suppressed — no real user profile")
        /// for backend troubleshooting without relying on the agent log file.
        /// Default: true (on in preview).
        /// </summary>
        public bool SendTraceEvents { get; set; } = true;

        /// <summary>
        /// When true, agent guardrails are relaxed: all registry, WMI, and command targets are allowed.
        /// File/diagnostics paths allow everything except C:\Users.
        /// Default: false.
        /// </summary>
        public bool UnrestrictedMode { get; set; } = false;

        /// <summary>
        /// SHA-256 hash (lowercase hex) of the latest published agent ZIP, provided by the backend.
        /// Used for integrity verification during self-update as a second trust channel
        /// (separate from the hash in version.json on blob storage).
        /// null = backend does not have a hash (backward compat with older backend deployments).
        /// </summary>
        public string LatestAgentSha256 { get; set; } = default!;

        /// <summary>
        /// SHA-256 hash (lowercase hex) of the latest published agent EXE, provided by the backend.
        /// Used for post-config binary integrity verification: the agent compares this against
        /// the hash of its own running executable.
        /// null = backend does not have an EXE hash (backward compat with older backend deployments).
        /// </summary>
        public string LatestAgentExeSha256 { get; set; } = default!;

        /// <summary>
        /// When true, the self-updater will install a lower version if the backend advertises one.
        /// Default: false (forward-only). Gated per tenant via the admin configuration.
        /// Only honoured by the runtime force-update path (hash mismatch) — startup version
        /// comparison never downgrades regardless of this flag.
        /// </summary>
        public bool AllowAgentDowngrade { get; set; } = false;

        /// <summary>
        /// NTP server address for time check during enrollment.
        /// Default: "time.windows.com"
        /// </summary>
        public string NtpServer { get; set; } = "time.windows.com";

        /// <summary>
        /// Whether to automatically set the device timezone based on IP geolocation.
        /// Requires EnableGeoLocation to be true. Uses tzutil /s to apply.
        /// Default: false
        /// </summary>
        public bool EnableTimezoneAutoSet { get; set; } = false;
    }

    /// <summary>
    /// Configuration for optional agent collectors
    /// </summary>
    public class CollectorConfiguration
    {
        /// <summary>
        /// Enable CPU, memory, disk, network performance monitoring (always on for UI chart)
        /// Generates traffic: ~1 event per interval
        /// </summary>
        public bool EnablePerformanceCollector { get; set; } = true;

        /// <summary>
        /// Interval in seconds for performance snapshots
        /// Default: 30 seconds
        /// </summary>
        public int PerformanceIntervalSeconds { get; set; } = 30;

        /// <summary>
        /// Idle timeout in minutes for periodic collectors (Performance, AgentSelfMetrics).
        /// Collectors stop after this many minutes without real enrollment activity and
        /// restart automatically when new activity is detected.
        /// 0 = disabled (collectors run indefinitely). Default: 15 minutes.
        /// </summary>
        public int CollectorIdleTimeoutMinutes { get; set; } = 15;

        /// <summary>
        /// DAD liveness threshold (minutes). After this many minutes of polling without
        /// detecting either an excluded-user explorer.exe or a real user desktop, the V2
        /// DesktopArrivalDetector emits a single <c>desktop_detector_no_candidate</c> event
        /// (state-change-only, NOT periodic). Distinguishes "user never logged in" from
        /// "detector wiring dead post-reboot" in sessions missing <c>desktop_arrived</c>.
        /// 0 = disabled. Default: 10 minutes.
        /// </summary>
        public int DesktopDetectorNoCandidateTimeoutMinutes { get; set; } = 10;

        /// <summary>
        /// Enable the agent self-metrics collector (process CPU, memory, network traffic).
        /// Default: true
        /// </summary>
        public bool EnableAgentSelfMetrics { get; set; } = true;

        /// <summary>
        /// Interval in seconds for agent self-metrics snapshots.
        /// Default: 60 seconds
        /// </summary>
        public int AgentSelfMetricsIntervalSeconds { get; set; } = 60;

        /// <summary>
        /// Seconds to wait for the Windows Hello wizard after ESP exit.
        /// Default: 30 seconds
        /// </summary>
        public int HelloWaitTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Maximum agent lifetime in minutes. Safety net to prevent zombie agents.
        /// 0 = disabled (no lifetime limit). Default: 360 (6 hours).
        /// </summary>
        public int AgentMaxLifetimeMinutes { get; set; } = 360;

        /// <summary>
        /// Enable OS-level Delivery Optimization status polling via Get-DeliveryOptimizationStatus.
        /// Captures DO peer caching telemetry for app downloads, especially when newer IME versions
        /// no longer emit [DO TEL] log entries.
        /// Default: true
        /// </summary>
        public bool EnableDeliveryOptimizationCollector { get; set; } = true;

        /// <summary>
        /// Interval in seconds for polling Get-DeliveryOptimizationStatus.
        /// Only polls when active downloads are detected (dormant otherwise).
        /// Default: 3 seconds
        /// </summary>
        public int DeliveryOptimizationIntervalSeconds { get; set; } = 3;

        /// <summary>
        /// Enable the Microsoft 365 Apps (Office Click-to-Run) install detector. Event-driven (no idle
        /// polling): woken by a WMI Win32_ProcessStartTrace push on OfficeC2RClient.exe, progress via
        /// RegNotifyChangeKeyValue, stop via Process.Exited. Surfaces the real background install
        /// lifecycle (+ Office's Delivery-Optimization download telemetry) even when the Intune
        /// "integrated" Office app reports done to IME within 1-2 min. High-value package → on by
        /// default, kept as a kill-switch. DO sampling reuses DeliveryOptimizationIntervalSeconds.
        /// Default: true
        /// </summary>
        public bool EnableOfficeInstallDetector { get; set; } = true;

        /// <summary>
        /// Settle window (seconds) for the Office install detector. C2R spawns several short-lived
        /// OfficeC2RClient.exe workers (with small gaps) during one install; after the last worker
        /// exits the detector waits this long before deciding terminal completed/failed, so a brief
        /// gap before the next worker does not produce a premature office_install_failed. A new worker
        /// within the window keeps the install window open. 0 = decide immediately. Default: 10.
        /// </summary>
        public int OfficeInstallSettleSeconds { get; set; } = 10;

        // -----------------------------------------------------------------------
        // Stall detection (Ebene 2 — StallProbeCollector)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Master switch for the stall probe mechanism. When enabled, probes run at
        /// idle-time thresholds (see StallProbeThresholdsMinutes) to detect stuck
        /// enrollments via registry, EventLog and AppWorkload log scans.
        /// Default: true
        /// </summary>
        public bool StallProbeEnabled { get; set; } = true;

        /// <summary>
        /// Idle-time thresholds in minutes at which probes run. Each probe is fire-once
        /// per idle window; the counter resets on any real (non-periodic) event.
        /// Default: [2, 15, 30, 60, 180]
        /// Probe 1 (2 min): early quick-response, silent unless anomaly found.
        /// Probe 2 (15 min): main gate with guaranteed trace heartbeat.
        /// Probe 3 (30 min): silent unless found.
        /// Probe 4 (60 min): fires session_stalled event → backend sets status to Stalled.
        /// Probe 5 (180 min): last-chance scan before 5h backend timeout, silent unless found.
        /// </summary>
        public int[] StallProbeThresholdsMinutes { get; set; } = new[] { 2, 15, 30, 60, 180 };

        /// <summary>
        /// Probe indices (1-based) that emit a stall_probe_check trace event even when
        /// no anomaly is found. Default: [2] — only Probe 2 (15 min) sends a heartbeat.
        /// Set to [1, 2] for more observability at the cost of extra trace events.
        /// </summary>
        public int[] StallProbeTraceIndices { get; set; } = new[] { 2 };

        /// <summary>
        /// Which sources to scan per probe. Any source can be removed from the list
        /// to disable it individually without affecting the others.
        /// Default: all four sources enabled.
        /// </summary>
        public string[] StallProbeSources { get; set; } = new[]
        {
            "provisioning_registry",
            "diagnostics_registry",
            "eventlog",
            "appworkload_log"
        };

        /// <summary>
        /// After which probe index (1-based) the fire-once session_stalled event is emitted.
        /// Default: 4 → after Probe 4 (60 min idle).
        /// </summary>
        public int SessionStalledAfterProbeIndex { get; set; } = 4;

        // -----------------------------------------------------------------------
        // ModernDeployment EventLog Watcher (Ebene 1 — live capture)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Master switch for the ModernDeployment EventLog watcher. When enabled, the agent
        /// subscribes to two Windows event channels and forwards matching events as
        /// modern_deployment_log events to the backend.
        /// Default: true
        /// </summary>
        public bool ModernDeploymentWatcherEnabled { get; set; } = true;

        /// <summary>
        /// Maximum Windows event level to capture from ModernDeployment channels.
        /// Windows levels: 1=Critical, 2=Error, 3=Warning, 4=Information, 5=Verbose.
        /// Default: 3 → captures Critical, Error and Warning.
        /// </summary>
        public int ModernDeploymentLogLevelMax { get; set; } = 3;

        /// <summary>
        /// Enable backfill for targeted ModernDeployment events (e.g. Event 509 / WhiteGlove start)
        /// that may have been written before the agent started during OOBE.
        /// Uses EventLogReader to scan the event log for recent occurrences on startup.
        /// Default: true
        /// </summary>
        public bool ModernDeploymentBackfillEnabled { get; set; } = true;

        /// <summary>
        /// List of Windows ModernDeployment EventIDs that are considered harmless.
        /// Matching Level 2 (Error) and Level 3 (Warning) events are downgraded to
        /// Debug severity and skipped by the stall-probe anomaly scan. Level 1
        /// (Critical) is never downgraded.
        /// Default: [100, 1005, 1010] — known noise EventIDs with no enrollment impact
        /// (1010 = Autopilot.dll WIL hardwareinfo.cpp HRESULT 0x80070002 / file-not-found probe).
        /// Configurable per Admin in the global settings.
        /// </summary>
        public int[] ModernDeploymentHarmlessEventIds { get; set; } = new[] { 100, 1005, 1010 };

        /// <summary>
        /// Lookback window in minutes for the ModernDeployment backfill scan.
        /// Only events within this window are considered. Set generously because the
        /// gap between WhiteGlove initiation and MDM enroll (when the agent starts)
        /// can be 5–15 minutes depending on TPM attestation and disk encryption setup.
        /// Default: 30 minutes.
        /// </summary>
        public int ModernDeploymentBackfillLookbackMinutes { get; set; } = 30;

        /// <summary>
        /// Creates default collector configuration
        /// </summary>
        public static CollectorConfiguration CreateDefault()
        {
            return new CollectorConfiguration();
        }
    }

    /// <summary>
    /// Configuration for agent-side analyzers (security and configuration checks).
    /// Analyzers differ from collectors: they run checks, produce a confidence-scored finding,
    /// and emit a single structured event — rather than streaming raw telemetry data.
    /// </summary>
    public class AnalyzerConfiguration
    {
        /// <summary>
        /// Whether to run the LocalAdminAnalyzer at startup and shutdown.
        /// Detects pre-enrollment local admin account creation (Autopilot bypass technique).
        /// Default: true
        /// </summary>
        public bool EnableLocalAdminAnalyzer { get; set; } = true;

        /// <summary>
        /// Additional local account names considered expected on a newly enrolled device.
        /// These are merged (union) with the built-in defaults:
        /// Administrator, Guest, DefaultAccount, WDAGUtilityAccount, Public, Default.
        /// Any local account not in the merged list will be flagged.
        /// Default: empty list (built-in defaults only)
        /// </summary>
        public List<string> LocalAdminAllowedAccounts { get; set; } = new List<string>();

        /// <summary>
        /// Whether to run the SoftwareInventoryAnalyzer at startup and shutdown.
        /// Collects installed software registry inventory, normalizes entries, and
        /// emits a baseline snapshot (startup) plus delta (shutdown) for vulnerability insight.
        /// Default: true
        /// </summary>
        public bool EnableSoftwareInventoryAnalyzer { get; set; } = false;

        /// <summary>
        /// Whether to run the IntegrityBypassAnalyzer at startup and shutdown.
        /// Detects Windows 11 TPM/SecureBoot/CPU/RAM/Disk install-time bypass keys,
        /// the MoSetup upgrade bypass flag, the per-user PCHC UpgradeEligibility flag,
        /// and suspicious post-OOBE SetupComplete/ErrorHandler scripts.
        /// Default: true
        /// </summary>
        public bool EnableIntegrityBypassAnalyzer { get; set; } = true;

        /// <summary>
        /// Whether to enable the RealmJoin watcher, which tracks RealmJoin deployment
        /// state and enrollment packages during provisioning. This is not an IAgentAnalyzer;
        /// the flag gates creation of the RealmJoin collector host in the agent runtime.
        /// Default: false (opt-in; most tenants do not deploy via RealmJoin).
        /// </summary>
        public bool EnableRealmJoinWatcher { get; set; } = false;

        /// <summary>
        /// Whether to keep the device awake (system + display) during the User-ESP (AccountSetup)
        /// phase so idle standby/sleep cannot stall app installs or account provisioning. This is
        /// not an IAgentAnalyzer; the flag gates creation of the UserEspKeepAwake host in the agent
        /// runtime. Reboots are unaffected (the hold only resets idle timers).
        /// Default: false (opt-in per tenant).
        /// </summary>
        public bool KeepAwakeDuringUserEsp { get; set; } = false;

        /// <summary>
        /// Whether to detect an interactive console opened during enrollment — the classic Shift+F10 OOBE
        /// bypass. Gates BOTH the live ConsoleBypass host (an interactive-session cmd.exe with a bare,
        /// non-scripted command line; stopped on real-user desktop arrival) and the startup
        /// ConsolePrefetchScanner (CMD.EXE-*.pf ran after boot). Detection is best-effort, not gapless —
        /// a press before the agent installs is only coarsely covered by the prefetch artifact.
        /// Security-sensitive; not an IAgentAnalyzer toggle in the usual sense (it also gates a collector host).
        /// Default: true (opt-out per tenant — a Shift+F10 console during enrollment is a finding worth
        /// surfacing by default; tenants that knowingly use Shift+F10 for support can disable it).
        /// </summary>
        public bool EnableConsoleBypassDetection { get; set; } = true;
    }
}

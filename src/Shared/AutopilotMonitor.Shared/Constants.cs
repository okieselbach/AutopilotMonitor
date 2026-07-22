namespace AutopilotMonitor.Shared
{
    /// <summary>
    /// Shared constants across all projects
    /// </summary>
    public static class Constants
    {
        // -----------------------------------------------------------------------
        // Agent runtime defaults
        // -----------------------------------------------------------------------

        /// <summary>
        /// Base data directory for all agent files (spool, logs, state)
        /// </summary>
        public const string AgentDataDirectory = @"%ProgramData%\AutopilotMonitor";

        /// <summary>
        /// Local spool directory for offline queueing
        /// </summary>
        public const string SpoolDirectory = @"%ProgramData%\AutopilotMonitor\Spool";

        /// <summary>
        /// Agent log directory
        /// </summary>
        public const string LogDirectory = @"%ProgramData%\AutopilotMonitor\Logs";

        /// <summary>
        /// Agent state directory (enrollment complete marker, IME tracker state, etc.)
        /// </summary>
        public const string StateDirectory = @"%ProgramData%\AutopilotMonitor\State";

        /// <summary>
        /// Marker file written by SelfUpdater after a successful update + restart; read by MonitoringService
        /// on next startup to emit an agent_version_check event with outcome=updated.
        /// </summary>
        public const string SelfUpdateMarkerFile = @"%ProgramData%\AutopilotMonitor\State\self-update-info.json";

        /// <summary>
        /// Marker file written by SelfUpdater when the startup update path is skipped (network timeout,
        /// download failure, integrity mismatch, etc.); read by MonitoringService on next startup to emit
        /// an agent_version_check event with outcome=skipped or check_failed. Only written on the startup
        /// trigger path — runtime-triggered failures are logged normally because the full logger is already up.
        /// </summary>
        public const string SelfUpdateSkippedMarkerFile = @"%ProgramData%\AutopilotMonitor\State\self-update-skipped.json";

        /// <summary>
        /// Marker file written by SelfUpdater on the happy path (current version already up to date);
        /// read by MonitoringService on next startup to emit an agent_version_check event with
        /// outcome=up_to_date. Subject to session-scoped dedup via LastVersionCheckEmitFile.
        /// </summary>
        public const string SelfUpdateCheckedMarkerFile = @"%ProgramData%\AutopilotMonitor\State\self-update-checked.json";

        /// <summary>
        /// Persists the last emitted agent_version_check event across agent restarts. Used by
        /// MonitoringService to dedup up_to_date events within the same session when the latestVersion
        /// has not changed. Updated every time an event is emitted.
        /// </summary>
        public const string LastVersionCheckEmitFile = @"%ProgramData%\AutopilotMonitor\State\last-version-check.json";

        /// <summary>
        /// Default path for the IME pattern match log file (debugging/diagnostics)
        /// </summary>
        public const string ImeMatchLogPath = @"%ProgramData%\AutopilotMonitor\Logs\ime_pattern_matches.log";

        /// <summary>
        /// Scheduled Task name used to run the agent as SYSTEM
        /// </summary>
        public const string ScheduledTaskName = "AutopilotMonitor-Agent";

        /// <summary>
        /// Default backend API base URL (overridable via AUTOPILOT_MONITOR_API env var or --api-url CLI arg)
        /// </summary>
        public const string ApiBaseUrl = "https://autopilotmonitor-api-eu.azurewebsites.net";

        /// <summary>
        /// Public base URL of the MCP server (no trailing slash, no /mcp suffix). Stable custom
        /// domain in front of the Container App — survives region/environment moves, unlike the
        /// generated *.azurecontainerapps.io FQDN. Used by the backend health check to probe
        /// <c>{McpServerBaseUrl}/health</c>. The Container App runs with minReplicas=0, so the
        /// first probe after idle may incur a cold start. Overridable via the
        /// <c>McpServerUrl</c> app setting (e.g. for dev).
        /// </summary>
        public const string McpServerBaseUrl = "https://mcp.autopilotmonitor.com";

        // -----------------------------------------------------------------------
        // Agent self-update
        // -----------------------------------------------------------------------

        /// <summary>
        /// Canonical public base URL for agent binaries: Front Door alias in front of the
        /// current blob origin (autopilotmonitoreu, container "agent"). Storage-account moves
        /// happen behind this name without touching customers. New bootstrap scripts and all
        /// availability probes use this URL.
        /// </summary>
        public const string AgentDownloadBaseUrl = "https://download.autopilotmonitor.com/agent";

        /// <summary>
        /// LEGACY base URL for agent binaries: the pre-migration storage account, kept alive
        /// (dual-push on every release) because bootstrap scripts already deployed in customer
        /// Intune tenants still download from it. Remove only after the customer migration to
        /// <see cref="AgentDownloadBaseUrl"/> is complete and the legacy account is torn down.
        /// </summary>
        public const string AgentBlobBaseUrl = "https://autopilotmonitor.blob.core.windows.net/agent";

        // =====================================================================
        // Stable namespace — what customer Intune Platform Scripts download.
        // Blob names are FOREVER stable; content rotates per cutover (V1→V2→V3).
        // Bootstrap-Script reads exclusively from these names. Build-script of
        // the current "stable line" pushes here when invoked with -PublishAsStable.
        // See .claude/plans/v2-cutover.md.
        // =====================================================================

        /// <summary>
        /// Stable version manifest filename (JSON: { "version": "x.y.z", "sha256": "..." }).
        /// Bootstrap-Script + LatestVersionsService + HealthCheck read this.
        /// </summary>
        public const string AgentVersionFileName = "version.json";

        /// <summary>
        /// Stable agent ZIP filename. Bootstrap-Script + HealthCheck read this.
        /// </summary>
        public const string AgentZipFileName = "AutopilotMonitor-Agent.zip";

        /// <summary>
        /// Stable bootstrap PowerShell script filename. Customer Intune Platform Scripts
        /// reference this URL — never rename, never version it.
        /// </summary>
        public const string BootstrapScriptName = "Install-AutopilotMonitor.ps1";

        /// <summary>Parallel stable-dev manifest for preview/lab Intune assignments.</summary>
        public const string AgentVersionFileNameDev = "version-dev.json";

        /// <summary>Parallel stable-dev agent ZIP for preview/lab Intune assignments.</summary>
        public const string AgentZipFileNameDev = "AutopilotMonitor-Agent-dev.zip";

        /// <summary>Parallel stable-dev bootstrap script for preview/lab Intune assignments.</summary>
        public const string BootstrapScriptNameDev = "Install-AutopilotMonitor-Dev.ps1";

        // =====================================================================
        // Per-line versioned namespace — SelfUpdater within a line + rollback reserve.
        // Each major-line (V1, V2, future V3...) has identical shape. SelfUpdater
        // of line N reads only its own namespace, never cross-line, never stable.
        // Backend GetAgentConfig dispatches by X-Agent-Version-Major header.
        // =====================================================================

        /// <summary>Per-line version manifest, e.g. major=2 → "version-v2.json".</summary>
        public static string AgentVersionFileNameForLine(int major) => $"version-v{major}.json";

        /// <summary>Per-line agent ZIP, e.g. major=2 → "AutopilotMonitor-Agent-v2.zip".</summary>
        public static string AgentZipFileNameForLine(int major) => $"AutopilotMonitor-Agent-v{major}.zip";

        /// <summary>Per-line bootstrap script, e.g. major=2 → "Install-AutopilotMonitor-v2.ps1".</summary>
        public static string BootstrapScriptNameForLine(int major) => $"Install-AutopilotMonitor-v{major}.ps1";

        /// <summary>Per-line dev-channel version manifest, e.g. major=2 → "version-v2-dev.json".</summary>
        public static string AgentVersionFileNameForLineDev(int major) => $"version-v{major}-dev.json";

        /// <summary>Per-line dev-channel agent ZIP, e.g. major=2 → "AutopilotMonitor-Agent-v2-dev.zip".</summary>
        public static string AgentZipFileNameForLineDev(int major) => $"AutopilotMonitor-Agent-v{major}-dev.zip";

        /// <summary>Per-line dev-channel bootstrap script.</summary>
        public static string BootstrapScriptNameForLineDev(int major) => $"Install-AutopilotMonitor-v{major}-Dev.ps1";

        /// <summary>
        /// Staging directory for self-update extraction
        /// </summary>
        public const string AgentUpdateStagingDir = @"%ProgramData%\AutopilotMonitor\Agent-Update";

        /// <summary>
        /// Download cache for the self-update ZIP. Lives under ProgramData (SYSTEM/Admin
        /// ACL) so non-admin local users cannot plant a junction at the ZIP path between
        /// pre-cleanup and FileStream open.
        /// </summary>
        public const string AgentUpdateDownloadDir = @"%ProgramData%\AutopilotMonitor\Updates";

        /// <summary>
        /// Agent binary directory
        /// </summary>
        public const string AgentDirectory = @"%ProgramData%\AutopilotMonitor\Agent";

        // -----------------------------------------------------------------------
        // Upload / batching defaults
        // -----------------------------------------------------------------------

        /// <summary>
        /// Maximum number of events per upload batch
        /// </summary>
        public const int MaxBatchSize = 100;

        /// <summary>
        /// Default upload interval in seconds (fallback timer; normal path uses FileSystemWatcher)
        /// </summary>
        public const int DefaultUploadIntervalSeconds = 30;

        // -----------------------------------------------------------------------
        // API endpoint paths (relative to ApiBaseUrl)
        // -----------------------------------------------------------------------

        /// <summary>
        /// API endpoint paths used by the agent
        /// </summary>
        public static class ApiEndpoints
        {
            public const string RegisterSession          = "/api/agent/register-session";
            public const string GetAgentConfig           = "/api/agent/config";
            public const string GatherRules              = "/api/rules/gather";
            public const string AnalyzeRules             = "/api/rules/analyze";
            public const string GetDiagnosticsUploadUrl  = "/api/agent/upload-url";
            public const string ReportAgentError         = "/api/agent/error";
            public const string BlockDevice              = "/api/devices/block";
            public const string GetBlockedDevices        = "/api/devices/blocked";
            public const string BlockVersion             = "/api/versions/block";
            public const string GetBlockedVersions       = "/api/versions/blocked";
            public const string ImeLogPatterns           = "/api/rules/ime-log-patterns";
            public const string ReseedFromGitHub         = "/api/rules/reseed-from-github";
            public const string ValidateBootstrapCode    = "/api/bootstrap/validate/{code}";

            // Agent generic telemetry transport (Plan §2.7a) — THE agent ingest path since the
            // legacy V1 NDJSON endpoint (/api/agent/ingest) was decommissioned. Heterogeneous
            // TelemetryItem[] payload (Event + Signal + DecisionTransition); backend routes per
            // TelemetryItem.Kind to Events / Signals / DecisionTransitions tables.
            public const string IngestTelemetry          = "/api/agent/telemetry";

            // MCP/Agent API search endpoints
            public const string SearchSessions          = "/api/search/sessions";
            public const string SearchSessionsByEvent   = "/api/search/sessions-by-event";
            public const string SearchSessionsByCve     = "/api/search/sessions-by-cve";
            public const string MetricsSummary          = "/api/metrics/summary";
            // Bootstrap agent endpoints (cert-free, token-auth for pre-enrollment agents)
            public const string BootstrapRegisterSession = "/api/bootstrap/register-session";
            public const string BootstrapGetAgentConfig  = "/api/bootstrap/config";
            public const string BootstrapReportError     = "/api/bootstrap/error";

            // Pre-auth distress channel (no authentication required)
            public const string ReportDistress           = "/api/agent/distress";

            // Critical-table backup feature (plan §PR1+, all GA-only)
            public const string GlobalBackupsTrigger     = "/api/global/backups/trigger";        // POST → 202 + jobId
            public const string GlobalBackupsList        = "/api/global/backups";                // GET  → list of backups
            public const string GlobalBackupsManifest    = "/api/global/backups/{backupId}";     // GET  → manifest detail
            public const string GlobalBackupsJobStatus   = "/api/global/backups/jobs/{jobId}";   // GET  → BackupJobStatus
            public const string GlobalBackupsRestoreRow  = "/api/global/backups/{backupId}/restore-row"; // POST → preview|commit single row
        }

        // -----------------------------------------------------------------------
        // Event types emitted by the agent
        // -----------------------------------------------------------------------

        /// <summary>
        /// Event type identifiers for EnrollmentEvent.EventType
        /// </summary>
        public static class EventTypes
        {
            public const string PhaseTransition     = "phase_transition";
            public const string AppInstallStart     = "app_install_started";
            public const string AppInstallComplete  = "app_install_completed";
            public const string AppInstallFailed    = "app_install_failed";
            public const string AppDownloadStarted  = "app_download_started";
            public const string AppInstallSkipped   = "app_install_skipped";
            // Liveness plan PR3 — one-shot Warning per app naming a required user-ESP app that
            // NEVER started installing (no download/install activity seen) while the ESP
            // AccountSetup apps gate waited on it. Emitted when the user-apps-settled probe
            // returns false after the ESP page exited, and again at termination for leftovers.
            // This is the actionable customer hint behind "session hangs in AccountSetup".
            public const string AppInstallStarved   = "app_install_starved";
            // Session 6b4993e5 / fc48c71a — on an ESP terminal failure of the DeviceSetup Apps
            // subcategory, names the device app(s) still in flight (e.g. stuck Downloading at 0%)
            // as the likely cause. Emitted WITHOUT mutating app state (no fabricated
            // app_install_failed); the app stays honestly Downloading. Lets MCP/Web surface the
            // culprit instead of only the opaque "Apps (Error)" registry string.
            public const string EspAppsFailureCorrelation = "esp_apps_failure_correlation";
            public const string NetworkStateChange  = "network_state_change";
            public const string NetworkConnectivityCheck = "network_connectivity_check";
            public const string ErrorDetected       = "error_detected";
            public const string PerformanceSnapshot = "performance_snapshot";
            public const string LogEntry            = "log_entry";
            public const string EspStateChange      = "esp_state_change";
            public const string DownloadProgress    = "download_progress";
            public const string CertValidation      = "cert_validation";
            public const string EspUiState          = "esp_ui_state";
            public const string GatherResult        = "gather_result";
            public const string WhiteGloveComplete  = "whiteglove_complete";
            public const string EnrollmentComplete  = "enrollment_complete";
            public const string EnrollmentFailed    = "enrollment_failed";
            // PR1 — ContinueAnyway-aware ESP terminal failure defang (Session 4fa5a2d4, 2026-05-22).
            // Emitted instead of enrollment_failed when the ESP profile permits ContinueAnyway
            // AND AccountSetup has already been entered — proves the device progressed past
            // DeviceSetup so the agent stays in monitoring instead of terminating.
            public const string EspFailureAdvisory  = "esp_failure_advisory";
            public const string DesktopArrived      = "desktop_arrived";
            // DAD liveness (state-change-only, max 3 events per detector lifetime, NOT periodic).
            // Distinguishes "DAD never started after reboot" vs "DAD started but timer/WMI dead" vs
            // "DAD running but user never logged in" — the three failure-modes that look identical
            // when only desktop_arrived is missing.
            public const string DesktopDetectorStarted      = "desktop_detector_started";    // 1x on Start() / ResetForRealUserSwitch()
            public const string DesktopDetectorFirstPoll    = "desktop_detector_first_poll"; // 1x after first PollForDesktop() completes
            public const string DesktopDetectorNoCandidate  = "desktop_detector_no_candidate"; // 1x after threshold polls without resolution (configurable)
            public const string CompletionCheck     = "completion_check";
            // Liveness plan PR2 — emitted (state-change-only, deduped via the
            // CompletionWaitingFingerprint state fact) whenever the DecisionEngine blocks or
            // defers a completion attempt. Data.missingPrerequisites names the stable literals
            // the engine is still waiting on (account_setup_provisioning_complete,
            // hello_resolution, desktop_arrival, realmjoin_resolution).
            public const string CompletionWaiting   = "completion_waiting";
            public const string ScriptStarted       = "script_started";
            public const string ScriptCompleted     = "script_completed";
            public const string ScriptFailed        = "script_failed";
            // A platform script ran to the IME script-execution timeout (~30 min) and was marked
            // Failed while the enrollment was still in progress — the prime suspect for a stalled
            // ESP pipeline (IME runs platform scripts serially; one hung script starves app
            // installs). Emitted at most once per policyId, derived from the script's own
            // start→completion duration (works on replay since start carries the source log
            // timestamp). Warning, advisory ("suspected"); no state mutation.
            public const string ScriptTimeoutSuspected = "script_timeout_suspected";
            public const string EspProvisioningStatus = "esp_provisioning_status";
            public const string SoftwareInventoryAnalysis = "software_inventory_analysis";
            public const string VulnerabilityReport       = "vulnerability_report";
            public const string AgentVersionCheck         = "agent_version_check";
            public const string AgentStarted              = "agent_started";        // Lifecycle anchor — fired Seq=1 at agent boot. PR1: replaces hardcoded string-literals at emit sites.
            public const string AgentShuttingDown         = "agent_shutting_down";  // V2 single-rail plan §6.2 — terminate-hygiene acknowledgement emitted before CleanupService tears down
            // Backend-materialized from the agent's best-effort SessionAgeEmergencyBreak error report
            // (tasks/enrollment-status-reclassification.md). The agent's 48h absolute session-age
            // break (Program.Guards.CheckSessionAgeEmergencyBreak) is otherwise silent to the backend; this
            // event closes that blind spot in the timeline AND tells the timeout classifier the agent is
            // definitively gone, so the session is terminalized now (by ESP rollup) instead of waiting out grace.
            public const string AgentEmergencyBreak       = "agent_emergency_break";
            // Low observation coverage: the agent started long after device boot AND lived only
            // briefly before a terminal outcome — i.e. it arrived after the enrollment had already
            // decided, so its diagnosis is a post-mortem of the registry/log end-state, not a live
            // observation. Flags sessions that look like normal multi-minute failures but where the
            // agent had near-zero coverage of the actual failure window. Warning; termination-time.
            public const string AgentLateStart            = "agent_late_start";
            public const string SystemRebootDetected      = "system_reboot_detected"; // Lifecycle anchor — fired when previousExitType=reboot_kill. PR1.
            public const string PerformanceCollectorStopped     = "performance_collector_stopped";    // Idle-stop anchor — fired after 15 min idle by PerformanceCollector. PR1.
            public const string AgentMetricsCollectorStopped    = "agent_metrics_collector_stopped";  // Idle-stop anchor — fired after 15 min idle by AgentMetricsCollector. PR1.
            public const string PriorRunDiedWithState           = "prior_run_died_with_state";        // Death-Rattle (Plan §B) — emitted on next run if previous exit was unclean. PR1.
            public const string CollectorDegraded               = "collector_degraded";               // One-shot Warning when a collector/watcher fails to arm (e.g. EventLogWatcher start error). Review MON-D1: without it a dead kernel watcher is indistinguishable from a genuine no-signal session.
            public const string StateQuarantineRecovered        = "state_quarantine_recovered";        // Warning emitted on the start that honours a prior run's quarantine marker (3 consecutive journal-append failures). Review TRACE-H2: without it a corruption-recovery looks like a normal restart on the backend.
            public const string TelemetryUploadPoisoned         = "telemetry_upload_poisoned";         // Warning when the drain quarantined a single oversized item (413 even on its own — the only locally-provable poison) to keep the pipeline flowing. Review TRACE-H1/P1.
            public const string TelemetryUploadBlocked          = "telemetry_upload_blocked";          // Warning (one-shot) when the drain is blocked by a retained unknown permanent 4xx (400 contract bug / 404 route mismatch / tenant-validation). Data is NOT discarded. LOCAL diagnostic (agent log + diag ZIP): this marker is queued behind the blocking batch and only reaches the backend after the block clears — no out-of-band bypass by design.
            public const string SessionParkedWithoutDeadline    = "session_parked_without_deadline";   // One-shot Warning tripwire (liveness plan PR1): a non-terminal session entered the post-AccountSetup dead-end zone (ESP final exit or advisory failure) with NO resolution-capable deadline armed. After the 2026-06-12 dead-end fixes this must never fire in production — every occurrence is a bug report for an unknown parking variant.
            public const string DiskSpaceLow                    = "disk_space_low";                    // One-shot Warning (state-change-only, 1 GB hysteresis — NOT periodic) when the system drive drops below 2 GB free. Review MON-B10: disk-full was previously only visible inside Debug performance_snapshot payloads (filtered out of timelines), so a disk-starved enrollment carried no actionable threshold signal.
            public const string KeepAwakeEngaged                = "keep_awake_engaged";                // Info (state-change, NOT periodic) emitted by the opt-in UserEspKeepAwake host when it holds the device awake (system + display) on entering the User-ESP/AccountSetup phase, so idle standby cannot stall app installs / account provisioning. Reboots are unaffected. Per-tenant toggle (AnalyzerConfiguration.KeepAwakeDuringUserEsp, default off).
            public const string KeepAwakeReleased               = "keep_awake_released";               // Info (Warning when reason=safety_cap) emitted when the keep-awake hold is released — reason = account_setup_complete | host_stop | safety_cap. Brackets KeepAwakeEngaged; the OS also auto-clears the hold on process exit/reboot.

            // IME log tracker — app / device / script telemetry (V2 single-rail plan §5.9)
            public const string EspPhaseChanged           = "esp_phase_changed";
            public const string ImeAgentVersion           = "ime_agent_version";
            public const string ImeUserSessionCompleted   = "ime_user_session_completed";
            public const string DoTelemetry               = "do_telemetry";
            public const string AllAppsCompleted          = "all_apps_completed";
            public const string AppTrackingSummary        = "app_tracking_summary";  // Plan §5 Fix 4b — terminal per-session app summary
            // Session-level internet-bandwidth estimate derived PASSIVELY from the DO byte counters
            // the DeliveryOptimizationCollector already polls — no synthetic traffic, no extra load.
            // At most twice per session (snapshotTrigger disambiguates): an interim snapshot at the
            // first AccountSetup sighting (device_setup_end — survives sessions that starve in the
            // account phase) and the authoritative final at collector stop (collector_stop).
            // WAN (HTTP + internet peers) and LAN
            // (LAN/group/link-local peers + Connected Cache) are reported separately so peer-fed
            // downloads cannot inflate the WAN figure; p90 of per-poll rates ≈ lower bound of the
            // effective line capacity during enrollment ("DSL 16 vs 250").
            public const string NetworkBandwidthEstimate  = "network_bandwidth_estimate";

            // Microsoft 365 Apps (Office Click-to-Run) install lifecycle — emitted by the V2
            // OfficeInstallDetector which watches the C2R client (registry + process) directly.
            // The Intune "integrated" Microsoft 365 Apps app reports done to IME within 1-2 min
            // while C2R keeps streaming/installing in the background; these events surface the
            // REAL install progress (phase-granular — no percentage is exposed by Office) plus
            // products, channel, version reached, duration and a best-effort failure code.
            public const string OfficeInstallStarted      = "office_install_started";
            public const string OfficeInstallProgress     = "office_install_progress";
            public const string OfficeInstallCompleted    = "office_install_completed";
            public const string OfficeInstallFailed       = "office_install_failed";
            // Emitted instead of a started/failed pair when the C2R activity is on an Office that is
            // ALREADY fully resident on disk at the first signal (OEM/consumer inbox Office running a
            // background CLIENTUPDATE/maintenance scenario). Informational — NOT an enrollment failure.
            public const string OfficePreinstalledDetected = "office_preinstalled_detected";

            // Stall detection (Ebene 2 — StallProbeCollector)
            public const string StallProbeCheck           = "stall_probe_check";   // Trace heartbeat from Probe 2 (15 min) when no anomaly found
            public const string StallProbeResult          = "stall_probe_result";  // Warning when a probe found an anomaly
            public const string SessionStalled            = "session_stalled";     // Fire-once after Probe 4 (60 min) — triggers backend Stalled status

            // ModernDeployment EventLog Watcher (Ebene 1 — live capture)
            public const string ModernDeploymentLog       = "modern_deployment_log";      // Info-level live capture
            public const string ModernDeploymentWarning   = "modern_deployment_warning";  // Level 3 (Warning)
            public const string ModernDeploymentError     = "modern_deployment_error";    // Level 1-2 (Critical/Error)

            // Windows Update during OOBE (WindowsUpdateTracker — Microsoft-Windows-WindowsUpdateClient/
            // Operational live watcher + startup backfill). Surfaces quality/cumulative updates that
            // install DURING enrollment — a blind spot no other tool (Intune console included) covers.
            // A cumulative update installing mid-OOBE can break the enrollment (r/Intune KB5095189) and
            // is becoming more common with the ESP "Install Windows quality updates during OOBE" feature.
            // WU Client EventIDs: 19=success, 20=failure (carries HRESULT), 43=install started, 44=download started.
            public const string WindowsUpdateSucceeded    = "windows_update_succeeded";      // WU Client EventID 19
            public const string WindowsUpdateFailed       = "windows_update_failed";         // WU Client EventID 20 — Data.hresult(hex)+hresultSymbol(decoded)+updateTitle+updateGuid
            public const string WindowsUpdateStarted      = "windows_update_started";        // WU Client EventID 43 (install) / 44 (download) — Debug context
            // Corroboration snapshots (gather rules, config-delivered). Secondary evidence that an
            // update landed during enrollment even when the watcher's backfill window missed it.
            public const string WindowsUpdateRebootPending = "windows_update_reboot_pending"; // CBS / WU Auto-Update pending-reboot regkey snapshot
            public const string WindowsUpdateHistory       = "windows_update_history";        // Get-HotFix installed-updates snapshot (QFE-registered only)
            // Deterministic update corroboration + blind-spot self-diagnosis (session 7443317c:
            // OS build jumped 26200.8037→.8655 across a mid-OOBE reboot, yet the WU channel carried
            // none of the targeted EventIDs — the update installed via a path the watcher is blind to).
            public const string OsBuildChanged             = "os_build_changed";               // OS build (CurrentBuild.UBR) differs across an agent restart — an update was installed during enrollment
            public const string WindowsUpdateChannelCensus = "windows_update_channel_census";  // One-shot EventID histogram of WU Client + UpdateOrchestrator channels when os_build_changed but zero targeted WU events captured

            // MDM reboot-required policy flags (MdmRebootPolicyTracker; PMPC "unexpected reboot /
            // second sign-in" research). DM-Enterprise EventID 2800 fires once per policy URI that
            // matches the OS reboot-required catalog ("The following URI has triggered a reboot:
            // (./Device/...)."; verified session b2e890c1). Only URIs applied DURING ESP DeviceSetup
            // cause the coalesced mid-ESP reboot — later syncs (AccountSetup+) merely flag, no forced
            // reboot. The tracker therefore emits ONE aggregated Info event per burst with the URI
            // list; the reboot CLAIM lives in ANALYZE-ESP-005, gated on an actually-observed
            // system_reboot_detected. Deliberately DISTINCT from system_reboot_detected: the backend
            // RebootCount matches that string exactly (EventIngestProcessor.Classification + terminal
            // recount) — this attribution event must never inflate the reboot count.
            public const string MdmPolicyRebootRequired    = "mdm_policy_reboot_required";     // Aggregated per burst — Data.rebootUris[] + uriCount + firstRebootUri

            // TEMPORARY: shadow SM rollout verification — remove when CompletionStateMachine is promoted to primary
            public const string ShadowDiscrepancy         = "shadow_discrepancy";

            // Hybrid User-Driven enrollment observability (V2 — Hybrid completion gaps, 2026-05-01).
            // Pure observability — never consumed by the DecisionEngine, no DecisionSignalKind wired up.
            public const string AadPlaceholderUserDetected = "aad_placeholder_user_detected"; // foouser@/autopilot@ first appearance in JoinInfo
            public const string HybridLoginPending         = "hybrid_login_pending";          // Hybrid: 10 min after reboot still placeholder, real AD login overdue

            // Real-user join — companion to AadPlaceholderUserDetected. The DecisionEngine consumes
            // these as DecisionSignalKind only (HandleAadUserJoinedLateV1 is observation-only and
            // emits no timeline effect), so the AadJoinWatcherAdapter dual-emits them as
            // informational events too. Without this the FailureSnapshotBuilder cannot tell that
            // the real AAD user was ever observed (Codex review 2026-05-01).
            public const string AadUserJoinedObserved      = "aad_user_joined_observed";     // Real user observed in JoinInfo — synchronous startup-read OR mid-session watcher fire

            // RealmJoin (RJ) deployment tracking — driven by HKLM\SYSTEM\...\realmjoin\Parameters
            // + HKLM/HKU \SOFTWARE\RealmJoin\Packages\<id>. Detection extends the V2 enrollment
            // session lifetime until DeploymentPhase reaches CompletedFirstDeployment (110) or
            // the 60-min hard timeout fires. Per-package events surface install lifecycle in
            // the same shape as IME app_install_started / app_install_completed.
            public const string RealmJoinDetected         = "realmjoin_detected";
            public const string RealmJoinPhaseChanged    = "realmjoin_phase_changed";
            public const string RealmJoinResolved        = "realmjoin_resolved";
            public const string RealmJoinTimeout         = "realmjoin_timeout";
            public const string RealmJoinPackageStarted  = "realmjoin_package_started";
            public const string RealmJoinPackageCompleted = "realmjoin_package_completed";

            // =====================================================================
            // Single-source consolidation (2026-05-29): event-type string literals
            // that were previously emitted inline across V2 collectors/trackers are
            // now defined here so Constants.EventTypes is the ONE canonical catalog.
            // Values are byte-identical to the former literals (persisted strings —
            // rules/UI/MCP match on them). A drift guard test enforces that no V2
            // emit site uses a raw literal not defined here.
            // =====================================================================

            // Device info / environment (DeviceInfoCollector + StartupEnvironmentProbes)
            public const string OsInfo                    = "os_info";
            public const string BootTime                  = "boot_time";
            public const string HardwareSpec              = "hardware_spec";
            public const string NetworkAdapters           = "network_adapters";
            public const string DnsConfiguration          = "dns_configuration";
            public const string ProxyConfiguration        = "proxy_configuration";
            public const string AutopilotProfile          = "autopilot_profile";
            public const string AutopilotProfileMissing   = "autopilot_profile_missing";
            public const string EnrollmentTypeDetected    = "enrollment_type_detected";
            public const string SecureBootStatus          = "secureboot_status";
            public const string BitLockerStatus           = "bitlocker_status";
            public const string TpmStatus                 = "tpm_status";
            public const string AadJoinStatus             = "aad_join_status";
            public const string EspConfigDetected         = "esp_config_detected";
            public const string NetworkInterfaceInfo      = "network_interface_info";
            public const string WifiSignalInfo            = "wifi_signal_info";
            public const string DeviceLocation            = "device_location";
            public const string OutboundIp                = "outbound_ip";
            public const string TimezoneAutoSet           = "timezone_auto_set";
            public const string NtpTimeCheck              = "ntp_time_check";
            public const string PowerStateCheck           = "power_state_check";
            public const string AgentTrace                = "agent_trace";

            // Crash / process lifecycle
            public const string PreviousCrashDetected     = "previous_crash_detected";
            public const string ImeProcessExited          = "ime_process_exited";

            // White Glove / pre-provisioning
            public const string WhiteGloveStarted         = "whiteglove_started";
            public const string WhiteGloveResumed         = "whiteglove_resumed";
            public const string WhiteGlovePart1Complete   = "whiteglove_part1_complete";

            // Windows Hello for Business provisioning
            public const string HelloPolicyDetected           = "hello_policy_detected";
            public const string HelloPolicyDetectionMismatch  = "hello_policy_detection_mismatch";
            public const string HelloWaitTimeout              = "hello_wait_timeout";
            public const string HelloCompletionTimeout        = "hello_completion_timeout";
            public const string HelloWizardStarted            = "hello_wizard_started";
            public const string HelloProcessingStarted        = "hello_processing_started";
            public const string HelloProcessingStopped        = "hello_processing_stopped";
            public const string HelloProvisioningCompleted    = "hello_provisioning_completed";
            public const string HelloProvisioningFailed       = "hello_provisioning_failed";
            public const string HelloProvisioningBlocked      = "hello_provisioning_blocked";
            public const string HelloPinStatus                = "hello_pin_status";
            public const string HelloSkipped                  = "hello_skipped";

            // ESP provisioning detail
            public const string EspFailureSettleStarted   = "esp_failure_settle_started";
            public const string EspFailureSettleRecovered = "esp_failure_settle_recovered"; // The failed subcategory left the "failed" state during the settle window (e.g. ESP "Try again" retry, session c071e92b) — the terminal EspFailureDetected fire is suppressed and monitoring continues; a subsequent failure re-arms a fresh settle window.
            public const string EspAppxFailureAnalysis    = "esp_appx_failure_analysis"; // One-shot AppXDeploymentServer/Operational scan during the ESP failure settle window: candidate MSIX/Store packages behind an "Apps (0x…)" subcategory failure invisible to ImeLogTracker (session 2bc884b6). Assessment, not a confirmed root cause.
            public const string EspProvisioningRaw        = "esp_provisioning_raw";
            public const string EspFailure                = "esp_failure";
            public const string EspExiting                = "esp_exiting";

            // Security / inventory analyzers
            public const string IntegrityBypassAnalysis   = "integrity_bypass_analysis";
            public const string LocalAdminAnalysis        = "local_admin_analysis";
            public const string SecurityWarning           = "security_warning";
            // Per-device provisioning-package scan. Carries an `artifacts[]` array (one element per
            // registry package / .ppkg file / Recovery\Customizations residue, each with a scalar
            // `identity`). The PPKG analyze rules iterate this array via an event_data_array
            // condition + allow-list regex — no per-package event spam (a clean Win11 device ships
            // ~22 OS-inbox .ppkg). A large artifact set is split across MULTIPLE such events
            // (chunkIndex/chunkCount); the rule engine evaluates the array across all of them.
            // PPKGs can be legitimate (bulk/OEM) or a manipulation vector.
            public const string ProvisioningPackageScan   = "provisioning_package_scan";
            // AutoLogon scan — runs at DeviceSetup-phase completion and at final shutdown. Reports
            // raw facts only (Winlogon registry indicators; DefaultPassword presence-only, never the
            // value) at Info severity. AutoLogon-enabled alone is NOT graded — it is the fingerprint
            // of Windows' own ESP auto-logon on every normal Autopilot enrollment. Backend analyze-
            // rules grade only the unambiguous cases: a plaintext DefaultPassword on disk (ANALYZE-
            // SEC-003, escalated) and an optional kiosk allow-list template (ANALYZE-SEC-004, off by
            // default). AutoLogon may be legitimate (kiosk) OR an enrollment/OOBE manipulation vector.
            public const string AutoLogonAnalysis         = "autologon_analysis";
            // OOBE-console / Shift+F10 detection (per-tenant opt-OUT, AnalyzerConfiguration.
            // EnableConsoleBypassDetection, default ON). Two complementary Warning signals:
            //   oobe_console_spawned    — LIVE: an interactive-session cmd.exe (SessionID != 0) with a
            //                             bare, non-scripted command line (no /c|/k) — an interactive
            //                             console a human can type into. confidence=high; an instant-close
            //                             cmd whose command line was unreadable is confidence=low. Stopped
            //                             on real-user desktop arrival (Shift+F10 gone). NOT parent-based:
            //                             the Shift+F10 cmd's parent is a short-lived non-winlogon launcher
            //                             (field 24H2). Misses presses BEFORE the agent installed.
            //   console_prefetch_detected — STARTUP FORENSIC: a CMD.EXE-*.pf prefetch artifact whose
            //                             last-run is after boot. Covers the pre-agent OOBE window the
            //                             live watcher cannot see, but cannot attribute the run to
            //                             Shift+F10 vs. a legitimate install-launched cmd once ESP runs
            //                             (cmd.exe shares one .pf). Both carry coverageComplete=false +
            //                             an honest coverageNote — detection is best-effort, not gapless.
            public const string OobeConsoleSpawned        = "oobe_console_spawned";
            public const string ConsolePrefetchDetected   = "console_prefetch_detected";

            // Termination / diagnostics / server actions
            public const string EnrollmentSummaryShown    = "enrollment_summary_shown";
            public const string DiagnosticsCollecting     = "diagnostics_collecting";
            public const string DiagnosticsUploaded       = "diagnostics_uploaded";
            public const string DiagnosticsUploadFailed   = "diagnostics_upload_failed";
            public const string RebootTriggered           = "reboot_triggered";
            public const string ServerActionReceived      = "server_action_received";
            public const string ServerActionExecuted      = "server_action_executed";
            public const string ServerActionFailed        = "server_action_failed";
            public const string AdminMarkedSession        = "admin_marked_session";
            // Server-authored: emitted by the maintenance sweep when a stalled session crosses the
            // SessionTimeoutHours (5h default) window and graduates to terminal Failed. Gives the
            // analyze pipeline a terminal event to fire on (parity with the agent's enrollment_failed).
            public const string SessionTimeout            = "session_timeout";
            public const string RemoteConfigFetchFailed   = "remote_config_fetch_failed";
            public const string AgentUnrestrictedModeChanged = "agent_unrestricted_mode_changed";

            // Gather-rules collection mode
            public const string GatherRulesCollectionStarted   = "gather_rules_collection_started";
            public const string GatherRulesCollectionCompleted = "gather_rules_collection_completed";

            // Agent self-metrics / back-pressure
            public const string SpoolPressureDetected     = "spool_pressure_detected";
            public const string AgentMetricsSnapshot      = "agent_metrics_snapshot";
            public const string IngressBackpressure       = "ingress_backpressure";

            // ---------------------------------------------------------------------
            // V1 (legacy) event types — emitted ONLY by the decommissioning V1 agent
            // (Agent.Core / Agent). Defined here so the canonical catalog and the MCP
            // search/catalog stay complete for historical V1 session analysis. V1 emit
            // sites are intentionally NOT refactored to use these (V1 is going away).
            // ---------------------------------------------------------------------
            public const string ConfigMgrClientDetected       = "configmgr_client_detected";
            public const string EnrollmentTypeMismatch        = "enrollment_type_mismatch";
            public const string DecisionProcessCompletion     = "decision_process_completion";
            public const string WhiteGloveClassification      = "whiteglove_classification";
            public const string EspResumed                    = "esp_resumed";
            public const string WaitingForHello               = "waiting_for_hello";
            public const string EspProvisioningSettleStarted  = "esp_provisioning_settle_started";
            public const string ImeSessionChange              = "ime_session_change";
            public const string SecurityAudit                 = "security_audit";
            public const string AgentShutdown                 = "agent_shutdown"; // distinct from V2 AgentShuttingDown
        }

        // -----------------------------------------------------------------------
        // Event sources
        // -----------------------------------------------------------------------

        /// <summary>
        /// Event source identifiers for EnrollmentEvent.Source
        /// </summary>
        public static class EventSources
        {
            public const string Agent    = "Agent";
            public const string IME      = "IME";
            public const string Registry = "Registry";
            public const string WMI      = "WMI";
            public const string Network  = "Network";
        }

        // -----------------------------------------------------------------------
        // App failure types
        // -----------------------------------------------------------------------

        /// <summary>
        /// Canonical failure-type identifiers carried on <c>app_install_failed</c>
        /// events (DataJson.failureType) and on <see cref="Models.AppInstallSummary.FailureCode"/>.
        /// Stable strings — UI and rule engine match on these.
        /// </summary>
        public static class AppFailureTypes
        {
            /// <summary>
            /// The Enrollment Status Page Apps-subcategory failed with no per-app HRESULT
            /// available, so the agent cannot tell whether the install ultimately would have
            /// succeeded, failed, or hung. UI renders these as "likely stuck" rather than
            /// confirmed failures.
            /// <para>
            /// Emitted only by the V2 EnrollmentTerminationHandler on the terminal-ESP-failure
            /// path when <see cref="ClassifyEspAppsFailure"/> falls through to this fallback.
            /// Apps with this failureType always carry <c>confidence: "presumed"</c> in their
            /// event payload.
            /// </para>
            /// </summary>
            public const string EspAppsTimeout = "esp_apps_timeout";

            /// <summary>
            /// The ESP Apps-subcategory failed with HRESULT <c>0x87D1041C</c> — "Application
            /// not detected after installation completed successfully". The installer ran
            /// (often to completion) but the Intune detection rule could not verify the app.
            /// This is a confirmed detection failure, NOT a timeout — UI renders these in red
            /// ("Detection failed") instead of orange ("Likely stuck").
            /// </summary>
            public const string EspAppsDetectionFailure = "esp_apps_detection_failure";

            /// <summary>
            /// The ESP Apps-subcategory failed with a non-detection HRESULT (anything other
            /// than <c>0x87D1041C</c>). The app install itself reported an error. UI renders
            /// these as a confirmed "Install failed" in red.
            /// </summary>
            public const string EspAppsInstallFailure = "esp_apps_install_failure";

            /// <summary>
            /// Windows HRESULT for "Application not detected after installation completed
            /// successfully" — the canonical marker for an Intune detection-rule mismatch
            /// surfacing on the ESP Apps subcategory. Lower-case so equality checks against
            /// <see cref="ProvisioningStatusTracker.TryExtractErrorCode"/> output line up.
            /// </summary>
            public const string DetectionFailureHResult = "0x87d1041c";

            /// <summary>
            /// Maps an ESP Apps-subcategory failure to a canonical <see cref="AppFailureTypes"/>
            /// identifier + a human-readable app-install message based on the HRESULT carried
            /// by the failed subcategory.
            /// <list type="bullet">
            ///   <item><c>0x87D1041C</c> → <see cref="EspAppsDetectionFailure"/> (install
            ///         ran but Intune could not detect the app afterwards).</item>
            ///   <item>Any other non-empty HRESULT → <see cref="EspAppsInstallFailure"/>
            ///         (the install itself reported an error).</item>
            ///   <item>No HRESULT available → <see cref="EspAppsTimeout"/> (fallback for
            ///         genuine timeout cases; the ESP gave up without a per-app verdict).
            ///         <paramref name="espTimeoutMinutes"/> is only mentioned in the message
            ///         on this branch — quoting it elsewhere is misleading because the ESP
            ///         did not necessarily run out the configured clock.</item>
            /// </list>
            /// </summary>
            public static (string FailureType, string Message) ClassifyEspAppsFailure(
                string? errorCode, int? espTimeoutMinutes)
            {
                // netstandard2.0's IsNullOrEmpty lacks [NotNullWhen(false)], so the compiler can't
                // see that the else branch is non-null (CS8602) — assert it.
                var normalised = string.IsNullOrEmpty(errorCode) ? null : errorCode!.ToLowerInvariant();

                if (string.Equals(normalised, DetectionFailureHResult, System.StringComparison.Ordinal))
                {
                    return (
                        EspAppsDetectionFailure,
                        $"Install completed but Intune detection rule did not find the app afterwards (HRESULT {normalised}).");
                }

                if (!string.IsNullOrEmpty(normalised))
                {
                    return (
                        EspAppsInstallFailure,
                        $"ESP reported an Apps-subcategory failure (HRESULT {normalised}) before this app finished installing.");
                }

                if (espTimeoutMinutes.HasValue)
                {
                    return (
                        EspAppsTimeout,
                        $"Install status unconfirmed — ESP gave up while this app was still installing " +
                        $"(ESP timeout was configured at {espTimeoutMinutes.Value} min; actual elapsed time may be lower).");
                }

                return (
                    EspAppsTimeout,
                    "Install status unconfirmed — ESP gave up while this app was still installing.");
            }
        }

        // -----------------------------------------------------------------------
        // Tenant Roles
        // -----------------------------------------------------------------------

        /// <summary>
        /// Roles for tenant members stored in the TenantAdmins table.
        /// Null/missing Role on existing entities is treated as Admin for backward compatibility.
        /// </summary>
        public static class TenantRoles
        {
            public const string Admin = "Admin";
            public const string Operator = "Operator";
            public const string Viewer = "Viewer";
        }

        /// <summary>
        /// Platform-wide roles stored in the GlobalAdmins table (Role column).
        /// Empty/missing Role on existing entities is treated as GlobalAdmin for backward compatibility.
        /// GlobalReader = read-only platform tier: cross-tenant read of telemetry + config (config secrets
        /// redacted) but performs NO mutating operation and cannot write global settings. Raw table/log
        /// access stays GlobalAdminOnly (it could dump secret-bearing tables, bypassing redaction).
        /// </summary>
        public static class GlobalRoles
        {
            public const string GlobalAdmin = "GlobalAdmin";
            public const string GlobalReader = "GlobalReader";
        }

        /// <summary>
        /// Per-tenant roles stored in the DelegatedAdmins table (Role column). A delegated admin is scoped
        /// to a SUBSET of tenants (its assignment rows) rather than the whole platform — the "scoped global"
        /// tier between a single-tenant member and a GlobalAdmin. Mirrors <see cref="GlobalRoles"/>:
        /// DelegatedReader = cross-tenant READ over the assigned tenants only (no mutation, secrets redacted);
        /// DelegatedAdmin = read + (later) scoped write over the assigned tenants. Externally surfaced as
        /// "MSP mode". An unrecognized Role string is treated as no role (fail-closed).
        /// </summary>
        public static class DelegatedRoles
        {
            public const string DelegatedReader = "DelegatedReader";
            public const string DelegatedAdmin = "DelegatedAdmin";
        }

        /// <summary>
        /// Lifecycle status of a DelegatedAdmins assignment row. Only <see cref="Active"/> rows confer scope;
        /// <see cref="PendingApproval"/> (customer-delegation awaiting tenant-admin approval) and
        /// <see cref="Revoked"/> rows are retained for audit but grant nothing.
        /// </summary>
        public static class DelegatedStatus
        {
            public const string Active = "Active";
            public const string PendingApproval = "PendingApproval";
            public const string Revoked = "Revoked";
        }

        /// <summary>
        /// How a DelegatedAdmins assignment was created: <see cref="OperatorGranted"/> (a platform
        /// GlobalAdmin assigned it centrally) or <see cref="CustomerDelegated"/> (the customer's own
        /// tenant admin delegated access to an external party — self-service).
        /// </summary>
        public static class DelegatedSource
        {
            public const string OperatorGranted = "OperatorGranted";
            public const string CustomerDelegated = "CustomerDelegated";
        }

        /// <summary>
        /// Placeholder substituted for secret-bearing string fields (SAS URLs, webhook URLs, API keys,
        /// custom webhook headers) when a config object is served to a read-only GlobalReader. Non-empty
        /// secrets become this sentinel so the UI shows "configured but hidden"; empty stays empty.
        /// </summary>
        public const string RedactedSecretPlaceholder = "***REDACTED***";

        // -----------------------------------------------------------------------
        // Audit logging
        // -----------------------------------------------------------------------

        /// <summary>
        /// Partition key used for audit log entries that have no tenant context
        /// (e.g. AdminConfiguration updates, VersionBlock rules).
        /// Uses a null-GUID so it passes EnsureValidGuid validation.
        /// </summary>
        public const string AuditGlobalTenantId = "00000000-0000-0000-0000-000000000000";

        // -----------------------------------------------------------------------
        // Azure Table Storage table names
        // All table names are defined here centrally and initialized at application startup
        // -----------------------------------------------------------------------

        /// <summary>
        /// Azure Table Storage table names
        /// </summary>
        public static class TableNames
        {
            // Core data tables
            public const string Sessions       = "Sessions";
            public const string SessionsIndex  = "SessionsIndex";
            public const string Events         = "Events";
            public const string AuditLogs      = "AuditLogs";
            public const string UsageMetrics   = "UsageMetrics";
            public const string UserActivity   = "UserActivity";
            // Live presence: ONE row per user (PK=TenantId, RowKey=SHA-256(lowercase UPN) hex), overwritten
            // on each authenticated web request. Self-maintaining (size = distinct users); "active now"
            // is a read-time LastSeen window filter, not row deletion. See UserPresenceMiddleware.
            public const string UserPresence   = "UserPresence";

            // Rules engine tables
            public const string RuleResults    = "RuleResults";
            public const string GatherRules    = "GatherRules";
            public const string AnalyzeRules   = "AnalyzeRules";
            public const string ImeLogPatterns = "ImeLogPatterns";
            public const string RuleStates     = "RuleStates";

            // App metrics tables
            public const string AppInstallSummaries = "AppInstallSummaries";
            public const string PlatformStats       = "PlatformStats";

            // Rule telemetry (daily per-rule fire/evaluation counters)
            public const string RuleStats           = "RuleStats";

            // Configuration tables
            public const string TenantConfiguration = "TenantConfiguration";
            public const string AdminConfiguration  = "AdminConfiguration";

            // Admin tables
            public const string GlobalAdmins = "GlobalAdmins";
            public const string TenantAdmins   = "TenantAdmins";
            public const string McpUsers       = "McpUsers";

            // Delegated (scoped-global) admin assignments — "MSP mode". PK=delegated-admin UPN (lowercase),
            // RK=TenantId (lowercase). One row per (admin, managed-tenant) pair.
            public const string DelegatedAdmins = "DelegatedAdmins";

            // Tenant Groups — app-internal named bundles of tenants for delegated admins ("MSP mode").
            // Two row layouts share this table (RowKey discriminates):
            //   PK=groupId, RK="meta"     → group metadata (display name, creator).
            //   PK=groupId, RK=tenantId   → one membership row per tenant in the group (lowercase).
            // A delegated UPN assigned to a group (see TenantGroupAssignments) gains read scope to
            // every tenant in it. Tenant offboarding purges the membership rows (RowKey == tenantId).
            public const string TenantGroups = "TenantGroups";

            // Tenant Group assignments — which delegated-admin UPN is assigned to which group.
            // PK=UPN (lowercase), RK=groupId. Hot path: GetScopeAsync point-scans by PK to expand the
            // UPN's groups into the effective tenant set. NOT tenant-id-keyed (offboarding never touches it).
            public const string TenantGroupAssignments = "TenantGroupAssignments";

            // Preview gating (temporary — remove after GA)
            public const string PreviewWhitelist = "PreviewWhitelist";
            public const string PreviewConfig    = "PreviewConfig";

            // User-submitted feedback. Survives tenant offboarding by design (NOT in any
            // wipe list) — feedback from offboarded tenants is exactly the data we want to
            // keep for product learning. Two partition layouts share this table:
            //   PK="InApp",       RK=upn               → in-app star rating + comment.
            //   PK="Offboarding", RK=historyRowKey     → free-form farewell comment
            //                                            captured during the drain barrier.
            public const string Feedback         = "Feedback";

            // Device blocking
            public const string BlockedDevices = "BlockedDevices";

            // Version-based blocking
            public const string BlockedVersions = "BlockedVersions";

            // Session reports (feedback from Tenant Admins)
            public const string SessionReports = "SessionReports";

            // Bootstrap sessions (OOBE pre-enrollment agent deployment)
            public const string BootstrapSessions = "BootstrapSessions";

            // Global Admin in-app notifications (persistent until dismissed)
            public const string GlobalNotifications = "GlobalNotifications";

            // Tenant-scoped in-app notifications (persistent until dismissed, PartitionKey = tenantId)
            public const string TenantNotifications = "TenantNotifications";

            // Per-tenant per-model dedup tracker for hardware-rejection bell notifications.
            // PartitionKey = tenantId, RowKey = "{manufacturer-lower}|{model-lower}". Lifetime dedup.
            public const string HardwareRejectionNotificationTracker = "HardwareRejectionNotificationTracker";

            // Vulnerability data cache (CPE mappings, CVE data)
            public const string VulnerabilityCache = "VulnerabilityCache";

            // Vulnerability reports per session (like RuleResults but for CVE correlation)
            public const string VulnerabilityReports = "VulnerabilityReports";

            // Persistent software inventory per tenant (aggregated from enrollment snapshots)
            public const string SoftwareInventory = "SoftwareInventory";

            // One side-row per inventory-correlated session, keyed by (tenantId, sessionId).
            // Drives at-most-once-per-session inventory counter increments via delta-update,
            // and gives the cascade-delete the exact decrement keys at preflight time.
            // Written by VulnerabilityCorrelationService (PR2); read by DeletionManifestBuilder (PR1).
            public const string SessionInventoryContributions = "SessionInventoryContributions";

            // SLA breach status per tenant (one row per tenant, RowKey = "status").
            // Persists across host recycles so SLA-breach notifications can be throttled
            // reliably and a GA cross-tenant overview can be served without re-aggregation.
            public const string SlaTenantStatus = "SlaTenantStatus";

            // Agent-queryable indexes (for MCP API)
            public const string EventTypeIndex = "EventTypeIndex";
            public const string DeviceSnapshot = "DeviceSnapshot";
            public const string CveIndex = "CveIndex";

            // Pre-auth distress reports (unverified agent error signals)
            public const string DistressReports = "DistressReports";

            // User usage tracking (per-user, per-day, per-endpoint)
            public const string UserUsageLog = "UserUsageLog";

            // Operational events (vital infrastructure signals for Global Admin Ops dashboard)
            public const string OpsEvents = "OpsEvents";

            // IME version history (permanent archive, survives data retention)
            public const string ImeVersionHistory = "ImeVersionHistory";

            // Lightweight index of sessions that have events (for orphan detection)
            public const string EventSessionIndex = "EventSessionIndex";

            // Short-lived "session was deleted" markers, written by the cascade-delete worker
            // immediately before the FINAL tombstone removes the Sessions row. Read by
            // SessionDeletionGuard when a writer (register / ingest) sees a missing Sessions row:
            // marker present → reject with 410 Gone. Pruned by SessionDeletionMaintenanceFunction
            // after the tombstone-retention window expires (default 7 days). PartitionKey =
            // {TenantId}, RowKey = {SessionId}.
            public const string SessionTombstones = "SessionTombstones";

            // V2 Decision Engine primary tables (Plan §M5).
            // SignalLog (input-truth) and Journal (decision-truth) projected to the backend for
            // the Inspector + Reducer-Verifier. Both partitioned by {TenantId}_{SessionId}.
            public const string Signals             = "Signals";
            public const string DecisionTransitions = "DecisionTransitions";

            // V2 Decision Engine index tables (Plan §2.8 query matrix + §M5.d). Secondary
            // projections written asynchronously via the `telemetry-index-reconcile` queue
            // after the primary Signals / DecisionTransitions row has committed. Eventual
            // consistency; a 2h timer re-indexes the last 4h as a safety-net on queue failures.
            public const string SessionsByTerminal          = "SessionsByTerminal";
            public const string SessionsByStage             = "SessionsByStage";
            public const string DeadEndsByReason            = "DeadEndsByReason";
            public const string ClassifierVerdictsByIdLevel = "ClassifierVerdictsByIdLevel";
            public const string SignalsByKind               = "SignalsByKind";

            // Tenant offboarding audit (cascade-worker plan, Rev 9). Houses three
            // PartitionKey patterns side-by-side:
            //   - "OffboardingMarker"   (1 row/tenant,  RK=tenantId) active 403-gate marker
            //   - "OffboardingHistory"  (N rows/tenant, RK="{yyyyMMddHHmmssfff}_{tenantId}") audit trail
            //   - "OffboardingByTenant" (1 row/tenant,  RK=tenantId) O(1) re-onboarding pointer
            // Marker survives the Completed transition for at least 15min so that warm
            // function-instance caches of TenantConfiguration cannot leak auth after wipe.
            // See OffboardingPartitionKeys for the canonical PK string literals.
            public const string OffboardingAudit = "OffboardingAudit";

            // Tenant offboarding customs archive (PR3.B). Phase 2.D-archive writes a
            // snapshot of every GatherRules / AnalyzeRules / ImeLogPatterns row keyed by
            // PK=tenantId before the originals are safe-wiped, so a Global Admin can
            // review them post-offboarding from the /admin/customs-archive page.
            //   - PartitionKey = "{normalizedTenantId}_{historyRowKey}" (one partition per offboarding run)
            //   - RowKey       = "{originalTable}_{base64url(originalRowKey)}"
            // Lives forever; cleanup is operator-driven via the admin UI.
            public const string TenantOffboardingCustomsArchive = "TenantOffboardingCustomsArchive";

            // Graph add-on permission feature — cache for Intune script display names.
            // PartitionKey = tenantId, RowKey = "{Kind}_{ScriptId}" or "{Kind}_$meta".
            // Daily cleanup function evicts entries older than the TTL.
            public const string ScriptNameCache = "ScriptNameCache";

            // Critical-table backup/restore job tracking (plan §PR1). Single-partition
            // PK="BackupJobs", RK={jobId}. Persists per-job state machine
            // (Queued / Running / Completed / Failed / Skipped / BlockedTerminal),
            // backupOutcome (Success / Partial) for Kind=Backup jobs, and lastHeartbeatUtc
            // for the watchdog (5min staleness threshold for Running, 60min for Queued).
            public const string BackupJobs = "BackupJobs";

            /// <summary>
            /// Returns all table names for initialization
            /// </summary>
            public static string[] All => new[]
            {
                Sessions,
                SessionsIndex,
                Events,
                AuditLogs,
                UsageMetrics,
                UserActivity,
                UserPresence,
                RuleResults,
                GatherRules,
                AnalyzeRules,
                ImeLogPatterns,
                RuleStates,
                AppInstallSummaries,
                PlatformStats,
                TenantConfiguration,
                AdminConfiguration,
                GlobalAdmins,
                TenantAdmins,
                McpUsers,
                DelegatedAdmins,
                TenantGroups,
                TenantGroupAssignments,
                PreviewWhitelist,
                PreviewConfig,
                Feedback,
                BlockedDevices,
                BlockedVersions,
                SessionReports,
                BootstrapSessions,
                GlobalNotifications,
                TenantNotifications,
                HardwareRejectionNotificationTracker,
                VulnerabilityCache,
                VulnerabilityReports,
                SoftwareInventory,
                SessionInventoryContributions,
                SlaTenantStatus,
                EventTypeIndex,
                DeviceSnapshot,
                CveIndex,
                DistressReports,
                UserUsageLog,
                OpsEvents,
                ImeVersionHistory,
                RuleStats,
                EventSessionIndex,
                SessionTombstones,
                Signals,
                DecisionTransitions,
                SessionsByTerminal,
                SessionsByStage,
                DeadEndsByReason,
                ClassifierVerdictsByIdLevel,
                SignalsByKind,
                OffboardingAudit,
                TenantOffboardingCustomsArchive,
                ScriptNameCache,
                BackupJobs,
            };
        }

        /// <summary>
        /// Canonical PartitionKey strings stored in <see cref="TableNames.OffboardingAudit"/>.
        /// All three patterns coexist in the same table; consumers must always read by
        /// (PartitionKey, RowKey) point-lookup, never by table scan.
        /// </summary>
        public static class OffboardingPartitionKeys
        {
            /// <summary>Active 403 sperrmarker. RowKey = normalized tenantId. 1 row per active offboarding.</summary>
            public const string Marker = "OffboardingMarker";

            /// <summary>Chronological audit history. RowKey = "{yyyyMMddHHmmssfff}_{normalizedTenantId}". N rows per tenant.</summary>
            public const string History = "OffboardingHistory";

            /// <summary>O(1) pointer index for re-onboarding lookup. RowKey = normalized tenantId. 1 row per tenant.</summary>
            public const string ByTenant = "OffboardingByTenant";
        }

        /// <summary>
        /// Azure Blob Storage container names. Single source of truth for container strings
        /// so that storage helpers, cascade workers, and offboarding workers all agree.
        /// </summary>
        public static class BlobContainers
        {
            /// <summary>
            /// Houses cascade-deletion manifests (snapshot + progress blobs) under prefix
            /// <c>{tenantId}/{sessionId}/{manifestId}.{snapshot.json.gz | progress.json}</c>.
            /// 30d lifecycle delete + 3d soft delete = ~33d effective retention.
            /// Phase 2.E of tenant offboarding wipes this container by <c>{tenantId}/</c> prefix.
            /// </summary>
            public const string DeletionManifests = "deletion-manifests";

            /// <summary>
            /// Houses tenant-offboarding state blobs that MUST survive the deletion-manifests wipe.
            /// Currently: per-tenant Expectations-Blob written at the first handler pickup
            /// (<c>{tenantId}/{historyRowKey}.expectations.json</c>). Phase 2.E intentionally
            /// skips this container; Phase 2.G deletes the blob explicitly as its last step,
            /// with <see cref="Functions.Maintenance.OffboardingMarkerCleanupFunction"/> as a
            /// defense-in-depth second-pass.
            /// </summary>
            public const string OffboardingState = "offboarding-state";

            /// <summary>
            /// Hosted diagnostics destination: tenant agents may upload their diagnostics
            /// package directly into the backend's own storage account when the tenant admin
            /// opts in via <see cref="Models.TenantConfiguration.DiagnosticsUploadDestination"/>
            /// = <c>"Hosted"</c>. Blobs are tenant-scoped via a <c>{tenantId}/</c> prefix
            /// (<c>{tenantId}/AgentDiagnostics-{sessionId}-{ts}.zip</c>) so per-tenant
            /// enumeration + retention deletion can iterate exactly one tenant without
            /// cross-tenant exposure. The opposite destination, <c>"CustomerSas"</c>, leaves
            /// the blob in the customer's own storage and never touches this container.
            /// </summary>
            public const string HostedDiagnostics = "diagnostics";

            /// <summary>
            /// Critical-table backup destination. Daily timer + manual GA trigger write
            /// per-table NDJSON dumps under <c>{backupId}/{tableName}.ndjson</c> plus a
            /// final <c>{backupId}/manifest.json</c>. Maintenance lease sentinel lives
            /// at <c>_lock/maintenance.lock</c> in the same container — coordinates
            /// backup + restore jobs across worker/timer/single-row paths.
            /// Lifecycle: 90-day delete on prefix <c>critical-table-backups/</c>; account
            /// settings (Soft-Delete + Versioning) provide defence-in-depth.
            /// </summary>
            public const string CriticalTableBackups = "critical-table-backups";
        }

        /// <summary>
        /// Azure Storage Queue names. Queues feed async fan-out / reconciliation paths
        /// that would otherwise block the hot ingest loop.
        /// </summary>
        public static class QueueNames
        {
            /// <summary>
            /// V2 Decision Engine index-table fan-out (Plan §2.8, §M5.d). One message per
            /// committed primary row (Signal or DecisionTransition); consumer writes the
            /// 0–3 applicable index rows. Eventual consistency; the 2h reconcile timer is
            /// the safety net on queue failures.
            /// </summary>
            public const string TelemetryIndexReconcile = "telemetry-index-reconcile";

            /// <summary>
            /// Auto-analyze fan-out at session end (enrollment_complete / enrollment_failed)
            /// and after async vulnerability correlation. Replaces the in-function
            /// fire-and-forget Task.Run that could be killed mid-flight by Functions
            /// scale-in. Consumer runs <c>RuleEngine.AnalyzeSessionAsync</c> and persists
            /// rule results. Manual "Analyze Now" remains the user-side fallback.
            /// </summary>
            public const string AnalyzeOnEnrollmentEnd = "analyze-on-enrollment-end";

            /// <summary>
            /// Vulnerability correlation fan-out triggered by the shutdown <c>software_inventory_analysis</c>
            /// event. Replaces the in-function fire-and-forget <c>Task.Run</c> in
            /// <c>EventIngestProcessor</c> that could be killed by Functions scale-in, leaving the
            /// session without a vulnerability report (manual "Analyze Now" was the only recovery).
            /// Consumer runs <c>VulnerabilityCorrelationService.CorrelateAsync</c>, stores the report,
            /// emits the synthetic <c>vulnerability_report</c> event, and enqueues a downstream
            /// re-analyze on <see cref="AnalyzeOnEnrollmentEnd"/> with
            /// <c>ReasonVulnerabilityCorrelated</c> so vulnerability-driven analyze rules can fire.
            /// </summary>
            public const string VulnerabilityCorrelate = "vulnerability-correlate";

            /// <summary>
            /// Cascade-delete worker queue (PR3+). Producer (admin click or maintenance retention
            /// fan-out) writes one envelope per session-to-delete after acquiring the
            /// <c>DeletionState=Preparing</c> CAS lock and uploading the snapshot blob. Worker
            /// (PR4) drains the queue, deletes by exact (PK, RK) per the manifest, and tombstones
            /// the Sessions row. Poison suffix <c>-poison</c>, max-dequeue 5.
            /// </summary>
            public const string SessionDeletion = "session-deletion";

            /// <summary>
            /// Tenant offboarding cascade-worker queue. Producer (TenantOffboardFunction) writes
            /// one envelope per tenant after inserting the History/Pointer/Marker rows in
            /// <see cref="TableNames.OffboardingAudit"/>. Worker (TenantOffboardingWorker)
            /// enumerates sessions, enqueues per-session cascades, drains against an
            /// Expectations-Blob in <see cref="BlobContainers.OffboardingState"/>, then safe-wipes
            /// all remaining tenant-scoped rows. Max-dequeue 5, paired with
            /// <see cref="TenantOffboardingPoison"/>.
            /// </summary>
            public const string TenantOffboarding = "tenant-offboarding";

            /// <summary>Poison sibling of <see cref="TenantOffboarding"/>.</summary>
            public const string TenantOffboardingPoison = "tenant-offboarding-poison";

            /// <summary>
            /// Critical-table backup job queue. Producer = HTTP trigger
            /// <c>/api/global/backups/trigger</c> (fail-hard); consumer =
            /// <c>CriticalTableBackupQueueWorker</c> (BackgroundService, BatchSize=1,
            /// VisibilityTimeout=60min with 25min PopReceipt-renewal). Carries a small
            /// envelope <c>{ jobId }</c> — full state lives in <see cref="TableNames.BackupJobs"/>.
            /// Poison-suffix <c>-poison</c>, max-dequeue 5; Failed-state is persisted on
            /// Poison-Move (NOT on first throw — that would defeat retry).
            /// </summary>
            public const string CriticalTableBackup = "critical-table-backup-jobs";

            /// <summary>Poison sibling of <see cref="CriticalTableBackup"/>.</summary>
            public const string CriticalTableBackupPoison = "critical-table-backup-jobs-poison";

            /// <summary>
            /// Manual-trigger queue for the session-deletion maintenance run. Producer = HTTP
            /// trigger <c>/api/global/session-deletions/maintenance/trigger</c> (fail-hard);
            /// consumer = <c>SessionDeletionMaintenanceQueueWorker</c> (BackgroundService,
            /// BatchSize=1, VisibilityTimeout=60min — the run budget is 50min plus cushion).
            /// Carries a tiny envelope <c>{ triggeredBy }</c>; concurrency against the 12h timer
            /// is serialized by the session-deletion maintenance blob lease, not the queue.
            /// Poison-suffix <c>-poison</c>, max-dequeue 5.
            /// </summary>
            public const string SessionDeletionMaintenance = "session-deletion-maintenance";

            /// <summary>Poison sibling of <see cref="SessionDeletionMaintenance"/>.</summary>
            public const string SessionDeletionMaintenancePoison = "session-deletion-maintenance-poison";
        }

        /// <summary>
        /// Critical tables that the daily backup feature snapshots. Order matters only for
        /// deterministic test output; the per-table loop is independent so a failure in
        /// one table does NOT block the others (Outcome=Partial covers that case).
        /// Four of these (<c>GlobalAdmins</c>, <c>TenantAdmins</c>, <c>McpUsers</c>,
        /// <c>DelegatedAdmins</c>) are gated to single-row restore in the API layer — full-table
        /// restore would reactivate disabled or re-create removed identities.
        /// <c>OffboardingAudit</c> additionally blocks replace-all (append-only audit).
        /// </summary>
        public static class CriticalBackupTables
        {
            public static readonly string[] All = new[]
            {
                TableNames.AdminConfiguration,
                TableNames.AnalyzeRules,
                TableNames.DelegatedAdmins,
                TableNames.Feedback,
                TableNames.GatherRules,
                TableNames.GlobalAdmins,
                TableNames.ImeLogPatterns,
                TableNames.ImeVersionHistory,
                TableNames.McpUsers,
                TableNames.OffboardingAudit,
                TableNames.PreviewConfig,
                TableNames.PreviewWhitelist,
                TableNames.RuleStates,
                TableNames.TenantAdmins,
                TableNames.TenantConfiguration,
                TableNames.TenantOffboardingCustomsArchive,
            };

            /// <summary>
            /// Full-table restore is API-blocked on these — the rows carry IsEnabled
            /// semantics whose accidental re-activation is a security incident.
            /// Single-row restore (with mandatory diff preview) remains allowed.
            /// </summary>
            public static readonly string[] AuthTablesFullRestoreForbidden = new[]
            {
                TableNames.GlobalAdmins,
                TableNames.TenantAdmins,
                TableNames.McpUsers,
                TableNames.DelegatedAdmins,
            };

            /// <summary>
            /// replace-all strategy is API-blocked on these — append-only audit semantics
            /// (orphan-deletion would erase audit history added after the backup).
            /// upsert-only remains allowed.
            /// </summary>
            public static readonly string[] AuditTablesReplaceAllForbidden = new[]
            {
                TableNames.OffboardingAudit,
            };
        }
    }
}

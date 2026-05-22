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
        public const string ApiBaseUrl = "https://autopilotmonitor-api.azurewebsites.net";

        // -----------------------------------------------------------------------
        // Agent self-update
        // -----------------------------------------------------------------------

        /// <summary>
        /// Base URL for agent binaries in Azure Blob Storage (public read)
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
            public const string IngestEvents             = "/api/agent/ingest";
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

            // V2 Agent generic telemetry transport (Plan §2.7a).
            // Sibling of IngestEvents under /api/agent/* — same Agent-writes namespace, heterogeneous
            // TelemetryItem[] payload (Event + Signal + DecisionTransition). Backend routes per
            // TelemetryItem.Kind to Events / Signals / DecisionTransitions tables. Replaces the
            // per-type IngestEvents path once the Legacy Agent is decommissioned (v11.1+).
            public const string IngestTelemetry          = "/api/agent/telemetry";

            // MCP/Agent API search endpoints
            public const string SearchSessions          = "/api/search/sessions";
            public const string SearchSessionsByEvent   = "/api/search/sessions-by-event";
            public const string SearchSessionsByCve     = "/api/search/sessions-by-cve";
            public const string MetricsSummary          = "/api/metrics/summary";
            // Bootstrap agent endpoints (cert-free, token-auth for pre-enrollment agents)
            public const string BootstrapRegisterSession = "/api/bootstrap/register-session";
            public const string BootstrapIngestEvents    = "/api/bootstrap/ingest";
            public const string BootstrapGetAgentConfig  = "/api/bootstrap/config";
            public const string BootstrapReportError     = "/api/bootstrap/error";

            // Pre-auth distress channel (no authentication required)
            public const string ReportDistress           = "/api/agent/distress";
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
            public const string ScriptStarted       = "script_started";
            public const string ScriptCompleted     = "script_completed";
            public const string ScriptFailed        = "script_failed";
            public const string EspProvisioningStatus = "esp_provisioning_status";
            public const string SoftwareInventoryAnalysis = "software_inventory_analysis";
            public const string VulnerabilityReport       = "vulnerability_report";
            public const string AgentVersionCheck         = "agent_version_check";
            public const string AgentStarted              = "agent_started";        // Lifecycle anchor — fired Seq=1 at agent boot. PR1: replaces hardcoded string-literals at emit sites.
            public const string AgentShuttingDown         = "agent_shutting_down";  // V2 single-rail plan §6.2 — terminate-hygiene acknowledgement emitted before CleanupService tears down
            public const string SystemRebootDetected      = "system_reboot_detected"; // Lifecycle anchor — fired when previousExitType=reboot_kill. PR1.
            public const string PerformanceCollectorStopped     = "performance_collector_stopped";    // Idle-stop anchor — fired after 15 min idle by PerformanceCollector. PR1.
            public const string AgentMetricsCollectorStopped    = "agent_metrics_collector_stopped";  // Idle-stop anchor — fired after 15 min idle by AgentMetricsCollector. PR1.
            public const string PriorRunDiedWithState           = "prior_run_died_with_state";        // Death-Rattle (Plan §B) — emitted on next run if previous exit was unclean. PR1.

            // IME log tracker — app / device / script telemetry (V2 single-rail plan §5.9)
            public const string EspPhaseChanged           = "esp_phase_changed";
            public const string ImeAgentVersion           = "ime_agent_version";
            public const string ImeUserSessionCompleted   = "ime_user_session_completed";
            public const string DoTelemetry               = "do_telemetry";
            public const string AllAppsCompleted          = "all_apps_completed";
            public const string AppTrackingSummary        = "app_tracking_summary";  // Plan §5 Fix 4b — terminal per-session app summary

            // Stall detection (Ebene 2 — StallProbeCollector)
            public const string StallProbeCheck           = "stall_probe_check";   // Trace heartbeat from Probe 2 (15 min) when no anomaly found
            public const string StallProbeResult          = "stall_probe_result";  // Warning when a probe found an anomaly
            public const string SessionStalled            = "session_stalled";     // Fire-once after Probe 4 (60 min) — triggers backend Stalled status

            // ModernDeployment EventLog Watcher (Ebene 1 — live capture)
            public const string ModernDeploymentLog       = "modern_deployment_log";      // Info-level live capture
            public const string ModernDeploymentWarning   = "modern_deployment_warning";  // Level 3 (Warning)
            public const string ModernDeploymentError     = "modern_deployment_error";    // Level 1-2 (Critical/Error)

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
            /// The Enrollment Status Page Apps-subcategory timed out while this app was still
            /// in an active install state. The agent cannot tell whether the install ultimately
            /// would have succeeded, failed, or hung — the ESP gave up first. UI renders these
            /// as "likely stuck" rather than confirmed failures.
            /// <para>
            /// Emitted only by the V2 EnrollmentTerminationHandler on the terminal-ESP-failure
            /// path. Apps with this failureType always carry <c>confidence: "presumed"</c> in
            /// their event payload.
            /// </para>
            /// </summary>
            public const string EspAppsTimeout = "esp_apps_timeout";
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
        }
    }
}

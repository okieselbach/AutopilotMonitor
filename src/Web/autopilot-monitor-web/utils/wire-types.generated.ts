// GENERATED from shared-manifests.json — do not edit by hand.
// Regenerate: node scripts/generate-shared-manifest-types.js
// (after AM_WRITE_SHARED_MANIFESTS=1 dotnet test --filter SharedManifestParityTests)
//
// Wire response types reflected from AutopilotMonitor.Shared (every IApiResponse
// implementer + [WireContract] type, transitively closed). Key ORDER, presence
// (optional = key absent under WhenWritingNull) and names mirror the C# wire exactly.


/** Response of POST delegations/accept. */
export interface AcceptDelegationInvitationResponse {
  homeTenantId: string;
  homeTenantDomain?: string;
  managedTenantId: string;
}

/** One currently active web user as listed by GET global/presence. */
export interface ActiveUserItem {
  tenantId: string;
  upn: string;
  userRole: string;
  lastSeen: string;
  /** Whole seconds since LastSeen, floored at 0. */
  secondsAgo: number;
}

/** Response of POST auth/global-admins (201): the created row. */
export interface AddGlobalAdminResponse {
  admin: GlobalAdminRow;
}

/** Response of POST vulnerability/ignored-software: how many entries were added. */
export interface AddIgnoredSoftwareResponse {
  success: boolean;
  added: number;
}

/** Response of POST global/mcp-users (201): the created whitelist row. */
export interface AddMcpUserResponse {
  user: McpUserEntry;
}

/** Global platform configuration managed by Global Admins Stored in Azure Table Storage with single instance PartitionKey = "GlobalConfig" RowKey = "config" */
export interface AdminConfiguration {
  /** Partition key (always "GlobalConfig") */
  partitionKey: string;
  /** Row key (always "config") */
  rowKey: string;
  /** When the configuration was last updated */
  lastUpdated: string;
  /** Updated by (Global Admin user email) */
  updatedBy: string;
  /** Global default rate limit: Maximum requests per minute per device This applies to all tenants unless they have a custom override Default: 100 */
  globalRateLimitRequestsPerMinute: number;
  /** Per-user rate limit for standard users (Tenant Admins, Operators, Viewers). Requests per minute keyed by UPN. Default: 120. */
  userRateLimitRequestsPerMinute: number;
  /** Per-user rate limit for Global Admins. Higher budget but not exempt. Default: 600. */
  globalAdminRateLimitRequestsPerMinute: number;
  /** JSON-serialized plan tier definitions mapping tier name to rate limits and features. Example: {"free":{"apiRateLimit":60},"pro":{"apiRateLimit":300},"enterprise":{"apiRateLimit":1000}} */
  planTierDefinitionsJson?: string;
  /** Container SAS URL used by maintenance to publish platform stats JSON files. Expected format: https://{account}.blob.core.windows.net/{container}?sv=...&sig=... */
  platformStatsBlobSasUrl: string;
  /** Idle timeout in minutes for periodic collectors (Performance, AgentSelfMetrics). When no real enrollment event (app install, ESP phase change, etc.) is detected within this window, collectors stop automatically to prevent session bloat. They restart automatically when new enrollment activity is detected. 0 = disabled (collectors run indefinitely). Default: 15 minutes. */
  collectorIdleTimeoutMinutes: number;
  /** DAD liveness threshold in minutes. When the V2 DesktopArrivalDetector polls for this duration without detecting either an excluded-user explorer.exe or a real user desktop, it emits a single desktop_detector_no_candidate observability event. State-change-only — NOT a periodic heartbeat; max 1 event per detector lifetime. Used to distinguish "user never logged in" from "detector wiring dead post-reboot" in sessions where desktop_arrived is missing. 0 = disabled (event never fires). Default: 10 minutes. */
  desktopDetectorNoCandidateTimeoutMinutes: number;
  /** Maintenance alarm threshold: sessions with more events than this value trigger an ExcessiveSessionEvents ops alert (dispatched to Telegram/Teams). 0 = disabled. Default: 2000 (largest real sessions observed are ~500). */
  excessiveEventCountThreshold: number;
  /** Auto-action mode for runaway sessions whose EventCount exceeds ExcessiveEventAutoActionThreshold. One of "Off", "Block", or "Kill". Off (default) keeps the warn-only behaviour; Block stops the device's uploads for ExcessiveEventAutoActionDurationHours; Kill issues a remote self-destruct signal. Action runs in the 2h maintenance batch, is idempotent per session via ExcessiveEventsAutoActioned, and never downgrades an existing Kill to Block. */
  excessiveEventAutoActionMode: string;
  /** Event-count threshold for auto-block/kill. Must be greater than ExcessiveEventCountThreshold so operators see the warn first. 0 disables regardless of ExcessiveEventAutoActionMode. Default: 2500. */
  excessiveEventAutoActionThreshold: number;
  /** Duration in hours for the device block created by the auto-action path. Default: 24. */
  excessiveEventAutoActionDurationHours: number;
  /** Retention period in days for operational events in the OpsEvents table. Events older than this are deleted by the periodic maintenance job. 0 = no cleanup (not recommended). Default: 90 days. */
  opsEventRetentionDays: number;
  /** Cooldown in hours between repeat SLA breach notifications for the same tenant + breach type. Persisted via SlaTenantStatus so it survives Function host recycles. Default: 24 hours (one notification per day). Range: 1-168. */
  slaNotificationCooldownHours: number;
  /** Global kill-switch for the in-app feedback prompt. When false, no user sees the feedback bubble regardless of other settings. Default: true. */
  feedbackEnabled: boolean;
  /** Minimum tenant age in days before users are prompted for feedback. Prevents asking brand-new tenants who haven't had meaningful experience yet. Default: 14 days. */
  feedbackMinTenantAgeDays: number;
  /** Cooldown in days after a user interacts with the feedback prompt before they are prompted again. 0 = never re-prompt (single wave only). Default: 60 days. */
  feedbackCooldownDays: number;
  /** JSON-serialized list of global diagnostics log paths/wildcards to include in the diagnostics ZIP package for all tenants. Each entry: { "path": "...", "description": "...", "isBuiltIn": true } */
  diagnosticsGlobalLogPathsJson: string;
  /** Maximum allowed diagnostics download size in MB. Blobs exceeding this are rejected before streaming (413). 0 = no limit. Default: 500 MB. */
  maxDiagnosticsDownloadSizeMB: number;
  /** Timeout in seconds for the entire diagnostics download+stream operation. 0 = no timeout. Default: 120 seconds. */
  diagnosticsDownloadTimeoutSeconds: number;
  /** Controls who can access the remote MCP server. "Disabled" = MCP off, "WhitelistOnly" = GlobalAdmins + McpUsers table (default), "AllMembers" = any authenticated user. */
  mcpAccessPolicy: string;
  /** NVD API key for higher rate limits (50 req/30s vs 5 req/30s without key). Free registration at https://nvd.nist.gov/developers/request-an-api-key null = operate without API key (slower, still functional). */
  nvdApiKey: string;
  /** JSON-serialized list of OpsAlertRule objects defining which event types trigger notifications. Provider-agnostic — rules apply to all enabled providers. */
  opsAlertRulesJson: string;
  /** Whether the Telegram alert provider is enabled. Default: false. */
  opsAlertTelegramEnabled: boolean;
  /** Telegram chat ID for ops alerts (e.g. ITEngineer channel). */
  opsAlertTelegramChatId: string;
  /** Whether the Teams alert provider is enabled. Default: false. */
  opsAlertTeamsEnabled: boolean;
  /** Teams Workflow webhook URL for ops alerts. */
  opsAlertTeamsWebhookUrl: string;
  /** Whether the Slack alert provider is enabled. Default: false. */
  opsAlertSlackEnabled: boolean;
  /** Slack Incoming Webhook URL for ops alerts. */
  opsAlertSlackWebhookUrl: string;
  /** JSON array of platform notification channels (NotificationChannel), the destinations an OpsAlertRule can target by id. Supersedes the three fixed provider slots above: each channel carries its own provider, destination and name, so an operator can keep e.g. an "IT Engineer" push channel and a "Sales" webhook apart and bind each event type to only the channels that should see it. Null/empty = not migrated yet — then synthesizes the list from the legacy slots, so dispatch behavior is unchanged until an operator saves the section once. */
  opsNotificationChannelsJson: string;
  /** Version string of the latest published V2 agent (e.g. "2.0.647"). */
  latestAgentV2Version: string;
  /** SHA-256 (lowercase hex) of the latest published V2 agent ZIP. */
  latestAgentV2Sha256: string;
  /** SHA-256 (lowercase hex) of the latest published V2 agent EXE. */
  latestAgentV2ExeSha256: string;
  /** Version string of the latest published V2 bootstrap script. */
  latestBootstrapV2ScriptVersion: string;
  /** When true, the agent's self-updater is allowed to install a version strictly lower than the one it is currently running. Default: false (forward-only updates; prevents dev builds from being silently downgraded via the runtime_hash_mismatch force path). Set to true only for controlled rollback scenarios — flip back to false immediately afterwards. Single global flag; applies to whichever line the calling agent runs on. */
  allowAgentDowngrade: boolean;
  /** Global migration target served to ALL agents as AgentConfigResponse.MigrateToApiBaseUrl (e.g. "https://autopilotmonitor-api-us.azurewebsites.net"). Empty = no global migration. Set this on the backend being ABANDONED; clear it once the migration window closes. Per-tenant overrides in AgentMigrateTenantOverridesJson win over this. */
  agentMigrateApiBaseUrl: string;
  /** JSON object mapping tenantId → migration target URL for per-tenant moves (e.g. one tenant relocating EU→US). An entry with an EMPTY string value pins that tenant to the current backend even while AgentMigrateApiBaseUrl is set (staged rollout). Example: {"11111111-...": "https://autopilotmonitor-api-us.azurewebsites.net", "22222222-...": ""} */
  agentMigrateTenantOverridesJson: string;
  /** JSON-serialized list of Windows ModernDeployment EventIDs that are considered harmless. Matching events (Level 2 Error or Level 3 Warning) are downgraded to Debug severity by the agent — they stay visible for troubleshooting but do not surface as Error/Warning in the session timeline and are ignored by the stall-probe anomaly scan. Level 1 Critical is never downgraded. Example: "[100, 1005, 1010]" */
  modernDeploymentHarmlessEventIdsJson: string;
  /** JSON-serialized list of ImeLogPattern IDs whose match is re-emitted by the V2 agent as a WhiteGloveSealingPatternDetected DecisionSignal (in addition to the normal ime_pattern_match event). Example: "[\"wg-seal-1\",\"wg-seal-2\"]". Default null/empty = feature off (M3-compatible, no regression risk). Only IDs in this list count as sealing signals; other IME pattern matches follow the regular event path. Global-only — no per-tenant override (plan §M5 M4.4.5.e decision). */
  whiteGloveSealingPatternIdsJson: string;
  /** Whether vulnerability correlation is globally enabled. When false, agents still collect inventory but backend skips correlation. Default: true */
  vulnerabilityCorrelationEnabled: boolean;
  /** Feature flag for V2 Decision Engine index-table dual-write (Plan §M5.d). When true, IngestTelemetryFunction enqueues telemetry-index-reconcile envelopes after committing each primary Signals / DecisionTransitions row; a queue-triggered handler (M5.d.3) then writes the 0–3 applicable index rows. Default: false — enables controlled rollout. The 2h reconcile timer (M5.d.4) is the safety-net for queue failures even once the flag is on. */
  enableIndexDualWrite: boolean;
  /** When true, new tenant signups are activated automatically ~1 minute after first sign-in (tenant-auto-approve queue worker). When false, every signup waits for a manual Global Admin approval in Tenant Management. Default false — the operator opts into auto-activation and can flip it off at any time (e.g. on abuse) to return to manual vetting; messages already in the queue are then dropped, not parked. Round-tripped via the 4-file web chain (memory feedback_admin_config_ui_roundtrip). */
  autoApproveNewTenants: boolean;
  /** When true, the first fleet-wide sighting of a new IME version enqueues an archive job (ime-msi-archive queue) that downloads the installer from the CSP-reported URL into the ime-archive blob container. Roughly one download per Microsoft IME release (~monthly). Default true — flip off to pause archiving (queued messages then wait, they are not dropped). Round-tripped via the 4-file web chain (memory feedback_admin_config_ui_roundtrip). */
  imeMsiArchivingEnabled: boolean;
  /** Size cap for the IME installer download in MB (Content-Length preflight AND streamed-byte guard). The MSI is ~12 MB today; the generous cap exists only so a tampered/wrong URL cannot stream gigabytes into the archive while still never missing a legitimately grown installer. 0 = no limit. Default: 250 MB. */
  maxImeMsiDownloadSizeMB: number;
  /** When true, the dual app-registration self-service homing flip is active: the Graph consent flow funnels legacy-homed tenants to the primary (new) app registration and auto-flips TenantConfiguration.HomedAppClientId once admin consent for the primary app is verified; tenant admins may also flip manually via POST config/{tenantId}/app-homing. When false (kill switch) all of the above stops immediately — consent URLs mint for the homed app again and only Global Admins can flip. Default false — the operator enables it after the dual-app config swap. Round-tripped via the 4-file web chain (memory feedback_admin_config_ui_roundtrip). */
  selfServiceAppHomingEnabled: boolean;
  /** Global emergency kill-switch for the cascade-deletion subsystem (Plan §1 P8 / §9). When true: cascade producers return 503 Service Unavailable;the cascade worker returns its message to the queue on entry without processing. Round-tripped via the 4-file web chain (memory feedback_admin_config_ui_roundtrip) so admin saves preserve it. Default false. */
  sessionDeletionKillSwitch: boolean;
  /** Last successful CISA KEV catalog sync timestamp (UTC ISO 8601). Updated by VulnerabilityDataSyncFunction (daily timer) and TriggerVulnerabilityDataSyncFunction (manual /api/vulnerability/sync). Pre-existing field — semantically means "last KEV sync" since KEV is the only live data refresh that ran via the manual endpoint historically. */
  vulnerabilityDataLastSyncUtc: string;
  /** Last successful MSRC CVRF index refresh timestamp (UTC ISO 8601). Updated by VulnerabilityDataSyncFunction (daily timer) and TriggerMsrcSyncFunction (manual /api/vulnerability/sync-msrc). Empty/null means MSRC has never refreshed successfully since this field was introduced. */
  msrcLastSyncUtc: string;
  /** Last COMPLETED refresh of the stale NVD CVE cache rows (UTC ISO 8601) — a run that walked every stale row without being cut short by an NVD throttle cooldown. Updated by VulnerabilityDataSyncFunction (daily timer) and the manual /api/vulnerability/sync-nvd. */
  nvdCacheLastRefreshUtc: string;
  /** Last successful FIRST EPSS re-score of the cached CVEs (UTC ISO 8601). Updated by VulnerabilityDataSyncFunction (daily timer) and the manual /api/vulnerability/sync-epss. */
  epssLastSyncUtc: string;
}

/** The immutable Entra identity behind a cross-tenant-role UPN: the HOME tenant the UPN was granted for and, once known, the user's object id. A role row (GlobalAdmins / DelegatedAdmins / TenantGroupAssignments) confers nothing unless the caller's validated JWT matches this binding. */
export interface AdminIdentityBinding {
  upn: string;
  /** The admin's home Entra tenant id (lowercase GUID) — must equal the JWT tid. */
  tenantId: string;
  /** The admin's Entra object id (lowercase GUID) — must equal the JWT oid. Empty ⇒ pinned on first sign-in. */
  objectId: string;
  boundBy: string;
  boundAt: string;
  /** When the object id was pinned (grant time, or the first matching sign-in). Null while unpinned. */
  objectIdPinnedAt?: string;
  readonly isObjectIdPinned: boolean;
}

/** Response of GET global/metrics/agent-efficiency (GetGlobalAgentEfficiency). */
export interface AgentEfficiencyMetricsResponse {
  windowDays: number;
  sessionLimit: number;
  /** Echo of the requested tenant filter; null = cross-tenant aggregate. */
  tenantId?: string;
  /** Sessions the scan covered — compare against SessionLimit for truncation. */
  sessionsScanned: number;
  sessionsWithSnapshots: number;
  byVersion: AgentVersionEfficiency[];
  overall?: AgentVersionEfficiency;
  computedAt: string;
  computeDurationMs: number;
  fromCache: boolean;
}

export interface AgentVersionEfficiency {
  /** Null on the cross-version "overall" bucket (omitted on the wire). */
  agentVersion?: string;
  sessionsScanned: number;
  sessionsWithSnapshots: number;
  spoolPressureSessions: number;
  avgCpuPercent?: PercentileStats;
  maxCpuPercent?: PercentileStats;
  maxWorkingSetMb?: PercentileStats;
  maxPrivateBytesMb?: PercentileStats;
  maxThreadCount?: PercentileStats;
  maxHandleCount?: PercentileStats;
  maxSpoolDepth?: PercentileStats;
  maxSpoolFileBytes?: PercentileStats;
  apiLatencyMs?: PercentileStats;
  apiRequestCount?: PercentileStats;
  crashRate?: CrashRateMetrics;
  topOffenders?: EfficiencyOffender[];
}

/** Defines how to analyze collected events to detect issues Analyze rules run server-side during event ingestion */
export interface AnalyzeRule {
  /** Unique rule identifier (e.g., "ANALYZE-NET-001") */
  ruleId: string;
  /** Human-readable rule title (e.g., "Proxy Authentication Required") */
  title: string;
  /** Detailed description of what this rule detects */
  description: string;
  /** Severity level: "info", "warning", "high", "critical" */
  severity: string;
  /** Rule category: network, identity, enrollment, apps, esp, device */
  category: string;
  /** Semantic version of this rule (e.g., "1.0.0") */
  version: string;
  /** Author of this rule */
  author: string;
  /** Whether this rule is currently enabled for the tenant */
  enabled: boolean;
  /** Whether this is a built-in rule (shipped with the system) */
  isBuiltIn: boolean;
  /** Whether this is a community-contributed rule Community rules behave like built-in rules (read-only, state stored separately) but are displayed with a distinct "Community" badge in the portal */
  isCommunity: boolean;
  /** Where this global rule row came from — see RuleProvenance. Drives the self-maintaining sunset: "embedded"/null = owned by the deployed binary's catalog (may be sunset when it leaves that catalog); "github" = reseeded from GitHub ahead of the binary (exempt from the embedded catalog sunset/filter). Null on pre-existing rows = embedded. */
  provenance?: string;
  /** Rule trigger type: "single" (matches individual events) or "correlation" (combines multiple event types) Both types run at the same time during analysis - this field is organizational/descriptive */
  trigger: string;
  /** When this rule is evaluated. Null/empty = ["enrollment_end"] — the historical terminal-only behavior. Additional interim triggers let a rule fire before the session is terminal: "whiteglove_sealed" (first genuine whiteglove_complete seal) and "on_event:<eventType>" (an ingest batch contained that event type). Interim runs suppress the KO path and record no stats — see AnalyzeRuleTriggers and docs/rules/analyze-rule-triggers.md. */
  evaluateOn?: string[];
  /** Optional device-fact gates evaluated BEFORE conditions. ALL preconditions must pass; if any fails the rule is silently skipped — no result, no UI card. Used to filter out hardware/OS profiles where a rule does not apply (e.g. "skip on virtual machines"). */
  preconditions: RulePrecondition[];
  /** Conditions that must be evaluated against the event stream All required conditions must match for the rule to fire */
  conditions: RuleCondition[];
  /** Base confidence score (0-100) when the rule's required conditions match Additional confidence is added from ConfidenceFactors */
  baseConfidence: number;
  /** Additional factors that increase confidence when matched */
  confidenceFactors: ConfidenceFactor[];
  /** Minimum confidence score (0-100) to create a RuleResult Default: 40 */
  confidenceThreshold: number;
  /** Detailed explanation of the detected issue Supports markdown formatting */
  explanation: string;
  /** Steps to remediate the detected issue */
  remediation: RemediationStep[];
  /** Links to relevant documentation */
  relatedDocs: RelatedDoc[];
  /** Template variables that must be customized per-tenant before the rule can be used. If non-empty, the rule is a template: enabling it creates a tenant custom copy with the user's values substituted into the conditions. */
  templateVariables: TemplateVariable[];
  /** If this custom rule was created from a template, stores the original template rule's ID. Used to track lineage and prevent duplicate copies. */
  derivedFromTemplateRuleId?: string;
  /** Rule-definition default for whether firing this rule should mark the entire session as failed. Shipped in the rule JSON. A tenant can override this via MarkSessionAsFailed in their RuleState — a firing rule is considered a "KO criterion" for the enrollment when the effective value (override ?? default) is true. */
  markSessionAsFailedDefault: boolean;
  /** Tenant-scoped override for MarkSessionAsFailedDefault. Not persisted in the rule JSON — populated at load time from the RuleStates table. Null means the tenant has not expressed a preference (fall back to the default). */
  markSessionAsFailed?: boolean;
  /** Rule-definition default for whether newly detected findings of this rule send an outbound notification. Off for all shipped rules — notification targets are tenant-specific channel ids, so notify only becomes actionable through the tenant override + channel selection. */
  notifyDefault: boolean;
  /** Tenant-scoped override for NotifyDefault. Not persisted in the rule JSON — populated at load time from the RuleStates table. Null = no preference (use the default). */
  notify?: boolean;
  /** Tenant-scoped notification targets: ids of the tenant's notification channels (TenantConfiguration.NotificationChannelsJson) that receive an alert when this rule fires. Populated at load time from the RuleStates table alongside Notify. Effective notify requires both the flag and at least one resolvable channel id. */
  notifyChannelIds?: string[];
  /** Tags for filtering and categorization */
  tags: string[];
  /** When this rule was created */
  createdAt: string;
  /** When this rule was last updated */
  updatedAt: string;
}

/** Analyze-rule listing envelope shared by GetAnalyzeRules (tenant-scoped) and GetGlobalAnalyzeRules (Global Admin, ?tenantId= scoped). */
export interface AnalyzeRuleListResponse {
  success: boolean;
  rules: AnalyzeRule[];
}

/** One of the top 5 failure codes of the analytics window. */
export interface AppAnalyticsFailureCode {
  code: string;
  /** First observed exit code; absent when none of the rows carried one. */
  exitCode?: number;
  count: number;
  sampleMessage: string;
}

/** Response of GET apps/{appName}/analytics and its global variant. */
export interface AppAnalyticsResponse {
  success: boolean;
  appName: string;
  appType: string;
  windowDays: number;
  collisionExcluded: number;
  /** "day" (windows up to 30 days) or "week". */
  bucket: string;
  summary: AppAnalyticsSummary;
  timeSeries: AppAnalyticsTimeBucket[];
  versionBreakdown: AppVersionBreakdownItem[];
  installerPhaseBreakdown: AppInstallerPhaseCount[];
  topFailureCodes: AppAnalyticsFailureCode[];
  /** Succeeded installs whose detection re-check reported NotDetected. */
  detectionLiesCount: number;
  deviceModelBreakdown: AppDeviceModelBreakdownItem[];
  /** Active duration-regression episodes for this app (tracker rows). */
  versionRegressions: AppVersionRegressionAlert[];
}

/** Headline aggregate of one app's analytics window. */
export interface AppAnalyticsSummary {
  totalInstalls: number;
  succeeded: number;
  skipped: number;
  unmeasured: number;
  failed: number;
  failureRate: number;
  avgDurationSeconds: number;
  p95DurationSeconds: number;
  avgDownloadBytes: number;
  /** "improving" | "worsening" | "stable". */
  trend: string;
  /** Absent when either window half has under 5 finished installs. */
  trendDelta?: number;
  /** Share of installs with AttemptNumber > 1 (0-1, three decimals). */
  flakinessScore: number;
}

/** One day/week bucket of the analytics time series. */
export interface AppAnalyticsTimeBucket {
  bucketStart: string;
  installs: number;
  succeeded: number;
  failed: number;
  failureRate: number;
  avgDurationSeconds: number;
}

/** Per device-model failure aggregate (lift vs the app's baseline rate, descending). */
export interface AppDeviceModelBreakdownItem {
  manufacturer: string;
  model: string;
  installs: number;
  failed: number;
  failureRate: number;
  liftVsBaseline: number;
}

/** One failure code with its occurrence count. */
export interface AppFailureCodeCount {
  code: string;
  count: number;
}

/** Consent-probe verdict embedded in app-homing responses (success and deny alike). Built by AppHomingFunction.ProbePayload. */
export interface AppHomingProbeWire {
  /** False when the decision needed no probe (e.g. GA force flip). */
  attempted: boolean;
  succeeded: boolean;
  isTransient: boolean;
  /** Legacy add-on Graph roles the primary app lacks (probe refused, not transient), or null — the key is omitted when null. The admin grants exactly these on the primary app. */
  missingRoles?: string[];
}

/** App install SLA snapshot. */
export interface AppInstallSlaSnapshot {
  totalInstalls: number;
  succeeded: number;
  failed: number;
  successRate: number;
  targetMet: boolean;
  /** Top failing apps by failure count. */
  topFailingApps: TopFailingApp[];
}

/** Failed installs per installer phase (descending). */
export interface AppInstallerPhaseCount {
  phase: string;
  failed: number;
}

/** One app's aggregate across its install rows in the window. */
export interface AppMetricsAppGroup {
  appName: string;
  totalInstalls: number;
  succeeded: number;
  skipped: number;
  unmeasured: number;
  failed: number;
  /** Failed / (failed + succeeded) as a percentage; skips never count as attempts. */
  failureRate: number;
  /** Average measured FINAL-attempt duration (whole seconds); 0 with no measured installs. */
  avgDurationSeconds: number;
  maxDurationSeconds: number;
  measuredInstalls: number;
  avgDownloadBytes: number;
  doTotalBytesDownloaded: number;
  doBytesFromPeers: number;
  doBytesFromCacheServer: number;
  doBytesFromHttp: number;
  peerOffloadPercent: number;
  /** Top 3 failure codes by count, descending. */
  topFailureCodes: AppFailureCodeCount[];
}

/** Fleet-wide Delivery Optimization rollup across every install row in the window. */
export interface AppMetricsDeliveryOptimization {
  totalBytesDownloaded: number;
  fromPeers: number;
  fromCacheServer: number;
  fromHttp: number;
  /** Share of bytes not pulled from the CDN (peers + Microsoft Connected Cache), 0-100 one decimal. */
  peerOffloadPercent: number;
}

/** Response of GET metrics/app and GET global/metrics/app: per-app install health over the requested window (slowest apps by average FINAL-attempt duration, top failing apps) plus the fleet Delivery Optimization rollup. */
export interface AppMetricsResponse {
  success: boolean;
  totalApps: number;
  totalInstalls: number;
  totalSkipped: number;
  totalUnmeasured: number;
  /** Rows excluded from per-app groups because their name-keyed row merged distinct appIds. */
  totalCollisionExcluded: number;
  slowestApps: AppMetricsAppGroup[];
  topFailingApps: AppMetricsAppGroup[];
  deliveryOptimization: AppMetricsDeliveryOptimization;
}

export interface AppScriptMetrics {
  avgAppsPerSession: number;
  totalUniqueApps: number;
  avgPlatformScriptsPerSession: number;
  avgRemediationScriptsPerSession: number;
  totalPlatformScripts: number;
  totalRemediationScripts: number;
}

/** One install row of the app-sessions drilldown (failed first, then in-progress, newest first). */
export interface AppSessionItem {
  sessionId: string;
  tenantId: string;
  deviceName: string;
  manufacturer: string;
  model: string;
  appVersion: string;
  status: string;
  installerPhase: string;
  failureCode: string;
  /** Absent when the install carried no exit code. */
  exitCode?: number;
  attemptNumber: number;
  startedAt: string;
  durationSeconds: number;
  /** 2+ = the IME processed this app in multiple passes (device-ESP evaluation + real install). */
  installPassCount: number;
}

/** Response of GET apps/{appName}/sessions and its global variant (offset-paged). */
export interface AppSessionsResponse {
  success: boolean;
  total: number;
  offset: number;
  limit: number;
  items: AppSessionItem[];
}

/** Per-app-version aggregate (installs descending). */
export interface AppVersionBreakdownItem {
  appVersion: string;
  installs: number;
  failed: number;
  failureRate: number;
  measuredInstalls: number;
  medianDurationSeconds: number;
  p95DurationSeconds: number;
}

/** One ACTIVE app-version duration regression: an app whose newest version's median install duration rose ≥2× (and ≥5 minutes absolute) over the previous version's median, both sides with enough measured installs. Persisted as the appversionregression|{app}|{version} keyspace of the notification tracker table — the row IS the dedup (one bell per episode), and the versionRegressions[] payload on the app-analytics response. Deleted when the episode re-arms (median falls back under 1.5× or the version drains out of the horizon) or by the tracker's 30-day retention sweep. Numbers refresh on every radar pass while the episode stays active; FirstNotifiedAt never moves. */
export interface AppVersionRegressionAlert {
  tenantId: string;
  appName: string;
  /** The regressed (newer) version — episode key together with the app. */
  currentVersion: string;
  /** The comparison version: latest first-seen strictly before the current version's first-seen. */
  previousVersion: string;
  currentMedianSeconds: number;
  previousMedianSeconds: number;
  /** Measured installs backing each median (gate: both ≥10). */
  currentMeasuredCount: number;
  previousMeasuredCount: number;
  /** Current median ÷ previous median (gate: ≥2.0; previous median is never 0 — measured durations are ≥1s). */
  lift: number;
  /** When the episode first fired (bell + ops event moment). Never moves on refresh; drives the 30d retention re-arm. */
  firstNotifiedAt: string;
  /** Last radar pass that re-confirmed/refreshed this episode. */
  lastEvaluatedAt: string;
}

/** One app row of the apps list (failed desc, then failure rate, then name). */
export interface AppsListItem {
  appName: string;
  appType: string;
  totalInstalls: number;
  succeeded: number;
  skipped: number;
  unmeasured: number;
  failed: number;
  failureRate: number;
  avgDurationSeconds: number;
  maxDurationSeconds: number;
  avgDownloadBytes: number;
  /** "improving" | "worsening" | "stable". */
  trend: string;
  /** Failure-rate delta between window halves; absent when either half has under 5 finished installs. */
  trendDelta?: number;
  lastSeenAt: string;
}

/** Response of GET apps/list and GET global/apps/list. Legacy mode (no pageSize) returns the full array with the paging keys absent; opt-in pagination adds count/offset/pageSize and a nextLink while more pages remain. */
export interface AppsListResponse {
  success: boolean;
  totalApps: number;
  totalInstalls: number;
  /** Rows excluded because their name-keyed row merged distinct appIds. */
  collisionExcluded: number;
  windowDays: number;
  /** Rows on this page; absent in legacy full-array mode. */
  count?: number;
  /** Absent in legacy full-array mode. */
  offset?: number;
  /** Absent in legacy full-array mode. */
  pageSize?: number;
  apps: AppsListItem[];
  /** Next-page link; absent in legacy mode and on the last page. */
  nextLink?: string;
}

export interface AuditLogEntry {
  id: string;
  tenantId: string;
  action: string;
  entityType: string;
  entityId: string;
  performedBy: string;
  timestamp: string;
  details: string;
}

/** Shared response of the audit-log listing endpoints (GET audit/logs and GET global/audit/logs), paged and non-paged: the non-paged variant leaves NextLink null so the key is absent, exactly like the old literal. */
export interface AuditLogListResponse {
  success: boolean;
  count: number;
  logs: AuditLogEntry[];
  /** Absolute-path link to the next page, or null on the last page / non-paged variant — the key is omitted when null. */
  nextLink?: string;
}

/** Success body of GET auth/me: the caller's resolved identity, roles and effective entitlement flags. Blocked outcomes (TenantSuspended / PendingActivation) are error shapes and stay anonymous by design. */
export interface AuthMeResponse {
  tenantId: string;
  upn: string;
  displayName: string;
  objectId: string;
  isGlobalAdmin: boolean;
  isGlobalReader: boolean;
  isTenantAdmin: boolean;
  /** True when the caller holds delegated ("MSP") assignments to other tenants. */
  isDelegated: boolean;
  /** The OTHER tenants this caller may manage; empty for non-delegated callers. */
  delegatedTenantIds: string[];
  /** Tenant role (Admin / Operator / Viewer); the key is omitted for a roleless caller. */
  role?: string;
  canManageBootstrapTokens: boolean;
  hasMcpAccess: boolean;
  /** "primary" or "legacy" — which app registration this tenant is homed on. */
  homedApp: string;
  bootstrapTokenEnabled: boolean;
  unrestrictedModeEnabled: boolean;
}

/** Response of POST vulnerability/cpe-mapping/auto-resolve: per-item outcomes plus totals. */
export interface AutoResolveCpeMappingResponse {
  resolved: AutoResolveResultItem[];
  failed: AutoResolveFailedItem[];
  totalProcessed: number;
  totalResolved: number;
  totalFailed: number;
}

/** One software title the bulk auto-resolver could not map, with a reason code. */
export interface AutoResolveFailedItem {
  softwareName: string;
  /** empty_name | empty_keyword | nvd_throttled | nvd_unavailable | no_match | no_results | nvd_api_error. */
  reason: string;
}

/** One software title the bulk auto-resolver mapped to a CPE. */
export interface AutoResolveResultItem {
  softwareName: string;
  cpeUri: string;
  /** Match score of the selected CPE candidate, rounded to two decimals. */
  confidence: number;
}

/** Response of GET config/{tenantId}/autopilot-device-validation/access-check. */
export interface AutopilotAccessCheckResponse {
  accessPresent: boolean;
  /** True when the probe was inconclusive (timeout / Graph 5xx) — treat as unknown, not "absent". */
  isTransient: boolean;
  requiredPermission: string;
  homingFlipped: boolean;
  /** True when the homing auto-flip was deferred (consent still propagating / transient) — a retry converges. */
  appHomingPending: boolean;
  /** Legacy add-on Graph roles the primary app lacks, blocking the homing flip until granted on the primary app; null when nothing blocks — the key is omitted when null. */
  appHomingMissingRoles?: string[];
}

/** Response of GET config/{tenantId}/autopilot-device-validation/consent-status. */
export interface AutopilotConsentStatusResponse {
  isConsented: boolean;
  /** Human-readable detail from the consent probe, or null — the key is omitted when null. */
  message?: string;
  homingFlipped: boolean;
  /** True when the homing auto-flip was deferred (consent still propagating / transient) — a retry converges. */
  appHomingPending: boolean;
  /** Legacy add-on Graph roles the primary app lacks, blocking the homing flip until granted on the primary app; null when nothing blocks — the key is omitted when null. */
  appHomingMissingRoles?: string[];
}

/** Response of GET config/{tenantId}/autopilot-device-validation/consent-url. */
export interface AutopilotConsentUrlResponse {
  consentUrl: string;
  /** True when the self-service app-homing funnel targeted the primary app registration. */
  willAutoFlipHoming: boolean;
}

/** Response of POST maintenance/backfill-occurred-utc: the backfill run report plus trigger attribution. Key layout is identical to ReclassifyJobRunResponse — the two one-shot job triggers share one wire shape, split into two classes only for the typed slot. */
export interface BackfillJobRunResponse {
  success: boolean;
  result: BackfillResult;
  triggeredBy: string;
  triggeredAt: string;
}

/** Run report of the one-shot OccurredUtc backfill (POST maintenance/backfill-occurred-utc). */
export interface BackfillResult {
  table: string;
  dryRun: boolean;
  rowsExamined: number;
  wouldWrite: number;
  written: number;
  skippedAlreadySet: number;
  skippedUndecodable: number;
  errors: number;
  nextContinuation?: string;
  samples: BackfillSample[];
}

/** One sampled row of a backfill run. PartitionKey/RowKey are PAYLOAD here — they identify the sampled storage row in a Global-Admin-only run report, not ITableEntity metadata. */
export interface BackfillSample {
  partitionKey: string;
  rowKey: string;
  decodedUtc: string;
}

/** Response of POST devices/block: block/kill acknowledgement. */
export interface BlockDeviceResponse {
  success: boolean;
  message: string;
  unblockAt: string;
  /** "Block" or "Kill" (normalized casing). */
  action: string;
}

/** Response of POST versions/block: block/kill rule acknowledgement. */
export interface BlockVersionResponse {
  success: boolean;
  message: string;
  versionPattern: string;
  /** "Block" or "Kill" (normalized casing). */
  action: string;
}

export interface BlockedDeviceEntry {
  tenantId: string;
  serialNumber: string;
  blockedAt: string;
  unblockAt?: string;
  blockedByEmail?: string;
  durationHours: number;
  reason?: string;
  action: string;
  /** Comma-separated session IDs that triggered this block (maintenance auto-block). Null = whole-device block (manual or legacy). When set, only these specific sessions are blocked; a new session on the same device will auto-unblock. */
  blockedSessionIds?: string;
}

/** Shared response of the blocked-device listings (GET devices/blocked and GET global/devices/blocked): the active blocks in scope. */
export interface BlockedDeviceListResponse {
  success: boolean;
  blocked: BlockedDeviceEntry[];
}

export interface BlockedVersionEntry {
  versionPattern: string;
  action: string;
  createdByEmail: string;
  createdAt: string;
  reason?: string;
}

/** Response of GET versions/blocked: every active version block rule. */
export interface BlockedVersionListResponse {
  success: boolean;
  rules: BlockedVersionEntry[];
}

/** Install interval of one ESP-blocking app, measured from EVENT timestamps (first started/download event → last terminal event; source-data audit Q1 forbids the agent payload timing, which freezes at the first terminal state across IME retries). */
export interface BlockingAppInterval {
  appId: string;
  appName: string;
  startUtc: string;
  endUtc: string;
  seconds: number;
}

/** Body of GET auth/mcp (200 when allowed, 403 when denied — same shape on both). The four platform/delegated keys are emitted ONLY when the caller actually holds the tier (null ⇒ key omitted): ordinary tenant users must not learn a platform tier exists, and IsGlobalAdmin is only ever emitted as literal true. */
export interface CheckMcpAccessResponse {
  allowed: boolean;
  upn: string;
  accessGrant: string;
  reason: string;
  /** Back-compat / write-tier hint for the MCP access-guard; true or omitted, never false. */
  isGlobalAdmin?: boolean;
  /** "GlobalAdmin" | "GlobalReader"; omitted without a platform role. */
  globalRole?: string;
  /** Managed tenant ids (lowercase) of a delegated (MSP) caller; omitted otherwise. */
  delegatedTenantIds?: string[];
  /** "DelegatedAdmin" | "DelegatedReader"; omitted without delegated scope. */
  delegatedRole?: string;
}

/** A factor that increases confidence when matched */
export interface ConfidenceFactor {
  /** Descriptive name for this factor */
  signal: string;
  /** Condition expression (e.g., "count >= 5", "exists", "duration > 300") */
  condition: string;
  /** Confidence weight to add when this factor matches (0-100) Total confidence = BaseConfidence + sum of matched factor weights, capped at 100 */
  weight: number;
}

/** One CPE mapping row (seed, custom, or community) as browsed by the Admin UI. */
export interface CpeMappingItem {
  normalizedVendor: string;
  normalizedProduct: string;
  cpeVendor: string;
  cpeProduct: string;
  cpeUri: string;
  category: string;
  displayNamePatterns: string[];
  publisherPatterns: string[];
  excludePatterns: string[];
  /** seed | custom | community, derived from the partition (falls back to the row's Source column). */
  source: string;
  /** Preformatted ISO-8601 creation/import timestamp; empty when the row carries neither. */
  createdAt: string;
}

export interface CrashExceptionSummary {
  exceptionType: string;
  count: number;
}

export interface CrashRateMetrics {
  totalStarts: number;
  cleanExits: number;
  exceptionCrashes: number;
  hardKills: number;
  rebootKills: number;
  firstRuns: number;
  crashRatePercent: number;
  topExceptions: CrashExceptionSummary[];
}

/** Response of POST rules/analyze/{ruleId}/create-from-template: the newly created custom rule instantiated from the template. */
export interface CreateAnalyzeRuleFromTemplateResponse {
  success: boolean;
  rule: AnalyzeRule;
  message: string;
}

/** Response of POST delegations/invitations: the token is shown ONCE (the link is copy-only). */
export interface CreateDelegationInvitationResponse {
  invitationId: string;
  token: string;
  expiresUtc: string;
}

/** Response of POST global/tenant-groups: the created group's id and (trimmed) name. */
export interface CreateTenantGroupResponse {
  groupId: string;
  name: string;
}

/** Response of DELETE global/customs-archive/{tenantId}/{historyRowKey}: bulk-delete acknowledgement. */
export interface CustomsArchiveDeleteRunResponse {
  success: boolean;
  /** Number of archive rows removed. */
  deleted: number;
}

/** Response of GET global/customs-archive/{tenantId}/{historyRowKey}: the run's entries. */
export interface CustomsArchiveEntryListResponse {
  success: boolean;
  count: number;
  entries: CustomsArchiveEntrySummary[];
}

/** Response of GET global/customs-archive/{tenantId}/{historyRowKey}/{archiveRowKey}: one full entry. */
export interface CustomsArchiveEntryResponse {
  success: boolean;
  entry: TenantOffboardingCustomsArchiveEntry;
}

/** One archived entry of a customs-archive run with a truncated EntityJson preview, as listed by GET global/customs-archive/{tenantId}/{historyRowKey}. */
export interface CustomsArchiveEntrySummary {
  partitionKey: string;
  rowKey: string;
  originalTable: string;
  originalRowKey: string;
  archivedAt: string;
  /** First 200 characters of the archived EntityJson. */
  entityJsonPreview: string;
}

/** Response of GET global/customs-archive: every archive run, newest first. */
export interface CustomsArchiveRunListResponse {
  success: boolean;
  count: number;
  runs: CustomsArchiveRunSummary[];
}

/** One customs-archive run (one (tenantId, historyRowKey) partition) with per-source-table row counts, as listed by GET global/customs-archive. */
export interface CustomsArchiveRunSummary {
  partitionKey: string;
  tenantId: string;
  historyRowKey: string;
  /** Earliest ArchivedAt across the run's rows. */
  archivedAt: string;
  gatherRulesCount: number;
  analyzeRulesCount: number;
  imeLogPatternsCount: number;
}

/** Fleet vulnerability exposure summary, aggregated from CveIndex rows. Serialized directly as the response body of GET metrics/vulnerability and GET global/metrics/vulnerability; consumed by the MCP get_vulnerability_summary tool - key names are part of that contract. */
export interface CveExposureSummary {
  windowDays: number;
  totalAffectedSessions: number;
  /** Distinct affected tenants; null (key omitted) on tenant-scoped reads. */
  totalAffectedTenants?: number;
  distinctCves: number;
  kevCves: number;
  severityBreakdown: SeverityBreakdown;
  topCves: TopCve[];
  truncated: boolean;
}

/** One reducer step. Taken false = dead-end edge (guard blocked the transition) so the Inspector can render the blocked path in a different style. */
export interface DecisionGraphEdge {
  stepIndex: number;
  fromStage: string;
  toStage: string;
  trigger: string;
  taken: boolean;
  deadEndReason?: string;
  signalOrdinalRef: number;
  occurredAtUtc: string;
  classifierVerdictId?: string;
  classifierHypothesisLevel?: string;
}

/** One stage the session visited. Identified by the !:SessionStage enum name so JSON payloads stay forward-compatible with new stages (string, not int ordinal). */
export interface DecisionGraphNode {
  /** Stage enum name (e.g. "EspInProgress"). Also used as the graph-node ID. */
  id: string;
  isTerminal: boolean;
  /** Outcome label derived from the terminal Id — "Succeeded", "Failed", "PausedForPart2", or null for non-terminal nodes. The richer termination metadata (TerminationReason + TerminationOutcome from the enrollment_terminated event in M4.6.β) is not inlined here — a future revision can enrich terminal nodes by joining the Events table if the UI needs it. Today's Inspector gets the high-level label. */
  terminalOutcome?: string;
  /** Number of edges that target this node (for UI sizing / heat-map rendering). */
  visitCount: number;
}

/** Server-side projection of a session's DecisionTransitionRecords into a renderable DAG for the Inspector (Plan §M5, §M6). Pre-computed on the backend so the UI receives one structured shape instead of rebuilding the graph from the raw journal. */
export interface DecisionGraphProjection {
  tenantId: string;
  sessionId: string;
  /** Unique stages reached in the session (de-duplicated from Transition From/To stages). */
  nodes: DecisionGraphNode[];
  /** One entry per transition — edges preserve chronological order via StepIndex. */
  edges: DecisionGraphEdge[];
  /** Plan §2.10 — lets the UI flag sessions running on an older ReducerVersion than current. */
  reducerVersion: string;
}

/** One delegated-admin assignment: UPN X may access tenant Y at role Role. The "scoped global" tier (subset of tenants) between a single-tenant member and a platform GlobalAdmin. Surfaced as "MSP mode". */
export interface DelegatedAdminEntry {
  upn: string;
  tenantId: string;
  /** Constants.DelegatedRoles: "DelegatedReader" (default) or "DelegatedAdmin". */
  role: string;
  isEnabled: boolean;
  /** Constants.DelegatedStatus: "Active" / "PendingApproval" / "Revoked". Only Active confers scope. */
  status: string;
  /** Constants.DelegatedSource: "OperatorGranted" / "CustomerDelegated". */
  source: string;
  grantedAt: string;
  grantedBy: string;
}

/** Response of POST global/delegated-admins: the granted (created/replaced) assignment. */
export interface DelegatedAdminGrantResponse {
  assignment: DelegatedAdminEntry;
}

/** Response of GET global/delegated-admins: every delegated assignment. */
export interface DelegatedAdminListResponse {
  assignments: DelegatedAdminEntry[];
}

/** One release hold nested in DelegatedSlotUsageResponse. */
export interface DelegatedSlotHold {
  invitationId: string;
  /** The managed tenant that was removed; absent when unknown. */
  tenantId?: string;
  holdUntilUtc: string;
  releasedBy: string;
}

/** 409 body when a delegated-admin mutation (grant, group assign, add tenant to group, self-service invitation or accept) would push a managing (MSP) tenant over its delegated tenant slot limit. error comes first so generic error rendering keeps working; code lets the GA UI offer the "raise the limit and retry" flow. */
export interface DelegatedSlotLimitReachedResponse {
  error: string;
  code: string;
  /** The managing (home) tenant whose slots are exhausted. */
  homeTenantId: string;
  /** Its display name; absent when the config row carries none. */
  homeTenantDomain?: string;
  /** Slots in use (distinct managed tenants + pending invitations + release holds). */
  used: number;
  /** The effective limit (plan entitlement or the Global Admin override). */
  limit: number;
  /** New slots the rejected mutation needed. */
  required: number;
}

/** Response of GET global/delegated-slots/{tenantId} and GET delegations/slots: a managing tenant's slot usage. */
export interface DelegatedSlotUsageResponse {
  homeTenantId: string;
  /** Effective limit (override when set, else the plan entitlement). */
  limit: number;
  /** The plan entitlement (Community 0, Pro 2). */
  catalogLimit: number;
  /** The Global Admin override; absent when the catalog value applies. */
  overrideLimit?: number;
  used: number;
  /** Distinct managed tenant ids (lowercase) reachable by users homed in this tenant. */
  managedTenantIds: string[];
  /** Pending self-service invitations (each holds a slot until accepted, cancelled or expired). */
  pendingInvitations: number;
  /** Release holds: slots freed by a removal that stay occupied for 24 hours. */
  holds: DelegatedSlotHold[];
}

/** Response of GET delegations/accept?token=: what accepting would do — no mutation. */
export interface DelegationAcceptPreviewResponse {
  /** The inviting (managing) tenant. */
  homeTenantId: string;
  homeTenantDomain?: string;
  expiresUtc: string;
  /** Pending | Accepted | Cancelled | Released | Expired. */
  status: string;
  /** The caller's tenant — the one that would be managed. */
  targetTenantId: string;
  targetTenantDomain?: string;
}

/** Response of POST delegations/assignees. */
export interface DelegationAssignResponse {
  assignment: TenantGroupAssignment;
}

/** Response of GET delegations/assignees: the managing tenant's own users on its self-service group. */
export interface DelegationAssigneeListResponse {
  homeTenantId: string;
  groupId: string;
  assignees: TenantGroupAssignment[];
}

/** One invitation row as the managing tenant sees it. Never carries the token. */
export interface DelegationInvitationItem {
  invitationId: string;
  /** Pending | Accepted | Cancelled | Released | Expired (derived: pending past its expiry). */
  status: string;
  createdBy: string;
  createdUtc: string;
  expiresUtc: string;
  acceptedUtc?: string;
  acceptedBy?: string;
  /** The managed tenant (accepted / released rows). */
  tenantId?: string;
  tenantDomain?: string;
  /** Released rows: while in the future the slot is still occupied. */
  holdUntilUtc?: string;
}

/** Response of GET delegations/invitations. */
export interface DelegationInvitationListResponse {
  homeTenantId: string;
  invitations: DelegationInvitationItem[];
}

/** One sampled table row key (delete preview / stored manifest summary). */
export interface DeletionRowKeySample {
  pk: string;
  rk: string;
}

export interface DeliveryLatencyMetrics {
  p50Ms: number;
  p95Ms: number;
  p99Ms: number;
  avgMs: number;
  sampleCount: number;
  clockSkewPercent: number;
}

export interface DeploymentTypeMetrics {
  userDriven: number;
  whiteGlove: number;
  userDrivenPercentage: number;
  whiteGlovePercentage: number;
}

/** Response of GET health/detailed: the full system health report (always 200; per-check status is in the body). Non-GA callers get a filtered check list. */
export interface DetailedHealthCheckResponse {
  service: string;
  timestamp: string;
  overallStatus: string;
  checks: HealthCheck[];
  version: string;
  commitHash: string;
  buildUtc: string;
}

/** Per-device enrollment history for F2 (insights spec §F2) — one row per device key (TenantId, normalized serial), holding the compact chain of the device's terminal sessions (capped at the 20 most recent) plus derived journey counts. Written inline at every session-terminal transition (so the session-detail banner is fresh) and healed by the rolling maintenance sweep, which also drops refs of deleted sessions (tombstone-driven) and deletes the row when the chain empties. Junk serials (placeholder identities) never get a row. Persisted in DeviceHistories; wiped on tenant offboarding — deliberately NOT part of the per-session deletion manifest, because the row aggregates many sessions. */
export interface DeviceHistory {
  tenantId: string;
  /** Normalized device key component: trimmed + lower-cased serial (spec: trim + case-fold). */
  serialKey: string;
  /** Display serial as last reported (trimmed, original casing). */
  serialNumber: string;
  manufacturer: string;
  model: string;
  /** Terminal session refs ordered by StartedAt ascending, capped at the 20 most recent. */
  chain: DeviceSessionRef[];
  /** Attempt count of the LAST journey in the chain (open or completed) — the "Attempt N" the session banner shows. */
  currentJourneyAttempts: number;
  /** Journeys represented in the retained chain (the 20-cap can hide older journeys — this is a chain-scoped count, not a lifetime claim). */
  journeyCount: number;
  /** Journey-grouping algorithm version (truthfulness rule 8: a definition change never silently mixes semantics). */
  journeyVersion: number;
  lastUpdated: string;
}

/** One attempt-histogram bucket: how many completed journeys took exactly Attempts attempts. */
export interface DeviceJourneyAttemptBucket {
  attempts: number;
  journeyCount: number;
}

/** Daily First-Time-Right rollup for one (tenant, date) — F2 PR4 (insights spec §F2). A journey counts on the StartedAt date of its COMPLETING success session (same StartedAt-date bucketing as every other daily aggregate; the rolling sweep re-buckets late-terminating sessions idempotently). Only journeys that ended with a terminal success are counted — open journeys (no success yet, incl. WhiteGlove waiting for its user session) and gap-abandoned journeys never enter numerator or denominator. Counts are additive across days, so a window rate is the sum of daily rows — no rolling-window row is needed (unlike the median-based time-attribution aggregates). Rows are written even below the ≥20-completed-journeys UI gate: the UI needs the n to say "insufficient data (n=3)" (truthfulness rule 4). Junk-serial exclusions are disclosed per day (rule 7). "global" mirror rows sum all tenants. */
export interface DeviceJourneyDailyAggregate {
  /** Calendar day (UTC, "yyyy-MM-dd") the completing success sessions STARTED. */
  date: string;
  /** Tenant GUID, or "global" for the cross-tenant row. */
  tenantId: string;
  /** Journey-grouping algorithm version the counts were computed with (rule 8). */
  journeyVersion: number;
  /** Journeys completed (first terminal success) on this date — the FTR denominator. */
  completedJourneyCount: number;
  /** Completed journeys with attempt count == 1 — the FTR numerator. */
  firstTimeRightCount: number;
  /** Attempt distribution across the completed journeys, ordered by attempts ascending. */
  attemptHistogram: DeviceJourneyAttemptBucket[];
  /** Terminal sessions on this date excluded for junk/placeholder serials — disclosed, never silent (rule 7). */
  excludedSessionCount: number;
  computedAt: string;
}

/** Response of GET metrics/device-journeys and GET global/metrics/device-journeys: daily First-Time-Right rows of the window plus their sums, the merged attempt histogram, and the repeat-devices violator list (absent on the cross-tenant aggregate). */
export interface DeviceJourneyMetricsResponse {
  success: boolean;
  windowDays: number;
  totals: DeviceJourneyWindowTotals;
  /** Daily rows of the window, date ordinal order. */
  daily: DeviceJourneyDailyAggregate[];
  /** Devices whose current journey took at least 2 attempts; absent on the cross-tenant aggregate (no per-device drill there). */
  repeatDevices?: DeviceJourneyRepeatDevice[];
}

/** One repeat-device violator row (current journey took at least 2 attempts, newest terminal session in the window). */
export interface DeviceJourneyRepeatDevice {
  serialNumber: string;
  manufacturer: string;
  model: string;
  attempts: number;
  journeyCount: number;
  lastStatus: string;
  lastSessionId: string;
  lastStartedAt: string;
  /** Failure reason of the newest failed attempt; empty when unavailable (fail-soft point-read). */
  lastFailureReason: string;
}

/** Window totals of the device-journey response (additive sums over the daily rows). */
export interface DeviceJourneyWindowTotals {
  completedJourneys: number;
  firstTimeRight: number;
  /** Null with zero completed journeys — no rate claim, never 0 (truthfulness rule 1). */
  ftrRatePct?: number;
  excludedSessions: number;
  attemptHistogram: DeviceJourneyAttemptBucket[];
}

/** One serial-number bucket in the GetDeviceNotRegistered aggregation. All values are self-reported by devices through the unauthenticated distress channel — UNVERIFIED. */
export interface DeviceNotRegisteredItem {
  serialNumber: string;
  manufacturer: string;
  model: string;
  /** Sticky-true across the bucket: once any report carried the W365 marker. */
  isCloudPc: boolean;
  attemptCount: number;
  firstSeen: string;
  lastSeen: string;
}

/** Success body of GET audit/device-not-registered: unregistered-device rejections aggregated by serial number over the distress-report retention window. */
export interface DeviceNotRegisteredResponse {
  success: boolean;
  aggregated: DeviceNotRegisteredItem[];
  /** Count of raw DeviceNotRegistered distress reports before aggregation. */
  totalRawReports: number;
  dataQualityNotice: string;
}

/** One terminal session of a device inside its Chain — the compact ref the F2 journey grouping runs on (insights spec §F2). Only terminal sessions (Succeeded / Failed / Incomplete) become refs: a WhiteGlove Pending or an AwaitingUser/Stalled session is an OPEN session and must never appear as an attempt. */
export interface DeviceSessionRef {
  sessionId: string;
  startedAt: string;
  /** Terminal timestamp; feeds the 30-day journey-gap rule (fallback: StartedAt). */
  completedAt?: string;
  /** SessionStatus name ("Succeeded" / "Failed" / "Incomplete"). */
  status: string;
  /** "v1" (Autopilot Classic/ESP) or "v2" (Windows Device Preparation). */
  enrollmentType: string;
  isPreProvisioned: boolean;
  /** The session's authoritative DurationSeconds verbatim (WhiteGlove pause excluded by design) — F2 surfaces must never recompute CompletedAt − StartedAt, which is later in 25 % of terminal sessions. Null for Incomplete (deliberately stores no duration). */
  durationSeconds?: number;
  /** An administrator flipped this session's terminal status via the portal — the chain flags it (truthfulness guard §F2). */
  adminMarked: boolean;
}

/** Wire projection of one built-in diagnostics section. Condition travels as the enum NAME ("Always" | "RealmJoinWatcher" | "DevicePreparation"), never the integer — the web switches on the string. */
export interface DiagnosticsBuiltInSectionWire {
  id: string;
  zipFolder: string;
  /** UNEXPANDED source folder (may contain %ProgramData% or the user-profile token). */
  sourceFolder: string;
  patterns: string[];
  includeSubfolders: boolean;
  description: string;
  condition: string;
}

/** Response of POST diagnostics/download-ticket: a short-lived, self-authenticating download URL for one diagnostics blob (HMAC ticket in the query string). */
export interface DiagnosticsDownloadTicketResponse {
  success: boolean;
  /** Relative download URL ("/api/diagnostics/download?t=..."). */
  url: string;
  expiresAt: string;
  blobName: string;
  /** "Hosted" or "CustomerSas". */
  destination: string;
  /** Best-effort blob size, or null when the size probe timed out/failed — the key is omitted when null. */
  sizeBytes?: number;
}

/** Represents a log file path (or wildcard pattern) to include in the diagnostics ZIP package. Global (built-in) entries are defined by Global Admins; tenants may add their own. */
export interface DiagnosticsLogPath {
  /** File system path or wildcard pattern. Environment variables are expanded by the agent. Wildcards are only allowed in the last path segment (e.g. "C:\Windows\Panther\*.log"). */
  path: string;
  /** Human-readable description shown in the portal. */
  description: string;
  /** True when defined globally by a Global Admin — displayed as read-only for tenants. False when added by the tenant itself. */
  isBuiltIn: boolean;
  /** When true, the agent also collects matching files from subdirectories recursively. Subdirectory structure is preserved in the ZIP (e.g. AdditionalLogs/Logs/subfolder/file.log). Default is false (top-level only). */
  includeSubfolders: boolean;
}

/** Response of GET diagnostics/paths: what every diagnostics package collects before a tenant's own entries — the built-in section catalog (compiled into the agent) and the platform-wide global paths set by Global Admins. Member-readable by design. */
export interface DiagnosticsPathsResponse {
  builtIn: DiagnosticsBuiltInSectionWire[];
  globalPaths: DiagnosticsLogPath[];
}

/** Shared response of POST global/notifications/dismiss-all and POST notifications/dismiss-all: how many notifications were dismissed. */
export interface DismissAllNotificationsResponse {
  success: boolean;
  dismissedCount: number;
}

export interface DistressReportEntry {
  tenantId: string;
  errorType: string;
  manufacturer?: string;
  model?: string;
  serialNumber?: string;
  agentVersion?: string;
  httpStatusCode?: number;
  message?: string;
  agentTimestamp: string;
  ingestedAt: string;
  sourceIp?: string;
  /** Agent-reported W365 marker verdict (CloudPcDetector). UNVERIFIED like every distress field; false for rows written before the field existed. */
  isCloudPc: boolean;
  certSourceState?: string;
  certThumbprint?: string;
  certSubject?: string;
  certIssuer?: string;
  certNotBefore?: string;
  certNotAfter?: string;
}

/** Success body of GET global/distress-reports: all pre-auth distress reports (Global Admin). */
export interface DistressReportListResponse {
  success: boolean;
  count: number;
  reports: DistressReportEntry[];
}

/** Response of POST rules/analyze/dryrun: the full diagnostic trace of one draft-rule evaluation against a session. */
export interface DryRunAnalyzeRuleResponse {
  success: boolean;
  sessionId: string;
  /** The full diagnostic trace of the evaluation. */
  result: RuleDryRun;
}

export interface EfficiencyOffender {
  sessionId: string;
  tenantId: string;
  deviceName?: string;
  dimension: string;
  value: number;
}

/** Response of DELETE global/email-templates/{kind}: reset to the built-in template. */
export interface EmailTemplateResetResponse {
  /** "welcome" or "farewell". */
  kind: string;
  isOverridden: boolean;
}

/** Response of GET global/email-templates/{kind}: the effective template. */
export interface EmailTemplateResponse {
  /** "welcome" or "farewell". */
  kind: string;
  subject: string;
  isOverridden: boolean;
  /** The effective template HTML: the override when stored, otherwise the built-in raw template. */
  html: string;
  builtInHtml: string;
  /** Who stored the override, or null when no override exists — the key is omitted when null. */
  updatedBy?: string;
  /** When the override was stored, or null when no override exists — the key is omitted when null. */
  updatedUtc?: string;
  /** The domain placeholder token used in the raw template. */
  placeholder: string;
  maxLength: number;
}

/** Response of PUT global/email-templates/{kind}: override stored. */
export interface EmailTemplateSaveResponse {
  /** "welcome" or "farewell". */
  kind: string;
  isOverridden: boolean;
  updatedBy: string;
  updatedUtc: string;
}

/** Response of POST global/email-templates/{kind}/test: the test mail was accepted by the provider. */
export interface EmailTemplateTestSendResponse {
  sentTo: string;
  domainName: string;
  /** True when an unsaved draft body was sent instead of the effective template. */
  draft: boolean;
}

/** Represents a single event during enrollment */
export interface EnrollmentEvent {
  /** Unique identifier for this event */
  eventId: string;
  /** Session identifier this event belongs to */
  sessionId?: string;
  /** Tenant identifier */
  tenantId?: string;
  /** Timestamp when the agent detected/created this event on the device (UTC). Set at construction time (DateTime.UtcNow) or from the source's native timestamp (e.g., Windows EventLog TimeCreated). The backend stores this as-is — it is the authoritative "agent-side" timestamp, not a server-side receive time. */
  timestamp: string;
  /** Server-side UTC timestamp set when the backend receives and stores this event. Null for events that pre-date this feature. Never set by the agent. */
  receivedAt?: string;
  /** Device-clock UTC timestamp of the moment the agent SENT the upload batch carrying this event (X-Send-Time-Utc request header, one value per ingest request — never set by the agent per event). Same clock frame as Timestamp, so SentAt − Timestamp is pure spool delay and ReceivedAt − SentAt is network latency plus device-vs-server clock offset — the two are indistinguishable without this field. Null for events from agents that pre-date the header. */
  sentAt?: string;
  /** Type of event (e.g., "phase_transition", "app_install_start", "error") */
  eventType: string;
  /** Severity as string for JSON serialization */
  readonly severity: string;
  /** Source of the event — typically the producing class name (e.g. "ImeLogTracker", "EnrollmentTracker", "DecisionEngine") or a stable lifecycle label ("Agent"). */
  source: string;
  /** Phase as number for JSON serialization (frontend expects number) */
  readonly phase: number;
  /** Phase name as string for JSON serialization */
  readonly phaseName: string;
  /** Human-readable message */
  message: string;
  /** Additional structured data */
  data: Record<string, unknown>;
  /** Sequence number for ordering events with same timestamp */
  sequence: number;
  /** Original agent-side timestamp preserved when the backend clamps an out-of-range value. Null when the timestamp was within the valid range and no correction was needed. Use this for troubleshooting and root-cause analysis of clock issues on devices. */
  originalTimestamp?: string;
  /** True when the backend had to clamp the agent-side Timestamp to a valid range. When set, OriginalTimestamp contains the raw value the agent sent. AI analysis and UI should treat clamped timestamps with caution. */
  timestampClamped: boolean;
  /** Azure Table Storage RowKey — format: {Timestamp:yyyyMMddHHmmssfff}_{Sequence:D10} Represents the exact sort key used in storage. */
  rowKey: string;
  /** Reducer StepIndex of the DecisionTransition whose EmitEventTimelineEntry effect produced this event. Nullable for forward-compat (events that pre-date Codex follow-up #3 or that are emitted outside the reducer pipeline). Together with CausedBySignalOrdinal this replaces the always-empty DecisionTransition.EmittedEventSequences — the forward link lives on the event side because the event's Sequence is assigned after the journal record is already on disk. */
  causedByTransitionStepIndex?: number;
  /** SessionSignalOrdinal of the signal that drove the transition which emitted this event. Nullable for the same reason as CausedByTransitionStepIndex. Lets the Inspector jump "event → causing signal" without joining through the transitions table. */
  causedBySignalOrdinal?: number;
}

/** Outcome of the last EPSS re-score run on THIS instance (in-memory). */
export interface EpssRefreshRunWire {
  rows: number;
  cves: number;
  scored: number;
  rowsRewritten: number;
  error?: string;
  /** Preformatted ISO-8601 start timestamp. */
  startedUtc: string;
  /** Preformatted ISO-8601 finish timestamp, or null (key omitted) while still running. */
  finishedUtc?: string;
}

/** Response of GET feedback/eligibility: whether the caller should be shown the feedback prompt. */
export interface FeedbackEligibilityResponse {
  eligible: boolean;
}

/** One feedback interaction as rendered on the Global Admin dashboard. */
export interface FeedbackEntryWire {
  type?: string;
  upn?: string;
  tenantId?: string;
  displayName?: string;
  /** Absent on dismissals. */
  rating?: number;
  /** Absent on dismissals. */
  comment?: string;
  dismissed: boolean;
  submitted: boolean;
  /** ISO-8601 round-trip string; absent when never stamped. */
  interactedAt?: string;
  historyRowKey?: string;
  domainName?: string;
}

/** Response of GET feedback/all (Global Admin dashboard): every stored feedback entry. */
export interface FeedbackListResponse {
  feedback: FeedbackEntryWire[];
}

export interface FleetDailyPoint {
  date: string;
  success: number;
  failed: number;
}

export interface FleetFailingModel {
  model: string;
  failed: number;
  /** All sessions on this model in the window, including in-flight ones. */
  total: number;
  /** Failed / (Failed + Succeeded) * 100, rounded — finished enrollments only. */
  failureRate: number;
}

export interface FleetFailureReason {
  reason: string;
  count: number;
}

/** Server-computed Fleet Health aggregates. Replaces the client-side pass that drained up to 200k raw sessions into the browser and aggregated on the main thread. Built once per request from the windowed session list — see MetricsMath.BuildFleetHealthPayload. Presentation-only derivations (bar-chart maxima, weekday/month axis labels) stay on the client; this payload carries the data, not the formatting. */
export interface FleetHealthMetrics {
  success: boolean;
  days: number;
  stats: FleetHealthStats;
  /** One entry per day in the window, oldest-first. Date is UTC "yyyy-MM-dd". */
  dailyData: FleetDailyPoint[];
  /** Top failure reasons by count (descending). */
  failureReasons: FleetFailureReason[];
  /** Device models by enrollment volume (descending). */
  modelHealth: FleetModelHealth[];
  /** Device models by average successful enrollment duration (descending). */
  slowestModels: FleetSlowModel[];
  /** Device models by failure count (descending). */
  topFailingModels: FleetFailingModel[];
  computedAt: string;
}

export interface FleetHealthStats {
  total: number;
  succeeded: number;
  failed: number;
  inProgress: number;
  /** Terminal, non-failure sessions (timeout reclassification). Surfaced as its own count; not a failure. */
  incomplete: number;
  /** Succeeded / (Succeeded + Failed) * 100 (one decimal) — finished enrollments only, matching the SLA convention. 0 when nothing has finished yet (clients render "—"). */
  successRate: number;
  /** Average duration in minutes over non-in-progress sessions that carry a duration. Kept for API compatibility; the cards lead with the median, which a handful of multi-hour outliers (overnight ESP, WhiteGlove late user phase) cannot drag around. */
  avgDurationMinutes: number;
  /** Median duration in minutes over the same population as AvgDurationMinutes. */
  medianDurationMinutes: number;
  /** 90th-percentile duration in minutes over the same population — the tail signal the median alone would hide. */
  p90DurationMinutes: number;
}

export interface FleetModelHealth {
  model: string;
  /** All sessions on this model in the window, including in-flight ones. */
  total: number;
  succeeded: number;
  /** Clients derive the per-model rate as Succeeded / (Succeeded + Failed). */
  failed: number;
}

export interface FleetSlowModel {
  model: string;
  avgMinutes: number;
  count: number;
}

/** Defines what data the agent should collect Gather rules are delivered to the agent via the config API and can be managed (enabled/disabled, created) through the portal */
export interface GatherRule {
  /** Unique rule identifier (e.g., "GATHER-NET-001") */
  ruleId: string;
  /** Human-readable rule title (e.g., "Collect WinHTTP Proxy Settings") */
  title: string;
  /** Detailed description of what this rule collects and why */
  description: string;
  /** Rule category: network, identity, apps, device, esp, enrollment */
  category: string;
  /** Semantic version of this rule (e.g., "1.0.0") */
  version: string;
  /** Author of this rule */
  author: string;
  /** Whether this rule is currently enabled for the tenant */
  enabled: boolean;
  /** Whether this is a built-in rule (shipped with the system) Built-in rules cannot be deleted, only disabled */
  isBuiltIn: boolean;
  /** Whether this is a community-contributed rule Community rules behave like built-in rules (read-only, state stored separately) but are displayed with a distinct "Community" badge in the portal */
  isCommunity: boolean;
  /** Where this global rule row came from — see RuleProvenance. Drives the self-maintaining sunset: "embedded"/null = owned by the deployed binary's catalog (may be sunset when it leaves that catalog); "github" = reseeded from GitHub ahead of the binary (exempt from the embedded catalog sunset/filter). Null on pre-existing rows = embedded. */
  provenance?: string;
  /** Type of data collection: - "registry": Read values from the Windows Registry - "eventlog": Read entries from a Windows Event Log - "wmi": Execute a WMI/CIM query - "file": Check file/directory existence and optionally read content - "command_allowlisted": Run a pre-approved command (PowerShell or CLI). Only commands on the agent's hardcoded allowlist in GatherRuleExecutor.cs are permitted. Unlisted commands are blocked and generate a security_warning event. See the allowlist in GatherRuleExecutor.cs for the full list of approved commands. - "logparser": Parse a CMTrace-format log file using a regex pattern with named capture groups */
  collectorType: string;
  /** Target for collection: - registry: Registry path (e.g., "HKLM\SOFTWARE\Microsoft\...") - eventlog: Event log name (e.g., "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin") - wmi: WMI query (e.g., "SELECT * FROM Win32_TPM WHERE __NAMESPACE='root\\CIMV2\\Security\\MicrosoftTpm'") - file: File or directory path with env vars (e.g., "C:\Windows\INF\setupapi.dev.log") - command_allowlisted: Exact command string as it appears in the allowlist (e.g., "Get-Tpm", "dsregcmd /status") - logparser: Log file path with env vars (e.g., "%ProgramData%\Microsoft\IntuneManagementExtension\Logs\AppWorkload.log") */
  target: string;
  /** Additional parameters for the collector: - registry: { "valueName": "ProxyServer" } — omit to read all values - eventlog: { "maxEntries": "10", "source": "...", "eventId": "62407", "messageFilter": "*ESPProgress*" } - wmi: { "namespace": "root\\CIMV2\\Security\\MicrosoftTpm" } - file: { "readContent": "true" } — only reads files <50 KB - command_allowlisted: (no additional parameters — command is the full string in Target) - logparser: { "pattern": "regex with (?<namedGroups>)", "trackPosition": "true", "maxLines": "1000" } */
  parameters: Record<string, string>;
  /** Trigger type: "startup", "phase_change", "phase_exit", "interval", "on_event". "phase_change" fires once when the phase in is ENTERED, "phase_exit" once when it is LEFT — the two one-shot bookends of a phase. */
  trigger: string;
  /** Interval in seconds (only used when Trigger = "interval") */
  intervalSeconds?: number;
  /** Phase to trigger on (used when Trigger = "phase_change" or "phase_exit"). Canonical tokens are the EnrollmentPhase enum names, e.g. "DeviceSetup", "AccountSetup", "Complete". Empty = every phase transition. */
  triggerPhase: string;
  /** Event type to trigger on (only used when Trigger = "on_event") e.g., "app_install_failed" */
  triggerEventType: string;
  /** Restricts the rule to run only while the current enrollment phase is one of these phases. Canonical tokens are the EnrollmentPhase enum names ("Start", "DevicePreparation", "DeviceSetup", "AppsDevice", "AccountSetup", "AppsUser", "FinalizingSetup", "Complete"); "Unknown" and "Failed" are rejected by backend validation. Null or empty = unrestricted (runs in every phase — legacy behavior). Mutually exclusive with ActiveFromPhase; if both are set the agent defensively prefers this list. Applies to ALL trigger types. Before the first phase signal of a session, scoped rules are inactive. */
  activePhases?: string[];
  /** Activates the rule once the enrollment phase first reaches this phase (ordinal comparison on EnrollmentPhase, ignoring Unknown/Failed), then keeps it active for the rest of the session (sticky latch — including through Failed). Canonical tokens as in ActivePhases. Null = unrestricted. Mutually exclusive with ActivePhases. */
  activeFromPhase?: string;
  /** Emit behavior for collected results: null / "always" = emit on every collection (legacy behavior); "on_change" = poll on the trigger cadence but emit only when the collected result differs from the last emitted one. The first in-scope result always emits; the suppressed poll count is carried on the next emitted event (suppressedPolls / suppressedSinceUtc in the event data). */
  emitMode?: string;
  /** EventType for the emitted event (e.g., "gather_proxy_settings") */
  outputEventType: string;
  /** Severity for the emitted event Default: "Info" */
  outputSeverity: string;
  /** Tags for filtering and categorization */
  tags: string[];
  /** When this rule was created */
  createdAt: string;
  /** When this rule was last updated */
  updatedAt: string;
}

/** Gather-rule listing envelope shared by GetGatherRules (tenant-scoped) and GetGlobalGatherRules (Global Admin, ?tenantId= scoped). */
export interface GatherRuleListResponse {
  success: boolean;
  rules: GatherRule[];
}

/** Default (lean) geographic drilldown envelope shared by GetGeographicLocationSessions and GetGlobalGeographicLocationSessions — per-row payload an order of magnitude smaller than the full LocationSessionRow shape. */
export interface GeographicLocationSessionsLeanResponse {
  success: boolean;
  sessions: LocationSessionLeanRow[];
  totalCount: number;
}

/** Full geographic drilldown envelope shared by GetGeographicLocationSessions and GetGlobalGeographicLocationSessions when the caller passes ?full=1. */
export interface GeographicLocationSessionsResponse {
  success: boolean;
  sessions: LocationSessionRow[];
  totalCount: number;
}

/** Response containing geographic performance metrics aggregated by location. */
export interface GeographicMetricsResponse {
  success: boolean;
  locations: LocationMetrics[];
  globalAverages: GlobalAverages;
  computedAt: string;
  totalSessions: number;
  locationsWithData: number;
  /** Whether geo-location collection is enabled for this tenant */
  geoLocationEnabled: boolean;
}

/** Response of GET global/presence: users active within the requested window. */
export interface GetActiveUsersResponse {
  success: boolean;
  windowMinutes: number;
  activeCount: number;
  users: ActiveUserItem[];
}

/** Response of GET preview/notification-emails: every stored notification address, keyed by lowercased tenant id. */
export interface GetAllPreviewNotificationEmailsResponse {
  count: number;
  emails: Record<string, string>;
}

/** Paginated/projected response of GET config/all (delegated one-shot page and the GA ?pageSize= mode): keep-list projections of the tenant configurations (TenantConfigProjection dictionaries — secrets can never be selected). */
export interface GetAllTenantConfigurationsResponse {
  count: number;
  /** Keep-list field projections (TenantConfigProjection.ProjectAll dictionaries). */
  tenants: (Record<string, unknown>)[];
  /** Absolute-path link to the next page, or null on the last page — the key is omitted when null. */
  nextLink?: string;
}

/** Response of GET vulnerability/cpe-mappings: every mapping plus the count. */
export interface GetCpeMappingsResponse {
  mappings: CpeMappingItem[];
  total: number;
}

/** Device enrollment-history envelope (GetDeviceHistory). History is absent as a NORMAL outcome (unknown device, junk serial, or every chain ref pruned). */
export interface GetDeviceHistoryResponse {
  success: boolean;
  /** Absent when the device has no history row — the banner simply stays hidden. */
  history?: DeviceHistory;
  /** The requesting session's attempt number within its journey; absent without a ?sessionId= parameter, when the session is unknown, or when the position cannot be computed (fail-soft, never a guessed position). */
  attemptNumber?: number;
}

/** Response of GET auth/global-admins: every Global Admin/Reader row. */
export interface GetGlobalAdminsResponse {
  admins: GlobalAdminRow[];
}

/** Global daily MCP/API usage summaries envelope (GetGlobalMcpUsageDaily). The MCP server paginates over the summaries key — its name is wire-critical. */
export interface GetGlobalMcpUsageDailyResponse {
  /** Echo of the request filter; absent when the caller passed no tenantId. */
  tenantId?: string;
  summaries: UserUsageDailySummary[];
}

/** Global per-tenant MCP/API usage envelope (GetGlobalMcpUsage). The MCP server paginates over the records key — its name is wire-critical. */
export interface GetGlobalMcpUsageResponse {
  /** Echo of the request filter; absent when the caller passed no tenantId. */
  tenantId?: string;
  records: UserUsageRecord[];
}

/** Response of GET tenants/{tenantId}/graph-permissions/status: the client id of the app homed for the tenant, the transient flag, the granted Graph app roles and the per-feature verdict matrix. */
export interface GetGraphPermissionsStatusResponse {
  /** ClientId of the app registration that acts for this tenant (empty string when unresolved, never null). */
  clientId: string;
  /** True when the snapshot is not authoritative (token-acquire timeout / transient failure) — the UI renders "try again". */
  isTransient: boolean;
  grantedRoles: string[];
  features: GraphFeatureStatusItem[];
}

/** Response of GET vulnerability/ignored-software: the full ignore list. */
export interface GetIgnoredSoftwareResponse {
  items: IgnoredSoftwareItem[];
  total: number;
}

/** Response of GET config/latest-versions: the latest published agent + bootstrap script versions (null slots when the version blob could not be fetched). */
export interface GetLatestVersionsResponse {
  latestAgentVersion?: string;
  latestBootstrapScriptVersion?: string;
  latestAgentSha256?: string;
  fetchedAtUtc?: string;
  /** "cache" or "blob". */
  source: string;
}

/** Organization-wide MCP usage by user for the caller's OWN tenant (GetMcpOrganizationUsage): every account whose requests were charged to this tenant's organization budget — its own members and any delegated (MSP) administrators reading the tenant. Built from the tenant's organization counters. */
export interface GetMcpOrganizationUsageResponse {
  tenantId: string;
  /** Effective range start (yyyyMMdd). */
  dateFrom: string;
  /** Effective range end (yyyyMMdd, inclusive). */
  dateTo: string;
  users: McpOrganizationUsageItem[];
}

/** Per-user MCP/API usage envelope (GetMcpUserUsage). Non-global callers only receive the records attributed to their own tenant; a foreign oid and an unknown oid are indistinguishable (both 200 with empty records). The MCP server paginates over the records key — its name is wire-critical. */
export interface GetMcpUserUsageResponse {
  userId: string;
  records: UserUsageRecord[];
}

/** Response of GET global/mcp-users: the effective MCP access policy name plus every whitelist row. */
export interface GetMcpUsersResponse {
  policy: string;
  users: McpUserEntry[];
}

/** Self-service MCP/API usage envelope (GetMyMcpUsage): the caller's own usage records plus resolved plan and quota state. The MCP server paginates over the records key — its name is wire-critical. */
export interface GetMyMcpUsageResponse {
  userId: string;
  /** Absent when the token carries no UPN claim. */
  upn?: string;
  /** Per-user plan override from the caller's own whitelist row; absent without one. */
  usagePlan?: string;
  /** Resolved effective plan (per-user override → tenant edition). */
  effectivePlan: string;
  quota: McpUsageQuotaNode;
  records: UserUsageRecord[];
}

/** Landing-page platform stats envelope (GetPlatformStats, unauthenticated). When no stats row has been computed yet, the zero shape is served: all counters 0 with TotalSignedUpTenants and LastUpdated absent. */
export interface GetPlatformStatsResponse {
  totalEnrollments: number;
  totalUsers: number;
  totalTenants: number;
  /** Absent on the not-yet-computed zero shape. */
  totalSignedUpTenants?: number;
  uniqueDeviceModels: number;
  totalEventsProcessed: number;
  successfulEnrollments: number;
  issuesDetected: number;
  /** Always absent today: the computed shape never sets it, the zero shape sets null. */
  lastUpdated?: string;
}

/** Response of GET preview/notification-email/{tenantId}. Empty string when no address is stored (never null — the site coalesces). */
export interface GetPreviewNotificationEmailResponse {
  email: string;
}

/** Response of GET preview/whitelist: every approved tenant (Global Admin only). */
export interface GetPreviewWhitelistResponse {
  tenants: PreviewWhitelistTenantEntry[];
}

/** Session ids where a rule produced a result within the window (GetRuleHitSessions) — powers the dashboard's ?ruleId= deep link. */
export interface GetRuleHitSessionsResponse {
  ruleId: string;
  days: number;
  sessionIds: string[];
  /** True when the result hit the hit-set cap (2000) — the list is a lower bound. */
  truncated: boolean;
}

/** Response of GET sessions/{sessionId}/analysis: persisted rule results plus severity counts over the still-open (unresolved) findings. */
export interface GetRuleResultsResponse {
  /** False when any rule result failed to persist during an on-demand reanalyze. */
  success: boolean;
  sessionId: string;
  /** All persisted results, including resolved findings (kept for audit). */
  results: RuleResult[];
  /** Count of open (unresolved) findings only. */
  totalIssues: number;
  criticalCount: number;
  highCount: number;
  warningCount: number;
  persistFailureCount: number;
  /** Rule ids that failed to persist during the reanalyze — the key is omitted entirely when there were no failures (the happy-path contract stays unchanged). */
  persistFailureRuleIds?: string[];
}

/** Response of POST tenants/{tenantId}/scripts/display-names: resolved Intune script display names keyed by the canonical ref string. Always 200, possibly partial — unresolved refs stay in the dictionary with a null VALUE (dictionary values are NOT subject to WhenWritingNull, so the key is emitted with an explicit JSON null). */
export interface GetScriptDisplayNamesResponse {
  /** Display name per canonical script ref ("Platform:{id}" / "Remediation:{id}"). IMPORTANT: dictionary KEYS do not run through the camelCase PropertyNamingPolicy (ApiJsonOptions sets no DictionaryKeyPolicy) — the ref strings are serialized verbatim, including the PascalCase type prefix. Pinned by GraphWireParityTests. */
  refs: Record<string, string | null>;
  /** Ref tokens from the request that failed to parse, or null when there were none — the key is omitted when null (the empty-body and empty-refs early-exit sites never wrote this key at all; they leave it null). */
  malformed?: string[];
}

/** Response of GET sessions/{sessionId}/annotations: the annotation lanes visible to the caller plus the server-computed list of lanes the caller may write. */
export interface GetSessionAnnotationsResponse {
  success: boolean;
  sessionId: string;
  tenantId: string;
  annotations: SessionAnnotationItem[];
  /** Lanes the caller is allowed to write — the web renders lanes writable exactly when this list says so. */
  writableLanes: string[];
}

/** Decision-graph envelope (GetSessionDecisionGraph). */
export interface GetSessionDecisionGraphResponse {
  success: boolean;
  truncated: boolean;
  graph: DecisionGraphProjection;
}

/** Dry-run cascade-delete preview envelope (GetSessionDeletePreview, mode=summary). */
export interface GetSessionDeletePreviewResponse {
  success: boolean;
  /** Always "summary" — full/download modes bypass the JSON envelope. */
  mode: string;
  /** Operator hint when a cascade is already in flight; absent otherwise. */
  inFlightHint?: string;
  preflightCounts: Record<string, number>;
  /** Up to five sample row keys per table/step. */
  sampleKeys: Record<string, DeletionRowKeySample[]>;
  estimatedRowCount: number;
  /** -1 when the size estimation itself failed. */
  estimatedSnapshotBytes: number;
  builderDurationMs: number;
  schemaHash: string;
  manifestId: string;
}

/** Stored cascade-delete snapshot summary envelope (GetSessionDeletionManifest, mode=summary). */
export interface GetSessionDeletionManifestResponse {
  success: boolean;
  /** Always "summary" — full/download modes bypass the JSON envelope. */
  mode: string;
  /** Always "stored" (vs the delete-preview's freshly built manifest). */
  source: string;
  manifestId: string;
  schemaHash: string;
  snapshotSha256: string;
  estimatedRowCount: number;
  estimatedSnapshotBytes: number;
  preflightCounts: Record<string, number>;
  /** Up to five sample row keys per table/step. */
  sampleKeys: Record<string, DeletionRowKeySample[]>;
  /** Absent while no progress blob exists yet (Preparing phase) or it could not be read. */
  progress?: SessionDeletionProgressWire;
}

/** Cross-tenant listing of sessions in a cascade-deletion state (GetSessionDeletionsList). */
export interface GetSessionDeletionsListResponse {
  success: boolean;
  state: string;
  /** Echo of the request filter; absent unless the caller passed it. */
  strandedSinceMinutes?: number;
  count: number;
  sessions: SessionDeletionListItem[];
}

/** Session events envelope (GetSessionEvents, paginated and unpaginated paths). Events carries full EnrollmentEvent items, or dictionary projections of them when the caller passed a fields= subset. */
export interface GetSessionEventsResponse {
  success: boolean;
  sessionId: string;
  count: number;
  events: Partial<EnrollmentEvent>[];
  /** Absent on the unpaginated path and when there is no further page. */
  nextLink?: string;
}

/** Reducer-verification envelope (GetSessionReducerVerification). */
export interface GetSessionReducerVerificationResponse {
  success: boolean;
  /** True when signals or transitions hit a load cap or the payload budget. */
  truncated: boolean;
  report: ReducerVerificationReport;
}

/** Single-session detail envelope (GetSession). */
export interface GetSessionResponse {
  success: boolean;
  session: SessionSummary;
}

/** SignalLog read envelope (GetSessionSignals). */
export interface GetSessionSignalsResponse {
  success: boolean;
  sessionId: string;
  count: number;
  /** True when the result hit the row cap or the cumulative payload budget. */
  truncated: boolean;
  signals: SignalRecord[];
}

/** Per-session time-attribution envelope (GetSessionTimeAttribution). A missing breakdown is a NORMAL outcome (pre-feature session, non-terminal, Incomplete — no wall clock). */
export interface GetSessionTimeAttributionResponse {
  success: boolean;
  /** Absent when no breakdown row exists — the UI simply omits the lane. */
  breakdown?: SessionTimeBreakdown;
}

/** Response of GET config/fields-schema: the machine-readable tenant-config field schema for the MCP write surface. */
export interface GetTenantConfigFieldsSchemaResponse {
  count: number;
  writableCount: number;
  fields: TenantConfigFieldSchema[];
}

/** Per-tenant deletion-manifest tree envelope (GetTenantDeletionManifests). */
export interface GetTenantDeletionManifestsResponse {
  success: boolean;
  tenantId: string;
  /** Echo of the optional sessionId filter; absent when not passed. */
  sessionFilter?: string;
  sessionCount: number;
  manifestCount: number;
  sessions: TenantDeletionManifestSessionNode[];
}

/** Tenants that have at least one deletion-manifest blob (GetTenantsWithDeletionManifests). */
export interface GetTenantsWithDeletionManifestsResponse {
  success: boolean;
  count: number;
  tenantIds: string[];
}

/** Response of GET vulnerability/unmatched-software: one page of the aggregated report. */
export interface GetUnmatchedSoftwareResponse {
  software: UnmatchedSoftwareItem[];
  /** Total distinct unmatched titles before paging. */
  total: number;
  skip: number;
  take: number;
}

/** Response of GET sessions/{sessionId}/vulnerability-report on the rescan paths and the no-stored-report path. (A stored-report hit streams the raw report JSON directly and bypasses this DTO.) */
export interface GetVulnerabilityReportResponse {
  success: boolean;
  sessionId: string;
  /** The freshly generated report, or null (key omitted) when there is none. */
  report?: Record<string, unknown>;
  /** True on the ?rescan=true paths; null (key omitted) on the plain read. */
  rescanned?: boolean;
  /** Human-readable outcome note on the no-inventory / no-findings rescan paths; null (key omitted) otherwise. */
  message?: string;
}

/** Response of GET vulnerability/sync-status: live KEV/MSRC/NVD/EPSS cache sizes, the persisted last-sync timestamps, and the in-memory last-run stats of this instance. */
export interface GetVulnerabilitySyncStatusResponse {
  kevCatalogEntries: number;
  /** Preformatted ISO-8601 timestamp from AdminConfiguration, or null (key omitted) when never synced. */
  kevLastSyncUtc?: string;
  msrcIndexedCves: number;
  msrcCoveredDocuments: number;
  msrcLastSyncUtc?: string;
  nvdCacheEntries: number;
  nvdCacheLastRefreshUtc?: string;
  nvdRefreshRunning: boolean;
  /** Last run on THIS instance (in-memory; null / key omitted after a cold start). */
  nvdLastRun?: NvdRefreshRunWire;
  epssLastSyncUtc?: string;
  epssRefreshRunning: boolean;
  /** Last run on THIS instance (in-memory; null / key omitted after a cold start). */
  epssLastRun?: EpssRefreshRunWire;
}

/** One Global Admin/Reader row on the wire. Deliberately NOT the storage entity: the ITableEntity keys (partitionKey/rowKey/eTag/timestamp) that the pre-2026-08-31 wire carried are storage internals and were dropped from the contract (no consumer read them). */
export interface GlobalAdminRow {
  /** User Principal Name (lowercase). */
  upn: string;
  isEnabled: boolean;
  addedDate: string;
  addedBy: string;
  /** "GlobalAdmin" or "GlobalReader" (legacy empty rows are normalized to GlobalAdmin). */
  role: string;
}

/** Global average benchmarks for geographic comparison. */
export interface GlobalAverages {
  avgDurationMinutes: number;
  medianDurationMinutes: number;
  avgMinutesPerApp: number;
  avgThroughputBytesPerSec: number;
  stdDevDurationMinutes: number;
  /** Global request-weighted average agent→backend API latency (ms); 0 = no data yet */
  avgApiLatencyMs: number;
  /** Global median of per-session average API latency (ms), robust against outliers; 0 = no data yet */
  medianApiLatencyMs: number;
  /** Global weighted average peer caching percentage */
  avgDoPercentPeerCaching: number;
  /** Total peer bytes across all locations with DO data */
  totalDoBytesFromPeers: number;
  /** Total HTTP bytes across all locations with DO data */
  totalDoBytesFromHttp: number;
}

/** One in-app notification (global pool or tenant-scoped; both endpoints share this DTO). A null Href key is omitted (WhenWritingNull). */
export interface GlobalNotificationDto {
  id: string;
  type: string;
  title: string;
  message: string;
  href?: string;
  createdAt: string;
}

/** One feature row of the graph-permissions status matrix: the feature identifier, the granted verdict (null while the snapshot is transient) and the Graph application permissions the feature requires. */
export interface GraphFeatureStatusItem {
  name: string;
  /** Granted verdict, or null when the snapshot is transient (verdict unknown) — the key is omitted when null. */
  granted?: boolean;
  requiredPermissions: string[];
}

export interface HardwareCount {
  name: string;
  count: number;
  percentage: number;
}

export interface HardwareMetrics {
  topManufacturers: HardwareCount[];
  topModels: HardwareCount[];
}

/** One manufacturer+model bucket in the GetHardwareRejected aggregation. All values are self-reported by devices through the unauthenticated distress channel — UNVERIFIED. */
export interface HardwareRejectedItem {
  manufacturer: string;
  model: string;
  attemptCount: number;
  uniqueSerials: number;
  firstSeen: string;
  lastSeen: string;
  /** Up to five distinct serial numbers from the bucket. */
  sampleSerialNumbers: string[];
}

/** Success body of GET audit/hardware-rejected: hardware-whitelist rejections aggregated by manufacturer+model over the distress-report retention window. */
export interface HardwareRejectedResponse {
  success: boolean;
  aggregated: HardwareRejectedItem[];
  /** Count of raw HardwareNotAllowed distress reports before aggregation. */
  totalRawReports: number;
  dataQualityNotice: string;
}

/** One health check result inside GET health/detailed / health/mcp. Details is a heterogeneous per-check bag by design (endpoint URLs only for Global Admins); a null Details key is omitted (WhenWritingNull). */
export interface HealthCheck {
  name: string;
  description: string;
  status: string;
  message: string;
  details?: Record<string, unknown>;
}

/** Response of GET health: liveness probe plus the backend build identity. */
export interface HealthCheckResponse {
  status: string;
  service: string;
  timestamp: string;
  version: string;
  commitHash: string;
  buildUtc: string;
}

/** Response of GET global/identity-bindings: every admin identity binding. */
export interface IdentityBindingListResponse {
  bindings: AdminIdentityBinding[];
}

/** Response of PUT global/identity-bindings/{upn}: the created/replaced binding. */
export interface IdentityBindingResponse {
  binding: AdminIdentityBinding;
}

/** One entry of the persistent software ignore list. */
export interface IgnoredSoftwareItem {
  softwareName: string;
  publisher: string;
  reason: string;
  /** Preformatted ISO-8601 timestamp; empty when the row carries none. */
  ignoredAt: string;
}

/** Defines a regex pattern for IME log parsing. Delivered from backend via agent config endpoint. Allows updating patterns without agent rebuild when Microsoft changes IME log formats. */
export interface ImeLogPattern {
  /** Unique pattern identifier (e.g., "IME-ESP-PHASE") */
  patternId: string;
  /** Pattern category controlling when the pattern is active: - "always": Always active regardless of ESP phase - "currentPhase": Only active during the current ESP phase - "otherPhases": Only active during non-current ESP phases (for history/completed apps) */
  category: string;
  /** Regex pattern string to match against IME log message content. Supports named capture groups (e.g., (?<id>...)) which are passed to the action handler. Uses {GUID} as placeholder for the standard GUID capture pattern. */
  pattern: string;
  /** Action to perform when the pattern matches: - "setCurrentApp": Set the current app being processed (uses 'id' capture group) - "updateStateInstalled": Mark app as installed - "updateStateDownloading": Mark app as downloading (uses 'bytes'/'ofbytes' for progress) - "updateStateInstalling": Mark app as installing - "updateStateSkipped": Mark app as skipped/not applicable - "updateStateError": Mark app as errored - "updateStatePostponed": Mark app as postponed (e.g., timeout) - "espPhaseDetected": ESP phase transition detected (uses 'espPhase' capture group) - "imeStarted": IME agent started - "policiesDiscovered": App policies JSON discovered (uses 'policies' capture group) - "ignoreCompletedApp": Add current app to ignore list (already completed in prior phase) - "imeAgentVersion": IME version detected (uses 'agentVersion' capture group) - "espTrackStatus": ESP tracked install status update (uses 'from'/'to'/'id' capture groups) - "updateName": Update app name (uses 'id'/'name' capture groups) - "updateWin32AppState": Update from Win32 app state (uses 'id'/'state' capture groups) - "cancelStuckAndSetCurrent": Cancel stuck app and set new current (uses 'id' capture group) */
  action: string;
  /** Optional extra parameters for the action handler. Examples: - { "phase": "AccountSetup" } for espPhaseDetected to force a specific phase - { "useCurrentApp": "true" } to use CurrentPackageId instead of captured 'id' - { "checkTo": "true" } to check the 'to' capture group value before applying state */
  parameters: Record<string, string>;
  /** Whether this pattern is enabled. Allows disabling patterns without removing them. */
  enabled: boolean;
  /** Human-readable description of what this pattern detects and why. Not used by the agent — purely for documentation and UI display. */
  description: string;
  /** Whether this is a built-in pattern (shipped with the system). Built-in patterns cannot be deleted, only disabled. */
  isBuiltIn: boolean;
}

/** IME log pattern listing envelope (GetImeLogPatterns). */
export interface ImeLogPatternListResponse {
  success: boolean;
  patterns: ImeLogPattern[];
}

export interface ImePatternDriftAlert {
  version: string;
  patternId: string;
  baselineVersion: string;
  baselineRate: number;
  sessions: number;
  flaggedAt?: string;
}

export interface ImePatternHealthCell {
  version: string;
  patternId: string;
  sessions: number;
  sessionsWithHit: number;
  hits: number;
  rate: number;
  driftFlaggedAt?: string;
}

export interface ImePatternHealthPattern {
  patternId: string;
  category?: string;
  enabled: boolean;
  /** Hit rate on the baseline version; null without a baseline. */
  baselineRate?: number;
  /** True when the baseline rate is at or above the expected threshold. */
  expected: boolean;
}

/** Response of GET metrics/ime-pattern-health. */
export interface ImePatternHealthResponse {
  baselineVersion?: string;
  minBaselineSessions: number;
  expectedHitRate: number;
  minCandidateSessions: number;
  versions: ImePatternHealthVersion[];
  patterns: ImePatternHealthPattern[];
  cells: ImePatternHealthCell[];
  alerts: ImePatternDriftAlert[];
  generatedAt: string;
}

export interface ImePatternHealthVersion {
  version: string;
  /** Sessions that delivered a histogram on this version (not the ImeVersionHistory session count). */
  sessions: number;
  firstSeenAt?: string;
  lastSeenAt?: string;
  /** Total sessions per ImeVersionHistory (includes sessions without a terminal run). */
  fleetSessions?: number;
}

/** A tracked IME version sighting. Permanent archive that survives data retention. */
export interface ImeVersionHistoryEntry {
  version: string;
  firstSeenAt: string;
  firstSeenSessionId: string;
  firstSeenTenantId: string;
  lastSeenAt: string;
  sessionCount: number;
  /** UTC time of the first sighting from a tenant OTHER than FirstSeenTenantId; null while only the first-seen tenant has reported the version. A version is shown to ordinary tenant members only once it is corroborated this way or its installer was archived with a matching ProductVersion — a single tenant's devices (the least trusted principal) cannot publish a version to every other tenant on their own. */
  corroboratedAt?: string;
  /** Outcome of the automatic installer archiving (ImeMsiArchiver): Archived, Queued (re-queued by a later sighting) or a Failed:* status; null for versions sighted before the feature existed or while the first archive job is still queued. Like the FirstSeen* identifiers, the archive fields are only serialized for Global Admin callers. */
  msiArchiveStatus?: string;
  /** UTC time of the last archive-status change (queue/attempt); drives the re-queue backoff. */
  msiArchiveUpdatedAt?: string;
  /** Blob path inside the ime-archive container, e.g. 1.104.102.0/IntuneWindowsAgent.msi. */
  msiArchiveBlobPath?: string;
  /** SHA-256 of the archived installer (hex, lowercase). */
  msiSha256?: string;
  /** Size of the archived installer in bytes. */
  msiBytes?: number;
  /** The URL the installer was downloaded from (CSP-reported or one of the distribution hosts). */
  msiSourceUrl?: string;
}

/** Redacted IME version row served to non-global callers of GET metrics/ime-versions: the platform-wide rollout facts without the first-seen tenant/session identifiers or the archive columns (see GetImeVersionHistoryFunction.BuildResponsePayload). */
export interface ImeVersionHistoryLeanEntry {
  version: string;
  firstSeenAt: string;
  lastSeenAt: string;
  sessionCount: number;
}

/** Response of GET auth/is-global-admin: whether the caller holds the Global Admin platform role, echoing the caller's UPN. */
export interface IsGlobalAdminResponse {
  isGlobalAdmin: boolean;
  /** Caller's UPN from the token, or null when the token carries no UPN claim — the key is omitted when null. */
  upn?: string;
}

/** Response of GET global/backups: every backupId in the critical-table-backups container, newest first. Serialized with SerializerOptions (the backup surface's own options), not the ApiJsonOptions pipeline. */
export interface ListBackupsResponse {
  backupIds: string[];
}

/** Success body of GET /api/global/raw/tables (ListRawTables): the queryable table names. */
export interface ListRawTablesResponse {
  count: number;
  tables: string[];
}

/** Response of GET config/{tenantId}/backups: the tenant's pre-write config snapshots, newest first. */
export interface ListTenantConfigBackupsResponse {
  tenantId: string;
  backups: TenantConfigBackupItem[];
}

/** Performance metrics for a single geographic location. */
export interface LocationMetrics {
  locationKey: string;
  country: string;
  region: string;
  city: string;
  loc: string;
  sessionCount: number;
  succeeded: number;
  failed: number;
  successRate: number;
  avgDurationMinutes: number;
  medianDurationMinutes: number;
  p95DurationMinutes: number;
  /** Average number of apps installed per session at this location */
  avgAppCount: number;
  /** Average minutes per app (AvgDurationMinutes / AvgAppCount) */
  minutesPerApp: number;
  /** Normalized score: 100 = global median, lower is better */
  appLoadScore: number;
  /** Average download throughput in bytes/sec at this location */
  avgThroughputBytesPerSec: number;
  totalDownloadBytes: number;
  /** Percentage difference from global avg duration (negative = faster) */
  durationVsGlobalPct: number;
  /** Percentage difference from global avg throughput (positive = faster) */
  throughputVsGlobalPct: number;
  /** Average agent→backend HTTP round-trip (ms) at this location, weighted per session by its request count. 0 when no session here carries latency data (pre-feature agents). A single corrupt session average (e.g. a request spanning a sleep/hibernate) can dominate this figure — use MedianApiLatencyMs as the display statistic. */
  avgApiLatencyMs: number;
  /** Median of the per-session average agent→backend round-trips (ms) at this location — robust against outlier sessions. 0 when no session here carries latency data. */
  medianApiLatencyMs: number;
  /** Sessions at this location that carry API-latency data */
  apiLatencySessionCount: number;
  /** Percentage difference from global median API latency (positive = slower/farther) */
  apiLatencyVsGlobalPct: number;
  isOutlier: boolean;
  /** "fast", "slow", or null */
  outlierDirection?: string;
  /** Sessions at this location that have DO telemetry data */
  doSessionCount: number;
  /** Weighted percentage of bytes from peers (0-100), computed from total peer/total DO bytes */
  avgDoPercentPeerCaching: number;
  /** Total bytes downloaded from all peer sources */
  totalDoBytesFromPeers: number;
  /** Total bytes downloaded from HTTP/CDN */
  totalDoBytesFromHttp: number;
  /** Bytes from LAN peers */
  totalDoBytesFromLanPeers: number;
  /** Bytes from group peers */
  totalDoBytesFromGroupPeers: number;
  /** Bytes from internet peers */
  totalDoBytesFromInternetPeers: number;
  /** Bytes from link-local peers (same subnet) */
  totalDoBytesFromLinkLocalPeers: number;
  /** Bytes served from a Microsoft Connected Cache (MCC) — separate from BytesFromPeers */
  totalDoBytesFromCacheServer: number;
}

/** Lean projection of LocationSessionRow used as the default drilldown row. Fields chosen to support the typical MCP triage flow (which session, when, where, how big a deal, was DO active). Built by GetGeographicLocationSessionsFunction.ToLeanRow. */
export interface LocationSessionLeanRow {
  sessionId: string;
  tenantId: string;
  serialNumber: string;
  deviceName: string;
  manufacturer: string;
  model: string;
  startedAt: string;
  /** Absent while the session has not completed. */
  completedAt?: string;
  status: SessionStatus;
  failureReason: string;
  /** Absent while the session carries no authoritative duration. */
  durationSeconds?: number;
  enrollmentType: string;
  geoCountry: string;
  geoCity: string;
  totalAppCount: number;
  hasDoTelemetry: boolean;
  /** Weighted peer-caching percentage for this session (0-100, one decimal). */
  doPercentPeerCaching: number;
}

/** Session row returned by the geographic drilldown endpoint. Extends SessionSummary with per-session Delivery Optimization aggregates so the user can troubleshoot DO usage without leaving the drilldown view. */
export interface LocationSessionRow {
  /** True if any app in this session has DO telemetry (DoDownloadMode >= 0). */
  hasDoTelemetry: boolean;
  /** Number of apps in this session that have DO telemetry. */
  doAppCount: number;
  /** Total number of app install summaries recorded for this session. */
  totalAppCount: number;
  /** Weighted peer-caching percentage for this session (0-100). */
  doPercentPeerCaching: number;
  doBytesFromPeers: number;
  doBytesFromHttp: number;
  doTotalBytesDownloaded: number;
  doBytesFromLanPeers: number;
  doBytesFromGroupPeers: number;
  doBytesFromInternetPeers: number;
  doBytesFromLinkLocalPeers: number;
  doBytesFromCacheServer: number;
  sessionId: string;
  tenantId: string;
  serialNumber: string;
  deviceName: string;
  manufacturer: string;
  model: string;
  startedAt: string;
  completedAt?: string;
  currentPhase: number;
  currentPhaseDetail: string;
  status: SessionStatus;
  failureReason: string;
  failureSource: string;
  verdictPath?: string;
  priorStatus?: string;
  priorVerdictPath?: string;
  reconcileReason: string;
  espSoftFailure: boolean;
  completionSource: string;
  adminMarkedAction?: string;
  validatedBy: string;
  eventCount: number;
  durationSeconds?: number;
  avgApiLatencyMs?: number;
  apiRequestCount?: number;
  connectionType?: string;
  enrollmentType: string;
  diagnosticsBlobName: string;
  diagnosticsBlobDestination?: string;
  lastEventAt?: string;
  lastIngestAt?: string;
  isPreProvisioned: boolean;
  resumedAt?: string;
  stalledAt?: string;
  isHybridJoin: boolean;
  isSelfDeployingProfile: boolean;
  isCloudPc: boolean;
  osName: string;
  osBuild: string;
  osDisplayVersion: string;
  osEdition: string;
  osLanguage: string;
  isUserDriven: boolean;
  agentVersion: string;
  imeAgentVersion: string;
  geoCountry: string;
  geoRegion: string;
  geoCity: string;
  geoLoc: string;
  platformScriptCount: number;
  remediationScriptCount: number;
  rebootCount: number;
  excessiveEventsAlerted: boolean;
  excessiveEventsAutoActioned: boolean;
  pendingActionsJson: string;
  pendingActionsQueuedAt?: string;
  failureSnapshotJson: string;
  deletionState: string;
  pendingDeletionManifestId?: string;
}

/** Per-line outcome row of one pattern test. */
export interface LogPatternLineResult {
  lineNumber: number;
  /** matched | no_match | parse_failed | regex_timeout */
  outcome: string;
  /** The named/numbered capture groups exactly as they would land in the emitted event's data (group "0" excluded, unsuccessful groups omitted). */
  groups?: Record<string, string>;
  matchedText?: string;
  /** cmtrace mode only: the parsed component/type/message the regex ran against. */
  component?: string;
  cmTraceType?: number;
  message?: string;
}

/** Aggregate outcome of one pattern test. Serialized camelCase to clients. */
export interface LogPatternTestResult {
  matchCount: number;
  parseFailureCount: number;
  timeoutCount: number;
  readonly lines: LogPatternLineResult[];
  readonly notes: string[];
}

/** Run report of a manual maintenance run (POST maintenance/trigger). */
export interface MaintenanceResult {
  success: boolean;
  error?: string;
  triggeredBy: string;
  triggeredAt: string;
  durationMs: number;
  stalledSessionsChecked: boolean;
  metricsAggregated: boolean;
  aggregatedDate?: string;
  dataCleanupExecuted: boolean;
  platformStatsRecomputed: boolean;
  devicesBlockedForExcessiveData: number;
  contactEmailsBackfilled: number;
}

/** One managed tenant as the managing tenant sees it. */
export interface ManagedTenantItem {
  tenantId: string;
  domain?: string;
  /** self-service (joined by invitation, removable here) | operator (provisioned by platform operators). */
  source: string;
  sinceUtc?: string;
  removable: boolean;
  /** Absent when not resolved (cap reached or read failure). */
  usage?: ManagedTenantQuotaUsage;
}

/** Response of GET delegations/managed. */
export interface ManagedTenantListResponse {
  homeTenantId: string;
  slots: DelegatedSlotUsageResponse;
  tenants: ManagedTenantItem[];
}

/** MCP organization budget of a managed tenant, nested in ManagedTenantItem. */
export interface ManagedTenantQuotaUsage {
  tenantPlan: string;
  tenantDailyLimit: number;
  tenantMonthlyLimit: number;
  tenantDailyUsed: number;
  tenantMonthlyUsed: number;
}

/** Response of GET health/mcp: the standalone MCP-server reachability probe. */
export interface McpHealthCheckResponse {
  timestamp: string;
  check: HealthCheck;
}

/** One account's share of the organization budget, nested in GetMcpOrganizationUsageResponse. */
export interface McpOrganizationUsageItem {
  userId: string;
  /** Absent for rows written before the UPN was recorded. */
  userPrincipalName?: string;
  /** True when the account is a delegated (MSP) administrator homed in another tenant. */
  delegated: boolean;
  /** The delegated administrator's home tenant; absent for the tenant's own members. */
  homeTenantId?: string;
  requestsToday: number;
  requestsThisMonth: number;
  requestsInRange: number;
  /** Absent for rows written before the timestamp was recorded. */
  lastRequestAt?: string;
}

/** 429 body written by McpQuotaEnforcementMiddleware when the per-user MCP daily/monthly quota is exhausted (structurally a success shape: first key is quotaExceeded). */
export interface McpQuotaExceededResponse {
  quotaExceeded: boolean;
  plan: string;
  /** Which window was exceeded ("daily"/"monthly") — always set on the blocked path. */
  scope?: string;
  /** Whose budget was exceeded: "user" (the caller's own plan) or "tenant" (the organization-wide windows). */
  level: string;
  /** Limit of the exceeded window. */
  limit: number;
  /** Used count of the exceeded window. */
  used: number;
  /** Reset time of the exceeded window, pre-formatted "yyyy-MM-ddTHH:mm:ssZ". */
  resetUtc: string;
  message: string;
  /** The MANAGED tenant whose organization windows blocked a delegated (MSP) read — its plan governs the budget, not the caller's. Absent when the caller's own tenant/plan was exceeded and on the all-managed-tenants-exhausted aggregate block. */
  targetTenantId?: string;
}

/** Effective quota state nested in GetMyMcpUsageResponse: the caller's own windows and the organization-wide windows of their tenant (shared by every member; 0 = unlimited). For a delegated (MSP) caller the tenant windows are those of their HOME tenant — reads into managed tenants are charged to the managed tenant per request and never appear here. */
export interface McpUsageQuotaNode {
  dailyLimit: number;
  monthlyLimit: number;
  dailyUsed: number;
  monthlyUsed: number;
  resetUtc: string;
  tenantPlan: string;
  tenantDailyLimit: number;
  tenantMonthlyLimit: number;
  tenantDailyUsed: number;
  tenantMonthlyUsed: number;
}

export interface McpUserEntry {
  upn: string;
  isEnabled: boolean;
  addedAt: string;
  addedBy: string;
  usagePlan?: string;
}

/** Per-tenant session-status tally envelope shared by MetricsSummary and MetricsSummaryGlobal. */
export interface MetricsSummaryResponse {
  success: boolean;
  summary: MetricsSummaryTenantItem[];
  windowDays: number;
}

/** One per-tenant status tally in MetricsSummaryResponse. WindowDays is repeated per item (envelope carries it too — historical wire shape, kept for parity). */
export interface MetricsSummaryTenantItem {
  tenantId: string;
  totalSessions: number;
  succeeded: number;
  failed: number;
  inProgress: number;
  pending: number;
  stalled: number;
  awaitingUser: number;
  incomplete: number;
  other: number;
  /** Failed over the TERMINAL outcomes only (Succeeded + Failed), percent rounded to one decimal; 0 without terminal sessions. */
  failureRate: number;
  windowDays: number;
}

/** Shared response of GET global/notifications and GET notifications: the active (non-dismissed) notifications visible to the caller, newest first. */
export interface NotificationListResponse {
  success: boolean;
  notifications: GlobalNotificationDto[];
}

/** Outcome of the last NVD stale-cache refresh walk on THIS instance (in-memory). */
export interface NvdRefreshRunWire {
  stale: number;
  refreshed: number;
  failed: number;
  /** Stale entries left untouched because NVD opened its throttle cooldown mid-run. */
  skippedThrottled: number;
  error?: string;
  /** Preformatted ISO-8601 start timestamp. */
  startedUtc: string;
  /** Preformatted ISO-8601 finish timestamp, or null (key omitted) while still running. */
  finishedUtc?: string;
}

/** 202/200 response body for the offboarding endpoint. Fields point the caller at the History row so subsequent reporting / status polling can resolve back to the audit trail. EarliestProcessingAt drives the "data deletion starts in mm ss" countdown in the Web UI's drain-barrier state. */
export interface OffboardResponse {
  tenantId: string;
  status: string;
  historyPartitionKey: string;
  historyRowKey: string;
  message: string;
  /** UTC timestamp before which the worker MUST NOT start Phase 2. Drives the cache-drain-barrier countdown UI. Absent on the idempotent-Completed/Failed branches. */
  earliestProcessingAt?: string;
}

export interface OpsEventEntry {
  id: string;
  category: string;
  eventType: string;
  severity: string;
  tenantId?: string;
  userId?: string;
  message: string;
  details?: string;
  timestamp: string;
}

/** Response of GET global/ops-events, paged and non-paged: the non-paged variant leaves NextLink null so the key is absent, exactly like the old literal. */
export interface OpsEventListResponse {
  success: boolean;
  count: number;
  events: OpsEventEntry[];
  /** Absolute-path link to the next page, or null on the last page / non-paged variant — the key is omitted when null. */
  nextLink?: string;
}

/** Distribution of a per-session statistic across a version bucket. */
export interface PercentileStats {
  p50: number;
  p95: number;
  max: number;
  avg: number;
  sampleCount: number;
}

export interface PerformanceMetrics {
  avgDurationMinutes: number;
  medianDurationMinutes: number;
  p95DurationMinutes: number;
  p99DurationMinutes: number;
  /** Number of sessions contributing to the duration distribution (after the >0 filter). */
  sampleCount: number;
  /** Number of sessions whose raw duration exceeded the clamp ceiling and were capped before aggregation. A non-zero value flags stuck/non-terminal sessions skewing the window — the percentiles above are computed on the clamped values, not the runaway wall-clock duration. */
  clampedSessionCount: number;
}

/** Defines a usage plan tier with request limits: the per-USER windows every account on the plan gets, and the organization-wide TENANT windows all members of a tenant on this plan share. Stored as JSON array in AdminConfiguration.PlanTierDefinitionsJson. 0 = unlimited for that window. */
export interface PlanTierDefinition {
  name: string;
  dailyRequestLimit: number;
  monthlyRequestLimit: number;
  description: string;
  /** Tenant-wide daily limit; null (not set) = the edition's catalog tenant limit, 0 = unlimited. */
  tenantDailyRequestLimit?: number;
  /** Tenant-wide monthly limit; null (not set) = the edition's catalog tenant limit, 0 = unlimited. */
  tenantMonthlyRequestLimit?: number;
}

/** Response of GET and PUT global/config/plan-tiers: the global usage-plan tier definitions. */
export interface PlanTierDefinitionsResponse {
  tiers: PlanTierDefinition[];
}

/** Response of GET global/metrics/platform (GetGlobalPlatformMetrics). */
export interface PlatformAgentMetricsResponse {
  sessions: SessionAgentMetric[];
  deliveryLatency?: DeliveryLatencyMetrics;
  crashRate?: CrashRateMetrics;
  computedAt: string;
  computeDurationMs: number;
  fromCache: boolean;
  windowDays: number;
  sessionLimit: number;
  /** Sessions the scan actually covered in the window (before the has-snapshots filter that shapes Sessions). Callers must compare THIS against SessionLimit to decide whether the window was truncated — Sessions.Count understates truncation on fleets where many sessions emit no agent_metrics_snapshot. */
  sessionsScanned: number;
}

/** Pre-computed platform-wide statistics for the public landing page. Stored as a single row (PartitionKey: "global", RowKey: "current"). Incremented during registration/ingest/login; the daily maintenance recompute treats every cumulative counter as a monotonic "since release" high-water-mark (raise-only — the scanned tables are retention-pruned, so a raw recompute would regress the figures). Only TotalSignedUpTenants is current-state (its source table is not retention-pruned). See MaintenanceService.BuildMonotonicPlatformStats. */
export interface PlatformStats {
  /** Total enrollment sessions monitored since launch */
  totalEnrollments: number;
  /** Total unique users who logged in */
  totalUsers: number;
  /** Total unique tenants using the platform (have at least one session) */
  totalTenants: number;
  /** Total tenants signed up (have a tenant configuration entry) */
  totalSignedUpTenants: number;
  /** Total unique device models seen (manufacturer + model) */
  uniqueDeviceModels: number;
  /** Total events processed across all tenants */
  totalEventsProcessed: number;
  /** Total successful enrollments */
  successfulEnrollments: number;
  /** Total analysis issues detected */
  issuesDetected: number;
  /** When these stats were last fully recomputed */
  lastFullCompute: string;
  /** When these stats were last updated (including incremental) */
  lastUpdated: string;
}

/** Platform usage metrics response */
export interface PlatformUsageMetrics {
  /** Session metrics */
  sessions: SessionMetrics;
  /** Tenant metrics */
  tenants: TenantMetrics;
  /** User metrics (requires Entra ID authentication) */
  users: UserMetrics;
  /** Performance metrics */
  performance: PerformanceMetrics;
  /** Hardware metrics */
  hardware: HardwareMetrics;
  /** Deployment type metrics (User Driven vs White Glove) */
  deploymentTypes: DeploymentTypeMetrics;
  /** App and script count metrics */
  appScripts: AppScriptMetrics;
  /** Platform statistics (cumulative since release) */
  platformStats?: PlatformStats;
  /** When these metrics were computed */
  computedAt: string;
  /** How long it took to compute (milliseconds) */
  computeDurationMs: number;
  /** Whether result is from cache */
  fromCache: boolean;
  /** Time window (in days) the metrics were computed over. */
  windowDays: number;
}

/** One approved tenant on the wire. Deliberately NOT the storage entity: the pre-2026-08-31 wire carried synthetic PreviewWhitelistEntity rows whose only real datum was the tenant id in partitionKey (plus garbage defaults) — the contract is now just the id. */
export interface PreviewWhitelistTenantEntry {
  tenantId: string;
}

/** Response of GET /api/progress/sessions/{sessionId}/events — the session's event stream after the serial knowledge proof passed. */
export interface ProgressGetSessionEventsResponse {
  success: boolean;
  sessionId: string;
  count: number;
  events: EnrollmentEvent[];
}

/** Response of GET /api/progress/sessions/lookup — resolves at most ONE session from the serial/device-name search term (Progress Portal knowledge-proof lookup). */
export interface ProgressLookupSessionResponse {
  success: boolean;
  /** True when a session matched the search term. */
  found: boolean;
  /** The matched session; null (key omitted on the wire) when nothing matched. */
  session?: SessionSummary;
}

/** Success body of GET /api/raw/events and /api/global/raw/events (QueryRawEvents / QueryRawEventsGlobal): raw event rows, PascalCase-verbatim stored columns. */
export interface QueryRawEventsResponse {
  /** Null on the global scope when no tenantId filter was given (cross-tenant query). */
  tenantId?: string;
  count: number;
  events: (Record<string, unknown>)[];
  /** Absent on the last page. */
  nextLink?: string;
  /** True when the server ended this page early because its scan budget was spent before pageSize index rows were walked. Nothing is lost: every event up to the cursor is on this page and NextLink resumes exactly after the last fully processed chunk. Absent on a page that filled or drained normally. */
  partial?: boolean;
}

/** Success body of GET /api/raw/sessions and /api/global/raw/sessions (QueryRawSessions / QueryRawSessionsGlobal): raw SessionsIndex rows, PascalCase-verbatim stored columns. */
export interface QueryRawSessionsResponse {
  /** Null on the global scope when no tenantId filter was given (cross-tenant query). */
  tenantId?: string;
  count: number;
  sessions: (Record<string, unknown>)[];
  /** Absent on the last page. */
  nextLink?: string;
}

/** Success body of GET /api/global/raw/tables/{tableName} (QueryRawTable): raw table rows, PascalCase-verbatim stored columns. */
export interface QueryRawTableResponse {
  table: string;
  count: number;
  entities: (Record<string, unknown>)[];
  /** Absent on the last page. */
  nextLink?: string;
}

/** Server-action queue acknowledgement (QueueSessionAction, 202 Accepted). */
export interface QueueSessionActionResponse {
  success: boolean;
  message: string;
  /** Server-stamped enqueue time (UTC). */
  queuedAt: string;
}

/** Lightweight result for the global quick-search typeahead. */
export interface QuickSearchResult {
  sessionId: string;
  serialNumber: string;
  deviceName: string;
  status: SessionStatus;
  startedAt: string;
  /** Which field matched the query: "sessionId", "serialNumber", or "deviceName". */
  matchedField: string;
}

/** Typeahead quick-search envelope (QuickSearchSessions). */
export interface QuickSearchSessionsResponse {
  success: boolean;
  count: number;
  results: QuickSearchResult[];
}

/** One observed reboot outage: the gap between the last pre-reboot event and the first post-reboot event, located via the lastBootUtc payload (audit Q7: the system_reboot_detected event itself is detection-time-stamped by the NEXT agent run and is never used as the reboot moment). */
export interface RebootSpan {
  startUtc: string;
  endUtc: string;
  seconds: number;
  /** Segment the reboot started in (TimeAttributionSegments key, or "unattributed" when it began in an unattributed hole). */
  segmentKey: string;
}

/** Run report of the legacy-session reclassification job (POST maintenance/reclassify-legacy). */
export interface ReclassificationResult {
  mode: string;
  dryRun: boolean;
  tenantsExamined: number;
  sessionsExamined: number;
  wouldChange: number;
  changed: number;
  toSucceeded: number;
  toIncomplete: number;
  keptFailed: number;
  skipped: number;
  errors: number;
  /** True when the maxSessions cap stopped the run before the backlog was exhausted — re-run to continue. */
  capReached: boolean;
  readonly samples: ReclassificationSample[];
}

export interface ReclassificationSample {
  tenantId: string;
  sessionId: string;
  oldStatus: string;
  newStatus: string;
  reason: string;
}

/** Response of POST maintenance/reclassify-legacy: the reclassification run report plus trigger attribution. See BackfillJobRunResponse for the shared key layout. */
export interface ReclassifyJobRunResponse {
  success: boolean;
  result: ReclassificationResult;
  triggeredBy: string;
  triggeredAt: string;
}

/** Structural health report for a session's persisted SignalLog + DecisionTransitions journal. Produced by GET /api/sessions/{id}/reducer-verification (Plan §M5, admin/ops endpoint, not tenant-exposed). Scope: this report covers structural invariants that can be checked without running the reducer — ordinal contiguity, cross-references, ReducerVersion drift, counts. A full engine replay with per-step diff would require polymorphic deserialisation of the DecisionSignal.Evidence payload and is a dedicated follow-up. */
export interface ReducerVerificationReport {
  tenantId: string;
  sessionId: string;
  signalCount: number;
  transitionCount: number;
  /** First transition's ReducerVersion (null when no transitions present). */
  storedReducerVersion?: string;
  /** The current live backend DecisionEngine.ReducerVersion. */
  currentReducerVersion: string;
  /** True when stored ≠ current — the session was journaled under a different reducer build. Plan §2.10 calls this out as a known drift signal rather than a bug; the report surfaces it so ops can decide if replay is still meaningful. */
  reducerVersionDrift: boolean;
  signalOrdinalsContiguous: boolean;
  signalOrdinalFirst: number;
  signalOrdinalLast: number;
  stepIndicesContiguous: boolean;
  stepIndexFirst: number;
  stepIndexLast: number;
  /** Transitions whose SignalOrdinalRef does not match any loaded signal row. A non-zero count indicates either a corrupted journal or — more likely — truncated data on the query (the verifier loaded a subset of transitions and the referenced signals fell outside the slice). */
  orphanedTransitionCount: number;
  /** True when the verifier re-played the persisted signal stream through the live backend DecisionEngine and compared the produced transitions to the stored journal. False when the replay was skipped — see SemanticReplaySkipReason. */
  semanticReplayPerformed: boolean;
  /** Discriminator when SemanticReplayPerformed is false. Known values: "empty_session", "reducer_version_drift", "non_contiguous_signal_ordinals", "non_contiguous_step_indices", "deserialization_failure". */
  semanticReplaySkipReason?: string;
  /** True when the replayed Stage matches the ToStage of the last stored transition. Only meaningful when SemanticReplayPerformed is true. */
  semanticReplayFinalStageMatches: boolean;
  /** The stage the replay arrived at (stringified SessionStage); null on skip. */
  replayedFinalStage?: string;
  /** Number of positions where the replayed transition diverged from the stored one on the compared fields (Trigger, FromStage, ToStage, Taken, DeadEndReason, StepIndex). 0 means perfect agreement. Individual divergences are emitted as replay_divergenceVerificationIssues up to a cap of 20. */
  transitionDivergenceCount: number;
  /** Human-readable issue stream for the Inspector's verification panel. */
  issues: VerificationIssue[];
}

/** A link to related documentation */
export interface RelatedDoc {
  /** Display title for the link */
  title: string;
  /** URL to the documentation */
  url: string;
}

/** Response of POST global/delegated-slots/{tenantId}/release-hold: how many holds ended now. */
export interface ReleaseDelegatedSlotHoldResponse {
  homeTenantId: string;
  released: number;
}

/** A remediation step with title and sub-steps */
export interface RemediationStep {
  /** Title of the remediation approach */
  title: string;
  /** Ordered steps to execute */
  steps: string[];
}

/** Response of POST rules/reseed-from-github: per-catalog reseed counters. */
export interface ReseedFromGitHubResponse {
  success: boolean;
  message: string;
  gather: ReseedRuleCountsNode;
  analyze: ReseedRuleCountsNode;
  ime: ReseedTableCountsNode;
  cpeCommunityMappings: ReseedTableCountsNode;
  cpeSeedMappings: ReseedTableCountsNode;
}

/** Response of POST rules/ime-log-patterns/reseed (Global Admin only). */
export interface ReseedImeLogPatternsResponse {
  success: boolean;
  message: string;
  deleted: number;
  written: number;
}

/** Reseed counters for a rule table with sunset handling (gather / analyze). */
export interface ReseedRuleCountsNode {
  deleted: number;
  written: number;
  /** Orphan per-tenant RuleState rows cleaned while sunsetting rules missing from the new catalog. */
  orphanStatesGcd: number;
  /** Sunset rules skipped on failure (retried on the next reseed). */
  sunsetSkipped: number;
}

/** Reseed counters for a plain delete-and-rewrite table (IME patterns, CPE mappings). */
export interface ReseedTableCountsNode {
  deleted: number;
  written: number;
}

/** A condition that is evaluated against the event stream */
export interface RuleCondition {
  /** Descriptive name for this signal (e.g., "proxy_407_error") */
  signal: string;
  /** Source of the signal: "event_type", "event_data", "event_data_array", "phase_duration", "event_count", "app_install_duration", "event_correlation", "clock_skew". Must stay in sync with DryRunAnalyzeRuleFunction.KnownSources and the RuleEngine switch. */
  source: string;
  /** For "clock_skew" only: which device-clock metric to evaluate — "clock_jump" (persistent step in the device's clock frame mid-session) or "sustained_offset" (whole session ran on a clock off by ≥ Value). Value is the threshold in seconds; Operator is limited to gt/gte on the magnitude. */
  skewMetric?: string;
  /** Event type to match on. For "event_type"/"event_data": the event type to match. For "event_correlation": the FIRST event type (Event A). */
  eventType: string;
  /** Data field to match on. For "event_data": field to check with Operator/Value. For "event_data_array": the ARRAY field to iterate (e.g. "artifacts"). For "event_correlation": optional filter field on Event B (the second event). Uses dot notation for nested fields (e.g., "data.errorCode"). */
  dataField: string;
  /** For "event_data_array" only: the sub-field on each array element to test with Operator/Value (e.g. "identity"). When empty, each element is treated as a scalar. The condition matches when ANY element satisfies the operator (e.g. one artifact whose identity does not match an allow-list regex). */
  itemField: string;
  /** Comparison operator: "equals", "not_equals", "contains", "not_contains", "regex", "not_regex", "gt", "lt", "gte", "lte", "exists", "not_exists", "count_gte", "count_per_group_gte", "in", "not_in". For "event_correlation": operator for the Event B filter (applied to DataField). Must stay in sync with DryRunAnalyzeRuleFunction.KnownOperators. */
  operator: string;
  /** Value to compare against. For "event_correlation": value for the Event B filter. */
  value: string;
  /** Whether this condition must match for the rule to fire If false, it only contributes to confidence scoring */
  required: boolean;
  /** Optional value filter for "event_count": only events whose FilterField satisfies FilterOperator/FilterValue are counted (e.g. count only performance_snapshot events with memory_used_percent > 90). Applies before counting for both count_gte and count_per_group_gte. */
  filterField: string;
  /** Operator for the count filter. Uses same operators as the main Operator field. */
  filterOperator: string;
  /** Value for the count filter. */
  filterValue: string;
  /** The second event type to correlate with (Event B). Example: "app_install_failed" */
  correlateEventType: string;
  /** The data field to join on — must have the same value in both Event A and Event B. Example: "appId" means both events must share the same appId value. */
  joinField: string;
  /** Maximum time in seconds between Event A and Event B. Null or 0 means no time limit. */
  timeWindowSeconds?: number;
  /** Optional suppression: if an event of SuppressByEvent.EventType exists with the same SuppressByEvent.JoinField value as the matched event, the match is skipped (the "resolving" event suppresses the finding). Used to prevent rules from firing when a subsequent event resolved the issue. */
  suppressByEvent?: SuppressByEventConfig;
  /** Optional filter field on Event A (the first event). Combined with EventAFilterOperator and EventAFilterValue. */
  eventAFilterField: string;
  /** Operator for the Event A filter. Uses same operators as the main Operator field. */
  eventAFilterOperator: string;
  /** Value for the Event A filter. */
  eventAFilterValue: string;
}

/** Full diagnostic trace of one dry-run evaluation. Serialized camelCase to clients. */
export interface RuleDryRun {
  verdict: string;
  /** Number of events in the session the rule was evaluated against. */
  eventCount: number;
  readonly preconditions: RuleDryRunPrecondition[];
  readonly conditions: RuleDryRunCondition[];
  /** Empty unless all required conditions were met (mirrors the production path, which never reaches factor evaluation otherwise). */
  readonly confidenceFactors: RuleDryRunFactor[];
  baseConfidence: number;
  /** base + matched factor weights, capped at 100. Null when the evaluation ended before the confidence stage (precondition skip / required miss / nothing matched). */
  finalConfidence?: number;
  confidenceThreshold: number;
  /** True only for verdict "fired" AND the rule's effective MarkSessionAsFailed flag. The dry-run itself never touches the session. */
  wouldMarkSessionAsFailed: boolean;
  /** The evidence map exactly as the production path would persist it on a RuleResult — keys are condition signals (plus factor_* markers). Clients use it to preview {{token}} interpolation of explanation/remediation. Values are heterogeneous evidence objects by design. */
  matchedConditions?: Record<string, unknown>;
}

export interface RuleDryRunCondition {
  signal: string;
  source: string;
  eventType?: string;
  required: boolean;
  matched: boolean;
  /** Matched: the evidence dictionary (eventId, timestamp, field, value, …). Not matched: the evaluator's reason string (e.g. "no matching events"). */
  evidence?: unknown;
  /** How many session events have this condition's eventType at all — the first thing an author checks when a condition unexpectedly doesn't match. Null when the condition has no eventType. */
  matchingEventCount?: number;
}

export interface RuleDryRunFactor {
  signal: string;
  condition: string;
  weight: number;
  matched: boolean;
}

export interface RuleDryRunPrecondition {
  source: string;
  eventType: string;
  dataField?: string;
  operator?: string;
  value?: string;
  passed: boolean;
}

/** A device-fact gate evaluated before a rule's conditions. Pure boolean filter — does not contribute to evidence or confidence. When any precondition on a rule fails, the rule is silently skipped (no result emitted). Currently only event_data source is supported. */
export interface RulePrecondition {
  /** Source of the fact. Currently only event_data. */
  source: string;
  /** Event type carrying the field to test (e.g., hardware_spec, os_info). */
  eventType: string;
  /** Data field to test (dot notation supported for nested fields). */
  dataField: string;
  /** Comparison operator. Same vocabulary as Operator minus the count_/correlation-specific operators. */
  operator: string;
  /** Value to compare against. Boolean values are stringified ("true"/"false"). */
  value: string;
  /** Optional human-readable note explaining the intent (e.g., "skip on virtual machines"). Not evaluated. */
  description?: string;
}

/** One ACTIVE rule-frequency regression (F3, insights spec §F3): an analyze rule whose 7-day hit rate rose ≥2× over its 28-day baseline with disjoint Wilson intervals. Persisted as the ruleregression|{ruleId} keyspace of the notification tracker table — the row IS the dedup (one bell per episode), the badge state (rules page) and the regressions[] payload (rule-stats response). Deleted when the rate re-arms (falls under 1.5× baseline or stops firing) or by the tracker's 30-day retention sweep (spec: retention cleanup re-arms). Numbers are refreshed on every radar pass while the episode stays active; FirstNotifiedAt never moves. */
export interface RuleRegressionAlert {
  tenantId: string;
  ruleId: string;
  ruleTitle: string;
  windowFireCount: number;
  windowSessionCount: number;
  baselineFireCount: number;
  baselineSessionCount: number;
  windowRatePct: number;
  baselineRatePct: number;
  /** Window rate ÷ baseline rate. Null when the baseline rate is 0 (a NEW signal has no finite lift — never invented). */
  lift?: number;
  /** Trailing 7-day window ("yyyy-MM-dd", inclusive) the current numbers describe. */
  windowStartDate: string;
  windowEndDate: string;
  /** Dimension concentration captured when the alert FIRST fired (stable story); null = no clear concentration. */
  dimension?: RuleRegressionDimension;
  /** When the episode first fired (bell + ops event moment). Never moves on refresh; drives the 30d retention re-arm. */
  firstNotifiedAt: string;
  /** Last radar pass that re-confirmed/refreshed this episode. */
  lastEvaluatedAt: string;
}

/** The dominant dimension value among a regression's hit sessions — CORRELATION only, and every consumer's wording must say so ("correlated — not necessarily causal", insights spec §F3 / truthfulness rule 6). Null on an alert means "no clear dimension concentration" — the radar never stretches for one. */
export interface RuleRegressionDimension {
  /** "osBuild", "model", "agentVersion" or "imeVersion". */
  dimension: string;
  value: string;
  /** Hit sessions carrying this value (gate: ≥5). */
  hitCount: number;
  hitSharePct: number;
  allSharePct: number;
  /** HitShare ÷ AllShare (gate: ≥2.0). */
  lift: number;
}

/** Result of an analyze rule evaluation against a session's events Stored in the RuleResults table and displayed in the session detail UI */
export interface RuleResult {
  /** Unique identifier for this result */
  resultId: string;
  /** Session this result belongs to */
  sessionId: string;
  /** Tenant this result belongs to */
  tenantId: string;
  /** The rule that produced this result */
  ruleId: string;
  /** Human-readable title of the rule */
  ruleTitle: string;
  /** Severity level: "info", "warning", "high", "critical" */
  severity: string;
  /** Rule category: network, identity, enrollment, apps, esp, device */
  category: string;
  /** Confidence score (0-100) Higher = more confident this issue is the root cause */
  confidenceScore: number;
  /** Detailed explanation of the detected issue */
  explanation: string;
  /** Remediation steps for the detected issue */
  remediation: RemediationStep[];
  /** Links to relevant documentation */
  relatedDocs: RelatedDoc[];
  /** Evidence: which conditions matched and their values */
  matchedConditions: Record<string, unknown>;
  /** When this issue was detected */
  detectedAt: string;
  /** When this finding FIRST fired for the session (stable across interim refreshes and the terminal finalization pass). Null on legacy rows — treat DetectedAt as the anchor. */
  firstDetectedAt?: string;
  /** When the rule was last (re-)evaluated for this session. Null on legacy rows. */
  lastEvaluatedAt?: string;
  /** True while the finding comes from an interim run (whiteglove_sealed / on_event trigger) and has not yet been confirmed by the terminal finalization pass. UI renders these with a "preliminary" badge. */
  isInterim: boolean;
  /** Set when a later evaluation no longer fired the rule (the session healed). Resolved rows are kept for audit but excluded from issue counts and hidden by default. */
  resolvedAt?: string;
  /** Set once the finding's channel notification was sent. The notification dedupe anchors here (one notification per session+rule), decoupled from row existence so interim refreshes and the manual reanalyze rebuild can never re-arm a duplicate send. */
  notifiedAt?: string;
}

/** Echo of the effective date window ("yyyy-MM-dd"). */
export interface RuleStatsPeriod {
  start: string;
  end: string;
}

/** Response of GET metrics/rule-stats and GET global/metrics/rule-stats: per-rule firing aggregates over the requested window, the active rule-frequency regression episodes (tenant scope only — empty on the cross-tenant aggregate), and window totals. */
export interface RuleStatsResponse {
  rules: RuleStatsRuleAggregate[];
  regressions: RuleRegressionAlert[];
  summary: RuleStatsSummary;
}

/** One rule's aggregate across all dates in the window (fires descending). */
export interface RuleStatsRuleAggregate {
  ruleId: string;
  ruleType: string;
  ruleTitle: string;
  category: string;
  severity: string;
  fireCount: number;
  evaluationCount: number;
  sessionsEvaluated: number;
  /** Fires per evaluation as a percentage (one decimal); 0 with no evaluations. */
  hitRate: number;
  /** Average confidence score across fires (one decimal); 0 with no fires. */
  avgConfidenceScore: number;
  /** Per-day trend rows, oldest first (one row per stored date). */
  trend: RuleTrendPoint[];
}

/** Window totals of a rule-stats response. */
export interface RuleStatsSummary {
  totalEvaluations: number;
  totalFires: number;
  overallHitRate: number;
  /** Rule id with the most fires; absent when the window holds no rules. */
  topRuleByFireCount?: string;
  /** Distinct rule count — set on the global route only (the key is absent on the tenant route, preserving its historical shape). */
  uniqueRules?: number;
  period: RuleStatsPeriod;
}

/** One day of a rule's trend ("yyyy-MM-dd"). */
export interface RuleTrendPoint {
  date: string;
  fireCount: number;
  evaluationCount: number;
}

/** Response of POST vulnerability/cpe-mapping on a successful save. */
export interface SaveCustomCpeMappingResponse {
  success: boolean;
  message: string;
  /** Sanitized Table Storage row key the mapping was stored under. */
  rowKey: string;
}

/** SearchSessions envelope. Sessions carries full SessionSummary items, or dictionary projections of them when the caller passed a fields= subset. */
export interface SearchSessionsResponse {
  success: boolean;
  count: number;
  sessions: Partial<SessionSummary>[];
  /** Absent when there is no further page. */
  nextLink?: string;
}

export interface SessionAgentMetric {
  sessionId: string;
  tenantId: string;
  deviceName?: string;
  manufacturer?: string;
  model?: string;
  startedAt?: string;
  status?: string;
  agentVersion?: string;
  snapshotCount: number;
  totalBytesUp: number;
  totalBytesDown: number;
  totalRequests: number;
  avgCpu: number;
  maxCpu: number;
  avgWorkingSet: number;
  maxWorkingSet: number;
  avgPrivateBytes: number;
  avgLatency: number;
  avgSpoolDepth: number;
  maxSpoolDepth: number;
  peakSpoolDepth: number;
  maxSpoolFileBytes: number;
  totalEventsEmitted: number;
  spoolPressureDetected: boolean;
}

/** Wire shape of one session annotation as returned by the session-scoped annotation endpoints (lane implied by context, tenant/session implied by the route). Built by AnnotationWire.ToWire. */
export interface SessionAnnotationItem {
  lane: string;
  /** One of the annotation verdict vocabulary values, or null (note-only annotation) — the key is omitted when null. */
  verdict?: string;
  /** Free-text note, or null (verdict-only annotation) — the key is omitted when null. */
  note?: string;
  authorUpn: string;
  authorDisplayName: string;
  createdByUpn: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  /** Snapshot of the rule ids that had fired for the session at write time. */
  ruleIds: string[];
}

/** Shared response of the annotation list endpoints (global/session-annotations and sessions/annotations/list): one page of scoped annotations with an optional continuation link. */
export interface SessionAnnotationListResponse {
  success: boolean;
  count: number;
  annotations: SessionAnnotationScopedItem[];
  /** Absolute-path link to the next page, or null on the last page — the key is omitted when null. */
  nextLink?: string;
}

/** Wire shape of one session annotation in the cross-session list endpoints, where each row carries its own tenant/session scope. Built by AnnotationWire.ToWireWithScope. */
export interface SessionAnnotationScopedItem {
  tenantId: string;
  sessionId: string;
  lane: string;
  /** One of the annotation verdict vocabulary values, or null (note-only annotation) — the key is omitted when null. */
  verdict?: string;
  /** Free-text note, or null (verdict-only annotation) — the key is omitted when null. */
  note?: string;
  authorUpn: string;
  authorDisplayName: string;
  createdByUpn: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  /** Snapshot of the rule ids that had fired for the session at write time. */
  ruleIds: string[];
}

/** One session row in a cascade-deletion state (GetSessionDeletionsList). */
export interface SessionDeletionListItem {
  tenantId: string;
  sessionId: string;
  deletionState: string;
  manifestId: string;
  /** Row timestamp, pre-formatted round-trip ("o"). */
  timestamp: string;
  ageMinutes: number;
}

/** Worker progress projection nested in GetSessionDeletionManifestResponse. */
export interface SessionDeletionProgressWire {
  snapshotSha256: string;
  completedStepOrders: number[];
  verificationDone: boolean;
  tombstoneStarted: boolean;
  completedAt?: string;
  aggregateDecrementsApplied: number;
  restoreReIncrementsApplied: number;
  lastFailureType?: string;
  lastFailureMessage?: string;
  /** The verifier's OBSERVED residual count, capped at the sample size (lower bound at the cap). */
  lastObservedResidualCount?: number;
  lastResidualSampleJson?: string;
}

/** Success body (202 Accepted) of the V2 cascade-delete enqueue — the non-success arms stay anonymous error bodies by design (one shape: success=false + message). */
export interface SessionDeletionQueuedResponse {
  success: boolean;
  /** Always "queued". */
  status: string;
  manifestId?: string;
  message: string;
}

/** Paged session listing envelope shared by GetSessions, GetAllSessions, SearchSessionsByCve and SearchSessionsByEvent (identical wire shape). */
export interface SessionListResponse {
  success: boolean;
  count: number;
  sessions: SessionSummary[];
  /** Absent when there is no further page. */
  nextLink?: string;
  /** Managed tenants dropped from a delegated (MSP) MCP fleet aggregate because their organization MCP budget is exhausted (each managed tenant's own plan governs it). Absent unless at least one tenant was excluded; only GetAllSessions ever sets it. */
  quotaExcludedTenants?: string[];
}

export interface SessionMetrics {
  total: number;
  /** Cumulative enrollment sessions since the tenant signed up (retention-independent per-tenant counter, see TenantStats). Null on the platform-wide (all-tenants) metrics, which carry the cumulative figure in PlatformStats instead. */
  totalAllTime?: number;
  today: number;
  last7Days: number;
  last30Days: number;
  succeeded: number;
  failed: number;
  inProgress: number;
  /** Terminal, non-failure sessions (timeout reclassification): the sweep classified them as Incomplete instead of Failed. Surfaced as its own count and deliberately excluded from SuccessRate (denominator = Succeeded + Failed only), mirroring SessionStats and FleetHealthStats. See tasks/enrollment-status-reclassification.md §5. */
  incomplete: number;
  successRate: number;
}

/** Success body of GET global/session-reports/download-url: short-lived SAS download URL for a session report blob. */
export interface SessionReportDownloadUrlResponse {
  success: boolean;
  downloadUrl: string;
}

/** Success body of GET global/session-reports — both the non-paged and the paged variant (the non-paged variant simply carries no nextLink; WhenWritingNull keeps the wire identical to the historical shape). */
export interface SessionReportListResponse {
  success: boolean;
  count: number;
  reports: SessionReportMetadata[];
  /** Absolute-path link to the next page; null/absent on the last page and in non-paged responses. */
  nextLink?: string;
}

/** Session report metadata for the admin-config reports table. Both Session-context reports and Diag-Files-only reports share this row schema — the ReportType discriminator distinguishes the two, and SessionId is empty for diag-files submissions. */
export interface SessionReportMetadata {
  reportId: string;
  tenantId: string;
  sessionId: string;
  comment: string;
  email: string;
  blobName: string;
  submittedBy: string;
  submittedAt: string;
  adminNote: string;
  /** One of ReportTypes. Defaults to "session" so legacy rows map cleanly. */
  reportType: string;
  /** Flat name of the session diagnostics archive copied into the session-reports container at submit time. Null when the copy was never requested or failed. */
  diagnosticsBlobName?: string;
  /** Outcome of the diagnostics copy: "Copied" or one of the "Failed:*" reasons. Null when the submitter did not request the copy. */
  diagnosticsCopyStatus?: string;
}

/** Body of POST global/sessions/{id}/restore for every outcome (success AND reject — the outcome/status mapping decides the HTTP code, the shape stays one). */
export interface SessionRestoreResponse {
  success: boolean;
  /** SessionRestoreOutcome name (e.g. "Restored", "DryRunOk", "RejectManifestNotFound"). */
  outcome: string;
  /** "full" | "partial" | "dryRun"; absent on early rejects. */
  mode?: string;
  /** Operator-readable reason; absent on clean successes. */
  message?: string;
  /** Reject diagnostics; absent otherwise. */
  currentState?: string;
  /** Reject diagnostics; absent otherwise. */
  pendingManifestId?: string;
  rowsRestoredByTable: Record<string, number>;
  rowsSkippedByTable: Record<string, number>;
  wouldRestoreByTable: Record<string, number>;
  inventoryReIncrements: number;
  durationMs: number;
}

/** Aggregated session counters for the dashboard stats cards. Computed server-side over a windowed scan of the SessionsIndex so the numbers don't drift with whatever the client happens to have paginated into memory. */
export interface SessionStats {
  /** Window the windowed counters were computed over (matches request). */
  days: number;
  /** InProgress sessions inside the window. Used for the "Active Sessions" card. */
  activeCount: number;
  /** Total sessions started inside the window. */
  totalLastNDays: number;
  succeededLastNDays: number;
  failedLastNDays: number;
  /** Terminal, non-failure sessions in the window (tasks/enrollment-status-reclassification.md): the sweep saw no completion or explicit failure. Reported as the third headline bucket and deliberately excluded from SuccessRatePct (which is over Succeeded + Failed only). */
  incompleteLastNDays: number;
  /** Succeeded / (Succeeded + Failed) * 100, rounded. Zero when no terminal sessions are in the window (the card renders "0%" rather than NaN). */
  successRatePct: number;
  /** Average duration of Succeeded sessions in the window, in minutes (rounded). Kept for API compatibility; the dashboard card leads with the median, which multi-hour outliers cannot drag around. */
  avgDurationMinutes: number;
  /** Median duration in minutes over the same Succeeded population. */
  medianDurationMinutes: number;
  /** 90th-percentile duration in minutes over the same population — the tail signal. */
  p90DurationMinutes: number;
  /** Sessions whose StartedAt is on or after UTC midnight of the current day. */
  totalToday: number;
  failedToday: number;
  /** UTC timestamp of when the snapshot was produced (server clock). */
  computedAt: string;
}

/** Dashboard stats envelope shared by GetSessionStats and GetAllSessionStats. */
export interface SessionStatsResponse {
  success: boolean;
  stats: SessionStats;
  /** See QuotaExcludedTenants; only GetAllSessionStats ever sets it. */
  quotaExcludedTenants?: string[];
}

/** Status of an enrollment session */
export type SessionStatus = "InProgress" | "Pending" | "Stalled" | "Succeeded" | "Failed" | "Unknown" | "AwaitingUser" | "Incomplete";

/** Session summary for UI display */
export interface SessionSummary {
  sessionId: string;
  tenantId: string;
  serialNumber: string;
  deviceName: string;
  manufacturer: string;
  model: string;
  startedAt: string;
  completedAt?: string;
  currentPhase: number;
  currentPhaseDetail: string;
  status: SessionStatus;
  failureReason: string;
  /** Origin of a Failed status — persisted ONLY on a Failed write (a non-Failed verdict discards it; see VerdictPath for the status-independent attribution). Values: - "" / null: agent-reported (default; terminal enrollment_failed event) - "rule:<RuleId>": session failed because an analyze rule with MarkSessionAsFailed fired - "manual": operator flipped the session via the portal - "max_lifetime_watchdog": the agent's max-lifetime shutdown verdict was Failed Consumers use this to render rule-based failures distinctly (badge + link to rule). */
  failureSource: string;
  /** Machine-readable origin of the CURRENT Status — which code path wrote it (VerdictPaths vocabulary, e.g. agent:complete, sweep:r5_incomplete, manual:failed). Stamped on every status write regardless of status, so the verdict calibration aggregate can count each path. Null on rows written before instrumentation (read-side derivation covers those). */
  verdictPath?: string;
  /** The verdict this session carried BEFORE the current status overrode it — set only when a write replaces a prior Succeeded/Failed/Incomplete/AwaitingUser (admin mark, late agent completion upgrading a sweep verdict, retro-reclassification). Together with PriorVerdictPath this is the correction stream: "path X was overridden". Null when the status never overrode a verdict. */
  priorStatus?: string;
  /** The VerdictPath of the overridden prior verdict; see PriorStatus. */
  priorVerdictPath?: string;
  /** Non-empty only when the BACKEND (not the agent) declared this session Succeeded: either the maintenance timeout sweep reconciled it (e.g. "user completed setup — desktop + Windows Hello observed") or a late completion report upgraded a prior Failed/Incomplete/AwaitingUser verdict. Carries the human-readable justification so operators can always tell a backend-declared success from an agent-reported one. Admin-marked successes are attributed via AdminMarkedAction instead and leave this empty. */
  reconcileReason: string;
  /** True only on a Succeeded session that completed while an ESP-failure advisory was still unresolved: the ESP demonstrably gave up on at least one blocking item (typically the 30-min timeout wall) and the user most likely pressed "Continue anyway" — the device reached a working desktop, but not everything the ESP tracked finished inside its window. Rendered as the amber "completed with issues" badge. False/absent = clean success. */
  espSoftFailure: boolean;
  /** How a soft-failure completion was detected. Values: - "continue_anyway_observation": Device-phase ESP failure, tenant opted into the Continue-Anyway observation window, real-user desktop arrived inside it - "continue_anyway_post_accountsetup": classic advisory defang (AccountSetup had already been entered when the ESP failure fired) Empty on clean successes. */
  completionSource: string;
  /** Non-null only when an administrator explicitly flipped the session via the portal (MarkSessionSucceeded / MarkSessionFailed). Values: null (default, agent-driven), "Succeeded", "Failed". This is the authoritative source for the backend's AdminAction response field sent to agents. Previously the backend inferred admin-override from "status is terminal + current event is not a completion marker", which fired falsely on every post-completion event the agent sent (agent_shutting_down, diagnostics_uploaded, enrollment_summary_shown). The dedicated field eliminates that false-positive. */
  adminMarkedAction?: string;
  /** Which backend device-validation path accepted this device at session registration — ValidatorType name as string: "AutopilotV1" (Autopilot S/N lookup), "CorporateIdentifier", "DeviceAssociation" (device preparation), "CloudPc", or "Bootstrap" (pre-MDM token). Latest non-Unknown validation wins (a Bootstrap session re-registering under cert auth upgrades to the cert-path validator). Empty for sessions that predate this field or tenants with device validation off. */
  validatedBy: string;
  eventCount: number;
  durationSeconds?: number;
  /** Session-wide average HTTP round-trip latency (ms) of the agent's calls to the backend API, as measured on the device (includes network RTT + TLS + server processing). Projected at ingest from the cumulative counters of the latest agent_metrics_snapshot (net_total_latency_ms / net_total_requests). Null for V1 sessions and sessions from agents that predate the field. Aggregate across sessions weighted by ApiRequestCount — e.g. per GeoCountry to compare regional backend reachability. */
  avgApiLatencyMs?: number;
  /** Number of HTTP requests behind AvgApiLatencyMs (the cumulative net_total_requests at the latest snapshot). Needed as the weight when averaging latency across sessions. Null whenever AvgApiLatencyMs is. */
  apiRequestCount?: number;
  /** Active network connection type during enrollment: "WiFi" or "Ethernet". Projected at ingest from the latest network_interface_info event (last emission wins — a device that switches media mid-enrollment reports the most recent state). Null for sessions that predate the projection. */
  connectionType?: string;
  /** Enrollment type: "v1" (Autopilot Classic/ESP) or "v2" (Windows Device Preparation). Defaults to "v1" for sessions that predate this field. */
  enrollmentType: string;
  /** Blob name of the uploaded diagnostics archive (null if not uploaded). Used to construct a download URL. Path semantics depend on : CustomerSas: blob name only (e.g. AgentDiagnostics-...zip); download builds the URL from the tenant's container SAS.Hosted: full path including the {tenantId}/ prefix (e.g. {tenantId}/AgentDiagnostics-...zip); download streams directly via the backend connection string. */
  diagnosticsBlobName: string;
  /** Where the diagnostics archive for THIS session was uploaded. Frozen at upload time so the download path can route correctly even if the tenant later switches DiagnosticsUploadDestination. "CustomerSas" — blob lives in the customer's storage; download uses the tenant's SAS URL (current behaviour)."Hosted" — blob lives in the backend's storage under {tenantId}/; download streams via the Functions connection string.null (legacy rows that predate this field) — treated as "CustomerSas" by the download path for back-compat. */
  diagnosticsBlobDestination?: string;
  /** Timestamp of the most recently received event for this session. Updated on every event batch ingestion. Used by maintenance to detect sessions that are still actively sending data beyond the configured window. Null for sessions that predate this field. */
  lastEventAt?: string;
  /** SERVER-side time of the most recent event ingest for this session. lives in the DEVICE clock frame (it is the maximum agent-supplied event timestamp). Comparing it against a server-derived cutoff mixes two clock frames: a device whose clock runs slow — or one whose IME-derived events carry a timezone skew — is judged silent far too early or not at all (measured in the field: skews from -17 h to +1 h, see tasks/todo.md). This field is stamped from the server clock on every ingest, so silence detection can compare like with like. Null for sessions that predate this field — callers MUST fall back to LastEventAt so rollout has no blind window. */
  lastIngestAt?: string;
  /** Whether this session used WhiteGlove (Pre-Provisioning). Set when a whiteglove_complete event is processed. */
  isPreProvisioned: boolean;
  /** Timestamp when the WhiteGlove session resumed for user enrollment (Part 2). Set when the agent sends a whiteglove_resumed event or re-registers from Pending state. Used to compute the user enrollment duration (Duration 2) for Teams notifications. */
  resumedAt?: string;
  /** Timestamp when the session was marked as Stalled. Set when the agent sends a session_stalled event (after 60 min without progress) or when the backend 2h maintenance sweep detects agent silence. Cleared (null) when the session heals back to InProgress via a new real event. */
  stalledAt?: string;
  /** Whether the Autopilot profile indicates Hybrid Azure AD Join. Derived from CloudAssignedDomainJoinMethod == 1 in the Autopilot profile. */
  isHybridJoin: boolean;
  /** Whether the Autopilot profile carries the self-deploying/kiosk OOBE marker (CloudAssignedOobeConfig bits 0x20|0x40). Sent by the agent at registration; sticky-true across re-registrations. */
  isSelfDeployingProfile: boolean;
  /** Whether the agent identified the device as a Windows 365 Cloud PC (Windows365 registry key + CloudManagedDesktopExtension service, marker AND). Sent by the agent at registration; sticky-true across re-registrations. Agent-reported context — independent of ValidatedBy == CloudPc, which is the server-derived cert-CN-bound Graph verification. */
  isCloudPc: boolean;
  osName: string;
  osBuild: string;
  osDisplayVersion: string;
  osEdition: string;
  osLanguage: string;
  isUserDriven: boolean;
  agentVersion: string;
  imeAgentVersion: string;
  geoCountry: string;
  geoRegion: string;
  geoCity: string;
  geoLoc: string;
  platformScriptCount: number;
  remediationScriptCount: number;
  /** Number of system reboots observed during the enrollment. Counts the agent's system_reboot_detected events (V2 only — one per reboot, detected via the System event-log boot time on the next agent start). Maintained incrementally per ingest batch for a live value, then overwritten with an authoritative distinct count from the Events table when the session reaches a terminal status (self-corrects any at-least-once batch double-count). 0 for V1 sessions and sessions that predate this field. */
  rebootCount: number;
  /** True once maintenance has emitted an ExcessiveSessionEvents ops alert for this session. Prevents duplicate alerts on subsequent maintenance runs for the same runaway session. */
  excessiveEventsAlerted: boolean;
  /** True once maintenance has auto-blocked or auto-killed the device for this runaway session (see ExcessiveEventAutoActionMode). Independent of ExcessiveEventsAlerted so warn and auto-action are each idempotent on their own — admins can change the auto-action mode mid-flight without re-firing the warn. */
  excessiveEventsAutoActioned: boolean;
  /** JSON-serialized List`1 of ServerAction pending delivery to the agent. Empty string when no actions are queued. The Ingest function reads this alongside the session's status fields (no extra I/O), attaches the actions to the response, and clears the column via a merge. */
  pendingActionsJson: string;
  /** When the first pending action was queued. Used for TTL and staleness detection — maintenance can purge actions older than a threshold to prevent zombie signals on long-dead sessions. */
  pendingActionsQueuedAt?: string;
  /** Compact JSON snapshot of "last known session state" written by the maintenance 5h-timeout sweep when a session graduates to terminal Failed (Hybrid User-Driven completion-gap fix, 2026-05-01). Captures the canonical lifecycle anchors — last ESP phase, desktop arrival, Hello policy, AAD-join state, missing signals — so operators don't have to scroll through hundreds of events to reconstruct where a stuck session was when the watchdog fired. Empty / null on healthy-completion paths and on sessions that predate the field. Built by FailureSnapshotBuilder. */
  failureSnapshotJson: string;
  /** Cascade-delete state-machine value (Plan §1 P7 / PR3). One of SessionDeletionState constants. Empty / null means no cascade in flight (legacy rows; treated as None). Written to the primary Sessions table only — the deletion CAS path does not sync SessionsIndex, so this is NOT part of the index mirror today and is read on the truth-served detail/guard paths. Mirroring it into SessionsIndex (so list/search can flag locked sessions) is a deferred follow-up. */
  deletionState: string;
  /** ULID of the in-flight cascade manifest when DeletionState is non-None. Used by the producer to detect concurrent re-enqueues (same ManifestId → resume, different ManifestId → 409 Conflict). Null otherwise. */
  pendingDeletionManifestId?: string;
}

/** Per-session enrollment time attribution (F1, insights spec) — an exact partition of the session's authoritative wall clock into named segments plus an explicit unattributed remainder. Invariant (unit-enforced): sum of all span seconds + UnattributedSeconds == WallClockSeconds == the session's DurationSeconds (which excludes the WhiteGlove pause by design — never CompletedAt − StartedAt, which diverges in 25 % of terminal sessions). Computed once at session-terminal processing by TimeAttributionCalculator; persisted in SessionTimeBreakdowns (F1 PR2). */
export interface SessionTimeBreakdown {
  tenantId: string;
  sessionId: string;
  /** Attribution algorithm version — a definition change bumps this so aggregates never silently mix semantics (truthfulness rule 8). */
  attributionVersion: number;
  /** The session's EventCount when this breakdown was computed — the maintenance sweep's change signal: late or replayed event batches move the session's count, so a mismatch means the row was computed from an incomplete stream and gets recomputed (Codex review: the inline terminal compute is one-shot). 0 = row written before this column existed → recomputed once by the next sweep. */
  eventCountAtCompute: number;
  /** The session's authoritative DurationSeconds at compute time. */
  wallClockSeconds: number;
  /** Attributed spans, ordered by StartUtc. Multiple spans may share a segment key. */
  segments: TimeAttributionSpan[];
  /** Exact remainder: WallClockSeconds − sum of span seconds. */
  unattributedSeconds: number;
  /** Total observed reboot outage seconds. Cross-cutting annotation — reboots overlap the segments they occur in and are NOT part of the wall-clock partition. */
  rebootSeconds: number;
  rebootSpans: RebootSpan[];
  /** Total observed in-window sleep seconds. Cross-cutting annotation like RebootSeconds — the wall clock keeps the pause; this discloses it. */
  sleepSeconds: number;
  sleepSpans: SleepSpan[];
  /** Install intervals of ESP-blocking apps (positive-evidence join against the latest esp_config_detected lists), top 20 by duration. BlockingAppCount carries the uncapped count of matched blocking apps (including those without a measurable interval, e.g. unobserved start). */
  blockingApps: BlockingAppInterval[];
  /** Uncapped count of apps matched against the blocking set (see BlockingApps). */
  blockingAppCount: number;
  /** Overlap-merged union of the blocking-app intervals clipped to the esp_apps spans — the critical-path occupancy. esp_apps total − occupancy = in-phase wait (provider stalls, settle, IME idle). Null when the blocking set is unknown (no lists observed): unknown, not zero. */
  espAppsOccupancySeconds?: number;
  qualityFlags: TimeAttributionFlags;
}

/** Response of PATCH global/mcp-users/{upn}/usage-plan: the UPN and the plan now in effect ("(inherit)" when cleared to the tenant default). */
export interface SetMcpUserUsagePlanResponse {
  upn: string;
  usagePlan: string;
}

/** Response of PATCH config/{tenantId}/plan: the resulting plan/trial state. */
export interface SetTenantPlanTierResponse {
  tenantId: string;
  planTier: string;
  trialExpiresUtc?: string;
  trialConsumed: boolean;
  /** Effective edition after the change, lowercase ("community" | "pro"). */
  effectiveEdition: string;
  /** End of the retention downgrade grace window, or null — the key is omitted when null. */
  retentionGraceEndsUtc?: string;
  /** Effective delegated (MSP) tenant slot limit after the change (override or plan entitlement). */
  maxDelegatedTenants: number;
  /** The Global Admin override; omitted when the plan entitlement applies. */
  maxDelegatedTenantsOverride?: number;
  /** Effective MCP usage plan name of the tenant after the change (override or edition default). */
  mcpUsagePlan: string;
  /** The Global Admin MCP usage-plan override (a SectionUsagePlans plan name); omitted when the edition default applies. */
  mcpUsagePlanOverride?: string;
}

/** Distinct-CVE counts grouped by their highest CVSS severity band. */
export interface SeverityBreakdown {
  critical: number;
  high: number;
  medium: number;
  low: number;
}

/** Response of POST realtime/negotiate: exactly the shape the @microsoft/signalr client's negotiate protocol expects. */
export interface SignalRNegotiateResponse {
  url: string;
  accessToken: string;
}

/** Backend storage record for a single DecisionSignal (Plan §M5). Flat shape projected from the agent's DecisionSignal so the Backend doesn't need to reference DecisionCore just to persist and serve it back out. Keys (authoritative, supplied by the agent for idempotent upsert via (PartitionKey, RowKey)): PK = {TenantId}_{SessionId}, RK = {SessionSignalOrdinal:D19}. Fidelity: carries the complete agent-serialized DecisionSignal (including Evidence + Payload dictionary). Typed columns exist only for query/projection; replay is driven off the JSON blob. */
export interface SignalRecord {
  tenantId: string;
  sessionId: string;
  /** Monotonic per SignalLog. Drives table RowKey ordering. */
  sessionSignalOrdinal: number;
  /** Session-wide monotonic across Event + Signal + Transition. Inspector correlation only. */
  sessionTraceOrdinal: number;
  /** DecisionSignalKind enum name — stored as string for forward-compat. */
  kind: string;
  kindSchemaVersion: number;
  occurredAtUtc: string;
  sourceOrigin: string;
  /** Agent-serialized DecisionSignal JSON (Evidence + Payload included). */
  payloadJson: string;
}

/** SLA metrics response for a given tenant and time window. */
export interface SlaMetricsResponse {
  targetSuccessRate?: number;
  targetMaxDurationMinutes?: number;
  targetAppInstallSuccessRate?: number;
  /** Current ISO week SLA snapshot. */
  currentWeek: SlaSnapshot;
  /** Weekly trend (newest first). */
  weeklyTrend: SlaWeeklyTrend[];
  /** Sessions that breached SLA targets (failed or exceeded duration). */
  violators: SlaViolatorSession[];
  /** App install SLA snapshot (null if no app install target configured). */
  appInstallSla?: AppInstallSlaSnapshot;
  computedAt: string;
  fromCache: boolean;
  computeDurationMs: number;
}

/** SLA compliance snapshot for a single period (ISO week). */
export interface SlaSnapshot {
  /** ISO week identifier, e.g. "2026-W15". */
  week: string;
  totalCompleted: number;
  succeeded: number;
  failed: number;
  successRate: number;
  avgDurationMinutes: number;
  p95DurationMinutes: number;
  /** Number of sessions that exceeded the duration target. */
  durationViolationCount: number;
  /** Whether the success rate target is met. */
  successRateMet: boolean;
  /** Whether the P95 duration target is met. */
  durationTargetMet: boolean;
}

/** A session that violated SLA targets. */
export interface SlaViolatorSession {
  sessionId: string;
  tenantId: string;
  deviceName: string;
  serialNumber: string;
  startedAt: string;
  completedAt?: string;
  durationSeconds?: number;
  status: number;
  failureReason?: string;
  /** "Failed", "DurationExceeded", or "Both". */
  violationType: string;
}

/** SLA compliance trend entry for one ISO week. */
export interface SlaWeeklyTrend {
  /** ISO week identifier, e.g. "2026-W15". */
  week: string;
  successRate: number;
  p95DurationMinutes: number;
  appInstallSuccessRate: number;
  totalCompleted: number;
  successRateMet: boolean;
  durationTargetMet: boolean;
  appInstallTargetMet: boolean;
}

/** One completed sleep episode observed during the session (system_sleep_episode payload — enteredAt/exitedAt are the authoritative instants, never the event's own timestamp, which is stamped at wake and may be clamped). Cross-cutting annotation like RebootSpan: sleep overlaps the segment it started in and is NOT part of the wall-clock partition — the session really took that long; the span explains where the time went. */
export interface SleepSpan {
  startUtc: string;
  endUtc: string;
  seconds: number;
  /** Segment the episode started in (TimeAttributionSegments key, or "unattributed"). */
  segmentKey: string;
  /** Episode kind from the agent payload: "sleep", "hibernate" or "modern_standby". */
  kind: string;
}

/** Shared response of GET vulnerability/software-inventory (Global Admin, ?tenantId=) and GET metrics/software-inventory (caller's own tenant): the raw SoftwareInventory rows plus matched/unmatched counts. Consumed by the MCP get_software_inventory tool — key names are part of that contract. */
export interface SoftwareInventoryResponse {
  success: boolean;
  tenantId: string;
  total: number;
  matched: number;
  unmatched: number;
  /** Raw SoftwareInventory table rows, serialized verbatim under their storage column keys. */
  inventory: (Record<string, unknown>)[];
}

/** Response of POST config/{tenantId}/trial: the started self-service trial. */
export interface StartTenantTrialResponse {
  tenantId: string;
  trialStartedUtc?: string;
  trialExpiresUtc?: string;
  /** Always the Pro tier name — starting a trial makes the tenant effectively Pro. */
  effectiveEdition: string;
}

/** Response from session report submission */
export interface SubmitSessionReportResponse {
  success: boolean;
  message: string;
  reportId: string;
}

/** Canonical mutation acknowledgement: { "success": ..., "message": ... }. No property defaults on purpose — every call site sets both, and a default would add a key the anonymous site never wrote. */
export interface SuccessMessageResponse {
  success: boolean;
  message: string;
}

/** Canonical minimal acknowledgement: { "success": ... }. */
export interface SuccessOnlyResponse {
  success: boolean;
}

/** Configuration for suppressing a condition match when a "resolving" event exists. Example: suppress an app_install_failed match when app_install_completed exists for the same appId. */
export interface SuppressByEventConfig {
  /** The event type that resolves/suppresses the matched event (e.g., "app_install_completed"). */
  eventType: string;
  /** The data field to join on — must have the same value in both the matched event and the suppressing event (e.g., "appId"). */
  joinField: string;
}

/** Defines a template variable in a rule condition that must be customized per-tenant. Points at a specific condition field (by index) and describes what value the user needs to provide. */
export interface TemplateVariable {
  /** Machine name for this variable (e.g., "cert_subject") */
  name: string;
  /** Human-readable label shown in the configuration UI (e.g., "Certificate Subject") */
  label: string;
  /** Help text explaining what value the user should provide */
  description?: string;
  /** Zero-based index into the rule's Conditions array where this variable lives */
  conditionIndex: number;
  /** Which field on the condition to customize: "value", "eventType", "dataField", "eventAFilterValue" */
  field: string;
  /** The placeholder value that ships with the template (e.g., "CN=YOUR-CERTIFICATE-SUBJECT") */
  placeholder: string;
  /** Optional regex pattern to validate user input */
  validation?: string;
}

/** Response of POST tenants/{tenantId}/admins: the created tenant member. */
export interface TenantAdminCreatedResponse {
  admin: TenantAdminRow;
}

/** One tenant member (Admin / Operator / Viewer) on the wire — the item of the bare-array GET tenants/{tenantId}/admins and the created member of the POST. Deliberately NOT the storage entity: the ITableEntity keys (partitionKey/rowKey/eTag/timestamp) that the pre-2026-08-31 wire carried are storage internals and were dropped from the contract (no consumer read them). */
export interface TenantAdminRow {
  /** Tenant ID (lowercase). */
  tenantId: string;
  /** User Principal Name (lowercase). */
  upn: string;
  isEnabled: boolean;
  addedDate: string;
  addedBy: string;
  /** Role: "Admin", "Operator", "Viewer". A null (legacy pre-role row) omits the key and means Admin. */
  role?: string;
  /** Whether this Operator can manage bootstrap tokens (only relevant for Operator). */
  canManageBootstrapTokens: boolean;
}

/** One pre-write config snapshot in GET config/{tenantId}/backups — metadata only, the raw EntityJson (clear-text secrets) is never returned. */
export interface TenantConfigBackupItem {
  /** The snapshot's RowKey (reverse-ticks + short guid) — the public backup id. */
  backupId: string;
  backupTakenAt: string;
  changedBy: string;
  /** Write path that triggered the snapshot (portal-put | plan | mcp-patch | ...). */
  source: string;
  /** Caller-provided intent, or null — the key is omitted when null. */
  reason?: string;
  /** Masked "old → new" change summary, or null when unparseable — the key is omitted when null. */
  diff?: Record<string, string>;
}

/** One entry of the tenant-config field schema (built by TenantConfigPatchService.BuildFieldsSchema). Serialized camelCase; null Format/Reason keys are omitted (WhenWritingNull) — the null and set cases are pinned by ConfigWireParityTests. */
export interface TenantConfigFieldSchema {
  name: string;
  type: string;
  format?: string;
  nullable: boolean;
  writable: boolean;
  reason?: string;
  gaOnly: boolean;
  revertProtected: boolean;
}

/** Success response of PATCH config/{tenantId}/fields and POST config/{tenantId}/revert (both flow through the same outcome writer): which fields changed, the masked diff, and the pre-write backup id. */
export interface TenantConfigPatchOutcomeResponse {
  success: boolean;
  appliedFields: string[];
  /** Masked "old → new" summary, or null — the key is omitted when null. */
  diff?: Record<string, string>;
  /** Pre-write snapshot id, or null (no-op writes take no backup) — the key is omitted when null. */
  backupId?: string;
  /** True when the patch changed nothing (zero applied fields). */
  noOp: boolean;
}

/** Tenant-specific configuration stored in Azure Table Storage PartitionKey = TenantId RowKey = "config" */
export interface TenantConfiguration {
  /** Tenant ID (PartitionKey in Table Storage) */
  tenantId: string;
  /** Domain name extracted from the first user's UPN Used for display purposes (e.g., contoso.com) */
  domainName: string;
  /** When the configuration was last updated */
  lastUpdated: string;
  /** Updated by (user email or system) */
  updatedBy: string;
  /** UPN of the user whose first login created this tenant configuration. Set once in HandleNewTenantDomainAsync alongside DomainName and never overwritten. Used by the preview-approval auto-promote path so background jobs that mutate UpdatedBy (e.g. global rate-limit sync) cannot leak a sentinel string into the TenantAdmins table. Null on rows that pre-date the OnboardedBy field — auto-promote falls back to UpdatedBy with a UPN-shape guard. */
  onboardedBy?: string;
  /** Address used to reach this tenant about the service itself — a technical problem, a security matter, or a change that needs an administrator's attention. Editable by the tenant's own admins under Settings → Tenant → Contact. Seeded once at onboarding from the tenant's notification address if one was given, and never re-synced afterwards: from that point the value belongs to the tenant, and a later edit must not be overwritten by the onboarding source. Purpose-limited by design — service communication only. It is never used for marketing and never disclosed. Null means we have no way to reach this tenant, which is why enforcement actions cannot promise prior warning. */
  contactEmail?: string;
  /** Organization name behind this tenant, as the tenant's admins want it read by a support engineer — the counterpart of ContactEmail in the tenant's contact profile. Editable under Settings → Tenant → Contact. Optional on Community. Together with it is required at the self-service Pro entry point (trial start) so a paying tenant is reachable and identifiable for support; it is never a runtime gate on Pro features, and a GA plan assignment does not require it (the admin UI warns instead). Never derived from : the domain is a technical label, the company name is what the tenant tells us. Null means not provided. */
  companyName?: string;
  /** When this tenant was first onboarded (derived from earliest TenantAdmin AddedDate). Used for feedback eligibility checks (tenant must be old enough before prompting). Backfilled by the maintenance job for existing tenants; set to UtcNow for new tenants. */
  onboardedAt?: string;
  /** Client id of the Entra app registration this tenant is homed on. Drives which app mints Graph client-credential tokens and admin-consent URLs for the tenant, and which app the portal signs the tenant's users in with (via the auth/me "homedApp" field). Null = the legacy (pre-migration) app registration — the invariant for every tenant onboarded before the C4A8 move. Set to the primary client id at onboarding when the first login arrived via the primary app; flipped by a Global Admin after a tenant re-consents to the new app (GA-only field, see UpdateTenantConfigurationFunction). */
  homedAppClientId?: string;
  /** Client id observed in the most recent portal login token's audience — pure observability for the app-reg migration (which app a tenant's users actually arrive through), never used for routing decisions. Written on change only. */
  lastAuthClientId?: string;
  /** When LastAuthClientId last changed (i.e. logins arrive via that app since this instant). Null on rows that pre-date the field. */
  lastAuthClientIdSince?: string;
  /** Whether this tenant is disabled/suspended If true, users from this tenant cannot log in Default: false */
  disabled: boolean;
  /** Optional reason why the tenant was disabled Displayed to users attempting to log in */
  disabledReason?: string;
  /** Optional date/time until which the tenant is disabled If set and in the past, the tenant can be automatically re-enabled If null, the tenant remains disabled until manually re-enabled */
  disabledUntil?: string;
  /** Optional per-tenant override for the device (agent/cert) API rate limit. If null, the effective limit is the global AdminConfiguration.GlobalRateLimitRequestsPerMinute. If set, this value takes precedence. Global-Admin-only (see UpdateTenantConfigurationFunction GA-gate). */
  customRateLimitRequestsPerMinute?: number;
  /** Optional per-tenant override for the user (portal/JWT) API rate limit applied to standard users (Tenant Admins, Operators, Viewers). If null, the effective limit is the global AdminConfiguration.UserRateLimitRequestsPerMinute. Global-Admin-only. Note: Global Admins are rate-limited by the global GlobalAdminRateLimitRequestsPerMinute (cross-tenant), so this override does not apply to them. */
  customUserRateLimitRequestsPerMinute?: number;
  /** Tenant plan tier. Determines default API rate limits and feature gates. Write-side values: "community", "pro". The legacy stored values "enterprise" (resolves to Pro) and "free" (resolves to Community) remain readable — see FeatureEntitlementCatalog. Managed by Global Admins. */
  planTier: string;
  /** End of the tenant's Pro trial (UTC). While this is in the future the tenant's effective edition is Pro regardless of PlanTier. Null = no trial. Expiry degrades the tenant to Community at read time — no timer involved. */
  trialExpiresUtc?: string;
  /** When the tenant's Pro trial was started (UTC). Informational/audit only. */
  trialStartedUtc?: string;
  /** Whether the tenant has used its one self-service trial. Once true, further trials can only be granted by a Global Admin via the plan management endpoint (which does not reset this flag). */
  trialConsumed: boolean;
  /** Who granted/started the trial (UPN of the self-service caller or the Global Admin). */
  trialGrantedBy?: string;
  /** When the tenant's EFFECTIVE edition last dropped Pro → Community via the plan endpoint (UTC). Anchors the retention downgrade grace period: for RetentionDowngradeGraceDays after losing Pro the retention cap stays at the Pro value so a downgrade (e.g. non-payment) does not immediately hard-delete data older than the Community cap. Trial expiry needs no write — TrialExpiresUtc itself is the anchor there. Cleared whenever the tenant becomes effectively Pro again. Backend-only: not delivered to the agent (no ConfigVersion impact). */
  proDowngradedUtc?: string;
  /** Global-Admin override of the delegated ("MSP") tenant slot limit — how many distinct customer tenants users homed in this tenant may manage. Null = the plan entitlement applies (Community 0, Pro 2); a value applies regardless of edition (pre-provisioning a package), while USING delegation still requires Pro. Mutable only via the plan endpoint. Backend-only: not delivered to the agent (no ConfigVersion impact). */
  maxDelegatedTenantsOverride?: number;
  /** Global-Admin override of the tenant's MCP usage plan — the NAME of a SectionUsagePlans plan (AdminConfiguration.PlanTierDefinitionsJson), e.g. "msp". Applies to the WHOLE tenant: every member's default user plan (a per-user McpUsers override still wins) AND the organization-wide windows. Null/blank = the edition default (community/pro). Does NOT change the edition — Pro feature gates stay on PlanTier. Mutable only via the plan endpoint (validated against the plan definitions there). Backend-only: not delivered to the agent (no ConfigVersion impact). */
  mcpUsagePlanOverride?: string;
  /** Hardware whitelist: Allowed manufacturers (supports wildcards like "Dell*") Comma-separated list */
  manufacturerWhitelist: string;
  /** Hardware whitelist: Allowed models (supports wildcards like "Latitude*") Comma-separated list Default: "*" (all models allowed) */
  modelWhitelist: string;
  /** Whether to validate devices against Intune Autopilot device registration Requires Graph API integration (admin consent for DeviceManagementServiceConfig.Read.All) */
  validateAutopilotDevice: boolean;
  /** Whether to validate devices against Intune Corporate Device Identifiers (manufacturer + model + serial number via importedDeviceIdentities/searchExistingIdentities). Requires Graph API integration (admin consent for DeviceManagementServiceConfig.ReadWrite.All) */
  validateCorporateIdentifier: boolean;
  /** Whether to validate devices against the Windows Autopilot device preparation "Device association" catalog via Graph (tenantAssociatedDevices, serial number match). One of the accepting methods of the device-validation gate, evaluated after the Autopilot and Corporate Identifier lookups (see SecurityValidator). Device association is GA since 2026-08-25; associated devices are marked corporate-owned by Intune itself, so no corporate identifier exists for them. Requires the same Graph permission as the other validators (DeviceManagementServiceConfig.Read.All). */
  validateDeviceAssociation: boolean;
  /** Whether to validate Windows 365 Cloud PCs as a fallback when the Autopilot / Corporate Identifier lookups miss. Cloud PCs are provisioned by the Windows 365 service and are structurally never Autopilot-registered; this validator instead resolves the Intune device id from the (chain-validated) MDM client certificate's Subject CN and requires a matching cloudPC object (virtualEndpoint/cloudPCs, managedDeviceId eq CN) in the tenant. Only service-provisioned Cloud PCs have such an object, so no other enrolled device can pass this stage. Requires the optional Graph permission CloudPC.Read.All (feature "W365CloudPcValidation" in the grant script). */
  validateCloudPcDevice: boolean;
  /** Cert-to-device binding check (Global-Admin-only preview, SHADOW mode). Resolves the Intune managedDevice id carried in the agent client certificate's Subject CN against this tenant's own managedDevices inventory, proving the certificate belongs to a device the tenant actually enrolled. The result is recorded as telemetry only and never blocks enrollment, because a device object can in principle appear later than the agent's first call - measuring that race is the point of the shadow pass. Requires the optional Graph permission DeviceManagementManagedDevices.Read.All (feature "IntuneDeviceBinding" in the grant script). */
  validateIntuneDeviceBinding: boolean;
  /** Emergency bypass for agent security gate (Global Admin use only). If true, agent requests are accepted even when ValidateAutopilotDevice is false. Default: false */
  allowInsecureAgentRequests: boolean;
  /** Data retention period in days Sessions and events older than this will be deleted by the daily maintenance job Default: 90 days */
  dataRetentionDays: number;
  /** Session inactivity timeout in hours. Once an "InProgress" session is idle past this, the maintenance sweep reclassifies it out of "InProgress" (see tasks/enrollment-status-reclassification.md): if Device Setup already finished it becomes AwaitingUser (non-terminal), otherwise it eventually settles as the terminal, non-failure Incomplete state once SessionGraceHours elapses — it is NOT counted as a failure. This prevents stalled sessions from running indefinitely and skewing statistics. Recommended: Use the same value as your ESP (Enrollment Status Page) timeout Default: 5 hours */
  sessionTimeoutHours: number;
  /** Grace window in hours for a session that reached the inactivity timeout with Device Setup already provisioned but no completion signal yet (tasks/enrollment-status-reclassification.md). At SessionTimeoutHours such a session becomes AwaitingUser (non-terminal) instead of Failed; only after this window elapses without a completion does it graduate to the terminal, non-failure Incomplete state. 0 (default) = auto-derive: AbsoluteMaxSessionHours + buffer (= 48h + 3h = 51h with defaults). The grace is always floored at the agent's absolute session-age cap plus buffer — until that cap fires the agent may still be legitimately enrolling, and because the cap is silent to the backend anything still quiet past cap+buffer is provably dead. A non-zero value acts as an override but can only raise the effective grace above that floor, never below it (see EnrollmentTimeoutClassifier.ResolveGraceHours). */
  sessionGraceHours: number;
  /** Maximum decompressed ingest-batch payload size in MB, enforced on the JSON-array ingest path (/api/agent/telemetry) — DoS/memory-exhaustion protection. The property name is historical (from the removed V1 NDJSON path); scope is shape-agnostic. Default: 5 MB. Table-only setting (not editable via the tenant-config public API). */
  maxNdjsonPayloadSizeMB: number;
  /** Enable Performance Collector (CPU, memory, disk, network monitoring) Generates ~1 event per interval - can create significant traffic Default: true */
  enablePerformanceCollector: boolean;
  /** Performance collector interval in seconds Default: 30 seconds */
  performanceCollectorIntervalSeconds: number;
  /** Seconds to wait for the Windows Hello wizard after ESP exit Default: 30 seconds */
  helloWaitTimeoutSeconds: number;
  /** Maximum consecutive authentication failures (401/403) before the agent shuts down. null = use default (5). 0 = disabled (retry forever). */
  maxAuthFailures?: number;
  /** Maximum time in minutes the agent keeps retrying after the first auth failure. null = use default (0 = disabled, only MaxAuthFailures applies). */
  authFailureTimeoutMinutes?: number;
  /** Maximum agent lifetime in minutes. Safety net to prevent zombie agents. null = use default (360 = 6 hours). 0 = disabled (no lifetime limit). */
  agentMaxLifetimeMinutes?: number;
  /** Absolute per-session age cap in hours enforced by the agent's emergency break (Program.Guards.CheckSessionAgeEmergencyBreak → AgentConfiguration.AbsoluteMaxSessionHours). null = agent default (48). Mirrored here so the backend can derive the session-grace floor from the same value: the timeout grace is never shorter than this cap + buffer. NOTE: the agent still reads its own AbsoluteMaxSessionHours today; wiring this override down to the agent config response is a follow-up so the two stay in lockstep. */
  absoluteMaxSessionHours?: number;
  /** Whether to self-destruct after enrollment completion (remove Scheduled Task and all files). null = use agent default (true). */
  selfDestructOnComplete?: boolean;
  /** Preserve logs during self-destruct. null = use agent default (false). */
  keepLogFile?: boolean;
  /** Whether to reboot the device after enrollment completes. null = use agent default (false). */
  rebootOnComplete?: boolean;
  /** Delay in seconds before the reboot is initiated (shutdown.exe /r /t X). null = use agent default (10 seconds). */
  rebootDelaySeconds?: number;
  /** Whether to enable geo-location detection (queries external IP service). null = use agent default (true). */
  enableGeoLocation?: boolean;
  /** NTP server address for time check during enrollment. null = use agent default ("time.windows.com"). */
  ntpServer: string;
  /** Whether to automatically set the device timezone based on IP geolocation. Requires EnableGeoLocation to be true. Uses tzutil /s to apply. null = use agent default (false). */
  enableTimezoneAutoSet?: boolean;
  /** Whether to set the Delivery Optimization group ID (DOGroupId policy value) from a network fingerprint: a deterministic GUID derived from the default gateway's IP and MAC address, so devices on the same local network peer with each other (byte layout is RealmJoin-compatible). Only takes effect with DO Download Mode = Group (2); existing DOGroupId/DOGroupIdSource policies (Intune/GPO) are never overwritten. null = use agent default (false). */
  enableDoGroupIdAutoSet?: boolean;
  /** Whether to write a match log for every IME log line matched by a pattern. When true, the agent writes to the default path Constants.ImeMatchLogPath. null = use agent default (false). */
  enableImeMatchLog?: boolean;
  /** Whether the agent writes a local gather-rule evaluation trace file so customers can diagnose rules that produce no timeline events (scope skips, on_change suppression, empty collector results, logparser details). When true, the agent writes to Constants.GatherRuleDebugLogPath. The trace never leaves the device. null = use agent default (false). */
  enableGatherRuleDebugLog?: boolean;
  /** Continue-Anyway observation mode: when true AND the ESP profile allows "Continue anyway", a Device-phase ESP terminal failure (AccountSetup never entered) does not fail the session immediately — the agent keeps monitoring for up to 60 minutes and completes with an esp-soft-failure marker once the DAD-validated real-user desktop (plus the Hello gate) proves the user continued; an expired window fails with the original esp_terminal_failure. Operator-set only (not exposed in the tenant admin UI). null = use agent default (false). */
  enableEspContinueAnywayObservation?: boolean;
  /** Log verbosity level override for this tenant's agents. null = use agent default ("Info"). Values: "Info", "Debug", "Verbose", "Trace". */
  logLevel: string;
  /** Maximum events per upload batch. null = use agent default (100). */
  maxBatchSize?: number;
  /** Whether to show a visual enrollment summary dialog to the end user after enrollment completes (success or failure). null = use agent default (false). */
  showEnrollmentSummary?: boolean;
  /** Auto-close timeout in seconds for the enrollment summary dialog. null = use agent default (60). 0 = no auto-close. */
  enrollmentSummaryTimeoutSeconds?: number;
  /** Optional URL to a branding image displayed as a banner at the top of the enrollment summary dialog. Expected size: 540 x 80 px. Larger images will be center-cropped. */
  enrollmentSummaryBrandingImageUrl: string;
  /** Maximum time in seconds the agent retries launching the enrollment summary dialog when the user's desktop is locked by a credential UI (e.g. Windows Hello). null = use agent default (120). 0 = no retry (single attempt). */
  enrollmentSummaryLaunchRetrySeconds?: number;
  /** Whether to show PowerShell script stdout in the web UI. When false, only stderr (error output) is visible for troubleshooting. stdout may contain sensitive data (credentials, tokens). Default true (show stdout). Data is always collected regardless of this setting. */
  showScriptOutput?: boolean;
  /** Whether the LocalAdminAnalyzer is enabled for this tenant's devices. null = use agent default (true). */
  enableLocalAdminAnalyzer?: boolean;
  /** Whether the SoftwareInventoryAnalyzer is enabled for this tenant's devices. null = use agent default (true). */
  enableSoftwareInventoryAnalyzer?: boolean;
  /** Whether the IntegrityBypassAnalyzer is enabled for this tenant's devices. null = use agent default (true). */
  enableIntegrityBypassAnalyzer?: boolean;
  /** Whether the RealmJoin watcher is enabled for this tenant's devices. RealmJoin enrollment-package tracking is off by default; enable only for tenants that deploy via RealmJoin. null = use agent default (false). */
  enableRealmJoinWatcher?: boolean;
  /** Whether to keep the device awake during the User-ESP (AccountSetup) phase for this tenant's devices. Prevents idle standby/sleep from stalling app installs / account provisioning; reboots are unaffected. Off by default. null = use agent default (false). */
  keepAwakeDuringUserEsp?: boolean;
  /** Whether to detect a SYSTEM console opened during enrollment (Shift+F10 OOBE bypass) for this tenant's devices. Gates the live ConsoleBypass watcher + the startup prefetch scanner. On by default (opt-out); tenants that knowingly use Shift+F10 for support can disable it. null = use agent default (true). */
  enableConsoleBypassDetection?: boolean;
  /** JSON-serialized list of additional local account names that are considered expected on a newly enrolled device (merged with built-in defaults on the agent). Example: ["SupportAdmin", "TechDesk"] */
  localAdminAllowedAccountsJson: string;
  /** Per-tenant ADDITIVE enable for OOBE Bootstrap Sessions (Global Admin only). The Pro plan includes the feature regardless of this flag; for Community tenants this is the on-request escape hatch. Effective value = plan-included OR this flag — resolved via TenantEntitlementService.IsBootstrapEnabled (backend); when effectively false, the feature is hidden in the UI and all bootstrap API endpoints reject requests. */
  bootstrapTokenEnabled: boolean;
  /** Whether the Unrestricted Mode feature is available for this tenant (the Pro-plan on-request gate). When false (default), the Unrestricted Mode section is hidden in the tenant settings UI and UnrestrictedMode cannot be activated by tenant admins. Only configurable by Global Admins. */
  unrestrictedModeEnabled: boolean;
  /** When effective, agent guardrails are relaxed: all registry paths, WMI queries, and commands are allowed via GatherRules. File paths and diagnostics paths are allowed except C:\Users. Default: false. Can only be toggled by tenant admins when UnrestrictedModeEnabled is true. EFFECTIVE only while the tenant's edition is Pro (read-time re-gate, fail-closed on trial expiry/downgrade) — see TenantEntitlementService.IsUnrestrictedModeActive. */
  unrestrictedMode: boolean;
  /** When enabled, tenant member roles (Admin / Operator) may also be granted via Entra ID app-role assignments on the application's Enterprise App (the "roles" claim in the user's token), in addition to the TenantAdmins table. Resolution is table-first: an explicit TenantAdmins entry always overrides a claim-derived role (e.g. to grant an Operator CanManageBootstrapTokens). Only Admin and Operator are claim-mappable; the platform-wide GlobalAdmin role is never derived from claims. Off by default — per-tenant opt-in. Backend-only setting: not delivered to the agent (no ConfigVersion impact). */
  entraAppRolesEnabled: boolean;
  /** JSON-serialized list of tenant-specific additional log paths/wildcards to include in the diagnostics ZIP package (additive to global paths). Each entry: { "path": "...", "description": "...", "isBuiltIn": false } */
  diagnosticsLogPathsJson: string;
  /** Azure Blob Storage Container SAS URL for diagnostics package upload. Used only when DiagnosticsUploadDestination is "CustomerSas". Each tenant provides their own container — data stays in the customer's storage. If null or empty (and destination is CustomerSas), diagnostics upload is disabled. */
  diagnosticsBlobSasUrl: string;
  /** When to upload diagnostics packages: "Off", "Always", "OnFailure". Applies to both destinations. Default: "Off" */
  diagnosticsUploadMode: string;
  /** Where diagnostics packages should be uploaded: "CustomerSas" (default) — customer's own storage account via the SAS URL in . Preserves existing behaviour; no data leaves the customer's Azure tenant boundary."Hosted" — opt-in only. Blobs land in the backend's storage account under {tenantId}/AgentDiagnostics-...zip in the container. Requires an explicit admin click in the tenant settings UI with a clearly-marked "data leaves your tenant" disclosure — never set silently. Default "CustomerSas" so existing tenants without the field set behave identically to today and customer data is never silently routed to hosted storage. */
  diagnosticsUploadDestination: string;
  /** Whether the agent should send Trace-severity events to the backend. Trace events capture key agent decisions for backend-side troubleshooting. Default: true (on in preview). Can be disabled per tenant to reduce traffic. */
  sendTraceEvents: boolean;
  /** URL of the Teams Incoming Webhook for enrollment notifications. If null or empty, no notifications are sent. */
  teamsWebhookUrl: string;
  /** Send a Teams notification when an enrollment completes successfully. Default: true */
  teamsNotifyOnSuccess: boolean;
  /** Send a Teams notification when an enrollment fails. Default: true */
  teamsNotifyOnFailure: boolean;
  /** Send a Teams notification when an enrollment starts (session registration). Opt-in: default false to avoid surprising existing tenants with a notification storm. */
  teamsNotifyOnStart: boolean;
  /** Webhook provider type. Determines which renderer formats the notification payload. 0=None, 1=TeamsLegacyConnector, 2=TeamsWorkflowWebhook, 10=Slack. Legacy tenants with TeamsWebhookUrl are auto-resolved via GetEffectiveWebhookConfig(). */
  webhookProviderType: number;
  /** Generic webhook URL for enrollment notifications. Replaces TeamsWebhookUrl for new configurations. */
  webhookUrl: string;
  /** Send a webhook notification when enrollment succeeds. Default: true. */
  webhookNotifyOnSuccess: boolean;
  /** Send a webhook notification when enrollment fails. Default: true. */
  webhookNotifyOnFailure: boolean;
  /** Send a webhook notification when a device is rejected by the hardware whitelist. Default: false (opt-in). */
  webhookNotifyOnHardwareRejection: boolean;
  /** Send a webhook notification when an enrollment starts (session registration on the backend). Opt-in: default false to avoid surprising existing tenants with a notification storm. */
  webhookNotifyOnStart: boolean;
  /** Custom HTTP request headers (JSON object: { "Header-Name": "value", ... }) sent with every generic-webhook POST. Used for API-key authentication against ticketing systems / SMTP gateways. Only applied when the effective provider is GenericJson. Restricted headers (Host, Content-Length, Content-Type, etc.) are ignored — see GetGenericWebhookHeaders. */
  webhookCustomHeadersJson: string;
  /** Named notification channels as a JSON array (camelCase, see NotificationChannel). Supersedes the single WebhookUrl/WebhookProviderType pair: each channel carries its own provider, URL, custom headers and per-event opt-in toggles, and analyze rules can target specific channels by id. Null/empty = tenant not migrated yet — GetNotificationChannels then synthesizes one channel from the legacy fields so existing tenants keep their exact behavior without a data migration. */
  notificationChannelsJson: string;
  /** Target enrollment success rate as a percentage (e.g. 95.0 = 95%). null = SLA tracking disabled for this tenant. */
  slaTargetSuccessRate?: number;
  /** Target maximum enrollment duration in minutes (P95 threshold). Sessions exceeding this are considered SLA violators. */
  slaTargetMaxDurationMinutes?: number;
  /** Target app install success rate as a percentage (e.g. 98.0 = 98%). Only evaluated when enough installs exist (20+). */
  slaTargetAppInstallSuccessRate?: number;
  /** Send notification when enrollment success rate drops below threshold. */
  slaNotifyOnSuccessRateBreach: boolean;
  /** Warning threshold for success rate notifications. Defaults to SlaTargetSuccessRate when null. Allows a separate warning level (e.g. target 99%, notify at 95%). */
  slaSuccessRateNotifyThreshold?: number;
  /** Send notification when P95 enrollment duration exceeds SlaTargetMaxDurationMinutes. */
  slaNotifyOnDurationBreach: boolean;
  /** Send notification when app install success rate drops below SlaTargetAppInstallSuccessRate. */
  slaNotifyOnAppInstallBreach: boolean;
  /** Send notification when consecutive enrollment failures reach the threshold. */
  slaNotifyOnConsecutiveFailures: boolean;
  /** Number of consecutive enrollment failures that triggers a notification. Default: 5. */
  slaConsecutiveFailureThreshold: number;
}

/** One manifest blob under a session (GetTenantDeletionManifests). */
export interface TenantDeletionManifestItem {
  manifestId: string;
  sizeBytes: number;
  /** Blob last-modified, pre-formatted round-trip ("o"). */
  lastModifiedUtc: string;
}

/** One session grouping in the deletion-manifest tree (GetTenantDeletionManifests). */
export interface TenantDeletionManifestSessionNode {
  sessionId: string;
  manifestCount: number;
  /** Newest manifest timestamp under this session, pre-formatted round-trip ("o"). */
  latestManifestUtc: string;
  manifests: TenantDeletionManifestItem[];
}

/** Read-time entitlement surface of the resolved edition. */
export interface TenantFeatureEntitlements {
  retentionCapDays: number;
  /** Absent when the platform default applies. */
  userRateLimitPerMinute?: number;
  delegatedAdminAllowed: boolean;
  mcpUsagePlan: string;
  /** Effective delegated (MSP) tenant slot limit (override or plan entitlement); 0 = no delegation. */
  maxDelegatedTenants: number;
}

/** Response of GET config/{tenantId}/feature-flags: the member-readable subset of the tenant configuration — UI display toggles, feature switches and the read-time edition/entitlement surface. Adding a field here is a deliberate decision that the field is non-sensitive (no webhook URLs, SAS tokens, allowlists, addresses). */
export interface TenantFeatureFlagsResponse {
  /** EFFECTIVE bootstrap availability (Pro includes it; the GA flag is the additive Community enable) — field name kept for web compatibility. */
  bootstrapTokenEnabled: boolean;
  /** Whether an on-demand diagnostics upload can succeed right now (mode not Off + usable destination). Deliberately no destination detail. */
  diagnosticsUploadConfigured: boolean;
  /** Drives the "Autopilot Device Validation disabled" dashboard banner. */
  validateAutopilotDevice: boolean;
  /** Dual app-reg self-service migration: consent flow targets the NEW app registration. Non-sensitive — exposes no client ids. */
  appHomingFunnelActive: boolean;
  showScriptOutput: boolean;
  enableSoftwareInventoryAnalyzer: boolean;
  enableIntegrityBypassAnalyzer: boolean;
  /** EFFECTIVE gather-rule unrestricted mode (Pro edition + GA gate + tenant toggle). */
  unrestrictedMode: boolean;
  /** Resolved edition, lowercase ("community" / "pro"). */
  edition: string;
  isTrial: boolean;
  /** Absent unless the tenant is on an active trial. */
  trialExpiresUtc?: string;
  trialAvailable: boolean;
  /** Whether a contact address is stored (boolean only — the address stays admin-gated). */
  contactEmailSet: boolean;
  /** Whether a company name is stored (boolean only — the value stays admin-gated). */
  companyNameSet: boolean;
  entitlements: TenantFeatureEntitlements;
}

/** A Tenant Group: an app-internal named bundle of tenants. A delegated admin assigned to the group (see TenantGroupAssignment) gains read scope to every tenant in TenantIds. Adding a tenant to the group grants it to all assignees at once. */
export interface TenantGroup {
  groupId: string;
  name: string;
  createdBy: string;
  createdAt: string;
  /** Tenant IDs (lowercase) in this group. */
  tenantIds: string[];
  /** Number of UPNs assigned to this group (== Assignees.Count). */
  assigneeCount: number;
  /** The UPNs assigned to this group (for the management UI). */
  assignees: TenantGroupAssignment[];
  /** Operator flag: MCP reads an assignee makes INTO this group's tenants are charged to the assignee's HOME tenant's quota instead of the managed tenant's. For operator-run managed-service groups whose customers must never pay (or be blocked) for the operator's own analysis. Off by default. */
  chargeHomeTenantQuota: boolean;
  /** The managing tenant that owns this self-service group (msp-{tenantId}); null for operator-created groups. */
  ownerTenantId?: string;
}

/** One UPN→group assignment. PK=UPN, RK=groupId in storage. */
export interface TenantGroupAssignment {
  upn: string;
  groupId: string;
  /** Constants.DelegatedRoles: "DelegatedReader" (default) or "DelegatedAdmin". */
  role: string;
  isEnabled: boolean;
  assignedBy: string;
  assignedAt: string;
}

/** Response of GET global/tenant-groups: every group with tenants + assignees. */
export interface TenantGroupListResponse {
  groups: TenantGroup[];
}

/** One person with access, nested in TenantManagerItem. */
export interface TenantManagerAssignee {
  upn: string;
  role: string;
  isEnabled: boolean;
}

/** One party that can read the caller's tenant, nested in TenantManagerListResponse. */
export interface TenantManagerItem {
  /** The Tenant Group conferring the access; absent for direct operator grants. */
  groupId?: string;
  /** The managing tenant that owns the group (self-service); absent for operator-created groups and direct grants. */
  ownerTenantId?: string;
  ownerDomain?: string;
  name: string;
  /** self-service | operator */
  source: string;
  assignees: TenantManagerAssignee[];
  sinceUtc?: string;
  /** True when the caller (the managed tenant's admin) may end this access here. */
  revocable: boolean;
}

/** Response of GET delegations/managers: who manages the caller's tenant. */
export interface TenantManagerListResponse {
  tenantId: string;
  managers: TenantManagerItem[];
}

export interface TenantMetrics {
  total: number;
  active7Days: number;
  active30Days: number;
}

/** Snapshot of a tenant's custom rule row written by the offboarding handler during Phase 2.D-archive, BEFORE the original row is safe-wiped. Survives forever so a Global Admin can review (and selectively delete) the tenant's custom rules from /admin/customs-archive after offboarding completes. Storage layout (see PR3.B plan §3.2): PartitionKey: "{normalizedTenantId}_{historyRowKey}". One partition per offboarding run — Re-Re-Offboarding produces a fresh, immutable partition each time so multiple runs co-exist without RowKey collisions.RowKey: "{originalTable}_{base64url(originalRowKey)}". The table-name prefix disambiguates collisions across the three rules tables; the base64url encoding of avoids the Azure-Tables RowKey-forbidden characters (#, ?, /, \, control chars). */
export interface TenantOffboardingCustomsArchiveEntry {
  /** "{normalizedTenantId}_{historyRowKey}". */
  partitionKey: string;
  /** "{originalTable}_{base64url(originalRowKey)}". */
  rowKey: string;
  tenantId: string;
  /** One of GatherRules / AnalyzeRules / ImeLogPatterns. */
  originalTable: string;
  originalPartitionKey: string;
  /** Original RowKey verbatim — base64url-encoded into RowKey's suffix. */
  originalRowKey: string;
  /** JSON dump of every property on the source row (excluding system properties). The full body is small enough to fit in a single Azure Table string property (64 KB limit; observed rule bodies are well under 4 KB). If a future rule type ever exceeds that, an EntityJsonBlobUrl overflow pointer can be added — not part of this PR. */
  entityJson: string;
  /** Cross-reference to the OffboardingHistory row for the run. */
  historyRowKey: string;
  archivedAt: string;
  /** Constant "TenantOffboardingHandler" in production. Test fakes may override. */
  archivedBy: string;
}

/** Response of POST rules/gather/test-pattern: the per-line evaluation of a logparser regex with the agent's exact matching semantics. */
export interface TestLogPatternResponse {
  success: boolean;
  /** "cmtrace" or "text" — the effective mode the lines were evaluated in. */
  format: string;
  result: LogPatternTestResult;
}

/** Response of POST config/{tenantId}/test-notification AND POST global/config/test-ops-channel: the delivery verdict of a test send (HTTP 200 for both verdicts — Success carries the outcome). One shape for both because the semantics are identical; the platform endpoint only differs in which channel list it resolves the id against. */
export interface TestWebhookNotificationResponse {
  success: boolean;
  /** HTTP status returned by the webhook endpoint, or null when the send never got a response — the key is omitted when null. */
  statusCode?: number;
  message: string;
}

/** Per-app rollup of ESP-blocking install intervals across the clean sessions of an aggregate row. The what-if numbers are the per-session critical-path savings from removing this app (recomputed union end without it) — an upper BOUND by construction: UI copy must say "up to", never "you will save" (truthfulness rule 3). */
export interface TimeAttributionBlockingAppStat {
  appId: string;
  appName: string;
  /** Clean sessions with a measured interval for this app (row gate: ≥5). */
  sessionCount: number;
  medianSeconds: number;
  p75Seconds: number;
  medianSavingSeconds: number;
  p75SavingSeconds: number;
}

/** Daily fleet rollup of session time attribution for one (tenant, enrollment class, date) — F1 PR2 (insights spec §F1 "Data & compute changes"). Enrollment classes are never mixed (a WhiteGlove flow has a structurally different time profile than user-driven). Only breakdowns with None enter the statistics; flagged sessions are excluded WITH a disclosed count (truthfulness rule 7). Rows are written even below the ≥20-session UI gate — the UI needs the n to say "insufficient data (n=3)" instead of silently rendering a small-n median (rule 4). Recomputed idempotently by the rolling 30-day maintenance sweep. */
export interface TimeAttributionDailyAggregate {
  /** Calendar day (UTC) the sessions STARTED, "yyyy-MM-dd" — same bucketing as the usage snapshots. */
  date: string;
  /** Tenant GUID, or "global" for the cross-tenant row. */
  tenantId: string;
  /** Enrollment class: "user_driven", "whiteglove", "self_deploying" or "device_preparation" (WDP v2). */
  enrollmentClass: string;
  /** Attribution algorithm version the underlying breakdowns were computed with (rule 8: never mix definitions). */
  attributionVersion: number;
  /** Breakdowns with QualityFlags == None that formed the statistics. */
  cleanSessionCount: number;
  /** Flagged breakdowns excluded from the statistics (disclosed, never silent). */
  flaggedExcludedCount: number;
  /** Terminal sessions of this bucket without a computable breakdown (e.g. events aged out before backfill). */
  missingBreakdownCount: number;
  segmentStats: TimeAttributionSegmentStat[];
  /** Top blocking apps by median interval, gated at ≥5 sessions per app, capped at 20. */
  topBlockingApps: TimeAttributionBlockingAppStat[];
  computedAt: string;
}

/** Data-quality flags for a SessionTimeBreakdown (truthfulness rule 7: problems flag the record and exclude it from fleet aggregates with a disclosed count, rather than silently skewing them). */
export type TimeAttributionFlags = "None" | "ClockSkewDropped" | "PartialObservation" | "BlockingSetUnknown" | "BlockingSetTruncated" | "WhiteGloveAnchorsIncomplete" | "PriorEnrollmentResidue";

/** Response of GET metrics/time-attribution and GET global/metrics/time-attribution: the rolling 30-day range statistics per enrollment class plus the daily rows for the per-day trend. The range window is FIXED at the sweep's 30 days. */
export interface TimeAttributionMetricsResponse {
  success: boolean;
  windowDays: number;
  /** Range statistics per enrollment class (never mixed), class-name ordinal order. */
  classes: TimeAttributionDailyAggregate[];
  /** Daily rows of the window, date ordinal order. */
  daily: TimeAttributionDailyAggregate[];
}

/** Distribution of one attribution segment across the clean sessions of an aggregate row. Values are whole seconds; a session that has no span of this segment contributes 0 — the distribution answers "time spent in this segment per enrollment of this class", not "per enrollment that happened to enter it". */
export interface TimeAttributionSegmentStat {
  /** Segment key (TimeAttributionSegments), including "unattributed" — the stack must sum to the wall clock (truthfulness rule 2). */
  segmentKey: string;
  medianSeconds: number;
  p75Seconds: number;
  p90Seconds: number;
}

/** One contiguous attributed span inside an observation window. */
export interface TimeAttributionSpan {
  /** Segment key — one of the TimeAttributionSegments constants (never "unattributed": the remainder is a scalar, not a span claim). */
  segmentKey: string;
  startUtc: string;
  endUtc: string;
  /** Whole seconds (floored). The sub-second dust lands in the unattributed remainder — never invented into a segment. */
  seconds: number;
}

/** A single CVE ranked by how many devices it affects in scope. EpssScore is the highest EPSS probability seen across the rows (null = unscored / legacy rows); Priority the highest act/attend/track band (empty on legacy rows). */
export interface TopCve {
  cveId: string;
  cvssSeverity: string;
  cvssScore: number;
  isKev: boolean;
  epssScore?: number;
  priority: string;
  affectedSessions: number;
  sampleSoftware: string[];
}

/** An app with a high failure rate. */
export interface TopFailingApp {
  appName: string;
  failCount: number;
  totalCount: number;
  successRate: number;
}

/** One serial-number bucket in the GetTpmPssUnsupported aggregation. All values are self-reported by devices through the unauthenticated distress channel — UNVERIFIED. */
export interface TpmPssUnsupportedItem {
  serialNumber: string;
  manufacturer: string;
  model: string;
  attemptCount: number;
  firstSeen: string;
  lastSeen: string;
}

/** Success body of GET audit/tpm-pss-unsupported: devices whose TPM cannot perform RSA-PSS signing, aggregated by serial number over the distress-report retention window. */
export interface TpmPssUnsupportedResponse {
  success: boolean;
  aggregated: TpmPssUnsupportedItem[];
  /** Count of raw TpmPssUnsupported distress reports before aggregation. */
  totalRawReports: number;
  dataQualityNotice: string;
}

/** Response of POST vulnerability/sync-epss (dual-purpose: success reflects whether the synchronous EPSS re-score finished without error). */
export interface TriggerEpssSyncResponse {
  success: boolean;
  message: string;
  epssRows: number;
  epssCves: number;
  epssScored: number;
  epssRowsRewritten: number;
  /** Error text of a failed run; null (key omitted) on success. */
  epssError?: string;
  durationMs: number;
  /** Preformatted ISO-8601 timestamp of this response. */
  syncedAt: string;
}

/** Response of POST maintenance/trigger. */
export interface TriggerMaintenanceResponse {
  success: boolean;
  message: string;
  result: MaintenanceResult;
  triggeredBy: string;
  triggeredAt: string;
}

/** Response of POST vulnerability/sync-msrc (dual-purpose: success reflects whether the MSRC index refresh finished without error). */
export interface TriggerMsrcSyncResponse {
  success: boolean;
  message: string;
  msrcCves: number;
  msrcDocs: number;
  msrcDocsFailed: number;
  msrcSkipped: boolean;
  /** Error text of a failed run; null (key omitted) on success. */
  msrcError?: string;
  durationMs: number;
  /** Preformatted ISO-8601 timestamp of this response. */
  syncedAt: string;
}

/** Response of POST vulnerability/sync-nvd (202): the background walk was started. */
export interface TriggerNvdCacheRefreshResponse {
  success: boolean;
  message: string;
  /** Preformatted ISO-8601 start timestamp. */
  startedAt: string;
}

/** Response of POST vulnerability/sync: KEV refresh plus optional CPE reseed counts. */
export interface TriggerVulnerabilityDataSyncResponse {
  success: boolean;
  message: string;
  kevCatalogEntries: number;
  cpeSeedEntries: number;
  /** Error text of a failed seed reseed; null (key omitted) when skipped or successful. */
  cpeSeedError?: string;
  cpeCommunityEntries: number;
  /** Error text of a failed community reseed; null (key omitted) when skipped or successful. */
  cpeCommunityError?: string;
  /** Preformatted ISO-8601 timestamp of this response. */
  syncedAt: string;
}

/** One unmatched software title (no CPE mapping), aggregated across tenants. Mutable on purpose: the aggregation sums Frequency and keeps the most recent sighting. */
export interface UnmatchedSoftwareItem {
  softwareName?: string;
  publisher?: string;
  /** Sum of SessionCount across all tenants' rows for this title. */
  frequency: number;
  lastSeenAt?: string;
  exampleSessionId?: string;
  normalizedVendor?: string;
  normalizedVersion?: string;
}

/** Response of PUT global/config: acknowledgement plus the stored admin configuration. */
export interface UpdateAdminConfigurationResponse {
  success: boolean;
  message: string;
  config: AdminConfiguration;
}

/** Response of POST config/{tenantId}/app-homing on an allowed flip (or allowed no-op): the resulting homing state plus the consent-probe verdict. */
export interface UpdateTenantAppHomingResponse {
  success: boolean;
  /** False for an allowed no-op (already homed at the target). */
  changed: boolean;
  /** "primary" or "legacy" — the app the tenant resolves to after the call. */
  homedApp: string;
  /** Explicit homing pin, or null (legacy default) — the key is omitted when null. */
  homedAppClientId?: string;
  /** Client id observed on the tenant's last agent auth, or null — the key is omitted when null. */
  lastAuthClientId?: string;
  lastAuthClientIdSince?: string;
  probe: AppHomingProbeWire;
}

/** Response of PUT config/{tenantId}: acknowledgement plus the stored tenant configuration. */
export interface UpdateTenantConfigurationResponse {
  success: boolean;
  message: string;
  config: TenantConfiguration;
}

/** Response of PUT sessions/{sessionId}/annotations/{lane} when both verdict and note were empty and the lane was cleared. */
export interface UpsertSessionAnnotationDeletedResponse {
  success: boolean;
  deleted: boolean;
}

/** Response of PUT sessions/{sessionId}/annotations/{lane} on a successful upsert: the stored annotation as the session-scoped endpoints would return it. */
export interface UpsertSessionAnnotationResponse {
  success: boolean;
  annotation: SessionAnnotationItem;
}

export interface UserMetrics {
  /** Total unique users (available after Entra ID integration) */
  total: number;
  /** Daily logins across all users */
  dailyLogins: number;
  /** Active users in last 7 days */
  active7Days: number;
  /** Active users in last 30 days */
  active30Days: number;
  /** Note about availability */
  note: string;
}

/** Aggregated daily usage summary across endpoints (and optionally users). */
export interface UserUsageDailySummary {
  date: string;
  tenantId?: string;
  totalRequests: number;
  uniqueUsers: number;
  uniqueEndpoints: number;
}

/** A single usage record: one user, one day, one endpoint. */
export interface UserUsageRecord {
  userId: string;
  userPrincipalName: string;
  tenantId: string;
  endpoint: string;
  date: string;
  requestCount: number;
  lastRequestAt: string;
}

/** One ACTIVE verdict-calibration alert episode (operator-only). Persisted as the verdictcalibration|{kind}|{path}|{status} keyspace of the notification tracker — the row IS the dedup (one ops event per episode) and the alerts[] payload of the calibration endpoint. Deleted when the signal re-arms (share back under 1.5× baseline, path stops occurring, evidence-gap share back under 15 %) or by the tracker's 30-day retention sweep. Numbers refresh on every radar pass; FirstNotifiedAt never moves. Dimension concentration is CORRELATION only — every consumer says so. */
export interface VerdictCalibrationAlert {
  tenantId: string;
  /** One of VerdictCalibrationAlertKinds. */
  kind: string;
  /** Verdict path, or the group label for group kinds ("sweep+maxlife", "r6"). */
  verdictPath: string;
  /** Status the path produced, or "*" for group kinds. */
  status: string;
  windowHitCount: number;
  windowSessionCount: number;
  baselineHitCount: number;
  baselineSessionCount: number;
  windowRatePct: number;
  baselineRatePct: number;
  /** Window rate ÷ baseline rate. Null when the baseline rate is 0 or the kind is absolute (evidence gap). */
  lift?: number;
  windowStartDate: string;
  windowEndDate: string;
  /** Dimension concentration captured when the alert FIRST fired; null = no clear concentration. */
  dimension?: RuleRegressionDimension;
  firstNotifiedAt: string;
  lastEvaluatedAt: string;
}

/** One verdict path × status row of the calibration matrix. */
export interface VerdictCalibrationPathRow {
  verdictPath: string;
  status: string;
  count: number;
  sharePct: number;
  derivedCount: number;
  eligible7d: number;
  reEnrolled7d: number;
  /** Null below the minimum eligible count — never a rate on a handful of sessions. */
  reEnrollRatePct?: number;
  overriddenByAdmin: number;
  overriddenByLateCompletion: number;
  overriddenOther: number;
  window7: VerdictCalibrationTrendWindow;
  baseline28: VerdictCalibrationTrendWindow;
  /** Window share ÷ baseline share; null when the baseline share is 0 (a new path has no finite lift — never invented). */
  lift?: number;
}

/** Response of GET global/metrics/verdict-calibration: per verdict path, how many sessions it produced in the window, its share, overrides attributed to the prior path, the 7-day re-enrollment proxy, a 7d-vs-28d trend, and the active drift alerts. Operator-only classifier diagnostics. */
export interface VerdictCalibrationResponse {
  success: boolean;
  /** Partition echo: a tenant GUID, or "global" for the cross-tenant aggregate. */
  tenantId: string;
  windowDays: number;
  /** Inclusive window start ("yyyy-MM-dd"). */
  windowStart: string;
  /** Inclusive window end ("yyyy-MM-dd", today). */
  windowEnd: string;
  /** Newest aggregate compute time in the window; absent when the window holds no rows. */
  computedAt?: string;
  /** Distinct aggregation algorithm versions contributing to the window, ascending. */
  versions: number[];
  totals: VerdictCalibrationTotals;
  trend: VerdictCalibrationTrendMeta;
  /** Rows ordered by count descending, then path/status ordinal. */
  paths: VerdictCalibrationPathRow[];
  /** Active drift episodes, newest first. */
  alerts: VerdictCalibrationAlert[];
}

/** Window totals of the calibration matrix. */
export interface VerdictCalibrationTotals {
  sessions: number;
  terminal: number;
  derived: number;
  /** Aggregate days that contributed to the window. */
  days: number;
}

/** Trend denominators shared by every row (single source, never per-row copies). */
export interface VerdictCalibrationTrendMeta {
  windowDays: number;
  baselineDays: number;
  windowSessions: number;
  baselineSessions: number;
}

/** One trend window of a path row. */
export interface VerdictCalibrationTrendWindow {
  count: number;
  sessions: number;
  sharePct: number;
}

export interface VerificationIssue {
  /** Info / Warning / Error. */
  severity: string;
  /** Discriminator string: reducer_version_drift, signal_ordinal_gap, step_index_gap, orphaned_transition, empty_session, … */
  kind: string;
  message: string;
}

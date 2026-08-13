export interface AdminConfiguration {
  partitionKey: string;
  rowKey: string;
  lastUpdated: string;
  updatedBy: string;
  globalRateLimitRequestsPerMinute: number;
  /** Per-user (portal/JWT) rate limit for standard users (Tenant Admins, Operators, Viewers). Default 120. */
  userRateLimitRequestsPerMinute?: number;
  /** Per-user (portal/JWT) rate limit for Global Admins. Default 600. */
  globalAdminRateLimitRequestsPerMinute?: number;
  platformStatsBlobSasUrl?: string;
  /**
   * Agent endpoint migration: global re-home target served to agents as
   * `MigrateToApiBaseUrl` on the config channel (e.g. "https://autopilotmonitor-api-us.azurewebsites.net").
   * Empty = no migration. Set on the backend being ABANDONED during a move.
   */
  agentMigrateApiBaseUrl?: string;
  /**
   * JSON object tenantId → target URL for per-tenant moves; an empty-string value
   * pins that tenant (no migration) even while the global target is set.
   */
  agentMigrateTenantOverridesJson?: string;
  collectorIdleTimeoutMinutes?: number;
  desktopDetectorNoCandidateTimeoutMinutes?: number;
  excessiveEventCountThreshold?: number;
  /**
   * Auto-action mode for runaway sessions whose EventCount crosses
   * `excessiveEventAutoActionThreshold`. "Off" keeps warn-only behaviour;
   * "Block" stops device uploads for `excessiveEventAutoActionDurationHours`;
   * "Kill" issues a remote self-destruct signal. Server tolerates casing drift
   * but the UI emits these canonical values.
   */
  excessiveEventAutoActionMode?: "Off" | "Block" | "Kill";
  /** Threshold for auto-block/kill. Should be higher than `excessiveEventCountThreshold`. 0 disables. */
  excessiveEventAutoActionThreshold?: number;
  /** Block duration in hours when the auto-action fires. */
  excessiveEventAutoActionDurationHours?: number;
  opsEventRetentionDays?: number;
  slaNotificationCooldownHours?: number;
  diagnosticsGlobalLogPathsJson?: string;
  /** Max diagnostics download size in MB; blobs above are rejected (413). 0 = no limit. Default 500. */
  maxDiagnosticsDownloadSizeMB?: number;
  /** Timeout in seconds for the whole diagnostics download+stream. 0 = no timeout. Default 120. */
  diagnosticsDownloadTimeoutSeconds?: number;
  /** JSON tier name → rate limits/features, e.g. {"pro":{"apiRateLimit":300}}. */
  planTierDefinitionsJson?: string;
  /** MCP server access: "Disabled" | "WhitelistOnly" (default) | "AllMembers". */
  mcpAccessPolicy?: string;
  /** Global kill-switch for the in-app feedback prompt. Default true. */
  feedbackEnabled?: boolean;
  /** Minimum tenant age in days before the feedback prompt shows. Default 14. */
  feedbackMinTenantAgeDays?: number;
  /** Cooldown in days before a user is re-prompted for feedback. 0 = single wave. Default 60. */
  feedbackCooldownDays?: number;
  /** JSON list of ImeLogPattern IDs re-emitted as WhiteGloveSealingPatternDetected. Empty = off. */
  whiteGloveSealingPatternIdsJson?: string;
  nvdApiKey?: string;
  vulnerabilityCorrelationEnabled?: boolean;
  vulnerabilityDataLastSyncUtc?: string;
  msrcLastSyncUtc?: string;
  opsAlertRulesJson?: string;
  opsAlertTelegramEnabled?: boolean;
  opsAlertTelegramChatId?: string;
  opsAlertTeamsEnabled?: boolean;
  opsAlertTeamsWebhookUrl?: string;
  opsAlertSlackEnabled?: boolean;
  opsAlertSlackWebhookUrl?: string;
  allowAgentDowngrade?: boolean;
  modernDeploymentHarmlessEventIdsJson?: string;
  // V2 agent hash oracle (written by CI/CD / build scripts, surfaced read-only; must
  // round-trip via Save to survive Replace). The V1-era latestAgent*/latestBootstrapScriptVersion
  // fields were retired with the legacy agent line and no longer exist on the C# model.
  latestAgentV2Version?: string;
  latestAgentV2Sha256?: string;
  latestAgentV2ExeSha256?: string;
  latestBootstrapV2ScriptVersion?: string;
  /**
   * Feature flag for the V2 Decision Engine index-table dual-write (Plan §M5.d).
   * When true, IngestTelemetryFunction enqueues telemetry-index-reconcile envelopes after
   * committing each primary row, and the 2h IndexReconcileTimer re-scans the last 4h as
   * a safety net. Default false — keeps pre-M5.d behaviour bit-exact until explicitly flipped.
   */
  enableIndexDualWrite?: boolean;
  /**
   * Global emergency kill-switch for the cascade-deletion subsystem (Plan §1 P8 / §9).
   * When true: cascade producers return 503 and the cascade worker pauses on entry.
   * Default false.
   */
  sessionDeletionKillSwitch?: boolean;
  /**
   * When true, new tenant signups are activated automatically ~1 minute after first
   * sign-in (tenant-auto-approve queue worker). When false, signups wait for manual
   * approval in Tenant Management. Default false.
   */
  autoApproveNewTenants?: boolean;
  /**
   * Dual app-reg self-service homing flip (consent funnel + auto-flip + tenant-admin
   * manual flip). Kill switch: turning it off stops all of the above immediately;
   * only Global Admins can flip while it is off. Default false.
   */
  selfServiceAppHomingEnabled?: boolean;
}

export interface OpsAlertRule {
  eventType: string;
  minSeverity: string;
  enabled: boolean;
}

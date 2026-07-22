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
  customSettings?: string;
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
  // Agent hash oracle (written by CI/CD / build scripts, surfaced read-only; must round-trip via Save to survive Replace)
  latestAgentVersion?: string;
  latestAgentSha256?: string;
  latestAgentExeSha256?: string;
  latestBootstrapScriptVersion?: string;
  // V2 agent hash oracle (separate release line — written by scripts/Deployment/V2/*.ps1)
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
}

export interface OpsAlertRule {
  eventType: string;
  minSeverity: string;
  enabled: boolean;
}

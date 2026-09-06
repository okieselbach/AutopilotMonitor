/**
 * Row catalog for the Tenant Config Report (admin/tenants/config-report).
 *
 * Both halves of the report are exhaustive over the generated wire types: `TENANT_FIELDS`
 * over every key of `TenantConfiguration` (GET config/{tenantId}) and `RUNTIME_FIELDS` /
 * `COLLECTOR_FIELDS` / `ANALYZER_FIELDS` over every key of `AgentConfigResponse` and its two
 * sub-objects (what GET agent/config serves). A key is either rendered (a row with section
 * and label) or excluded with a reason — nothing can be silently forgotten: a new C# property
 * forces the wire-type regeneration (SharedManifestParityTests) and `tsc` then fails here
 * until the key is catalogued. The vitest next to this module pins the same thing at runtime
 * against `SHARED_MANIFEST.tenantConfiguration.fields`.
 *
 * Runtime rows mirror the resolution rules of GetAgentConfigFunction.cs (backend); every
 * rule carries the source it reads from so the operator sees which knob to turn.
 */
import type {
  AdminConfiguration,
  AgentConfigResponse,
  AnalyzerConfiguration,
  CollectorConfiguration,
  TenantConfiguration,
  TenantFeatureFlagsResponse,
} from "@/utils/wire-types.generated";

// ── Row model ────────────────────────────────────────────────────────────────

/** How a value is rendered. `secret` never shows the value, only whether one is set. */
export type RowKind = "value" | "date" | "masked" | "secret";

export interface TenantRow {
  section: TenantSection;
  label: string;
  kind?: RowKind;
  /**
   * The C# initializer of TenantConfiguration.cs. A row WITH this key (even `null`) is
   * compared against it and highlighted as "custom" when the stored value differs; a row
   * without it is informational and never highlighted.
   */
  default?: unknown;
}

export interface Excluded {
  excluded: string;
}

export const isExcluded = <T extends object>(spec: T | Excluded): spec is Excluded =>
  "excluded" in spec;

export const TENANT_SECTIONS = [
  "Tenant Status",
  "Plan & Billing",
  "Identity & App Homing",
  "Security & Validation",
  "Hardware Whitelist",
  "Data Management",
  "Agent Collectors",
  "Auth Circuit Breaker",
  "Agent Behavior",
  "Enrollment Summary",
  "Analyzers",
  "Webhooks",
  "SLA Targets",
  "Diagnostics",
  "Feature Flags (Global)",
] as const;
export type TenantSection = (typeof TENANT_SECTIONS)[number];

const NTP_DEFAULT = "time.windows.com";

// ── Tenant configuration (left column) ───────────────────────────────────────

const HEADER = { excluded: "shown in the tenant header card" } as const;
const CHANNEL_BLOCK = { excluded: "rendered by the notification channel block (legacy fallback order)" } as const;

export const TENANT_FIELDS: Record<keyof TenantConfiguration, TenantRow | Excluded> = {
  tenantId: HEADER,
  domainName: HEADER,
  lastUpdated: HEADER,
  updatedBy: HEADER,

  // Tenant Status
  disabled: { section: "Tenant Status", label: "Disabled", default: false },
  disabledReason: { section: "Tenant Status", label: "Disabled Reason" },
  disabledUntil: { section: "Tenant Status", label: "Disabled Until", kind: "date" },
  mcpDisabled: { section: "Tenant Status", label: "MCP Disabled", default: false },
  mcpDisabledReason: { section: "Tenant Status", label: "MCP Disabled Reason" },
  onboardedAt: { section: "Tenant Status", label: "Onboarded At", kind: "date" },
  onboardedBy: { section: "Tenant Status", label: "Onboarded By" },
  contactEmail: { section: "Tenant Status", label: "Contact Email" },
  companyName: { section: "Tenant Status", label: "Company" },

  // Plan & Billing (stored values; the effective edition comes from feature-flags, header card)
  planTier: { section: "Plan & Billing", label: "Plan Tier (stored)", default: "free" },
  trialExpiresUtc: { section: "Plan & Billing", label: "Trial Expires", kind: "date" },
  trialStartedUtc: { section: "Plan & Billing", label: "Trial Started", kind: "date" },
  trialGrantedBy: { section: "Plan & Billing", label: "Trial Granted By" },
  trialConsumed: { section: "Plan & Billing", label: "Trial Consumed", default: false },
  proDowngradedUtc: { section: "Plan & Billing", label: "Pro Downgraded At", kind: "date" },
  payingCustomer: { section: "Plan & Billing", label: "Paying Customer", default: false },
  maxDelegatedTenantsOverride: { section: "Plan & Billing", label: "Delegated Tenant Slots (override)", default: null },
  mcpUsagePlanOverride: { section: "Plan & Billing", label: "MCP Usage Plan (override)", default: null },
  managedByProTenantId: { section: "Plan & Billing", label: "Managed By Pro Tenant", default: null },

  // Identity & App Homing
  homedAppClientId: { section: "Identity & App Homing", label: "Homed App Client ID" },
  lastAuthClientId: { section: "Identity & App Homing", label: "Last Auth Client ID" },
  lastAuthClientIdSince: { section: "Identity & App Homing", label: "Last Auth Client ID Since", kind: "date" },
  entraAppRolesEnabled: { section: "Identity & App Homing", label: "Entra App Roles Enabled", default: false },

  // Security & Validation
  validateAutopilotDevice: { section: "Security & Validation", label: "Validate Autopilot Device", default: false },
  validateCorporateIdentifier: { section: "Security & Validation", label: "Validate Corporate Identifier", default: false },
  validateDeviceAssociation: { section: "Security & Validation", label: "Validate Device Association", default: false },
  validateCloudPcDevice: { section: "Security & Validation", label: "Validate Cloud PC Device", default: false },
  validateIntuneDeviceBinding: { section: "Security & Validation", label: "Validate Intune Device Binding", default: false },
  allowInsecureAgentRequests: { section: "Security & Validation", label: "Allow Insecure Agent Requests", default: false },
  customRateLimitRequestsPerMinute: { section: "Security & Validation", label: "Device API Rate Limit Override", default: null },
  customUserRateLimitRequestsPerMinute: { section: "Security & Validation", label: "MCP & Integrations API Rate Limit Override", default: null },

  // Hardware Whitelist
  manufacturerWhitelist: { section: "Hardware Whitelist", label: "Manufacturers", default: "Dell*,HP*,Lenovo*,Microsoft Corporation" },
  modelWhitelist: { section: "Hardware Whitelist", label: "Models", default: "*" },
  webhookNotifyOnHardwareRejection: { section: "Hardware Whitelist", label: "Notify On Hardware Rejection", default: false },

  // Data Management
  dataRetentionDays: { section: "Data Management", label: "Data Retention (days)", default: 90 },
  sessionTimeoutHours: { section: "Data Management", label: "Session Timeout (hours)", default: 5 },
  sessionGraceHours: { section: "Data Management", label: "Session Grace (hours)", default: 0 },
  absoluteMaxSessionHours: { section: "Data Management", label: "Absolute Max Session (hours)", default: null },
  maxNdjsonPayloadSizeMB: { section: "Data Management", label: "Max NDJSON Payload (MB)", default: 5 },

  // Agent Collectors
  enablePerformanceCollector: { section: "Agent Collectors", label: "Performance Collector", default: true },
  performanceCollectorIntervalSeconds: { section: "Agent Collectors", label: "Perf. Interval (sec)", default: 30 },
  helloWaitTimeoutSeconds: { section: "Agent Collectors", label: "Hello Wait Timeout (sec)", default: 30 },

  // Auth Circuit Breaker
  maxAuthFailures: { section: "Auth Circuit Breaker", label: "Max Auth Failures", default: null },
  authFailureTimeoutMinutes: { section: "Auth Circuit Breaker", label: "Auth Failure Timeout (min)", default: null },
  agentMaxLifetimeMinutes: { section: "Auth Circuit Breaker", label: "Agent Max Lifetime (min)", default: null },

  // Agent Behavior
  selfDestructOnComplete: { section: "Agent Behavior", label: "Self-Destruct On Complete", default: true },
  keepLogFile: { section: "Agent Behavior", label: "Keep Log File", default: false },
  rebootOnComplete: { section: "Agent Behavior", label: "Reboot On Complete", default: null },
  rebootDelaySeconds: { section: "Agent Behavior", label: "Reboot Delay (sec)", default: null },
  enableGeoLocation: { section: "Agent Behavior", label: "Geo-Location", default: null },
  enableTimezoneAutoSet: { section: "Agent Behavior", label: "Timezone Auto-Set", default: null },
  enableDoGroupIdAutoSet: { section: "Agent Behavior", label: "DO GroupId Auto-Set", default: null },
  // TableConfigRepository maps an empty stored value to the default, so the wire never carries null here.
  ntpServer: { section: "Agent Behavior", label: "NTP Server", default: NTP_DEFAULT },
  keepAwakeDuringUserEsp: { section: "Agent Behavior", label: "Keep Awake During User ESP", default: null },
  enableImeMatchLog: { section: "Agent Behavior", label: "IME Match Log", default: null },
  enableGatherRuleDebugLog: { section: "Agent Behavior", label: "Gather Rule Debug Log", default: null },
  enableEspContinueAnywayObservation: { section: "Agent Behavior", label: "Continue-Anyway Observation", default: null },
  logLevel: { section: "Agent Behavior", label: "Log Level", default: null },
  maxBatchSize: { section: "Agent Behavior", label: "Max Batch Size", default: null },
  showScriptOutput: { section: "Agent Behavior", label: "Show Script Output", default: true },
  sendTraceEvents: { section: "Agent Behavior", label: "Send Trace Events", default: true },

  // Enrollment Summary
  showEnrollmentSummary: { section: "Enrollment Summary", label: "Show Summary", default: null },
  enrollmentSummaryTimeoutSeconds: { section: "Enrollment Summary", label: "Timeout (sec)", default: null },
  enrollmentSummaryBrandingImageUrl: { section: "Enrollment Summary", label: "Branding Image URL", default: null },
  enrollmentSummaryLaunchRetrySeconds: { section: "Enrollment Summary", label: "Launch Retry (sec)", default: null },

  // Analyzers
  enableLocalAdminAnalyzer: { section: "Analyzers", label: "Local Admin Analyzer", default: null },
  localAdminAllowedAccountsJson: { section: "Analyzers", label: "Allowed Local Admin Accounts", default: null },
  enableSoftwareInventoryAnalyzer: { section: "Analyzers", label: "Software Inventory Analyzer", default: null },
  enableIntegrityBypassAnalyzer: { section: "Analyzers", label: "Integrity Bypass Analyzer", default: null },
  enableConsoleBypassDetection: { section: "Analyzers", label: "Console Bypass Detection", default: null },
  enableRealmJoinWatcher: { section: "Analyzers", label: "Realm Join Watcher", default: null },

  // Webhooks — channel list plus the legacy single-webhook fields it falls back to
  notificationChannelsJson: CHANNEL_BLOCK,
  webhookProviderType: CHANNEL_BLOCK,
  webhookUrl: CHANNEL_BLOCK,
  webhookNotifyOnSuccess: CHANNEL_BLOCK,
  webhookNotifyOnFailure: CHANNEL_BLOCK,
  webhookNotifyOnStart: CHANNEL_BLOCK,
  teamsWebhookUrl: CHANNEL_BLOCK,
  teamsNotifyOnSuccess: CHANNEL_BLOCK,
  teamsNotifyOnFailure: CHANNEL_BLOCK,
  teamsNotifyOnStart: CHANNEL_BLOCK,
  webhookCustomHeadersJson: { section: "Webhooks", label: "Custom Headers", kind: "secret" },

  // SLA Targets
  slaTargetSuccessRate: { section: "SLA Targets", label: "Target Success Rate (%)", default: null },
  slaTargetMaxDurationMinutes: { section: "SLA Targets", label: "Target Max Duration (min)", default: null },
  slaTargetAppInstallSuccessRate: { section: "SLA Targets", label: "Target App Install Success Rate (%)", default: null },
  slaNotifyOnSuccessRateBreach: { section: "SLA Targets", label: "Notify On Success Rate Breach", default: false },
  slaSuccessRateNotifyThreshold: { section: "SLA Targets", label: "Success Rate Notify Threshold (%)", default: null },
  slaNotifyOnDurationBreach: { section: "SLA Targets", label: "Notify On Duration Breach", default: false },
  slaNotifyOnAppInstallBreach: { section: "SLA Targets", label: "Notify On App Install Breach", default: false },
  slaNotifyOnConsecutiveFailures: { section: "SLA Targets", label: "Notify On Consecutive Failures", default: false },
  slaConsecutiveFailureThreshold: { section: "SLA Targets", label: "Consecutive Failure Threshold", default: 5 },

  // Diagnostics
  diagnosticsUploadMode: { section: "Diagnostics", label: "Upload Mode", default: "Off" },
  diagnosticsUploadDestination: { section: "Diagnostics", label: "Upload Destination", default: "CustomerSas" },
  diagnosticsBlobSasUrl: { section: "Diagnostics", label: "Blob SAS URL", kind: "masked" },
  diagnosticsLogPathsJson: { section: "Diagnostics", label: "Custom Log Paths", default: null },

  // Feature Flags (Global)
  bootstrapTokenEnabled: { section: "Feature Flags (Global)", label: "Bootstrap Token Enabled", default: false },
  unrestrictedModeEnabled: { section: "Feature Flags (Global)", label: "Unrestricted Mode Enabled (GA gate)", default: false },
  unrestrictedMode: { section: "Feature Flags (Global)", label: "Unrestricted Mode (tenant toggle)", default: false },
};

// ── Runtime parameters (right column) ────────────────────────────────────────

export interface RuntimeContext {
  config: TenantConfiguration;
  /** GET global/config — null while loading or when the call failed. */
  adminConfig: AdminConfiguration | null;
  /** GET config/{tenantId}/feature-flags — backend-resolved entitlement surface. */
  flags: TenantFeatureFlagsResponse | null;
}

/** Which knob a runtime value is resolved from. */
export type RuntimeSource = "tenant" | "admin" | "flags" | "constant";

export const RUNTIME_SECTIONS = [
  "Upload & Batching",
  "Agent Behavior",
  "Auth Circuit Breaker",
  "Enrollment Summary",
  "Collectors",
  "Diagnostics",
  "Analyzers",
] as const;
export type RuntimeSection = (typeof RUNTIME_SECTIONS)[number];

/** Sentinel for a value that cannot be resolved because its source did not load. */
export const UNRESOLVED = Symbol("unresolved");

export interface RuntimeRow {
  section: RuntimeSection;
  label: string;
  source: RuntimeSource;
  /** Backend resolution rule (GetAgentConfigFunction.cs). */
  value: (ctx: RuntimeContext) => unknown;
  /** The agent-side default (AgentConfigResponse.cs / AdminConfiguration.cs initializer) for the "custom" highlight. */
  default?: unknown;
}

/** Shared.Constants.DefaultUploadIntervalSeconds — the one runtime constant not on any config row. */
export const DEFAULT_UPLOAD_INTERVAL_SECONDS = 10;

/** GetAgentConfigFunction.ResolveDiagnosticsUploadEnabled: a SAS URL or the hosted destination, regardless of mode. */
export const resolveDiagnosticsUploadEnabled = (c: TenantConfiguration): boolean =>
  !!c.diagnosticsBlobSasUrl || (c.diagnosticsUploadDestination ?? "").toLowerCase() === "hosted";

/** TenantConfiguration.GetLocalAdminAllowedAccounts: JSON string array, anything else → empty. */
export const parseLocalAdminAllowedAccounts = (json: string | null | undefined): string[] => {
  if (!json) return [];
  try {
    const parsed: unknown = JSON.parse(json);
    return Array.isArray(parsed) ? parsed.filter((x): x is string => typeof x === "string") : [];
  } catch {
    return [];
  }
};

/** AdminConfiguration.GetModernDeploymentHarmlessEventIds: JSON int array, blank/invalid → defaults. */
export const parseModernDeploymentHarmlessEventIds = (json: string | null | undefined): number[] => {
  const defaults = [100, 1005, 1010];
  if (!json || !json.trim()) return defaults;
  try {
    const parsed: unknown = JSON.parse(json);
    return Array.isArray(parsed) && parsed.every((x) => typeof x === "number") ? parsed : defaults;
  } catch {
    return defaults;
  }
};

const admin = <T>(pick: (a: AdminConfiguration) => T) =>
  (ctx: RuntimeContext): T | typeof UNRESOLVED => (ctx.adminConfig ? pick(ctx.adminConfig) : UNRESOLVED);

const NOT_CONFIG = { excluded: "per-request payload, not a configuration value" } as const;
const SUB_OBJECT = { excluded: "sub-object, catalogued separately" } as const;
const CLASS_DEFAULT = { excluded: "agent class default, neither tenant- nor operator-tunable" } as const;

export const RUNTIME_FIELDS: Record<keyof AgentConfigResponse, RuntimeRow | Excluded> = {
  configVersion: { excluded: "compiled into the backend" },
  uploadIntervalSeconds: { section: "Upload & Batching", label: "Upload Interval (sec)", source: "constant", value: () => DEFAULT_UPLOAD_INTERVAL_SECONDS, default: DEFAULT_UPLOAD_INTERVAL_SECONDS },
  maxBatchSize: { section: "Upload & Batching", label: "Max Batch Size", source: "tenant", value: ({ config }) => config.maxBatchSize ?? 100, default: 100 },

  selfDestructOnComplete: { section: "Agent Behavior", label: "Self-Destruct", source: "tenant", value: ({ config }) => config.selfDestructOnComplete ?? true, default: true },
  keepLogFile: { section: "Agent Behavior", label: "Keep Log File", source: "tenant", value: ({ config }) => config.keepLogFile ?? false, default: false },
  rebootOnComplete: { section: "Agent Behavior", label: "Reboot On Complete", source: "tenant", value: ({ config }) => config.rebootOnComplete ?? false, default: false },
  rebootDelaySeconds: { section: "Agent Behavior", label: "Reboot Delay (sec)", source: "tenant", value: ({ config }) => config.rebootDelaySeconds ?? 10, default: 10 },
  enableGeoLocation: { section: "Agent Behavior", label: "Geo-Location", source: "tenant", value: ({ config }) => config.enableGeoLocation ?? true, default: true },
  enableTimezoneAutoSet: { section: "Agent Behavior", label: "Timezone Auto-Set", source: "tenant", value: ({ config }) => config.enableTimezoneAutoSet ?? false, default: false },
  enableDoGroupIdAutoSet: { section: "Agent Behavior", label: "DO GroupId Auto-Set", source: "tenant", value: ({ config }) => config.enableDoGroupIdAutoSet ?? false, default: false },
  ntpServer: { section: "Agent Behavior", label: "NTP Server", source: "tenant", value: ({ config }) => config.ntpServer || NTP_DEFAULT, default: NTP_DEFAULT },
  enableImeMatchLog: { section: "Agent Behavior", label: "IME Match Log", source: "tenant", value: ({ config }) => config.enableImeMatchLog ?? false, default: false },
  enableGatherRuleDebugLog: { section: "Agent Behavior", label: "Gather Rule Debug Log", source: "tenant", value: ({ config }) => config.enableGatherRuleDebugLog ?? false, default: false },
  enableEspContinueAnywayObservation: { section: "Agent Behavior", label: "Continue-Anyway Observation", source: "tenant", value: ({ config }) => config.enableEspContinueAnywayObservation ?? false, default: false },
  logLevel: { section: "Agent Behavior", label: "Log Level", source: "tenant", value: ({ config }) => config.logLevel ?? "Info", default: "Info" },
  sendTraceEvents: { section: "Agent Behavior", label: "Send Trace Events", source: "tenant", value: ({ config }) => config.sendTraceEvents, default: true },
  // TenantEntitlementService.IsUnrestrictedModeActive = edition entitlement && GA gate && tenant toggle;
  // feature-flags carries the backend-resolved value, so the entitlement logic is not rebuilt here.
  unrestrictedMode: { section: "Agent Behavior", label: "Unrestricted Mode (effective)", source: "flags", value: ({ flags }) => (flags ? flags.unrestrictedMode : UNRESOLVED), default: false },
  allowAgentDowngrade: { section: "Agent Behavior", label: "Allow Agent Downgrade", source: "admin", value: admin((a) => a.allowAgentDowngrade), default: false },

  maxAuthFailures: { section: "Auth Circuit Breaker", label: "Max Auth Failures", source: "tenant", value: ({ config }) => config.maxAuthFailures ?? 5, default: 5 },
  authFailureTimeoutMinutes: { section: "Auth Circuit Breaker", label: "Auth Failure Timeout (min)", source: "tenant", value: ({ config }) => config.authFailureTimeoutMinutes ?? 0, default: 0 },

  showEnrollmentSummary: { section: "Enrollment Summary", label: "Show Summary", source: "tenant", value: ({ config }) => config.showEnrollmentSummary ?? false, default: false },
  enrollmentSummaryTimeoutSeconds: { section: "Enrollment Summary", label: "Timeout (sec)", source: "tenant", value: ({ config }) => config.enrollmentSummaryTimeoutSeconds ?? 60, default: 60 },
  enrollmentSummaryBrandingImageUrl: { section: "Enrollment Summary", label: "Branding Image URL", source: "tenant", value: ({ config }) => config.enrollmentSummaryBrandingImageUrl ?? null },
  enrollmentSummaryLaunchRetrySeconds: { section: "Enrollment Summary", label: "Launch Retry (sec)", source: "tenant", value: ({ config }) => config.enrollmentSummaryLaunchRetrySeconds ?? 120, default: 120 },

  diagnosticsUploadEnabled: { section: "Diagnostics", label: "Upload Enabled", source: "tenant", value: ({ config }) => resolveDiagnosticsUploadEnabled(config), default: false },
  diagnosticsUploadMode: { section: "Diagnostics", label: "Upload Mode", source: "tenant", value: ({ config }) => config.diagnosticsUploadMode ?? "Off", default: "Off" },
  diagnosticsLogPaths: { excluded: "merged global + tenant path list (see Custom Log Paths and the global diagnostics settings)" },

  collectors: SUB_OBJECT,
  analyzers: SUB_OBJECT,
  gatherRules: { excluded: "active gather-rule catalog for the tenant" },
  imeLogPatterns: { excluded: "active IME log-pattern catalog for the tenant" },
  whiteGloveSealingPatternIds: { excluded: "global pattern-id list (AdminConfiguration)" },
  latestAgentSha256: { excluded: "per-major agent hash oracle selected from the caller's X-Agent-Version (AdminConfiguration)" },
  latestAgentExeSha256: { excluded: "per-major agent hash oracle selected from the caller's X-Agent-Version (AdminConfiguration)" },
  deviceBlocked: NOT_CONFIG,
  deviceKillSignal: NOT_CONFIG,
  unblockAt: NOT_CONFIG,
  migrateToApiBaseUrl: { excluded: "endpoint-migration target, global or per-tenant override (AdminConfiguration)" },
};

export const COLLECTOR_FIELDS: Record<keyof CollectorConfiguration, RuntimeRow | Excluded> = {
  enablePerformanceCollector: { section: "Collectors", label: "Performance Collector", source: "tenant", value: ({ config }) => config.enablePerformanceCollector, default: true },
  performanceIntervalSeconds: { section: "Collectors", label: "Perf. Interval (sec)", source: "tenant", value: ({ config }) => config.performanceCollectorIntervalSeconds, default: 30 },
  collectorIdleTimeoutMinutes: { section: "Collectors", label: "Idle Timeout (min)", source: "admin", value: admin((a) => a.collectorIdleTimeoutMinutes), default: 15 },
  desktopDetectorNoCandidateTimeoutMinutes: { section: "Collectors", label: "Desktop Detector No-Candidate Timeout (min)", source: "admin", value: admin((a) => a.desktopDetectorNoCandidateTimeoutMinutes), default: 10 },
  enableAgentSelfMetrics: { section: "Collectors", label: "Agent Self-Metrics", source: "constant", value: () => true, default: true },
  agentSelfMetricsIntervalSeconds: { section: "Collectors", label: "Self-Metrics Interval (sec)", source: "constant", value: () => 60, default: 60 },
  helloWaitTimeoutSeconds: { section: "Collectors", label: "Hello Wait Timeout (sec)", source: "tenant", value: ({ config }) => config.helloWaitTimeoutSeconds, default: 30 },
  agentMaxLifetimeMinutes: { section: "Collectors", label: "Agent Max Lifetime (min)", source: "tenant", value: ({ config }) => config.agentMaxLifetimeMinutes ?? 360, default: 360 },
  modernDeploymentHarmlessEventIds: { section: "Collectors", label: "Modern Deployment Harmless Event IDs", source: "admin", value: admin((a) => parseModernDeploymentHarmlessEventIds(a.modernDeploymentHarmlessEventIdsJson)), default: [100, 1005, 1010] },
  enableDeliveryOptimizationCollector: CLASS_DEFAULT,
  deliveryOptimizationIntervalSeconds: CLASS_DEFAULT,
  enableOfficeInstallDetector: CLASS_DEFAULT,
  officeInstallSettleSeconds: CLASS_DEFAULT,
  enablePowerStateWatcher: CLASS_DEFAULT,
  stallProbeEnabled: CLASS_DEFAULT,
  stallProbeThresholdsMinutes: CLASS_DEFAULT,
  stallProbeTraceIndices: CLASS_DEFAULT,
  stallProbeSources: CLASS_DEFAULT,
  sessionStalledAfterProbeIndex: CLASS_DEFAULT,
  modernDeploymentWatcherEnabled: CLASS_DEFAULT,
  modernDeploymentLogLevelMax: CLASS_DEFAULT,
  modernDeploymentBackfillEnabled: CLASS_DEFAULT,
  modernDeploymentBackfillLookbackMinutes: CLASS_DEFAULT,
  windowsUpdateWatcherEnabled: CLASS_DEFAULT,
  windowsUpdateTargetedEventIds: CLASS_DEFAULT,
  windowsUpdateBackfillLookbackMinutes: CLASS_DEFAULT,
  windowsUpdateChannelCensusEnabled: CLASS_DEFAULT,
  mdmRebootPolicyWatcherEnabled: CLASS_DEFAULT,
  mdmRebootPolicyBackfillLookbackMinutes: CLASS_DEFAULT,
  enableSystemTimelineWatcher: CLASS_DEFAULT,
  systemEventBackfillLookbackMinutes: CLASS_DEFAULT,
};

export const ANALYZER_FIELDS: Record<keyof AnalyzerConfiguration, RuntimeRow | Excluded> = {
  enableLocalAdminAnalyzer: { section: "Analyzers", label: "Local Admin Analyzer", source: "tenant", value: ({ config }) => config.enableLocalAdminAnalyzer ?? true, default: true },
  localAdminAllowedAccounts: { section: "Analyzers", label: "Allowed Local Admin Accounts", source: "tenant", value: ({ config }) => parseLocalAdminAllowedAccounts(config.localAdminAllowedAccountsJson), default: [] },
  enableSoftwareInventoryAnalyzer: { section: "Analyzers", label: "Software Inventory Analyzer", source: "tenant", value: ({ config }) => config.enableSoftwareInventoryAnalyzer ?? false, default: false },
  enableIntegrityBypassAnalyzer: { section: "Analyzers", label: "Integrity Bypass Analyzer", source: "tenant", value: ({ config }) => config.enableIntegrityBypassAnalyzer ?? true, default: true },
  enableRealmJoinWatcher: { section: "Analyzers", label: "Realm Join Watcher", source: "tenant", value: ({ config }) => config.enableRealmJoinWatcher ?? false, default: false },
  keepAwakeDuringUserEsp: { section: "Analyzers", label: "Keep Awake During User ESP", source: "tenant", value: ({ config }) => config.keepAwakeDuringUserEsp ?? false, default: false },
  enableConsoleBypassDetection: { section: "Analyzers", label: "Console Bypass Detection", source: "tenant", value: ({ config }) => config.enableConsoleBypassDetection ?? true, default: true },
};

// ── Ordered views for rendering ──────────────────────────────────────────────

export interface CataloguedRow<TRow> {
  key: string;
  row: TRow;
}

function groupBySection<TSection extends string, TRow extends { section: TSection }>(
  sections: readonly TSection[],
  records: Array<Record<string, TRow | Excluded>>,
): Array<{ section: TSection; rows: CataloguedRow<TRow>[] }> {
  const bySection = new Map<TSection, CataloguedRow<TRow>[]>(sections.map((s) => [s, []]));
  for (const record of records) {
    for (const [key, spec] of Object.entries(record)) {
      if (isExcluded(spec)) continue;
      bySection.get(spec.section)!.push({ key, row: spec });
    }
  }
  return sections.map((section) => ({ section, rows: bySection.get(section)! }));
}

export const TENANT_REPORT_SECTIONS = groupBySection<TenantSection, TenantRow>(TENANT_SECTIONS, [TENANT_FIELDS]);

export const RUNTIME_REPORT_SECTIONS = groupBySection<RuntimeSection, RuntimeRow>(RUNTIME_SECTIONS, [
  RUNTIME_FIELDS,
  COLLECTOR_FIELDS,
  ANALYZER_FIELDS,
]);

/**
 * Value equality for the "custom" highlight: null, undefined and the empty string are one
 * "unset" value (the table row stores a cleared string as "", the C# initializer is null),
 * arrays compare by content.
 */
export function isNonDefault(value: unknown, def: unknown): boolean {
  const isNil = (v: unknown) => v === null || v === undefined || v === "";
  const valueNil = isNil(value);
  const defNil = isNil(def);
  if (valueNil || defNil) return valueNil !== defNil;
  if (Array.isArray(value) && Array.isArray(def)) {
    return value.length !== def.length || value.some((v, i) => v !== def[i]);
  }
  return value !== def;
}

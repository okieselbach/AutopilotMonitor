// GENERATED — do not edit by hand. Second copy for the MCP server.
// Source: src/Web/autopilot-monitor-web/utils/shared-manifests.json.
// Regenerate: npm run generate:manifests in src/Web/autopilot-monitor-web.
//
// Vocabularies reflected from AutopilotMonitor.Shared. These are VALUES (not just types)
// so tool input schemas can derive their z.enum() from the backend truth instead of
// retyping it — a hand-typed list drifts silently and the tool then advertises a
// vocabulary the backend no longer has (or omits one it gained).


/** Enrollment session statuses (C# SessionStatus). */
export const SESSION_STATUSES = [
  "InProgress",
  "Pending",
  "Stalled",
  "Succeeded",
  "Failed",
  "Unknown",
  "AwaitingUser",
  "Incomplete",
] as const;
export type SessionStatusName = (typeof SESSION_STATUSES)[number];

/** Telemetry event severities (C# EventSeverity) — includes Debug and Trace. */
export const EVENT_SEVERITIES = [
  "Debug",
  "Info",
  "Warning",
  "Error",
  "Critical",
  "Trace",
] as const;
export type EventSeverityName = (typeof EVENT_SEVERITIES)[number];

/** Ops event categories = OpsEvents partition keys (C# OpsEventCategory). */
export const OPS_EVENT_CATEGORIES = [
  "Consent",
  "Maintenance",
  "Security",
  "Tenant",
  "Agent",
  "SLA",
  "Platform",
] as const;
export type OpsEventCategoryName = (typeof OPS_EVENT_CATEGORIES)[number];

/** Ops event severities, ascending (C# OpsEventSeverity) — the order IS the ladder. */
export const OPS_EVENT_SEVERITIES = [
  "Info",
  "Warning",
  "Error",
  "Critical",
] as const;
export type OpsEventSeverityName = (typeof OPS_EVENT_SEVERITIES)[number];

/** Every ops event type the backend can write (C# OpsEventTypes). */
export const OPS_EVENT_TYPES = [
  "ConsentFlowStarted",
  "ConsentFlowSuccess",
  "ConsentFlowFailed",
  "ConsentRedirectUriMismatch",
  "AppHomingFlipped",
  "AppHomingFlippedWithEntraRoles",
  "MaintenanceCompleted",
  "MaintenanceFailed",
  "MaintenanceLongRunning",
  "SessionSweepCompleted",
  "SessionSweepFailed",
  "OpsEventCleanup",
  "OrphanEventsCleaned",
  "SessionDeletionMaintenanceStarted",
  "SessionDeletionMaintenanceBudgetExceeded",
  "SessionDeletionMaintenanceSkippedLocked",
  "SessionDeletionMaintenanceLongRunning",
  "SessionDeletionMaintenanceLongRunningSevere",
  "SessionDeletionMaintenanceFailed",
  "SessionDeletionStrandedQueued",
  "SessionDeletionPoisoned",
  "SessionDeletionMaintenanceCompleted",
  "SessionDeletionMaintenanceFanoutSkipped",
  "CriticalTableBackupCompleted",
  "CriticalTableBackupPartial",
  "CriticalTableBackupFailed",
  "CriticalTableBackupSkippedLocked",
  "BackupRowRestored",
  "VerdictCalibrationDrift",
  "DeviceBlocked",
  "VersionBlocked",
  "SessionTenantConflict",
  "SessionOwnerMismatch",
  "KillSignalDelivered",
  "EmbeddedCertExpiringSoon",
  "EmbeddedCertExpiringUrgent",
  "EmbeddedCertExpired",
  "EmbeddedCertBundleEmpty",
  "SignalRConnectionsHigh",
  "SignalRConnectionsCritical",
  "SignalRMessagesHigh",
  "SignalRMessagesCritical",
  "PoisonQueueBacklogHigh",
  "PoisonQueueBacklogCritical",
  "ExcessiveSessionEventsAutoActioned",
  "OffboardingFeedbackReceived",
  "TenantOffboarded",
  "TenantOffboardingFailed",
  "TenantAutoApproved",
  "WelcomeEmailSent",
  "WelcomeEmailSkipped",
  "WelcomeEmailFailed",
  "TenantTrialStarted",
  "TenantTrialExpiring",
  "TenantTrialExpired",
  "TenantPlanDowngraded",
  "TenantRetentionGraceExpiring",
  "TenantRetentionGraceEnded",
  "RuleFrequencyRegression",
  "AppVersionDurationRegression",
  "CollectLogsQuickConfigEnabled",
  "DiagnosticsUploadEnabled",
  "DiagnosticsUploadDisabled",
  "SessionActionQueued",
  "SessionTimeouts",
  "AgentEmergencyBreak",
  "AgentBinaryIntegrityMismatch",
  "CmTraceTimeSkewRegression",
  "ExcessiveSessionEvents",
  "NewImeVersionDetected",
  "ImePatternDriftSuspected",
  "BlobStorageMissing",
  "BlobStorageUnreachable",
  "SlaBreachNotification",
  "SlaConsecutiveFailures",
  "SlaEvaluationCompleted",
  "AzureMonitorAlert",
] as const;
export type OpsEventTypeName = (typeof OPS_EVENT_TYPES)[number];

/** Session-annotation lanes (C# AnnotationLanes). */
export const ANNOTATION_LANES = [
  "operator",
  "tenantadmin",
  "globaladmin",
] as const;
export type AnnotationLane = (typeof ANNOTATION_LANES)[number];

/** Session-annotation verdicts (C# AnnotationVerdicts). */
export const ANNOTATION_VERDICTS = [
  "root_cause_confirmed",
  "analysis_wrong",
  "different_problem",
  "inconclusive",
] as const;
export type AnnotationVerdict = (typeof ANNOTATION_VERDICTS)[number];

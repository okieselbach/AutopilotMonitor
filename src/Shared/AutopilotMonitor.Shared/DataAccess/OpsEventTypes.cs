using System.Collections.Generic;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Every ops event type the backend can write — the canonical vocabulary.
    /// <para>
    /// These used to be bare string literals at the call sites in <c>OpsEventService</c>, which
    /// meant nothing could enumerate them: the portal alert-rule catalog and the MCP had to
    /// retype the list by hand, and a type that nobody re-typed was written but unroutable and
    /// undiscoverable. Declaring them here makes the vocabulary reflectable, so the alert-rule
    /// catalog check and the shared manifest (which feeds the MCP) derive from ONE source.
    /// </para>
    /// <para>
    /// Adding a type: declare it here, use the constant at the call site (a raw literal fails
    /// <c>OpsEventTypeDualRegisterTests</c>), and list it in <c>OPS_EVENT_TYPES</c> in
    /// <c>OpsAlertRulesSection.tsx</c> so an operator can route an alert on it. Constant name
    /// == wire value.
    /// </para>
    /// </summary>
    public static class OpsEventTypes
    {
        // ── Consent ── Consent flow + dual app-registration homing.
        public const string ConsentFlowStarted             = "ConsentFlowStarted";
        public const string ConsentFlowSuccess             = "ConsentFlowSuccess";
        public const string ConsentFlowFailed              = "ConsentFlowFailed";
        public const string ConsentRedirectUriMismatch     = "ConsentRedirectUriMismatch";
        public const string AppHomingFlipped               = "AppHomingFlipped";
        public const string AppHomingFlippedWithEntraRoles = "AppHomingFlippedWithEntraRoles";

        // ── Maintenance ── Scheduled maintenance, cascade deletion, critical-table backup.
        public const string MaintenanceCompleted                        = "MaintenanceCompleted";
        public const string MaintenanceFailed                           = "MaintenanceFailed";
        public const string MaintenanceLongRunning                      = "MaintenanceLongRunning";
        public const string SessionSweepCompleted                       = "SessionSweepCompleted";
        public const string SessionSweepFailed                          = "SessionSweepFailed";
        public const string OpsEventCleanup                             = "OpsEventCleanup";
        public const string OrphanEventsCleaned                         = "OrphanEventsCleaned";
        public const string SessionDeletionMaintenanceStarted           = "SessionDeletionMaintenanceStarted";
        public const string SessionDeletionMaintenanceBudgetExceeded    = "SessionDeletionMaintenanceBudgetExceeded";
        public const string SessionDeletionMaintenanceSkippedLocked     = "SessionDeletionMaintenanceSkippedLocked";
        public const string SessionDeletionMaintenanceLongRunning       = "SessionDeletionMaintenanceLongRunning";
        public const string SessionDeletionMaintenanceLongRunningSevere = "SessionDeletionMaintenanceLongRunningSevere";
        public const string SessionDeletionMaintenanceFailed            = "SessionDeletionMaintenanceFailed";
        public const string SessionDeletionStrandedQueued               = "SessionDeletionStrandedQueued";
        public const string SessionDeletionPoisoned                     = "SessionDeletionPoisoned";
        public const string SessionDeletionMaintenanceCompleted         = "SessionDeletionMaintenanceCompleted";
        public const string SessionDeletionMaintenanceFanoutSkipped     = "SessionDeletionMaintenanceFanoutSkipped";
        public const string CriticalTableBackupCompleted                = "CriticalTableBackupCompleted";
        public const string CriticalTableBackupPartial                  = "CriticalTableBackupPartial";
        public const string CriticalTableBackupFailed                   = "CriticalTableBackupFailed";
        public const string CriticalTableBackupSkippedLocked            = "CriticalTableBackupSkippedLocked";
        public const string BackupRowRestored                           = "BackupRowRestored";
        public const string VerdictCalibrationDrift                     = "VerdictCalibrationDrift";

        // ── Security ── Blocks, ownership conflicts, certificate expiry, capacity + poison-queue alarms.
        public const string DeviceBlocked                      = "DeviceBlocked";
        public const string VersionBlocked                     = "VersionBlocked";
        public const string SessionTenantConflict              = "SessionTenantConflict";
        public const string SessionOwnerMismatch               = "SessionOwnerMismatch";
        public const string KillSignalDelivered                = "KillSignalDelivered";
        public const string EmbeddedCertExpiringSoon           = "EmbeddedCertExpiringSoon";
        public const string EmbeddedCertExpiringUrgent         = "EmbeddedCertExpiringUrgent";
        public const string EmbeddedCertExpired                = "EmbeddedCertExpired";
        public const string EmbeddedCertBundleEmpty            = "EmbeddedCertBundleEmpty";
        public const string SignalRConnectionsHigh             = "SignalRConnectionsHigh";
        public const string SignalRConnectionsCritical         = "SignalRConnectionsCritical";
        public const string SignalRMessagesHigh                = "SignalRMessagesHigh";
        public const string SignalRMessagesCritical            = "SignalRMessagesCritical";
        public const string PoisonQueueBacklogHigh             = "PoisonQueueBacklogHigh";
        public const string PoisonQueueBacklogCritical         = "PoisonQueueBacklogCritical";
        public const string ExcessiveSessionEventsAutoActioned = "ExcessiveSessionEventsAutoActioned";
        /// <summary>
        /// An authenticated caller without the GlobalAdmin role was refused (403) on a
        /// <c>GlobalAdminOnly</c> route — including the MCP probe a non-GA tool call triggers.
        /// Critical for callers without any platform role, Warning for a Global Reader.
        /// </summary>
        public const string PrivilegedRouteDenied               = "PrivilegedRouteDenied";

        // ── Tenant ── Tenant lifecycle: offboarding, trials, plan changes, regression radars, config toggles.
        public const string OffboardingFeedbackReceived   = "OffboardingFeedbackReceived";
        public const string TenantOffboarded              = "TenantOffboarded";
        public const string TenantOffboardingFailed       = "TenantOffboardingFailed";
        public const string TenantAutoApproved            = "TenantAutoApproved";
        public const string WelcomeEmailSent              = "WelcomeEmailSent";
        public const string WelcomeEmailSkipped           = "WelcomeEmailSkipped";
        public const string WelcomeEmailFailed            = "WelcomeEmailFailed";
        public const string TenantTrialStarted            = "TenantTrialStarted";
        public const string TenantTrialExpiring           = "TenantTrialExpiring";
        public const string TenantTrialExpired            = "TenantTrialExpired";
        public const string TenantPlanDowngraded          = "TenantPlanDowngraded";
        public const string TenantRetentionGraceExpiring  = "TenantRetentionGraceExpiring";
        public const string TenantRetentionGraceEnded     = "TenantRetentionGraceEnded";
        public const string RuleFrequencyRegression       = "RuleFrequencyRegression";
        public const string AppVersionDurationRegression  = "AppVersionDurationRegression";
        public const string CollectLogsQuickConfigEnabled = "CollectLogsQuickConfigEnabled";
        public const string DiagnosticsUploadEnabled      = "DiagnosticsUploadEnabled";
        public const string DiagnosticsUploadDisabled     = "DiagnosticsUploadDisabled";

        // ── Agent ── Agent + device-side health signals.
        public const string SessionActionQueued          = "SessionActionQueued";
        public const string SessionTimeouts              = "SessionTimeouts";
        public const string AgentEmergencyBreak          = "AgentEmergencyBreak";
        public const string AgentBinaryIntegrityMismatch = "AgentBinaryIntegrityMismatch";
        public const string CmTraceTimeSkewRegression    = "CmTraceTimeSkewRegression";
        public const string ExcessiveSessionEvents       = "ExcessiveSessionEvents";
        public const string NewImeVersionDetected        = "NewImeVersionDetected";
        public const string ImePatternDriftSuspected     = "ImePatternDriftSuspected";
        public const string BlobStorageMissing           = "BlobStorageMissing";
        public const string BlobStorageUnreachable       = "BlobStorageUnreachable";

        // ── Sla ── SLA evaluation outcomes.
        public const string SlaBreachNotification  = "SlaBreachNotification";
        public const string SlaConsecutiveFailures = "SlaConsecutiveFailures";
        public const string SlaEvaluationCompleted = "SlaEvaluationCompleted";

        // ── Platform ── Platform infrastructure alerts relayed from Azure Monitor.
        public const string AzureMonitorAlert = "AzureMonitorAlert";

        /// <summary>Every declared type, declaration order (grouped by category).</summary>
        public static readonly IReadOnlyList<string> All = new[]
        {
            ConsentFlowStarted, ConsentFlowSuccess, ConsentFlowFailed, ConsentRedirectUriMismatch, AppHomingFlipped, AppHomingFlippedWithEntraRoles,
            MaintenanceCompleted, MaintenanceFailed, MaintenanceLongRunning, SessionSweepCompleted, SessionSweepFailed, OpsEventCleanup, OrphanEventsCleaned, SessionDeletionMaintenanceStarted, SessionDeletionMaintenanceBudgetExceeded, SessionDeletionMaintenanceSkippedLocked, SessionDeletionMaintenanceLongRunning, SessionDeletionMaintenanceLongRunningSevere, SessionDeletionMaintenanceFailed, SessionDeletionStrandedQueued, SessionDeletionPoisoned, SessionDeletionMaintenanceCompleted, SessionDeletionMaintenanceFanoutSkipped, CriticalTableBackupCompleted, CriticalTableBackupPartial, CriticalTableBackupFailed, CriticalTableBackupSkippedLocked, BackupRowRestored, VerdictCalibrationDrift,
            DeviceBlocked, VersionBlocked, SessionTenantConflict, SessionOwnerMismatch, KillSignalDelivered, EmbeddedCertExpiringSoon, EmbeddedCertExpiringUrgent, EmbeddedCertExpired, EmbeddedCertBundleEmpty, SignalRConnectionsHigh, SignalRConnectionsCritical, SignalRMessagesHigh, SignalRMessagesCritical, PoisonQueueBacklogHigh, PoisonQueueBacklogCritical, ExcessiveSessionEventsAutoActioned, PrivilegedRouteDenied,
            OffboardingFeedbackReceived, TenantOffboarded, TenantOffboardingFailed, TenantAutoApproved, WelcomeEmailSent, WelcomeEmailSkipped, WelcomeEmailFailed, TenantTrialStarted, TenantTrialExpiring, TenantTrialExpired, TenantPlanDowngraded, TenantRetentionGraceExpiring, TenantRetentionGraceEnded, RuleFrequencyRegression, AppVersionDurationRegression, CollectLogsQuickConfigEnabled, DiagnosticsUploadEnabled, DiagnosticsUploadDisabled,
            SessionActionQueued, SessionTimeouts, AgentEmergencyBreak, AgentBinaryIntegrityMismatch, CmTraceTimeSkewRegression, ExcessiveSessionEvents, NewImeVersionDetected, ImePatternDriftSuspected, BlobStorageMissing, BlobStorageUnreachable,
            SlaBreachNotification, SlaConsecutiveFailures, SlaEvaluationCompleted,
            AzureMonitorAlert,
        };
    }
}

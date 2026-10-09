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
        public const string MaintenanceStarted                          = "MaintenanceStarted";
        public const string MaintenanceSkippedLocked                    = "MaintenanceSkippedLocked";
        public const string SessionSweepCompleted                       = "SessionSweepCompleted";
        public const string SessionSweepFailed                          = "SessionSweepFailed";
        public const string OpsEventCleanup                             = "OpsEventCleanup";
        public const string OrphanEventsCleaned                         = "OrphanEventsCleaned";
        /// <summary>Every orphan-session sweep run (Info heartbeat): scanned handles, candidates, cleaned, remaining, per-table totals.</summary>
        public const string OrphanSweepCompleted                        = "OrphanSweepCompleted";
        /// <summary>The sweep run itself failed before or while enumerating (Error); per-candidate failures stay inside Completed.</summary>
        public const string OrphanSweepFailed                           = "OrphanSweepFailed";
        /// <summary>Another sweep run (timer, manual maintenance or a second host instance) held the sweep lease (Warning).</summary>
        public const string OrphanSweepSkippedLocked                    = "OrphanSweepSkippedLocked";
        /// <summary>Weekly read-only reconciliation of EventTypeIndex against the Sessions keys: Info at zero residue, Warning with a sample otherwise.</summary>
        public const string OrphanReconcileCompleted                    = "OrphanReconcileCompleted";
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
        /// <summary>
        /// A vulnerability-correlation run could not write every SoftwareInventory counter, or the
        /// per-session contributions side-row. Warning; one event per run with failure counts and
        /// up to five sample keys, never one per key and never in the tenant audit trail.
        /// </summary>
        public const string SoftwareInventoryCounterWriteFailed         = "SoftwareInventoryCounterWriteFailed";

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
        /// <summary>
        /// An application principal (service principal presenting an app-only token, key
        /// <c>app:&lt;client-id&gt;</c>) opened its first MCP session — recorded once per application and
        /// home tenant so new automation never starts unnoticed. Info; the radar for what it does
        /// afterwards is the per-user usage view.
        /// </summary>
        public const string McpServicePrincipalFirstSeen         = "McpServicePrincipalFirstSeen";

        // ── Tenant ── Tenant lifecycle: offboarding, trials, plan changes, regression radars, config toggles.
        public const string OffboardingFeedbackReceived   = "OffboardingFeedbackReceived";
        /// <summary>
        /// A member of a tenant sent feedback from the portal's help menu (<c>POST feedback/general</c>).
        /// Info. Message and details never carry the feedback text or the sender — the text is read in
        /// the portal (User Feedback), so a channel bound to this rule receives no personal data.
        /// </summary>
        public const string FeedbackReceived              = "FeedbackReceived";
        /// <summary>
        /// A new tenant signed up: its first sign-in seeded the domain (<c>auth/me</c>). Info. Recorded
        /// next to the direct Telegram ping until that is retired (plan push-relay, Phase 0); message and
        /// details carry no personal data — the Tenants page has the rest.
        /// </summary>
        public const string TenantSignup                  = "TenantSignup";
        /// <summary>A member submitted a session report. Info; details name the session and report id only.</summary>
        public const string SessionReportSubmitted        = "SessionReportSubmitted";
        /// <summary>A member submitted logs without a session (Submit Logs). Info; details name the report id only.</summary>
        public const string DiagFilesReportSubmitted      = "DiagFilesReportSubmitted";
        /// <summary>A member submitted a gather or analyze rule for review. Info; details name submission, kind and source rule id only.</summary>
        public const string RuleSubmissionReceived        = "RuleSubmissionReceived";
        public const string TenantOffboarded              = "TenantOffboarded";
        public const string TenantOffboardingFailed       = "TenantOffboardingFailed";
        public const string TenantAutoApproved            = "TenantAutoApproved";
        public const string WelcomeEmailSent              = "WelcomeEmailSent";
        public const string WelcomeEmailSkipped           = "WelcomeEmailSkipped";
        public const string WelcomeEmailFailed            = "WelcomeEmailFailed";
        /// <summary>
        /// Post-offboarding farewell mail, recorded by the offboarding worker after the History
        /// row went Completed. Sent (Info) = the provider accepted it; Skipped (Warning) = no
        /// contact address was captured before the wipe, the customer left in silence; Failed
        /// (Error) = an address was there and the mail did not go out. The only record of the
        /// send: its success log is Information and never reaches Application Insights.
        /// </summary>
        public const string FarewellEmailSent             = "FarewellEmailSent";
        public const string FarewellEmailSkipped          = "FarewellEmailSkipped";
        public const string FarewellEmailFailed           = "FarewellEmailFailed";
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
        /// <summary>
        /// The telemetry ingest answered a batch with 422 poison: items with an unknown Kind or an
        /// unparseable payload were named back to the agent instead of being dropped silently.
        /// One event per rejected batch — a burst across sessions of one agent version is a
        /// wire-contract drift between that agent line and the deployed backend.
        /// </summary>
        public const string TelemetryItemsRejected       = "TelemetryItemsRejected";

        // ── Sla ── SLA evaluation outcomes.
        public const string SlaBreachNotification  = "SlaBreachNotification";
        public const string SlaConsecutiveFailures = "SlaConsecutiveFailures";
        public const string SlaEvaluationCompleted = "SlaEvaluationCompleted";

        // ── Platform ── Platform infrastructure alerts relayed from Azure Monitor, push channel health.
        public const string AzureMonitorAlert = "AzureMonitorAlert";
        /// <summary>
        /// The push sender got a configuration-class answer from a push service (400/401/403:
        /// bad VAPID key, bad TTL) or a device's VAPID kid no longer matches the configured keys.
        /// Error; at most one per hour per cause and instance (in-process cache). Details name
        /// the service host, the status and the kid — never an endpoint.
        /// </summary>
        public const string PushDeliveryFailed = "PushDeliveryFailed";
        /// <summary>
        /// A pairing or re-subscribe named an endpoint outside the push-service allow-list. Warning;
        /// details carry the hostname only, so a new browser push service shows up without the
        /// endpoint (a credential) leaving the backend.
        /// </summary>
        public const string PushEndpointRefused = "PushEndpointRefused";

        /// <summary>Every declared type, declaration order (grouped by category).</summary>
        public static readonly IReadOnlyList<string> All = new[]
        {
            ConsentFlowStarted, ConsentFlowSuccess, ConsentFlowFailed, ConsentRedirectUriMismatch, AppHomingFlipped, AppHomingFlippedWithEntraRoles,
            MaintenanceCompleted, MaintenanceFailed, MaintenanceLongRunning, MaintenanceStarted, MaintenanceSkippedLocked, SessionSweepCompleted, SessionSweepFailed, OpsEventCleanup, OrphanEventsCleaned, OrphanSweepCompleted, OrphanSweepFailed, OrphanSweepSkippedLocked, OrphanReconcileCompleted, SessionDeletionMaintenanceStarted, SessionDeletionMaintenanceBudgetExceeded, SessionDeletionMaintenanceSkippedLocked, SessionDeletionMaintenanceLongRunning, SessionDeletionMaintenanceLongRunningSevere, SessionDeletionMaintenanceFailed, SessionDeletionStrandedQueued, SessionDeletionPoisoned, SessionDeletionMaintenanceCompleted, SessionDeletionMaintenanceFanoutSkipped, CriticalTableBackupCompleted, CriticalTableBackupPartial, CriticalTableBackupFailed, CriticalTableBackupSkippedLocked, BackupRowRestored, VerdictCalibrationDrift, SoftwareInventoryCounterWriteFailed,
            DeviceBlocked, VersionBlocked, SessionTenantConflict, SessionOwnerMismatch, KillSignalDelivered, EmbeddedCertExpiringSoon, EmbeddedCertExpiringUrgent, EmbeddedCertExpired, EmbeddedCertBundleEmpty, SignalRConnectionsHigh, SignalRConnectionsCritical, SignalRMessagesHigh, SignalRMessagesCritical, PoisonQueueBacklogHigh, PoisonQueueBacklogCritical, ExcessiveSessionEventsAutoActioned, PrivilegedRouteDenied, McpServicePrincipalFirstSeen,
            OffboardingFeedbackReceived, FeedbackReceived, TenantSignup, SessionReportSubmitted, DiagFilesReportSubmitted, RuleSubmissionReceived, TenantOffboarded, TenantOffboardingFailed, TenantAutoApproved, WelcomeEmailSent, WelcomeEmailSkipped, WelcomeEmailFailed, FarewellEmailSent, FarewellEmailSkipped, FarewellEmailFailed, TenantTrialStarted, TenantTrialExpiring, TenantTrialExpired, TenantPlanDowngraded, TenantRetentionGraceExpiring, TenantRetentionGraceEnded, RuleFrequencyRegression, AppVersionDurationRegression, CollectLogsQuickConfigEnabled, DiagnosticsUploadEnabled, DiagnosticsUploadDisabled,
            SessionActionQueued, SessionTimeouts, AgentEmergencyBreak, AgentBinaryIntegrityMismatch, CmTraceTimeSkewRegression, ExcessiveSessionEvents, NewImeVersionDetected, ImePatternDriftSuspected, BlobStorageMissing, BlobStorageUnreachable, TelemetryItemsRejected,
            SlaBreachNotification, SlaConsecutiveFailures, SlaEvaluationCompleted,
            AzureMonitorAlert, PushDeliveryFailed, PushEndpointRefused,
        };
    }
}

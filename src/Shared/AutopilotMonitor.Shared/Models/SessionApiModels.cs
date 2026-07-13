using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Request to register a new session
    /// </summary>
    public class RegisterSessionRequest
    {
        public SessionRegistration Registration { get; set; } = default!;
    }

    /// <summary>
    /// Identifies which validator authorized the device during session registration.
    /// Surfaced in the RegisterSession response so the agent can reconcile against its
    /// own registry-based detection and, when needed, switch its enrollment flow.
    /// </summary>
    public enum ValidatorType
    {
        Unknown = 0,
        AutopilotV1 = 1,           // AutopilotDeviceValidator (windowsAutopilotDeviceIdentities)
        CorporateIdentifier = 2,   // CorporateIdentifierValidator (importedDeviceIdentities)
        DeviceAssociation = 3,     // DevPrep DeviceAssociationValidator (tenantAssociatedDevices) — future
        Bootstrap = 4              // Bootstrap token auth (pre-MDM OOBE)
    }

    /// <summary>
    /// Response from session registration
    /// </summary>
    public class RegisterSessionResponse
    {
        public string SessionId { get; set; } = default!;
        public bool Success { get; set; }
        public string Message { get; set; } = default!;
        public DateTime RegisteredAt { get; set; }

        /// <summary>
        /// Non-null when the session was already marked as terminal by an admin before agent restart.
        /// Values: "Succeeded", "Failed". Agent should run cleanup instead of starting monitoring.
        /// </summary>
        public string? AdminAction { get; set; }

        /// <summary>
        /// Authoritative signal: which validator accepted this device.
        /// Lets the agent reconcile its registry-based enrollment-type detection
        /// against the backend's verdict (e.g. AutopilotV1 → Classic flow, DeviceAssociation → DevPrep flow).
        /// Older backends that do not set this return Unknown — agent falls back to its own detection.
        /// </summary>
        public ValidatorType ValidatedBy { get; set; } = ValidatorType.Unknown;
    }

    /// <summary>
    /// Request to ingest events (batched)
    /// </summary>
    public class IngestEventsRequest
    {
        public string SessionId { get; set; } = default!;
        public string TenantId { get; set; } = default!;
        public List<EnrollmentEvent> Events { get; set; }
        public bool IsCompressed { get; set; }

        public IngestEventsRequest()
        {
            Events = new List<EnrollmentEvent>();
        }
    }

    /// <summary>
    /// Response from event ingestion
    /// </summary>
    public class IngestEventsResponse
    {
        public bool Success { get; set; }
        public int EventsReceived { get; set; }
        public int EventsProcessed { get; set; }
        public string Message { get; set; } = default!;
        public DateTime ProcessedAt { get; set; }

        /// <summary>
        /// Whether the request was rejected due to rate limiting
        /// </summary>
        public bool RateLimitExceeded { get; set; }

        /// <summary>
        /// Rate limit details (only populated if RateLimitExceeded is true)
        /// </summary>
        public RateLimitInfo? RateLimitInfo { get; set; }

        /// <summary>
        /// Whether the device has been temporarily blocked by an admin
        /// </summary>
        public bool DeviceBlocked { get; set; }

        /// <summary>
        /// When the block expires (only populated if DeviceBlocked is true)
        /// </summary>
        public DateTime? UnblockAt { get; set; }

        /// <summary>
        /// Whether the device has been issued a remote kill signal (graceful self-destruct).
        /// The agent should execute its self-destruct routine and exit.
        /// </summary>
        public bool DeviceKillSignal { get; set; }

        /// <summary>
        /// Non-null when an admin has externally changed the session status.
        /// Values: "Succeeded", "Failed". Agent should treat as terminal signal and run cleanup.
        /// </summary>
        public string? AdminAction { get; set; }

        /// <summary>
        /// Generic server→agent action channel. Null/empty when no actions are pending.
        /// Delivered at-least-once; agents must handle actions idempotently.
        /// Unknown action types should be logged and skipped (forward-compatibility).
        /// </summary>
        public List<ServerAction>? Actions { get; set; }
    }

    /// <summary>
    /// Rate limit information for UI display
    /// </summary>
    public class RateLimitInfo
    {
        /// <summary>
        /// Number of requests in current window
        /// </summary>
        public int RequestsInWindow { get; set; }

        /// <summary>
        /// Maximum allowed requests
        /// </summary>
        public int MaxRequests { get; set; }

        /// <summary>
        /// Window duration in seconds
        /// </summary>
        public int WindowDurationSeconds { get; set; }

        /// <summary>
        /// Seconds to wait before retrying
        /// </summary>
        public int RetryAfterSeconds { get; set; }
    }

    /// <summary>
    /// Request to get a short-lived SAS URL for diagnostics package upload.
    /// Called by the agent just before upload — the URL is never cached in config.
    /// </summary>
    public class GetDiagnosticsUploadUrlRequest
    {
        public string TenantId { get; set; } = default!;
        public string SessionId { get; set; } = default!;
        public string FileName { get; set; } = default!;
    }

    /// <summary>
    /// Response containing a short-lived SAS URL for diagnostics package upload.
    /// </summary>
    public class GetDiagnosticsUploadUrlResponse
    {
        public bool Success { get; set; }
        public string? UploadUrl { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string? Message { get; set; }

        /// <summary>
        /// Canonical blob path the agent MUST persist as
        /// <c>SessionSummary.DiagnosticsBlobName</c> after a successful upload. Encodes
        /// the destination-specific path layout so the download path can find the blob
        /// later without having to reconstruct it:
        /// <list type="bullet">
        ///   <item><c>CustomerSas</c>: equal to the request filename (e.g.
        ///         <c>AgentDiagnostics-...zip</c>).</item>
        ///   <item><c>Hosted</c>: tenant-prefixed (e.g.
        ///         <c>{tenantId}/AgentDiagnostics-...zip</c>).</item>
        /// </list>
        /// Null for older backends that predate this field — the agent then falls back
        /// to the request filename for back-compat.
        /// </summary>
        public string? BlobName { get; set; }

        /// <summary>
        /// Which destination minted the SAS in <see cref="UploadUrl"/>:
        /// <c>"CustomerSas"</c> or <c>"Hosted"</c>. Surfaced so the agent's ingest path
        /// can attach it to the next telemetry batch (the download path needs the
        /// per-session destination, NOT the current tenant setting — those can diverge
        /// if the admin switches modes after some sessions have already uploaded).
        /// </summary>
        public string? Destination { get; set; }
    }

    /// <summary>
    /// Session summary for UI display
    /// </summary>
    public class SessionSummary
    {
        public string SessionId { get; set; } = default!;
        public string TenantId { get; set; } = default!;
        public string SerialNumber { get; set; } = default!;
        public string DeviceName { get; set; } = default!;
        public string Manufacturer { get; set; } = default!;
        public string Model { get; set; } = default!;
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        // Serialize as integer (0-7) not string for frontend compatibility
        public int CurrentPhase { get; set; }
        public string CurrentPhaseDetail { get; set; } = default!;
        public SessionStatus Status { get; set; }
        public string FailureReason { get; set; } = default!;

        /// <summary>
        /// Origin of a Failed status. Values:
        ///   - "" / null: agent-reported (default; terminal enrollment_failed event)
        ///   - "rule:&lt;RuleId&gt;": session failed because an analyze rule with MarkSessionAsFailed fired
        ///   - "manual": operator flipped the session via the portal
        /// Consumers use this to render rule-based failures distinctly (badge + link to rule).
        /// </summary>
        public string FailureSource { get; set; } = string.Empty;

        /// <summary>
        /// Non-empty only when the BACKEND (not the agent) declared this session Succeeded:
        /// either the maintenance timeout sweep reconciled it (e.g. "user completed setup —
        /// desktop + Windows Hello observed") or a late completion report upgraded a prior
        /// Failed/Incomplete/AwaitingUser verdict. Carries the human-readable justification so
        /// operators can always tell a backend-declared success from an agent-reported one.
        /// Admin-marked successes are attributed via <see cref="AdminMarkedAction"/> instead
        /// and leave this empty.
        /// </summary>
        public string ReconcileReason { get; set; } = string.Empty;

        /// <summary>
        /// Non-null only when an administrator explicitly flipped the session via the portal
        /// (MarkSessionSucceeded / MarkSessionFailed). Values: <c>null</c> (default, agent-driven),
        /// <c>"Succeeded"</c>, <c>"Failed"</c>.
        /// <para>
        /// This is the authoritative source for the backend's <c>AdminAction</c> response field
        /// sent to agents. Previously the backend inferred admin-override from "status is terminal
        /// + current event is not a completion marker", which fired falsely on every post-completion
        /// event the agent sent (agent_shutting_down, diagnostics_uploaded, enrollment_summary_shown).
        /// The dedicated field eliminates that false-positive.
        /// </para>
        /// </summary>
        public string? AdminMarkedAction { get; set; }

        public int EventCount { get; set; }
        public int? DurationSeconds { get; set; }

        /// <summary>
        /// Enrollment type: "v1" (Autopilot Classic/ESP) or "v2" (Windows Device Preparation).
        /// Defaults to "v1" for sessions that predate this field.
        /// </summary>
        public string EnrollmentType { get; set; } = "v1";

        /// <summary>
        /// Blob name of the uploaded diagnostics archive (null if not uploaded).
        /// Used to construct a download URL.
        /// <para>
        /// Path semantics depend on <see cref="DiagnosticsBlobDestination"/>:
        /// <list type="bullet">
        ///   <item><c>CustomerSas</c>: blob name only (e.g. <c>AgentDiagnostics-...zip</c>);
        ///         download builds the URL from the tenant's container SAS.</item>
        ///   <item><c>Hosted</c>: full path including the <c>{tenantId}/</c> prefix
        ///         (e.g. <c>{tenantId}/AgentDiagnostics-...zip</c>); download streams
        ///         directly via the backend connection string.</item>
        /// </list>
        /// </para>
        /// </summary>
        public string DiagnosticsBlobName { get; set; } = default!;

        /// <summary>
        /// Where the diagnostics archive for THIS session was uploaded.
        /// Frozen at upload time so the download path can route correctly even if the
        /// tenant later switches <see cref="TenantConfiguration.DiagnosticsUploadDestination"/>.
        /// <list type="bullet">
        ///   <item><c>"CustomerSas"</c> — blob lives in the customer's storage; download uses
        ///         the tenant's SAS URL (current behaviour).</item>
        ///   <item><c>"Hosted"</c> — blob lives in the backend's storage under
        ///         <c>{tenantId}/</c>; download streams via the Functions connection string.</item>
        ///   <item><c>null</c> (legacy rows that predate this field) — treated as
        ///         <c>"CustomerSas"</c> by the download path for back-compat.</item>
        /// </list>
        /// </summary>
        public string? DiagnosticsBlobDestination { get; set; }

        /// <summary>
        /// Timestamp of the most recently received event for this session.
        /// Updated on every event batch ingestion. Used by maintenance to detect
        /// sessions that are still actively sending data beyond the configured window.
        /// Null for sessions that predate this field.
        /// </summary>
        public DateTime? LastEventAt { get; set; }

        /// <summary>
        /// Whether this session used WhiteGlove (Pre-Provisioning).
        /// Set when a whiteglove_complete event is processed.
        /// </summary>
        public bool IsPreProvisioned { get; set; }

        /// <summary>
        /// Timestamp when the WhiteGlove session resumed for user enrollment (Part 2).
        /// Set when the agent sends a whiteglove_resumed event or re-registers from Pending state.
        /// Used to compute the user enrollment duration (Duration 2) for Teams notifications.
        /// </summary>
        public DateTime? ResumedAt { get; set; }

        /// <summary>
        /// Timestamp when the session was marked as Stalled.
        /// Set when the agent sends a session_stalled event (after 60 min without progress)
        /// or when the backend 2h maintenance sweep detects agent silence.
        /// Cleared (null) when the session heals back to InProgress via a new real event.
        /// </summary>
        public DateTime? StalledAt { get; set; }

        /// <summary>
        /// Whether the Autopilot profile indicates Hybrid Azure AD Join.
        /// Derived from CloudAssignedDomainJoinMethod == 1 in the Autopilot profile.
        /// </summary>
        public bool IsHybridJoin { get; set; }

        /// <summary>
        /// Whether the Autopilot profile carries the self-deploying/kiosk OOBE marker
        /// (CloudAssignedOobeConfig bits 0x20|0x40). Sent by the agent at registration;
        /// sticky-true across re-registrations.
        /// </summary>
        public bool IsSelfDeployingProfile { get; set; }

        // Device detail fields — stored in the Sessions table but omitted from earlier versions
        public string OsName { get; set; } = default!;
        public string OsBuild { get; set; } = default!;
        public string OsDisplayVersion { get; set; } = default!;
        public string OsEdition { get; set; } = default!;
        public string OsLanguage { get; set; } = default!;
        public bool IsUserDriven { get; set; }
        public string AgentVersion { get; set; } = default!;
        public string ImeAgentVersion { get; set; } = default!;

        // Geographic location fields — populated from device_location event geo data
        public string GeoCountry { get; set; } = string.Empty;
        public string GeoRegion { get; set; } = string.Empty;
        public string GeoCity { get; set; } = string.Empty;
        public string GeoLoc { get; set; } = string.Empty;

        // Script execution counts — incremented during ingest
        public int PlatformScriptCount { get; set; }
        public int RemediationScriptCount { get; set; }

        /// <summary>
        /// Number of system reboots observed during the enrollment. Counts the agent's
        /// <c>system_reboot_detected</c> events (V2 only — one per reboot, detected via the
        /// System event-log boot time on the next agent start). Maintained incrementally per
        /// ingest batch for a live value, then overwritten with an authoritative distinct count
        /// from the Events table when the session reaches a terminal status (self-corrects any
        /// at-least-once batch double-count). 0 for V1 sessions and sessions that predate this field.
        /// </summary>
        public int RebootCount { get; set; }

        /// <summary>
        /// True once maintenance has emitted an ExcessiveSessionEvents ops alert for this session.
        /// Prevents duplicate alerts on subsequent maintenance runs for the same runaway session.
        /// </summary>
        public bool ExcessiveEventsAlerted { get; set; }

        /// <summary>
        /// True once maintenance has auto-blocked or auto-killed the device for this runaway
        /// session (see <see cref="AutopilotMonitor.Shared.Models.AdminConfiguration.ExcessiveEventAutoActionMode"/>).
        /// Independent of <see cref="ExcessiveEventsAlerted"/> so warn and auto-action are
        /// each idempotent on their own — admins can change the auto-action mode mid-flight
        /// without re-firing the warn.
        /// </summary>
        public bool ExcessiveEventsAutoActioned { get; set; }

        /// <summary>
        /// JSON-serialized <see cref="System.Collections.Generic.List{T}"/> of <see cref="ServerAction"/>
        /// pending delivery to the agent. Empty string when no actions are queued.
        /// The Ingest function reads this alongside the session's status fields (no extra I/O),
        /// attaches the actions to the response, and clears the column via a merge.
        /// </summary>
        public string PendingActionsJson { get; set; } = string.Empty;

        /// <summary>
        /// When the first pending action was queued. Used for TTL and staleness detection —
        /// maintenance can purge actions older than a threshold to prevent zombie signals
        /// on long-dead sessions.
        /// </summary>
        public DateTime? PendingActionsQueuedAt { get; set; }

        /// <summary>
        /// Compact JSON snapshot of "last known session state" written by the maintenance
        /// 5h-timeout sweep when a session graduates to terminal Failed (Hybrid User-Driven
        /// completion-gap fix, 2026-05-01). Captures the canonical lifecycle anchors —
        /// last ESP phase, desktop arrival, Hello policy, AAD-join state, missing signals —
        /// so operators don't have to scroll through hundreds of events to reconstruct
        /// where a stuck session was when the watchdog fired. Empty / null on
        /// healthy-completion paths and on sessions that predate the field. Built by
        /// <see cref="AutopilotMonitor.Functions.Services.FailureSnapshotBuilder"/>.
        /// </summary>
        public string FailureSnapshotJson { get; set; } = string.Empty;

        /// <summary>
        /// Cascade-delete state-machine value (Plan §1 P7 / PR3). One of
        /// <see cref="AutopilotMonitor.Shared.Models.Deletion.SessionDeletionState"/> constants.
        /// Empty / null means no cascade in flight (legacy rows; treated as <c>None</c>).
        /// Written to the primary Sessions table only — the deletion CAS path does not sync
        /// SessionsIndex, so this is NOT part of the index mirror today and is read on the
        /// truth-served detail/guard paths. Mirroring it into SessionsIndex (so list/search can flag
        /// locked sessions) is a deferred follow-up.
        /// </summary>
        public string DeletionState { get; set; } = string.Empty;

        /// <summary>
        /// ULID of the in-flight cascade manifest when <see cref="DeletionState"/> is non-None.
        /// Used by the producer to detect concurrent re-enqueues (same ManifestId → resume,
        /// different ManifestId → 409 Conflict). Null otherwise.
        /// </summary>
        public string? PendingDeletionManifestId { get; set; }
    }

    /// <summary>
    /// Lightweight result for the global quick-search typeahead.
    /// </summary>
    public class QuickSearchResult
    {
        public string SessionId { get; set; } = string.Empty;
        public string SerialNumber { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public SessionStatus Status { get; set; }
        public DateTime StartedAt { get; set; }
        /// <summary>Which field matched the query: "sessionId", "serialNumber", or "deviceName".</summary>
        public string MatchedField { get; set; } = string.Empty;
    }

    /// <summary>
    /// Status of an enrollment session
    /// </summary>
    public enum SessionStatus
    {
        InProgress,
        Pending,      // WhiteGlove pre-provisioning complete, awaiting user enrollment
        Stalled,      // Agent reported no progress for >60 min, or backend sweep detected >2h silence. Non-terminal, can heal back to InProgress.
        Succeeded,
        Failed,
        Unknown,
        // Appended 2026-07-08 (tasks/enrollment-status-reclassification.md). New members
        // MUST stay appended so existing persisted ordinals never shift.
        AwaitingUser, // Device Setup (ESP DeviceSetup) fully succeeded but the user/Account-Setup
                      // phase has not completed and the agent went silent within the grace window.
                      // NON-TERMINAL: reconciles to Succeeded on a late completion, or graduates to
                      // Incomplete once SessionGraceHours elapses. NOT a failure.
        Incomplete    // Terminal, NON-FAILURE. The session never produced a terminal completion or
                      // failure signal and the grace window expired (or it went silent before Device
                      // Setup completed with no explicit failure). Excluded from the failure-rate
                      // denominator; surfaced to operators as "Incomplete".
    }

    /// <summary>
    /// Aggregated session counters for the dashboard stats cards.
    /// Computed server-side over a windowed scan of the SessionsIndex so the
    /// numbers don't drift with whatever the client happens to have paginated
    /// into memory.
    /// </summary>
    public class SessionStats
    {
        /// <summary>Window the windowed counters were computed over (matches request).</summary>
        public int Days { get; set; }

        /// <summary>InProgress sessions inside the window. Used for the "Active Sessions" card.</summary>
        public int ActiveCount { get; set; }

        /// <summary>Total sessions started inside the window.</summary>
        public int TotalLastNDays { get; set; }

        public int SucceededLastNDays { get; set; }
        public int FailedLastNDays { get; set; }

        /// <summary>
        /// Terminal, non-failure sessions in the window (tasks/enrollment-status-reclassification.md):
        /// the sweep saw no completion or explicit failure. Reported as the third headline bucket and
        /// deliberately excluded from <see cref="SuccessRatePct"/> (which is over Succeeded + Failed only).
        /// </summary>
        public int IncompleteLastNDays { get; set; }

        /// <summary>
        /// Succeeded / (Succeeded + Failed) * 100, rounded. Zero when no terminal
        /// sessions are in the window (the card renders "0%" rather than NaN).
        /// </summary>
        public int SuccessRatePct { get; set; }

        /// <summary>Average duration of Succeeded sessions in the window, in minutes (rounded).</summary>
        public int AvgDurationMinutes { get; set; }

        /// <summary>Sessions whose StartedAt is on or after UTC midnight of the current day.</summary>
        public int TotalToday { get; set; }
        public int FailedToday { get; set; }

        /// <summary>UTC timestamp of when the snapshot was produced (server clock).</summary>
        public DateTime ComputedAt { get; set; }
    }

    /// <summary>
    /// A tracked IME version sighting. Permanent archive that survives data retention.
    /// </summary>
    public class ImeVersionHistoryEntry
    {
        public string Version { get; set; } = default!;
        public DateTime FirstSeenAt { get; set; }
        public string FirstSeenSessionId { get; set; } = string.Empty;
        public string FirstSeenTenantId { get; set; } = string.Empty;
        public DateTime LastSeenAt { get; set; }
        public int SessionCount { get; set; }
    }
}

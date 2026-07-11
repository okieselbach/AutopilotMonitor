using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Classification of agent-side critical errors reported via the emergency channel.
    /// </summary>
    public enum AgentErrorType
    {
        /// <summary>
        /// POST /api/agent/telemetry returned a non-auth error (5xx / network failure).
        /// Events remain in the spool but are not reaching the backend.
        /// </summary>
        IngestFailed = 0,

        /// <summary>
        /// GET /api/agent/config failed — agent continues with defaults.
        /// </summary>
        ConfigFetchFailed = 1,

        /// <summary>
        /// POST /api/agent/register-session failed — session may not be tracked correctly.
        /// </summary>
        RegisterSessionFailed = 2,

        /// <summary>
        /// Post-config integrity check: SHA-256 of the running agent binary does not match
        /// the hash provided by the backend. Possible tampering or stale blob storage.
        /// </summary>
        IntegrityCheckFailed = 3,

        /// <summary>
        /// The agent's absolute session-age emergency break fired
        /// (Program.Guards.CheckSessionAgeEmergencyBreak → AbsoluteMaxSessionHours): the agent is
        /// cleaning up and exiting. Best-effort report over the resilient emergency channel (may be
        /// lost if the device is fully offline) so the backend can materialize an
        /// <c>agent_emergency_break</c> timeline event and terminalize the session instead of waiting
        /// out the silence grace. See tasks/enrollment-status-reclassification.md.
        /// </summary>
        SessionAgeEmergencyBreak = 4,
    }

    /// <summary>
    /// Lightweight payload sent by the agent to the emergency channel endpoint
    /// when a critical backend communication failure occurs.
    /// Never stored on disk; fire-and-forget from the agent.
    /// </summary>
    public class AgentErrorReport
    {
        /// <summary>
        /// The agent's current session ID (from SessionPersistence).
        /// Allows correlation with session data already in the backend.
        /// </summary>
        public string SessionId { get; set; } = default!;

        /// <summary>
        /// Tenant ID, from the device's MDM enrollment registry key.
        /// </summary>
        public string TenantId { get; set; } = default!;

        /// <summary>
        /// Classification of the error.
        /// </summary>
        public AgentErrorType ErrorType { get; set; }

        /// <summary>
        /// Error message from the exception or HTTP response.
        /// Kept short — no stack traces, no sensitive data.
        /// </summary>
        public string Message { get; set; } = default!;

        /// <summary>
        /// HTTP status code returned by the backend, if applicable.
        /// Null for network-level failures (timeout, DNS, TLS).
        /// </summary>
        public int? HttpStatusCode { get; set; }

        /// <summary>
        /// The agent's current spool sequence number at the time of failure.
        /// Useful for estimating how many events are at risk of being lost.
        /// </summary>
        public long? SequenceNumber { get; set; }

        /// <summary>
        /// Agent version string for diagnosing version-specific issues.
        /// </summary>
        public string AgentVersion { get; set; } = default!;

        /// <summary>
        /// UTC timestamp of when the error occurred on the agent.
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}

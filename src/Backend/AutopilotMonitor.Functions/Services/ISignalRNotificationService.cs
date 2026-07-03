using System.Threading.Tasks;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Programmatic SignalR message sender. Extracted as an interface so that consumers
    /// (TenantNotificationService, GlobalNotificationService, EventIngestProcessor, etc.)
    /// can be unit-tested without instantiating the real Azure SignalR ServiceManager,
    /// whose constructor validates the connection string.
    /// </summary>
    public interface ISignalRNotificationService
    {
        Task NotifyRuleResultsAvailableAsync(string tenantId, string sessionId, int resultCount);
        Task NotifyVulnerabilityReportAvailableAsync(string tenantId, string sessionId, string overallRisk);

        /// <summary>
        /// Emitted by the cascade-deletion worker after a session's tombstone step completes
        /// (plan §5 PR4). The web UI subscribes to <c>session-{tenantId}-{sessionId}</c> and
        /// removes the row + closes any open detail pane on receipt. Idempotent — a re-pickup of
        /// an already-tombstoned session is a no-op at the worker level, so receivers may see
        /// the event at most once per cascade.
        /// </summary>
        Task NotifySessionDeletedAsync(string tenantId, string sessionId);

        Task SendTenantNotificationAsync(string tenantId, NotificationAudience audience, object dto);
        Task SendTenantNotificationDismissedAsync(string tenantId, string notificationId);
        Task SendTenantNotificationDismissedAllAsync(string tenantId);

        Task SendGlobalNotificationAsync(object dto);
        Task SendGlobalNotificationDismissedAsync(string notificationId);
        Task SendGlobalNotificationDismissedAllAsync();

        /// <summary>
        /// Negotiates a client connection whose access token is bound to a SignalR USER ID (the
        /// lowercased UPN). Binding the user id is what makes a later revoke enforceable server-side:
        /// without it, connections are anonymous to the service and
        /// <see cref="RemoveUserFromAllGroupsAsync"/> has nothing to target. Returns null when
        /// negotiation fails (caller should respond 503 — the client cannot connect anyway).
        /// </summary>
        Task<(string Url, string AccessToken)?> NegotiateClientAsync(string userId);

        /// <summary>
        /// Cuts every live SignalR stream of the given user (lowercased UPN) — the enforcement half
        /// of a delegated-grant / tenant-group revoke. Group authorization is join-time only, so an
        /// already-joined, revoked caller would otherwise keep receiving live tenant telemetry until
        /// the connection drops. Strips the user from all groups server-side AND pushes
        /// "accessRevoked" to the user's connections; the web client restarts its connection on
        /// receipt, and every re-join re-runs authorization — revoked groups 403, still-authorized
        /// streams recover automatically. Only reaches connections negotiated WITH a user id (see
        /// <see cref="NegotiateClientAsync"/>); best-effort — failures are logged at Warning, the
        /// authoritative revoke (table row) has already happened.
        /// </summary>
        Task DisconnectUserAsync(string userId);
    }
}

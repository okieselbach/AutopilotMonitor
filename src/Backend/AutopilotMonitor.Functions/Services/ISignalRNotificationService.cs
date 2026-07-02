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
    }
}

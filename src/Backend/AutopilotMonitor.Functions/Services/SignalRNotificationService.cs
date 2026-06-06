using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using Microsoft.Azure.SignalR.Management;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Programmatic SignalR message sender for background tasks that run outside
    /// of Azure Function output bindings (e.g., async rule engine, vulnerability correlation).
    /// Uses the Azure SignalR Management SDK to send messages to connected clients.
    /// </summary>
    public class SignalRNotificationService : ISignalRNotificationService, IDisposable
    {
        private readonly ILogger<SignalRNotificationService> _logger;
        private readonly ServiceManager _serviceManager;
        private IServiceHubContext? _hubContext;
        private static readonly SemaphoreSlim _initLock = new(1, 1);

        private const string HubName = "autopilotmonitor";

        /// <summary>
        /// Hard ceiling for every imperative send (hub-context resolution + SendCoreAsync).
        /// Without it a slow/unreachable SignalR endpoint blocks for the SDK's HttpClient
        /// default of 100s, pinning the calling thread (queue worker or Task.Run) for the
        /// full duration. These pushes are observability nudges, never correctness — fail fast.
        /// </summary>
        private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

        public SignalRNotificationService(IConfiguration configuration, ILogger<SignalRNotificationService> logger)
        {
            _logger = logger;
            var connectionString = configuration["AzureSignalRConnectionString"]
                ?? throw new InvalidOperationException("AzureSignalRConnectionString is not configured");

            _serviceManager = new ServiceManagerBuilder()
                .WithOptions(o =>
                {
                    o.ConnectionString = connectionString;
                    o.ServiceTransportType = ServiceTransportType.Persistent;
                })
                .BuildServiceManager();
        }

        /// <summary>
        /// Notify a session's connected clients that new rule analysis results are available.
        /// Sends to the session-specific SignalR group so only the relevant session detail page reacts.
        /// </summary>
        public async Task NotifyRuleResultsAvailableAsync(string tenantId, string sessionId, int resultCount)
        {
            try
            {
                using var cts = new CancellationTokenSource(SendTimeout);
                var hub = await GetHubContextAsync(cts.Token);
                var groupName = $"session-{tenantId}-{sessionId}";

                await hub.Clients.Group(groupName).SendCoreAsync("ruleResultsReady", new object[]
                {
                    new
                    {
                        sessionId,
                        tenantId,
                        ruleResultCount = resultCount,
                        timestamp = DateTime.UtcNow.ToString("o")
                    }
                }, cts.Token);

                _logger.LogDebug("Sent ruleResultsReady to group {Group} ({Count} results)", groupName, resultCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send ruleResultsReady notification for session {SessionId}", sessionId);
            }
        }

        /// <summary>
        /// Notify a session's connected clients that vulnerability correlation results are available.
        /// </summary>
        public async Task NotifyVulnerabilityReportAvailableAsync(string tenantId, string sessionId, string overallRisk)
        {
            try
            {
                using var cts = new CancellationTokenSource(SendTimeout);
                var hub = await GetHubContextAsync(cts.Token);
                var groupName = $"session-{tenantId}-{sessionId}";

                await hub.Clients.Group(groupName).SendCoreAsync("vulnerabilityReportReady", new object[]
                {
                    new
                    {
                        sessionId,
                        tenantId,
                        overallRisk,
                        timestamp = DateTime.UtcNow.ToString("o")
                    }
                }, cts.Token);

                _logger.LogDebug("Sent vulnerabilityReportReady to group {Group} (risk={Risk})", groupName, overallRisk);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send vulnerabilityReportReady notification for session {SessionId}", sessionId);
            }
        }

        /// <summary>
        /// Notify the session group that the cascade-deletion worker has tombstoned the session
        /// (plan §5 PR4). Receivers (web UI) remove the row and close any open detail pane.
        /// Failures are logged and swallowed — the SignalR push is observability, not correctness;
        /// the audit log entry <c>deletion_completed</c> remains the canonical record.
        /// </summary>
        public async Task NotifySessionDeletedAsync(string tenantId, string sessionId)
        {
            try
            {
                using var cts = new CancellationTokenSource(SendTimeout);
                var hub = await GetHubContextAsync(cts.Token);
                var groupName = $"session-{tenantId}-{sessionId}";

                await hub.Clients.Group(groupName).SendCoreAsync("sessionDeleted", new object[]
                {
                    new
                    {
                        sessionId,
                        tenantId,
                        timestamp = DateTime.UtcNow.ToString("o")
                    }
                }, cts.Token);

                _logger.LogDebug("Sent sessionDeleted to group {Group}", groupName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send sessionDeleted notification for session {SessionId}", sessionId);
            }
        }

        /// <summary>
        /// Push a newly created tenant notification to the appropriate audience-tier group.
        /// Member-tier notifications go to the -notify-member group (joined by all tenant members);
        /// Admin-tier notifications go to the -notify-admin group (joined by Tenant Admins + Global Admins only).
        /// </summary>
        public virtual async Task SendTenantNotificationAsync(string tenantId, NotificationAudience audience, object dto)
        {
            try
            {
                using var cts = new CancellationTokenSource(SendTimeout);
                var hub = await GetHubContextAsync(cts.Token);
                var groupName = audience == NotificationAudience.Admin
                    ? SignalRGroupHelper.TenantNotifyAdminGroup(tenantId)
                    : SignalRGroupHelper.TenantNotifyMemberGroup(tenantId);

                await hub.Clients.Group(groupName).SendCoreAsync("tenantNotification", new[] { dto }, cts.Token);
                _logger.LogDebug("Sent tenantNotification to group {Group}", groupName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send tenantNotification for tenant {TenantId}", tenantId);
            }
        }

        /// <summary>
        /// Push a tenant notification dismissal to all audience-tier groups for the tenant.
        /// We do not know the original audience at dismiss time, so we broadcast to both groups —
        /// receivers filter by id and remove if present (idempotent).
        /// </summary>
        public virtual async Task SendTenantNotificationDismissedAsync(string tenantId, string notificationId)
        {
            try
            {
                using var cts = new CancellationTokenSource(SendTimeout);
                var hub = await GetHubContextAsync(cts.Token);
                var memberGroup = SignalRGroupHelper.TenantNotifyMemberGroup(tenantId);
                var adminGroup = SignalRGroupHelper.TenantNotifyAdminGroup(tenantId);
                var payload = new { id = notificationId };

                await Task.WhenAll(
                    hub.Clients.Group(memberGroup).SendCoreAsync("tenantNotificationDismissed", new object[] { payload }, cts.Token),
                    hub.Clients.Group(adminGroup).SendCoreAsync("tenantNotificationDismissed", new object[] { payload }, cts.Token)
                );
                _logger.LogDebug("Sent tenantNotificationDismissed for tenant {TenantId} id {Id}", tenantId, notificationId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send tenantNotificationDismissed for tenant {TenantId} id {Id}", tenantId, notificationId);
            }
        }

        /// <summary>
        /// Push a "dismiss all" event to all audience-tier groups for the tenant. Receivers clear local state.
        /// </summary>
        public virtual async Task SendTenantNotificationDismissedAllAsync(string tenantId)
        {
            try
            {
                using var cts = new CancellationTokenSource(SendTimeout);
                var hub = await GetHubContextAsync(cts.Token);
                var memberGroup = SignalRGroupHelper.TenantNotifyMemberGroup(tenantId);
                var adminGroup = SignalRGroupHelper.TenantNotifyAdminGroup(tenantId);

                await Task.WhenAll(
                    hub.Clients.Group(memberGroup).SendCoreAsync("tenantNotificationsDismissedAll", Array.Empty<object>(), cts.Token),
                    hub.Clients.Group(adminGroup).SendCoreAsync("tenantNotificationsDismissedAll", Array.Empty<object>(), cts.Token)
                );
                _logger.LogDebug("Sent tenantNotificationsDismissedAll for tenant {TenantId}", tenantId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send tenantNotificationsDismissedAll for tenant {TenantId}", tenantId);
            }
        }

        /// <summary>
        /// Push a newly created global notification to the global-admins group.
        /// </summary>
        public virtual async Task SendGlobalNotificationAsync(object dto)
        {
            try
            {
                using var cts = new CancellationTokenSource(SendTimeout);
                var hub = await GetHubContextAsync(cts.Token);
                await hub.Clients.Group(SignalRGroupHelper.GlobalAdminsGroup).SendCoreAsync("globalNotification", new[] { dto }, cts.Token);
                _logger.LogDebug("Sent globalNotification to group {Group}", SignalRGroupHelper.GlobalAdminsGroup);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send globalNotification");
            }
        }

        public virtual async Task SendGlobalNotificationDismissedAsync(string notificationId)
        {
            try
            {
                using var cts = new CancellationTokenSource(SendTimeout);
                var hub = await GetHubContextAsync(cts.Token);
                var payload = new { id = notificationId };
                await hub.Clients.Group(SignalRGroupHelper.GlobalAdminsGroup).SendCoreAsync("globalNotificationDismissed", new object[] { payload }, cts.Token);
                _logger.LogDebug("Sent globalNotificationDismissed for id {Id}", notificationId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send globalNotificationDismissed for id {Id}", notificationId);
            }
        }

        public virtual async Task SendGlobalNotificationDismissedAllAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(SendTimeout);
                var hub = await GetHubContextAsync(cts.Token);
                await hub.Clients.Group(SignalRGroupHelper.GlobalAdminsGroup).SendCoreAsync("globalNotificationsDismissedAll", Array.Empty<object>(), cts.Token);
                _logger.LogDebug("Sent globalNotificationsDismissedAll");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send globalNotificationsDismissedAll");
            }
        }

        private async Task<IServiceHubContext> GetHubContextAsync(CancellationToken ct = default)
        {
            if (_hubContext != null) return _hubContext;

            await _initLock.WaitAsync(ct);
            try
            {
                _hubContext ??= await _serviceManager.CreateHubContextAsync(HubName, ct);
                return _hubContext;
            }
            finally
            {
                _initLock.Release();
            }
        }

        public void Dispose()
        {
            (_hubContext as IDisposable)?.Dispose();
            _serviceManager?.Dispose();
        }
    }
}

using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Service for managing tenant-scoped persistent in-app notifications.
/// Backed by <see cref="ITenantNotificationRepository"/>; all reads/writes are partitioned per
/// tenantId so cross-tenant access is impossible at the repository layer.
/// Designed to be called fire-and-forget from request handlers — failures are logged, never thrown.
/// </summary>
public class TenantNotificationService
{
    private readonly ITenantNotificationRepository _repo;
    private readonly ISignalRNotificationService _signalr;
    private readonly ILogger<TenantNotificationService> _logger;

    public TenantNotificationService(
        ITenantNotificationRepository repo,
        ISignalRNotificationService signalr,
        ILogger<TenantNotificationService> logger)
    {
        _repo = repo;
        _signalr = signalr;
        _logger = logger;
    }

    public virtual async Task CreateNotificationAsync(string tenantId, string type, string title, string message, string? href = null)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            _logger.LogWarning("CreateNotificationAsync called with empty tenantId — skipping ({Type}: {Title})", type, title);
            return;
        }

        try
        {
            var notificationId = Guid.NewGuid().ToString("N")[..12];

            var notification = new GlobalNotification
            {
                NotificationId = notificationId,
                Type = type,
                Title = title,
                Message = message,
                Href = href,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = "system",
                IsDismissed = false
            };

            await _repo.AddNotificationAsync(tenantId, notification);
            _logger.LogInformation("Tenant notification created: tenant={TenantId} id={NotificationId} type={Type}",
                tenantId, notificationId, type);

            // Push to the audience-tier SignalR group so connected clients update live without polling.
            var audience = TenantNotificationAudienceCatalog.Resolve(type);
            var dto = new GlobalNotificationDto
            {
                Id = notificationId,
                Type = type,
                Title = title,
                Message = message,
                Href = href,
                CreatedAt = notification.CreatedAt
            };
            await _signalr.SendTenantNotificationAsync(tenantId, audience, dto);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create tenant notification (tenant={TenantId} type={Type}: {Title})", tenantId, type, title);
        }
    }

    /// <summary>
    /// Returns active (non-dismissed) tenant notifications visible to a caller of the given audience tier.
    /// A notification is included iff <c>TenantNotificationAudienceCatalog.Resolve(type) &lt;= callerAudience</c>:
    /// Admin callers see Member + Admin notifications, Member callers see only Member notifications.
    /// </summary>
    public async Task<List<GlobalNotificationDto>> GetActiveNotificationsAsync(string tenantId, NotificationAudience callerAudience)
    {
        try
        {
            var notifications = await _repo.GetNotificationsAsync(tenantId);

            return notifications
                .Where(n => TenantNotificationAudienceCatalog.Resolve(n.Type) <= callerAudience)
                .Select(n => new GlobalNotificationDto
                {
                    Id = n.NotificationId,
                    Type = n.Type,
                    Title = n.Title,
                    Message = n.Message,
                    Href = n.Href,
                    CreatedAt = n.CreatedAt
                }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve tenant notifications for tenant {TenantId}", tenantId);
            return new List<GlobalNotificationDto>();
        }
    }

    public async Task<bool> DismissNotificationAsync(string tenantId, string notificationId, string dismissedBy)
    {
        try
        {
            var dismissed = await _repo.DismissNotificationAsync(tenantId, notificationId, dismissedBy);
            if (dismissed)
            {
                await _signalr.SendTenantNotificationDismissedAsync(tenantId, notificationId);
            }
            return dismissed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dismiss tenant notification (tenant={TenantId} id={NotificationId})",
                tenantId, notificationId);
            return false;
        }
    }

    public async Task<int> DismissAllNotificationsAsync(string tenantId)
    {
        try
        {
            var count = await _repo.DismissAllNotificationsAsync(tenantId);
            if (count > 0)
            {
                await _signalr.SendTenantNotificationDismissedAllAsync(tenantId);
            }
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dismiss all tenant notifications for tenant {TenantId}", tenantId);
            return 0;
        }
    }
}

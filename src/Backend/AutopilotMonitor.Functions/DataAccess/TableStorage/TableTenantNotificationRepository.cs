using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of <see cref="ITenantNotificationRepository"/>.
    /// PartitionKey = tenantId (lowercased), RowKey = invertedTicks_notificationId (newest-first).
    /// All reads and writes scope queries to a single partition so cross-tenant access is impossible.
    /// </summary>
    public class TableTenantNotificationRepository : ITenantNotificationRepository
    {
        private readonly TableClient _table;
        private readonly ILogger<TableTenantNotificationRepository> _logger;

        public TableTenantNotificationRepository(
            TableStorageService storage,
            ILogger<TableTenantNotificationRepository> logger)
        {
            _logger = logger;
            _table = storage.GetTableClient(Constants.TableNames.TenantNotifications);
        }

        public async Task<bool> AddNotificationAsync(string tenantId, GlobalNotification notification)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
                return false;

            try
            {
                var partitionKey = tenantId.ToLowerInvariant();
                var invertedTicks = RowKeyCodec.InvertedTicks(notification.CreatedAt);
                var notificationId = notification.NotificationId;
                if (string.IsNullOrEmpty(notificationId))
                    notificationId = Guid.NewGuid().ToString("N")[..12];

                var entity = new TableEntity(partitionKey, $"{invertedTicks}_{notificationId}")
                {
                    ["NotificationId"] = notificationId,
                    ["Type"] = notification.Type ?? "info",
                    ["Title"] = notification.Title ?? string.Empty,
                    ["Message"] = notification.Message ?? string.Empty,
                    ["Href"] = notification.Href,
                    ["CreatedAt"] = notification.CreatedAt,
                    ["CreatedBy"] = notification.CreatedBy ?? string.Empty,
                    ["Dismissed"] = notification.IsDismissed
                };

                await _table.UpsertEntityAsync(entity);
                _logger.LogInformation("Tenant notification stored: tenant={TenantId} id={NotificationId} type={Type}",
                    tenantId, notificationId, notification.Type);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to store tenant notification (tenant={TenantId} type={Type})", tenantId, notification.Type);
                return false;
            }
        }

        public async Task<List<GlobalNotification>> GetNotificationsAsync(string tenantId, int maxResults = 50)
        {
            var notifications = new List<GlobalNotification>();
            if (string.IsNullOrWhiteSpace(tenantId))
                return notifications;

            try
            {
                var partitionKey = tenantId.ToLowerInvariant();
                var count = 0;

                await foreach (var entity in _table.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq '{partitionKey}'"))
                {
                    var dismissed = entity.GetBoolean("Dismissed") ?? false;
                    if (dismissed) continue;

                    notifications.Add(MapToGlobalNotification(entity));
                    count++;
                    if (count >= maxResults) break;
                }

                return notifications;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning("TenantNotifications table not found — returning empty list");
                return notifications;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve tenant notifications for tenant {TenantId}", tenantId);
                return notifications;
            }
        }

        // TODO(per-user-dismiss): Dismissal is currently tenant-shared — flipping `Dismissed` on the
        // single row hides the notification for every member of the tenant. When notification volume
        // grows enough that this matters, model dismissal as a separate per-user fact:
        //   PartitionKey = $"{tenantId}|{userObjectId}", RowKey = notificationId.
        // The GET path then left-anti-joins the dismissed-by-this-user set against the tenant rows;
        // the dismiss endpoints can drop to MemberRead because each user only writes their own row.
        public async Task<bool> DismissNotificationAsync(string tenantId, string notificationId, string dismissedBy)
        {
            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(notificationId))
                return false;

            try
            {
                var partitionKey = tenantId.ToLowerInvariant();

                await foreach (var entity in _table.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq '{partitionKey}'"))
                {
                    if (entity.GetString("NotificationId") == notificationId)
                    {
                        entity["Dismissed"] = true;
                        entity["DismissedBy"] = dismissedBy ?? string.Empty;
                        entity["DismissedAt"] = DateTime.UtcNow;
                        await _table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
                        _logger.LogInformation("Tenant notification dismissed: tenant={TenantId} id={NotificationId}",
                            tenantId, notificationId);
                        return true;
                    }
                }

                return false;
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
            if (string.IsNullOrWhiteSpace(tenantId))
                return 0;

            try
            {
                var partitionKey = tenantId.ToLowerInvariant();
                var count = 0;

                await foreach (var entity in _table.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq '{partitionKey}'"))
                {
                    var dismissed = entity.GetBoolean("Dismissed") ?? false;
                    if (dismissed) continue;

                    entity["Dismissed"] = true;
                    entity["DismissedAt"] = DateTime.UtcNow;
                    await _table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
                    count++;
                }

                _logger.LogInformation("Dismissed {Count} tenant notifications for tenant {TenantId}", count, tenantId);
                return count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dismiss all tenant notifications for tenant {TenantId}", tenantId);
                return 0;
            }
        }

        public async Task<int> DeleteNotificationsByRetentionAsync(DateTime dismissedCutoffUtc, DateTime unreadCutoffUtc)
        {
            try
            {
                // Cross-partition retention sweep (one partition per tenant). Hybrid policy mirrors the
                // global notifications table: a dismissed row drops 30 days after DISMISSAL (DismissedAt),
                // unread rows survive until the long creation-age cutoff so an unacknowledged tenant-admin
                // warning is never silently lost. The server-side filter keeps only prune-eligible rows on the wire.
                var filter = NotificationRetentionFilter.BuildPredicate(dismissedCutoffUtc, unreadCutoffUtc);
                var query = _table.QueryAsync<TableEntity>(filter: filter, select: new[] { "PartitionKey", "RowKey" });

                int deleted = 0;
                await foreach (var entity in query)
                {
                    try
                    {
                        await _table.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete tenant notification {PK}/{RK}", entity.PartitionKey, entity.RowKey);
                    }
                }

                if (deleted > 0)
                    _logger.LogInformation(
                        "Deleted {Count} tenant notifications (dismissed<{DismissedCutoff:yyyy-MM-dd}, any<{UnreadCutoff:yyyy-MM-dd})",
                        deleted, dismissedCutoffUtc, unreadCutoffUtc);

                return deleted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete old tenant notifications");
                return 0;
            }
        }

        private static GlobalNotification MapToGlobalNotification(TableEntity entity)
        {
            return new GlobalNotification
            {
                NotificationId = entity.GetString("NotificationId") ?? string.Empty,
                Type = entity.GetString("Type") ?? "info",
                Title = entity.GetString("Title") ?? string.Empty,
                Message = entity.GetString("Message") ?? string.Empty,
                Href = entity.GetString("Href"),
                CreatedAt = entity.GetDateTimeOffset("CreatedAt")?.UtcDateTime ?? DateTime.MinValue,
                CreatedBy = entity.GetString("CreatedBy") ?? string.Empty,
                IsDismissed = entity.GetBoolean("Dismissed") ?? false,
                DismissedBy = entity.GetString("DismissedBy"),
                DismissedAt = entity.GetDateTimeOffset("DismissedAt")?.UtcDateTime
            };
        }
    }
}

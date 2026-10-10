using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Notifications;
using Azure;
using Azure.Data.Tables;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of <see cref="INotificationChannelHealthRepository"/>.
    /// PartitionKey = scope key, RowKey = channel id. Whole-row Replace (never Merge), so a
    /// cleared field such as <see cref="NotificationChannelHealth.FailingNotifiedUtc"/> really clears.
    /// </summary>
    public class TableNotificationChannelHealthRepository : INotificationChannelHealthRepository
    {
        private readonly TableClient _table;

        public TableNotificationChannelHealthRepository(TableStorageService storage)
        {
            _table = storage.GetTableClient(Constants.TableNames.NotificationChannelHealth);
        }

        public async Task<(NotificationChannelHealth? Row, string? ETag)> GetWithETagAsync(string scopeKey, string channelId)
        {
            try
            {
                var entity = await _table.GetEntityAsync<TableEntity>(scopeKey, channelId).ConfigureAwait(false);
                return (MapFromEntity(entity.Value), entity.Value.ETag.ToString());
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return (null, null);
            }
        }

        public async Task<bool> TryUpsertAsync(NotificationChannelHealth row, string? ifMatchETag)
        {
            var entity = MapToEntity(row);
            try
            {
                if (ifMatchETag is null)
                    await _table.AddEntityAsync(entity).ConfigureAwait(false);
                else
                    await _table.UpdateEntityAsync(entity, new ETag(ifMatchETag), TableUpdateMode.Replace).ConfigureAwait(false);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 409 || ex.Status == 412)
            {
                // 409: a concurrent first writer created the row; 412: the row changed since the read.
                return false;
            }
        }

        public async Task<List<NotificationChannelHealth>> ListAsync(string scopeKey)
        {
            var result = new List<NotificationChannelHealth>();
            var filter = TableClient.CreateQueryFilter($"PartitionKey eq {scopeKey}");
            try
            {
                await foreach (var entity in _table.QueryAsync<TableEntity>(filter).ConfigureAwait(false))
                    result.Add(MapFromEntity(entity));
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Table not created yet: no channel has ever sent.
            }
            return result;
        }

        // ── Mapping ───────────────────────────────────────────────────────────

        internal static TableEntity MapToEntity(NotificationChannelHealth h)
        {
            return new TableEntity(h.ScopeKey, h.ChannelId)
            {
                { "Fingerprint", h.Fingerprint },
                { "Recent", h.Recent },
                { "ConsecutiveFailures", h.ConsecutiveFailures },
                { "LastAttemptUtc", Utc(h.LastAttemptUtc) },
                { "LastSuccessUtc", Utc(h.LastSuccessUtc) },
                { "LastFailureUtc", Utc(h.LastFailureUtc) },
                { "FailingSinceUtc", Utc(h.FailingSinceUtc) },
                { "LastStatusCode", h.LastStatusCode },
                { "LastError", h.LastError },
                { "FailingNotifiedUtc", Utc(h.FailingNotifiedUtc) },
            };
        }

        internal static NotificationChannelHealth MapFromEntity(TableEntity e)
        {
            return new NotificationChannelHealth
            {
                ScopeKey = e.PartitionKey,
                ChannelId = e.RowKey,
                Fingerprint = e.GetString("Fingerprint") ?? string.Empty,
                Recent = e.GetString("Recent") ?? string.Empty,
                ConsecutiveFailures = e.GetInt32("ConsecutiveFailures") ?? 0,
                LastAttemptUtc = e.GetDateTime("LastAttemptUtc"),
                LastSuccessUtc = e.GetDateTime("LastSuccessUtc"),
                LastFailureUtc = e.GetDateTime("LastFailureUtc"),
                FailingSinceUtc = e.GetDateTime("FailingSinceUtc"),
                LastStatusCode = e.GetInt32("LastStatusCode"),
                LastError = e.GetString("LastError"),
                FailingNotifiedUtc = e.GetDateTime("FailingNotifiedUtc"),
            };
        }

        private static DateTime? Utc(DateTime? value)
            => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;
    }
}

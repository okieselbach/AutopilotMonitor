using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.WhatsNew;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of <see cref="IWhatsNewNotificationStateRepository"/>.
    /// Lives as a side row in the AdminConfiguration table (PartitionKey
    /// <see cref="PartitionKey"/>, RowKey <see cref="RowKey"/>) — platform-scoped state next to the
    /// table-schema sentinel, so no new table is needed. Deliberately NOT a field on the
    /// AdminConfiguration row itself: that row is round-tripped wholesale by the Global Admin
    /// settings UI, and a stale save there would roll the watermark back and re-announce entries.
    /// </summary>
    public class TableWhatsNewNotificationStateRepository : IWhatsNewNotificationStateRepository
    {
        internal const string PartitionKey = "WhatsNewNotifications";
        internal const string RowKey = "state";

        private const string KnownEntryKeysProperty = "KnownEntryKeysJson";
        private const string LastDocsCommitProperty = "LastDocsCommit";
        private const string LastRunUtcProperty = "LastRunUtc";
        private const string LastNotifiedUtcProperty = "LastNotifiedUtc";
        private const string LastNotifiedEntryCountProperty = "LastNotifiedEntryCount";

        private readonly TableClient _table;
        private readonly ILogger<TableWhatsNewNotificationStateRepository> _logger;

        public TableWhatsNewNotificationStateRepository(
            TableStorageService storage,
            ILogger<TableWhatsNewNotificationStateRepository> logger)
        {
            _logger = logger;
            _table = storage.GetTableClient(Constants.TableNames.AdminConfiguration);
        }

        public async Task<(WhatsNewNotificationState? State, string? ETag)> GetWithETagAsync()
        {
            try
            {
                var response = await _table.GetEntityIfExistsAsync<TableEntity>(PartitionKey, RowKey);
                if (!response.HasValue)
                    return (null, null);

                var entity = response.Value!;
                return (MapFromEntity(entity), entity.ETag.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load What's new notification state");
                throw;
            }
        }

        public async Task<bool> TryUpsertAsync(WhatsNewNotificationState state, string? ifMatchETag)
        {
            if (state == null)
                return false;

            try
            {
                var entity = MapToEntity(state);

                if (ifMatchETag is null)
                {
                    await _table.AddEntityAsync(entity);
                    return true;
                }

                await _table.UpdateEntityAsync(entity, new ETag(ifMatchETag), TableUpdateMode.Replace);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 409 || ex.Status == 412)
            {
                // 409 EntityAlreadyExists (AddEntity) or 412 PreconditionFailed (ETag mismatch):
                // a parallel run committed first — the caller must not send.
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed conditional upsert of What's new notification state");
                return false;
            }
        }

        private static TableEntity MapToEntity(WhatsNewNotificationState state)
        {
            var entity = new TableEntity(PartitionKey, RowKey)
            {
                [KnownEntryKeysProperty] = JsonSerializer.Serialize(state.KnownEntryKeys),
                [LastNotifiedEntryCountProperty] = state.LastNotifiedEntryCount,
            };

            if (!string.IsNullOrEmpty(state.LastDocsCommit))
                entity[LastDocsCommitProperty] = state.LastDocsCommit;
            if (state.LastRunUtc.HasValue)
                entity[LastRunUtcProperty] = DateTime.SpecifyKind(state.LastRunUtc.Value, DateTimeKind.Utc);
            if (state.LastNotifiedUtc.HasValue)
                entity[LastNotifiedUtcProperty] = DateTime.SpecifyKind(state.LastNotifiedUtc.Value, DateTimeKind.Utc);

            return entity;
        }

        private WhatsNewNotificationState MapFromEntity(TableEntity entity)
        {
            var known = new HashSet<string>(StringComparer.Ordinal);
            var json = entity.GetString(KnownEntryKeysProperty);
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<string>>(json!);
                    if (parsed != null)
                    {
                        foreach (var key in parsed)
                        {
                            if (!string.IsNullOrWhiteSpace(key))
                                known.Add(key);
                        }
                    }
                }
                catch (JsonException ex)
                {
                    // Treated as an empty watermark: the service then re-baselines silently
                    // (empty known set + existing row = "first run" semantics), never re-announces.
                    _logger.LogWarning(ex, "What's new notification state has malformed KnownEntryKeysJson — re-baselining");
                }
            }

            return new WhatsNewNotificationState
            {
                KnownEntryKeys = known,
                LastDocsCommit = entity.GetString(LastDocsCommitProperty),
                LastRunUtc = entity.GetDateTime(LastRunUtcProperty),
                LastNotifiedUtc = entity.GetDateTime(LastNotifiedUtcProperty),
                LastNotifiedEntryCount = entity.GetInt32(LastNotifiedEntryCountProperty) ?? 0,
            };
        }
    }
}

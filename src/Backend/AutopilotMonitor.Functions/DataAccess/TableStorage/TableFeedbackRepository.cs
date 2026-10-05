using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
    /// Table-storage implementation of <see cref="IFeedbackRepository"/>. Backed by the
    /// dedicated <c>Feedback</c> table. Survives tenant offboarding by design — feedback
    /// from offboarded tenants is the data we care about most.
    /// </summary>
    public class TableFeedbackRepository : IFeedbackRepository
    {
        private readonly TableClient _tableClient;
        private readonly ILogger<TableFeedbackRepository> _logger;

        public TableFeedbackRepository(TableStorageService storage, ILogger<TableFeedbackRepository> logger)
        {
            _tableClient = storage.GetTableClient(Constants.TableNames.Feedback);
            _logger = logger;
        }

        // ── In-App ─────────────────────────────────────────────────────────────

        public async Task<FeedbackEntry?> GetInAppFeedbackAsync(string upn)
        {
            try
            {
                var entity = await _tableClient.GetEntityAsync<TableEntity>(
                    FeedbackEntryType.InApp, upn.ToLowerInvariant());
                return MapInApp(entity.Value);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading in-app feedback for {Upn}", upn);
                throw;
            }
        }

        public async Task<FeedbackEntry?> UpdateInAppFeedbackAsync(string upn, Func<FeedbackEntry?, FeedbackEntry?> decide)
        {
            var rowKey = upn.ToLowerInvariant();
            for (var attempt = 1; ; attempt++)
            {
                TableEntity? stored = null;
                try
                {
                    stored = (await _tableClient.GetEntityAsync<TableEntity>(FeedbackEntryType.InApp, rowKey)).Value;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                }

                var next = decide(stored == null ? null : MapInApp(stored));
                if (next == null)
                    return null;

                next.Type = FeedbackEntryType.InApp;
                next.Upn = rowKey;
                try
                {
                    // Conditional on what was read: two tabs answering at once must not let a dismissal
                    // replace a rating — the loser decides again on the winner's row.
                    if (stored == null)
                        await _tableClient.AddEntityAsync(StoreInApp(next));
                    else
                        await _tableClient.UpdateEntityAsync(StoreInApp(next), stored.ETag, TableUpdateMode.Replace);
                    return next;
                }
                catch (RequestFailedException ex) when ((ex.Status == 409 || ex.Status == 412) && attempt < 3)
                {
                    _logger.LogInformation("In-app feedback for {Upn} changed concurrently; deciding again", rowKey);
                }
            }
        }

        // ── Offboarding ────────────────────────────────────────────────────────

        public async Task<FeedbackEntry?> GetOffboardingFeedbackAsync(string historyRowKey)
        {
            if (string.IsNullOrEmpty(historyRowKey))
                throw new ArgumentException("historyRowKey required", nameof(historyRowKey));

            try
            {
                var entity = await _tableClient.GetEntityAsync<TableEntity>(
                    FeedbackEntryType.Offboarding, historyRowKey);
                return MapOffboarding(entity.Value);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading offboarding feedback for {HistoryRowKey}", historyRowKey);
                throw;
            }
        }

        public async Task SaveOffboardingFeedbackAsync(FeedbackEntry entry)
        {
            entry.Type = FeedbackEntryType.Offboarding;
            if (string.IsNullOrEmpty(entry.HistoryRowKey))
                throw new ArgumentException(
                    "HistoryRowKey required for offboarding feedback", nameof(entry));
            try
            {
                await _tableClient.UpsertEntityAsync(StoreOffboarding(entry), TableUpdateMode.Replace);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error saving offboarding feedback for {HistoryRowKey}", entry.HistoryRowKey);
                throw;
            }
        }

        // ── General ────────────────────────────────────────────────────────────

        public async Task SaveGeneralFeedbackAsync(FeedbackEntry entry)
        {
            entry.Type = FeedbackEntryType.General;
            entry.InteractedAt ??= DateTime.UtcNow;
            entry.Upn = entry.Upn.ToLowerInvariant();
            entry.FeedbackId = $"{RowKeyCodec.InvertedTicks(entry.InteractedAt.Value)}_{Guid.NewGuid().ToString("N")[..12]}";
            try
            {
                // Insert, never upsert: every submission is its own row.
                await _tableClient.AddEntityAsync(StoreGeneral(entry));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving general feedback for {Upn}", entry.Upn);
                throw;
            }
        }

        public async Task<int> CountGeneralFeedbackSinceAsync(string upn, DateTime sinceUtc)
        {
            var normalizedUpn = upn.ToLowerInvariant();
            // Newest-first RowKeys: a row at or after sinceUtc has an inverted-tick prefix <= that of
            // sinceUtc, so everything below the prefix of the tick before sinceUtc is in the window.
            var bound = RowKeyCodec.InvertedTicks(sinceUtc.AddTicks(-1));
            var filter = $"PartitionKey eq '{FeedbackEntryType.General}' and RowKey lt '{bound}' " +
                         $"and Upn eq '{normalizedUpn.Replace("'", "''")}'";
            var count = 0;
            await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
                filter, select: new[] { "Upn", "InteractedAt" }))
            {
                // The service filter is the efficient bound; the same conditions are re-checked here.
                if (entity.PartitionKey == FeedbackEntryType.General
                    && string.Equals(entity.GetString("Upn"), normalizedUpn, StringComparison.Ordinal)
                    && entity.GetDateTime("InteractedAt") is DateTime at && at >= sinceUtc)
                {
                    count++;
                }
            }
            return count;
        }

        // ── Reports ────────────────────────────────────────────────────────────

        public async Task<List<FeedbackEntry>> GetAllAsync()
        {
            var entries = new List<FeedbackEntry>();
            try
            {
                await foreach (var entity in _tableClient.QueryAsync<TableEntity>())
                {
                    entries.Add(entity.PartitionKey switch
                    {
                        FeedbackEntryType.InApp => MapInApp(entity),
                        FeedbackEntryType.Offboarding => MapOffboarding(entity),
                        FeedbackEntryType.General => MapGeneral(entity),
                        _ => MapUnknown(entity),
                    });
                }
                return entries;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading all feedback entries");
                throw;
            }
        }

        // ── Store / Map (memory: feedback_table_storage_serialization) ─────────

        private static TableEntity StoreInApp(FeedbackEntry e) =>
            new(FeedbackEntryType.InApp, e.Upn.ToLowerInvariant())
            {
                ["TenantId"] = e.TenantId,
                ["DisplayName"] = e.DisplayName,
                ["Rating"] = e.Rating,
                ["Comment"] = e.Comment,
                ["Dismissed"] = e.Dismissed,
                ["Submitted"] = e.Submitted,
                ["DismissCount"] = e.DismissCount,
                ["InteractedAt"] = e.InteractedAt,
            };

        private static FeedbackEntry MapInApp(TableEntity e)
        {
            var dismissed = e.GetBoolean("Dismissed") ?? false;
            var submitted = e.GetBoolean("Submitted") ?? false;
            return new FeedbackEntry
            {
                Type = FeedbackEntryType.InApp,
                Upn = e.RowKey,
                TenantId = e.GetString("TenantId") ?? string.Empty,
                DisplayName = e.GetString("DisplayName") ?? string.Empty,
                Rating = e.GetInt32("Rating"),
                Comment = e.GetString("Comment"),
                Dismissed = dismissed,
                Submitted = submitted,
                // Rows from before the count existed: one recorded dismissal at most.
                DismissCount = e.GetInt32("DismissCount") ?? (dismissed && !submitted ? 1 : 0),
                InteractedAt = e.GetDateTime("InteractedAt"),
            };
        }

        private static TableEntity StoreOffboarding(FeedbackEntry e) =>
            new(FeedbackEntryType.Offboarding, e.HistoryRowKey!)
            {
                ["TenantId"] = e.TenantId,
                ["Upn"] = e.Upn,
                ["DisplayName"] = e.DisplayName,
                ["DomainName"] = e.DomainName,
                ["Comment"] = e.Comment,
                ["InteractedAt"] = e.InteractedAt,
            };

        private static FeedbackEntry MapOffboarding(TableEntity e) => new()
        {
            Type = FeedbackEntryType.Offboarding,
            HistoryRowKey = e.RowKey,
            TenantId = e.GetString("TenantId") ?? string.Empty,
            Upn = e.GetString("Upn") ?? string.Empty,
            DisplayName = e.GetString("DisplayName") ?? string.Empty,
            DomainName = e.GetString("DomainName"),
            Comment = e.GetString("Comment"),
            InteractedAt = e.GetDateTime("InteractedAt"),
        };

        private static TableEntity StoreGeneral(FeedbackEntry e) =>
            new(FeedbackEntryType.General, e.FeedbackId!)
            {
                ["TenantId"] = e.TenantId,
                ["Upn"] = e.Upn,
                ["DisplayName"] = e.DisplayName,
                ["DomainName"] = e.DomainName,
                ["Comment"] = e.Comment,
                ["ContactEmail"] = e.ContactEmail,
                ["InteractedAt"] = e.InteractedAt,
            };

        private static FeedbackEntry MapGeneral(TableEntity e) => new()
        {
            Type = FeedbackEntryType.General,
            FeedbackId = e.RowKey,
            TenantId = e.GetString("TenantId") ?? string.Empty,
            Upn = e.GetString("Upn") ?? string.Empty,
            DisplayName = e.GetString("DisplayName") ?? string.Empty,
            DomainName = e.GetString("DomainName"),
            Comment = e.GetString("Comment"),
            ContactEmail = e.GetString("ContactEmail"),
            InteractedAt = e.GetDateTime("InteractedAt"),
        };

        // Defensive fall-through. The Feedback table is owned by this repository so any
        // unknown PartitionKey indicates a data-quality issue; we still want to return the
        // row in /feedback/all so it surfaces on the Reports page rather than vanish.
        private static FeedbackEntry MapUnknown(TableEntity e) => new()
        {
            Type = e.PartitionKey,
            Upn = e.RowKey,
            TenantId = e.GetString("TenantId") ?? string.Empty,
            Comment = e.GetString("Comment"),
            InteractedAt = e.GetDateTime("InteractedAt"),
        };
    }
}

using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of <see cref="IDelegationInvitationRepository"/>.
    /// DelegationInvitations — PartitionKey: home (managing) tenant id, RowKey: invitation id (Guid "N").
    /// The managed tenant is denormalized into the <c>TenantId</c> column so the offboarding property wipe
    /// purges rows for an offboarded MANAGED tenant; the home tenant's own offboarding wipes the partition.
    /// </summary>
    public class TableDelegationInvitationRepository : IDelegationInvitationRepository
    {
        private readonly TableClient _table;
        private readonly ILogger<TableDelegationInvitationRepository> _logger;

        public TableDelegationInvitationRepository(TableStorageService storage, ILogger<TableDelegationInvitationRepository> logger)
        {
            _table = storage.GetTableClient(Constants.TableNames.DelegationInvitations);
            _logger = logger;
        }

        public async Task CreateAsync(DelegationInvitation invitation)
        {
            // Fresh GUID row — AddEntity (not Upsert) so a (vanishingly unlikely) collision fails loud.
            await _table.AddEntityAsync(Build(invitation));
        }

        public async Task<DelegationInvitation?> GetAsync(string homeTenantId, string invitationId)
        {
            if (string.IsNullOrWhiteSpace(homeTenantId) || string.IsNullOrWhiteSpace(invitationId))
                return null;
            try
            {
                var result = await _table.GetEntityAsync<TableEntity>(homeTenantId.ToLowerInvariant(), invitationId);
                return Map(result.Value);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        public async Task<List<DelegationInvitation>> GetByHomeTenantAsync(string homeTenantId)
        {
            var rows = new List<DelegationInvitation>();
            if (string.IsNullOrWhiteSpace(homeTenantId))
                return rows;
            await foreach (var entity in _table.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{ODataSanitizer.EscapeValue(homeTenantId.ToLowerInvariant())}'"))
            {
                rows.Add(Map(entity));
            }
            return rows;
        }

        public async Task<bool> TryAcceptAsync(string homeTenantId, string invitationId, string etag, string acceptedTenantId, string acceptedBy, DateTime nowUtc)
        {
            try
            {
                var result = await _table.GetEntityAsync<TableEntity>(homeTenantId.ToLowerInvariant(), invitationId);
                var entity = result.Value;
                if (!string.Equals(entity.GetString("Status"), Constants.DelegationInvitationStatus.Pending, StringComparison.Ordinal))
                    return false;
                entity["Status"] = Constants.DelegationInvitationStatus.Accepted;
                entity["AcceptedDate"] = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));
                entity["AcceptedBy"] = acceptedBy.ToLowerInvariant();
                entity["TenantId"] = acceptedTenantId.ToLowerInvariant();
                // The ETag the caller READ is the guard: a concurrent accept/cancel changed it → 412 → false.
                await _table.UpdateEntityAsync(entity, new ETag(etag), TableUpdateMode.Replace);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status is 404 or 412)
            {
                return false;
            }
        }

        public async Task<bool> SetStatusAsync(string homeTenantId, string invitationId, string status, DateTime nowUtc, string? actor, DateTime? holdUntilUtc)
        {
            try
            {
                var result = await _table.GetEntityAsync<TableEntity>(homeTenantId.ToLowerInvariant(), invitationId);
                var entity = result.Value;
                entity["Status"] = status;
                if (status == Constants.DelegationInvitationStatus.Released)
                {
                    entity["ReleasedDate"] = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));
                    if (!string.IsNullOrEmpty(actor)) entity["ReleasedBy"] = actor.ToLowerInvariant();
                }
                if (holdUntilUtc.HasValue)
                    entity["HoldUntilDate"] = new DateTimeOffset(DateTime.SpecifyKind(holdUntilUtc.Value, DateTimeKind.Utc));
                await _table.UpdateEntityAsync(entity, ETag.All, TableUpdateMode.Replace);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
        }

        public async Task<int> DeleteOlderThanAsync(DateTime cutoffUtc)
        {
            var deleted = 0;
            try
            {
                var filter = $"CreatedDate lt datetime'{cutoffUtc:yyyy-MM-ddTHH:mm:ss}Z'";
                await foreach (var entity in _table.QueryAsync<TableEntity>(filter: filter, select: new[] { "PartitionKey", "RowKey" }))
                {
                    try
                    {
                        await _table.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete delegation invitation row {PK}/{RK}", entity.PartitionKey, entity.RowKey);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sweep old delegation invitation rows");
            }
            return deleted;
        }

        /// <summary>Model → entity (project rule "table-serialization": every field in BOTH directions).</summary>
        internal static TableEntity Build(DelegationInvitation i)
        {
            var e = new TableEntity(i.HomeTenantId.ToLowerInvariant(), i.InvitationId)
            {
                ["InvitationId"] = i.InvitationId,
                ["HomeTenantId"] = i.HomeTenantId.ToLowerInvariant(),
                ["Status"] = i.Status,
                ["Role"] = i.Role,
                ["Source"] = i.Source,
                ["CreatedBy"] = i.CreatedBy.ToLowerInvariant(),
                ["CreatedDate"] = new DateTimeOffset(DateTime.SpecifyKind(i.CreatedAt, DateTimeKind.Utc)),
                ["ExpiresDate"] = new DateTimeOffset(DateTime.SpecifyKind(i.ExpiresAt, DateTimeKind.Utc)),
            };
            if (i.AcceptedAt.HasValue) e["AcceptedDate"] = new DateTimeOffset(DateTime.SpecifyKind(i.AcceptedAt.Value, DateTimeKind.Utc));
            if (!string.IsNullOrEmpty(i.AcceptedBy)) e["AcceptedBy"] = i.AcceptedBy.ToLowerInvariant();
            if (!string.IsNullOrEmpty(i.TenantId)) e["TenantId"] = i.TenantId.ToLowerInvariant();
            if (i.ReleasedAt.HasValue) e["ReleasedDate"] = new DateTimeOffset(DateTime.SpecifyKind(i.ReleasedAt.Value, DateTimeKind.Utc));
            if (!string.IsNullOrEmpty(i.ReleasedBy)) e["ReleasedBy"] = i.ReleasedBy.ToLowerInvariant();
            if (i.HoldUntilUtc.HasValue) e["HoldUntilDate"] = new DateTimeOffset(DateTime.SpecifyKind(i.HoldUntilUtc.Value, DateTimeKind.Utc));
            return e;
        }

        internal static DelegationInvitation Map(TableEntity e) => new()
        {
            InvitationId = e.GetString("InvitationId") ?? e.RowKey,
            HomeTenantId = e.GetString("HomeTenantId") ?? e.PartitionKey,
            Status = e.GetString("Status") ?? string.Empty,
            Role = e.GetString("Role") ?? Constants.DelegatedRoles.DelegatedReader,
            Source = e.GetString("Source") ?? Constants.DelegatedSource.CustomerDelegated,
            CreatedBy = e.GetString("CreatedBy") ?? string.Empty,
            CreatedAt = e.GetDateTimeOffset("CreatedDate")?.UtcDateTime ?? DateTime.MinValue,
            ExpiresAt = e.GetDateTimeOffset("ExpiresDate")?.UtcDateTime ?? DateTime.MinValue,
            AcceptedAt = e.GetDateTimeOffset("AcceptedDate")?.UtcDateTime,
            AcceptedBy = e.GetString("AcceptedBy"),
            TenantId = e.GetString("TenantId"),
            ReleasedAt = e.GetDateTimeOffset("ReleasedDate")?.UtcDateTime,
            ReleasedBy = e.GetString("ReleasedBy"),
            HoldUntilUtc = e.GetDateTimeOffset("HoldUntilDate")?.UtcDateTime,
            ETag = e.ETag.ToString(),
        };
    }
}

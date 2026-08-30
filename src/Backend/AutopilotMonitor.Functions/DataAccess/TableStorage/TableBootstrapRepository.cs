using System.Security.Cryptography;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of IBootstrapRepository.
    /// Uses a dual-partition scheme:
    /// - Main entity: PK=TenantId, RK=ShortCode (for tenant-scoped listing)
    /// - Lookup entity: PK="CodeLookup", RK=ShortCode (for anonymous code validation)
    /// </summary>
    public class TableBootstrapRepository : IBootstrapRepository
    {
        private readonly TableClient _tableClient;
        private readonly ILogger<TableBootstrapRepository> _logger;

        private const string CodeLookupPartition = "CodeLookup";

        public TableBootstrapRepository(
            TableStorageService storage,
            ILogger<TableBootstrapRepository> logger)
        {
            _logger = logger;
            _tableClient = storage.GetTableClient(Constants.TableNames.BootstrapSessions);
        }

        /// <summary>
        /// Creates a bootstrap session with both main + lookup entities.
        /// The lookup entity insertion acts as a uniqueness guard for the short code.
        /// </summary>
        public async Task<bool> CreateBootstrapSessionAsync(BootstrapSession session)
        {
            try
            {
                // Insert lookup entity first (acts as uniqueness guard for short code)
                var lookupEntity = new TableEntity(CodeLookupPartition, session.ShortCode)
                {
                    { "TenantId", session.TenantId },
                    { "Token", session.Token },
                    { "ExpiresAt", session.ExpiresAt },
                    { "IsRevoked", false }
                };
                await _tableClient.AddEntityAsync(lookupEntity);

                // Insert main entity
                var mainEntity = new TableEntity(session.TenantId, session.ShortCode)
                {
                    { "Token", session.Token },
                    { "CreatedAt", session.CreatedAt },
                    { "ExpiresAt", session.ExpiresAt },
                    { "CreatedByUpn", session.CreatedByUpn },
                    { "IsRevoked", false },
                    { "UsageCount", 0 },
                    { "Label", session.Label ?? "" }
                };
                await _tableClient.AddEntityAsync(mainEntity);

                _logger.LogInformation("Created bootstrap session {ShortCode} for tenant {TenantId}, expires {ExpiresAt}",
                    session.ShortCode, session.TenantId, session.ExpiresAt);

                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                // Short code collision
                _logger.LogWarning("Bootstrap short code collision: {ShortCode}", session.ShortCode);
                return false;
            }
        }

        /// <summary>
        /// Gets a bootstrap session by short code using the CodeLookup partition for O(1) lookup,
        /// then reads the main entity for full details.
        /// </summary>
        public async Task<BootstrapSession?> GetBootstrapSessionByCodeAsync(string shortCode)
        {
            try
            {
                // O(1) lookup via the CodeLookup partition
                var lookupResult = await _tableClient.GetEntityAsync<TableEntity>(CodeLookupPartition, shortCode);
                var lookup = lookupResult.Value;

                var isRevoked = lookup.GetBoolean("IsRevoked") ?? false;
                var expiresAt = lookup.GetDateTime("ExpiresAt") ?? DateTime.MinValue;

                if (isRevoked)
                {
                    _logger.LogWarning("Bootstrap code {ShortCode} has been revoked", shortCode);
                    return null;
                }

                if (expiresAt < DateTime.UtcNow)
                {
                    _logger.LogWarning("Bootstrap code {ShortCode} has expired (expired {ExpiresAt})", shortCode, expiresAt);
                    return null;
                }

                var tenantId = lookup.GetString("TenantId") ?? "";

                // Read main entity for full details
                var mainResult = await _tableClient.GetEntityAsync<TableEntity>(tenantId, shortCode);
                return MapFromEntity(mainResult.Value);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning("Bootstrap code {ShortCode} not found", shortCode);
                return null;
            }
        }

        /// <summary>
        /// Validates a bootstrap token by scanning the table for a matching Token property.
        /// Acceptable performance because BootstrapSessions table is small.
        /// </summary>
        public async Task<BootstrapSession?> ValidateBootstrapTokenAsync(string token)
        {
            var safeToken = ODataSanitizer.EscapeValue(token);
            var query = _tableClient.QueryAsync<TableEntity>(
                filter: $"Token eq '{safeToken}' and PartitionKey ne '{CodeLookupPartition}'",
                maxPerPage: 1);

            BootstrapSession? session = null;
            await foreach (var entity in query)
            {
                session = MapFromEntity(entity);
                break; // We only need the first match
            }

            if (session == null || session.IsRevoked || session.ExpiresAt < DateTime.UtcNow)
                return null;

            return session;
        }

        /// <summary>
        /// Lists bootstrap sessions for a tenant (includes active and recently expired/revoked).
        /// </summary>
        public async Task<List<BootstrapSession>> GetBootstrapSessionsAsync(string tenantId)
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            var sessions = new List<BootstrapSession>();

            var safeTenantId = ODataSanitizer.EscapeValue(tenantId);
            var query = _tableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{safeTenantId}'");

            await foreach (var entity in query)
            {
                var expiresAt = entity.GetDateTime("ExpiresAt") ?? DateTime.MinValue;
                var isRevoked = entity.GetBoolean("IsRevoked") ?? false;

                // Skip sessions that expired more than 24h ago and are not active
                if (expiresAt < cutoff && (isRevoked || expiresAt < DateTime.UtcNow))
                    continue;

                sessions.Add(MapFromEntity(entity));
            }

            return sessions.OrderByDescending(s => s.CreatedAt).ToList();
        }

        /// <summary>
        /// Revokes a bootstrap session. Resolves the owning tenant via the CodeLookup partition
        /// and refuses (returns false, same as not-found) when it differs from the caller's tenant:
        /// short codes are platform-global, so tenant scope must be checked against the object itself.
        /// </summary>
        public async Task<bool> RevokeBootstrapSessionAsync(string tenantId, string shortCode)
        {
            try
            {
                // Look up the owning tenant from the CodeLookup partition
                var lookupResult = await _tableClient.GetEntityAsync<TableEntity>(CodeLookupPartition, shortCode);
                var lookup = lookupResult.Value;
                var owningTenantId = lookup.GetString("TenantId") ?? "";

                if (!string.Equals(owningTenantId, tenantId, StringComparison.Ordinal))
                {
                    // Do not leak the owner; the caller sees the same 404 as for an unknown code.
                    _logger.LogWarning("Bootstrap session {ShortCode} revoke refused: code is not owned by tenant {TenantId}",
                        shortCode, tenantId);
                    return false;
                }

                // Update main entity
                var mainResult = await _tableClient.GetEntityAsync<TableEntity>(tenantId, shortCode);
                mainResult.Value["IsRevoked"] = true;
                await _tableClient.UpdateEntityAsync(mainResult.Value, mainResult.Value.ETag, TableUpdateMode.Merge);

                // Update lookup entity
                lookup["IsRevoked"] = true;
                await _tableClient.UpdateEntityAsync(lookup, lookup.ETag, TableUpdateMode.Merge);

                _logger.LogInformation("Revoked bootstrap session {ShortCode} for tenant {TenantId}", shortCode, tenantId);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning("Bootstrap session {ShortCode} not found during revoke", shortCode);
                return false;
            }
        }

        /// <summary>
        /// Increments usage count for a bootstrap session.
        /// </summary>
        public async Task<bool> IncrementBootstrapUsageAsync(string shortCode)
        {
            try
            {
                // Look up tenant from CodeLookup
                var lookupResult = await _tableClient.GetEntityAsync<TableEntity>(CodeLookupPartition, shortCode);
                var tenantId = lookupResult.Value.GetString("TenantId") ?? "";

                var mainResult = await _tableClient.GetEntityAsync<TableEntity>(tenantId, shortCode);
                var currentCount = mainResult.Value.GetInt32("UsageCount") ?? 0;
                mainResult.Value["UsageCount"] = currentCount + 1;
                await _tableClient.UpdateEntityAsync(mainResult.Value, mainResult.Value.ETag, TableUpdateMode.Merge);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to increment usage count for bootstrap code {ShortCode}", shortCode);
                return false;
            }
        }

        /// <summary>
        /// Deletes bootstrap sessions that expired more than 7 days ago.
        /// Returns the count of deleted entities.
        /// </summary>
        public async Task<int> CleanupExpiredAsync()
        {
            var cutoff = DateTime.UtcNow.AddDays(-7);
            var toDelete = new List<(string partitionKey, string rowKey, ETag etag)>();

            var query = _tableClient.QueryAsync<TableEntity>(
                filter: $"ExpiresAt lt datetime'{cutoff:yyyy-MM-ddTHH:mm:ssZ}'");

            await foreach (var entity in query)
            {
                toDelete.Add((entity.PartitionKey, entity.RowKey, entity.ETag));
            }

            foreach (var (partitionKey, rowKey, etag) in toDelete)
            {
                try
                {
                    await _tableClient.DeleteEntityAsync(partitionKey, rowKey, etag);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // Already deleted — ignore
                }
            }

            if (toDelete.Count > 0)
                _logger.LogInformation("Cleaned up {Count} expired bootstrap sessions", toDelete.Count);

            return toDelete.Count;
        }

        // --- Helpers ---

        private static BootstrapSession MapFromEntity(TableEntity entity)
        {
            return new BootstrapSession
            {
                TenantId = entity.PartitionKey,
                ShortCode = entity.RowKey,
                Token = entity.GetString("Token"),
                CreatedAt = entity.GetDateTime("CreatedAt") ?? DateTime.MinValue,
                ExpiresAt = entity.GetDateTime("ExpiresAt") ?? DateTime.MinValue,
                CreatedByUpn = entity.GetString("CreatedByUpn") ?? "",
                IsRevoked = entity.GetBoolean("IsRevoked") ?? false,
                UsageCount = entity.GetInt32("UsageCount") ?? 0,
                Label = entity.GetString("Label") ?? ""
            };
        }
    }
}

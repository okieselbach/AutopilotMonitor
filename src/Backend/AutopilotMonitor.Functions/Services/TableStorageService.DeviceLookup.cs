using Azure.Data.Tables;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Partial: device-keyed session lookups — serial / device name → sessions. Serial and device
    /// name are non-key columns, so every lookup here is a server-side filtered scan of ONE tenant
    /// partition with a narrow projection; they run on rare admin/portal paths (device block,
    /// progress-portal lookup), never on the agent hot path.
    /// </summary>
    public partial class TableStorageService
    {
        /// <summary>
        /// OData clause matching the value as the agent announced it (stored verbatim on the
        /// row, trimmed) and its upper-case form — Table Storage compares strings ordinally, and
        /// admins/users type serials and device names in either case.
        /// </summary>
        internal static string ExactOrUpperClause(string column, string value)
        {
            var trimmed = value.Trim();
            var upper = trimmed.ToUpperInvariant();
            var clause = $"{column} eq '{ODataSanitizer.EscapeValue(trimmed)}'";
            if (!string.Equals(upper, trimmed, StringComparison.Ordinal))
                clause = $"({clause} or {column} eq '{ODataSanitizer.EscapeValue(upper)}')";
            return clause;
        }

        public Task<string?> FindNewestSessionIdBySerialAsync(string tenantId, string serialNumber)
            => FindNewestSessionIdByColumnAsync(tenantId, "SerialNumber", serialNumber);

        public Task<string?> FindNewestSessionIdByDeviceNameAsync(string tenantId, string deviceName)
            => FindNewestSessionIdByColumnAsync(tenantId, "DeviceName", deviceName);

        /// <summary>
        /// SessionsIndex partition query on one always-projected column. The index RowKey is
        /// <c>{InvertedTicks(StartedAt)}_{sessionId}</c>, so the ordinally smallest RowKey among the
        /// matches is the newest session. Rows written before the column was projected are not
        /// matched by <c>eq</c> (same caveat as the other pushed-down search filters) — for the
        /// progress portal's "current enrollment" question that is immaterial.
        /// </summary>
        private async Task<string?> FindNewestSessionIdByColumnAsync(string tenantId, string column, string value)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            if (string.IsNullOrWhiteSpace(value))
                return null;

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionsIndex);
                var filter = $"PartitionKey eq '{tenantId}' and {ExactOrUpperClause(column, value)}";
                var select = new[] { "PartitionKey", "RowKey", "SessionId" };

                string? bestRowKey = null;
                string? bestSessionId = null;
                await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter: filter, select: select))
                {
                    if (bestRowKey != null && string.CompareOrdinal(entity.RowKey, bestRowKey) >= 0)
                        continue;
                    var sessionId = entity.GetString("SessionId");
                    if (string.IsNullOrEmpty(sessionId))
                    {
                        var sep = entity.RowKey.IndexOf('_');
                        sessionId = sep >= 0 ? entity.RowKey.Substring(sep + 1) : null;
                    }
                    if (string.IsNullOrEmpty(sessionId))
                        continue;
                    bestRowKey = entity.RowKey;
                    bestSessionId = sessionId;
                }
                return bestSessionId;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session lookup by {Column} failed in tenant {TenantId} (fail-soft: not found)", column, tenantId);
                return null;
            }
        }

        /// <summary>
        /// Certificate identities (<c>OwnerDeviceId</c>) the device with this serial has registered
        /// sessions under — newest session first, distinct, capped. Read from the primary Sessions
        /// row (the owner columns are deliberately never mirrored into SessionsIndex). Fail-soft:
        /// an empty list means "no alias", the serial-keyed block still applies.
        /// </summary>
        public async Task<IReadOnlyList<string>> GetOwnerDeviceIdsForSerialAsync(string tenantId, string serialNumber, int max = 5)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            if (string.IsNullOrWhiteSpace(serialNumber) || max <= 0)
                return Array.Empty<string>();

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                var filter = $"PartitionKey eq '{tenantId}' and {ExactOrUpperClause("SerialNumber", serialNumber)}";
                var select = new[] { "PartitionKey", "RowKey", "OwnerDeviceId", "StartedAt" };

                var seen = new List<(DateTimeOffset startedAt, string deviceId)>();
                await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter: filter, select: select))
                {
                    var deviceId = entity.GetString("OwnerDeviceId");
                    if (string.IsNullOrWhiteSpace(deviceId) || !Guid.TryParse(deviceId, out var parsed))
                        continue;
                    var startedAt = entity.GetDateTimeOffset("StartedAt") ?? DateTimeOffset.MinValue;
                    seen.Add((startedAt, parsed.ToString("D")));
                }

                return seen
                    .OrderByDescending(s => s.startedAt)
                    .Select(s => s.deviceId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(max)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Owner-device-id lookup failed for a serial in tenant {TenantId} (fail-soft: no alias)", tenantId);
                return Array.Empty<string>();
            }
        }
    }
}

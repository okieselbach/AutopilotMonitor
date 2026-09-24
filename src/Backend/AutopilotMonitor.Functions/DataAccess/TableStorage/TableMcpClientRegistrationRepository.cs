using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table-storage implementation of <see cref="IMcpClientRegistrationRepository"/>.
    /// PK = <see cref="PartitionKey"/> for every tenant, RK = registration id; the tenant view is a
    /// property filter, the MCP server's lookup a point read.
    /// </summary>
    public class TableMcpClientRegistrationRepository : IMcpClientRegistrationRepository
    {
        internal const string PartitionKey = "registrations";

        private readonly TableClient _table;
        private readonly ILogger<TableMcpClientRegistrationRepository> _logger;

        public TableMcpClientRegistrationRepository(TableStorageService storage, ILogger<TableMcpClientRegistrationRepository> logger)
        {
            _table = storage.GetTableClient(Constants.TableNames.McpClientRegistrations);
            _logger = logger;
        }

        public async Task<bool> CreateAsync(McpClientRegistration registration)
        {
            try
            {
                await _table.AddEntityAsync(Build(registration));
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to store MCP client registration {RegistrationId}", registration.RegistrationId);
                return false;
            }
        }

        public async Task<McpClientRegistration?> GetAsync(string registrationId)
        {
            try
            {
                var response = await _table.GetEntityIfExistsAsync<TableEntity>(PartitionKey, registrationId);
                return response.HasValue && response.Value != null ? Map(response.Value) : null;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        public async Task<List<McpClientRegistration>> GetForTenantAsync(string tenantId)
        {
            var results = new List<McpClientRegistration>();
            try
            {
                await foreach (var entity in _table.QueryAsync<TableEntity>(filter: BuildTenantFilter(tenantId)))
                    results.Add(Map(entity));
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogDebug("McpClientRegistrations table does not exist yet, returning empty list");
            }
            return results;
        }

        public async Task<bool> DeleteAsync(string registrationId)
        {
            try
            {
                await _table.DeleteEntityAsync(PartitionKey, registrationId, ETag.All);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
        }

        internal static string BuildTenantFilter(string tenantId)
            => $"PartitionKey eq '{PartitionKey}' and TenantId eq '{tenantId.ToLowerInvariant().Replace("'", "''")}'";

        /// <summary>Model → entity (every field in both directions; the offboarding wipe matches the lowercase TenantId).</summary>
        internal static TableEntity Build(McpClientRegistration r) => new(PartitionKey, r.RegistrationId)
        {
            ["RegistrationId"] = r.RegistrationId,
            ["TenantId"] = r.TenantId.ToLowerInvariant(),
            ["Name"] = r.Name,
            ["RedirectUri"] = r.RedirectUri,
            ["CreatedBy"] = r.CreatedBy,
            ["CreatedDate"] = new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)),
        };

        internal static McpClientRegistration Map(TableEntity e) => new()
        {
            RegistrationId = e.GetString("RegistrationId") ?? e.RowKey,
            TenantId = e.GetString("TenantId") ?? string.Empty,
            Name = e.GetString("Name") ?? string.Empty,
            RedirectUri = e.GetString("RedirectUri") ?? string.Empty,
            CreatedBy = e.GetString("CreatedBy") ?? string.Empty,
            CreatedAt = e.GetDateTimeOffset("CreatedDate")?.UtcDateTime ?? DateTime.MinValue,
        };
    }
}

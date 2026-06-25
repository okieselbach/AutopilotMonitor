using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of IAdminRepository.
    /// Performs direct CRUD against GlobalAdmins and TenantAdmins tables.
    /// </summary>
    public class TableAdminRepository : IAdminRepository
    {
        private readonly TableClient _globalAdminsTableClient;
        private readonly TableClient _tenantAdminsTableClient;
        private readonly TableClient _mcpUsersTableClient;
        private readonly TableClient _delegatedAdminsTableClient;
        private readonly ILogger<TableAdminRepository> _logger;

        public TableAdminRepository(
            TableStorageService storage,
            ILogger<TableAdminRepository> logger)
        {
            _logger = logger;
            _globalAdminsTableClient = storage.GetTableClient(Constants.TableNames.GlobalAdmins);
            _tenantAdminsTableClient = storage.GetTableClient(Constants.TableNames.TenantAdmins);
            _mcpUsersTableClient = storage.GetTableClient(Constants.TableNames.McpUsers);
            _delegatedAdminsTableClient = storage.GetTableClient(Constants.TableNames.DelegatedAdmins);
        }

        // --- Global Admins ---

        public async Task<bool> IsGlobalAdminAsync(string upn)
        {
            // Delegate to the role resolver so the GlobalAdmin/GlobalReader distinction is enforced at the
            // repository contract too: an enabled row with Role=GlobalReader is NOT a Global Admin. (Resolving
            // this only in GlobalAdminService would leave a direct repo caller as a silent privilege-escalation
            // footgun — a GlobalReader row reading back as "admin".)
            return await GetGlobalRoleAsync(upn) == Constants.GlobalRoles.GlobalAdmin;
        }

        public async Task<string?> GetGlobalRoleAsync(string upn)
        {
            if (string.IsNullOrWhiteSpace(upn))
                return null;

            try
            {
                var normalizedUpn = upn.ToLowerInvariant();
                var entity = await _globalAdminsTableClient.GetEntityAsync<GlobalAdminEntity>(
                    "GlobalAdmins", normalizedUpn);
                if (entity?.Value == null || !entity.Value.IsEnabled)
                    return null;

                // Empty/missing Role on an existing enabled row ⇒ GlobalAdmin (back-compat). An
                // unrecognized Role string is treated as no role (fail-closed) rather than silently
                // granting GlobalAdmin.
                var role = entity.Value.Role;
                if (string.IsNullOrWhiteSpace(role))
                    return Constants.GlobalRoles.GlobalAdmin;
                if (role == Constants.GlobalRoles.GlobalAdmin || role == Constants.GlobalRoles.GlobalReader)
                    return role;

                _logger.LogWarning("Unrecognized global Role '{Role}' for {Upn} — treating as no role", role, upn);
                return null;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving global role for {Upn}", upn);
                return null;
            }
        }

        public async Task<List<GlobalAdminEntry>> GetAllGlobalAdminsAsync()
        {
            var admins = new List<GlobalAdminEntry>();
            await foreach (var entity in _globalAdminsTableClient.QueryAsync<GlobalAdminEntity>(
                filter: $"PartitionKey eq 'GlobalAdmins'"))
            {
                admins.Add(new GlobalAdminEntry
                {
                    Upn = entity.Upn,
                    IsEnabled = entity.IsEnabled,
                    AddedAt = entity.AddedDate,
                    AddedBy = entity.AddedBy,
                    Role = string.IsNullOrWhiteSpace(entity.Role) ? Constants.GlobalRoles.GlobalAdmin : entity.Role
                });
            }
            return admins;
        }

        public async Task<bool> AddGlobalAdminAsync(string upn, string addedBy)
        {
            upn = upn.ToLowerInvariant();
            addedBy = addedBy.ToLowerInvariant();

            var entity = new GlobalAdminEntity
            {
                PartitionKey = "GlobalAdmins",
                RowKey = upn,
                Upn = upn,
                IsEnabled = true,
                AddedDate = DateTime.UtcNow,
                AddedBy = addedBy
            };

            await _globalAdminsTableClient.UpsertEntityAsync(entity);
            return true;
        }

        public async Task<bool> RemoveGlobalAdminAsync(string upn)
        {
            upn = upn.ToLowerInvariant();
            await _globalAdminsTableClient.DeleteEntityAsync("GlobalAdmins", upn);
            return true;
        }

        public async Task<bool> DisableGlobalAdminAsync(string upn)
        {
            upn = upn.ToLowerInvariant();

            try
            {
                var result = await _globalAdminsTableClient.GetEntityAsync<GlobalAdminEntity>(
                    "GlobalAdmins", upn);
                var entity = result.Value;
                if (entity != null)
                {
                    entity.IsEnabled = false;
                    await _globalAdminsTableClient.UpdateEntityAsync(entity, ETag.All);
                }
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
        }

        // --- MCP Users ---

        public async Task<bool> IsMcpUserAsync(string upn)
        {
            if (string.IsNullOrWhiteSpace(upn))
                return false;

            try
            {
                var normalizedUpn = upn.ToLowerInvariant();
                var entity = await _mcpUsersTableClient.GetEntityAsync<McpUserEntity>(
                    "McpUsers", normalizedUpn);
                return entity?.Value != null && entity.Value.IsEnabled;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking MCP user status for {Upn}", upn);
                return false;
            }
        }

        public async Task<McpUserEntry?> GetMcpUserAsync(string upn)
        {
            if (string.IsNullOrWhiteSpace(upn))
                return null;

            try
            {
                var normalizedUpn = upn.ToLowerInvariant();
                var result = await _mcpUsersTableClient.GetEntityAsync<McpUserEntity>(
                    "McpUsers", normalizedUpn);
                var entity = result.Value;
                if (entity == null) return null;

                return new McpUserEntry
                {
                    Upn = entity.Upn,
                    IsEnabled = entity.IsEnabled,
                    AddedAt = entity.AddedDate,
                    AddedBy = entity.AddedBy,
                    UsagePlan = entity.UsagePlan
                };
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        public async Task<List<McpUserEntry>> GetAllMcpUsersAsync()
        {
            var users = new List<McpUserEntry>();
            await foreach (var entity in _mcpUsersTableClient.QueryAsync<McpUserEntity>(
                filter: $"PartitionKey eq 'McpUsers'"))
            {
                users.Add(new McpUserEntry
                {
                    Upn = entity.Upn,
                    IsEnabled = entity.IsEnabled,
                    AddedAt = entity.AddedDate,
                    AddedBy = entity.AddedBy,
                    UsagePlan = entity.UsagePlan
                });
            }
            return users;
        }

        public async Task<bool> AddMcpUserAsync(string upn, string addedBy)
        {
            upn = upn.ToLowerInvariant();
            addedBy = addedBy.ToLowerInvariant();

            var entity = new McpUserEntity
            {
                PartitionKey = "McpUsers",
                RowKey = upn,
                Upn = upn,
                IsEnabled = true,
                AddedDate = DateTime.UtcNow,
                AddedBy = addedBy
            };

            await _mcpUsersTableClient.UpsertEntityAsync(entity);
            return true;
        }

        public async Task<bool> RemoveMcpUserAsync(string upn)
        {
            upn = upn.ToLowerInvariant();
            await _mcpUsersTableClient.DeleteEntityAsync("McpUsers", upn);
            return true;
        }

        public async Task<bool> SetMcpUserEnabledAsync(string upn, bool isEnabled)
        {
            upn = upn.ToLowerInvariant();

            try
            {
                var result = await _mcpUsersTableClient.GetEntityAsync<McpUserEntity>(
                    "McpUsers", upn);
                var entity = result.Value;
                if (entity != null)
                {
                    entity.IsEnabled = isEnabled;
                    await _mcpUsersTableClient.UpdateEntityAsync(entity, ETag.All);
                }
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
        }

        public async Task<bool> SetMcpUserUsagePlanAsync(string upn, string? usagePlan)
        {
            upn = upn.ToLowerInvariant();

            try
            {
                var result = await _mcpUsersTableClient.GetEntityAsync<McpUserEntity>(
                    "McpUsers", upn);
                var entity = result.Value;
                if (entity != null)
                {
                    entity.UsagePlan = usagePlan;
                    await _mcpUsersTableClient.UpdateEntityAsync(entity, ETag.All);
                }
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
        }

        // --- Delegated Admins (scoped-global / "MSP mode") ---

        public async Task<List<DelegatedAdminEntry>> GetAllDelegatedAdminsAsync()
        {
            // Full-table scan: the management UI lists every grant. The DelegatedAdmins table holds only
            // admin assignment rows (one per admin×managed-tenant), so this is small and off the hot path.
            var entries = new List<DelegatedAdminEntry>();
            await foreach (var entity in _delegatedAdminsTableClient.QueryAsync<DelegatedAdminEntity>())
            {
                entries.Add(MapToDelegatedEntry(entity));
            }
            return entries;
        }

        public async Task<List<DelegatedAdminEntry>> GetDelegatedTenantsAsync(string upn)
        {
            var entries = new List<DelegatedAdminEntry>();
            if (string.IsNullOrWhiteSpace(upn))
                return entries;

            var normalizedUpn = upn.ToLowerInvariant();
            // Typed predicate overload builds the OData filter safely (escapes quotes) — no string interpolation.
            await foreach (var entity in _delegatedAdminsTableClient.QueryAsync<DelegatedAdminEntity>(
                e => e.PartitionKey == normalizedUpn))
            {
                entries.Add(MapToDelegatedEntry(entity));
            }
            return entries;
        }

        public async Task<List<DelegatedAdminEntry>> GetDelegatedAssigneesAsync(string tenantId)
        {
            var entries = new List<DelegatedAdminEntry>();
            if (string.IsNullOrWhiteSpace(tenantId))
                return entries;

            // Cross-partition scan on RowKey — admin-UI path, not the hot auth path.
            // Typed predicate overload builds the OData filter safely (escapes quotes) — no string interpolation.
            var normalizedTenantId = tenantId.ToLowerInvariant();
            await foreach (var entity in _delegatedAdminsTableClient.QueryAsync<DelegatedAdminEntity>(
                e => e.RowKey == normalizedTenantId))
            {
                entries.Add(MapToDelegatedEntry(entity));
            }
            return entries;
        }

        public async Task<DelegatedAdminEntry?> GetDelegatedAdminAsync(string upn, string tenantId)
        {
            if (string.IsNullOrWhiteSpace(upn) || string.IsNullOrWhiteSpace(tenantId))
                return null;

            try
            {
                var result = await _delegatedAdminsTableClient.GetEntityAsync<DelegatedAdminEntity>(
                    upn.ToLowerInvariant(), tenantId.ToLowerInvariant());
                return result.Value == null ? null : MapToDelegatedEntry(result.Value);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        public async Task<bool> UpsertDelegatedAdminAsync(
            string upn, string tenantId, string role, string status, string source, string grantedBy)
        {
            upn = upn.ToLowerInvariant();
            tenantId = tenantId.ToLowerInvariant();
            grantedBy = grantedBy.ToLowerInvariant();

            var entity = new DelegatedAdminEntity
            {
                PartitionKey = upn,
                RowKey = tenantId,
                Upn = upn,
                TenantId = tenantId,
                Role = role,
                IsEnabled = true,
                Status = status,
                Source = source,
                GrantedDate = DateTime.UtcNow,
                GrantedBy = grantedBy
            };

            await _delegatedAdminsTableClient.UpsertEntityAsync(entity);
            return true;
        }

        public async Task<bool> SetDelegatedAdminStatusAsync(string upn, string tenantId, string status)
        {
            upn = upn.ToLowerInvariant();
            tenantId = tenantId.ToLowerInvariant();

            try
            {
                var result = await _delegatedAdminsTableClient.GetEntityAsync<DelegatedAdminEntity>(upn, tenantId);
                var entity = result.Value;
                if (entity == null) return false;

                entity.Status = status;
                await _delegatedAdminsTableClient.UpdateEntityAsync(entity, ETag.All);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
        }

        public async Task<bool> SetDelegatedAdminEnabledAsync(string upn, string tenantId, bool isEnabled)
        {
            upn = upn.ToLowerInvariant();
            tenantId = tenantId.ToLowerInvariant();

            try
            {
                var result = await _delegatedAdminsTableClient.GetEntityAsync<DelegatedAdminEntity>(upn, tenantId);
                var entity = result.Value;
                if (entity == null) return false;

                entity.IsEnabled = isEnabled;
                await _delegatedAdminsTableClient.UpdateEntityAsync(entity, ETag.All);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
        }

        public async Task<bool> RemoveDelegatedAdminAsync(string upn, string tenantId)
        {
            upn = upn.ToLowerInvariant();
            tenantId = tenantId.ToLowerInvariant();
            await _delegatedAdminsTableClient.DeleteEntityAsync(upn, tenantId);
            return true;
        }

        // --- Tenant Members ---

        public async Task<List<TenantMember>> GetTenantMembersAsync(string tenantId)
        {
            tenantId = tenantId.ToLowerInvariant();
            var members = new List<TenantMember>();

            await foreach (var entity in _tenantAdminsTableClient.QueryAsync<TenantAdminEntity>(
                filter: $"PartitionKey eq '{tenantId}'"))
            {
                members.Add(MapToTenantMember(entity));
            }

            return members;
        }

        public async Task<bool> AddTenantMemberAsync(string tenantId, string upn, string addedBy, string role, bool canManageBootstrapTokens = false)
        {
            tenantId = tenantId.ToLowerInvariant();
            upn = upn.ToLowerInvariant();
            addedBy = addedBy.ToLowerInvariant();

            var entity = new TenantAdminEntity
            {
                PartitionKey = tenantId,
                RowKey = upn,
                TenantId = tenantId,
                Upn = upn,
                IsEnabled = true,
                AddedDate = DateTime.UtcNow,
                AddedBy = addedBy,
                Role = role,
                CanManageBootstrapTokens = canManageBootstrapTokens
            };

            await _tenantAdminsTableClient.UpsertEntityAsync(entity);
            return true;
        }

        public async Task<bool> RemoveTenantMemberAsync(string tenantId, string upn)
        {
            tenantId = tenantId.ToLowerInvariant();
            upn = upn.ToLowerInvariant();
            await _tenantAdminsTableClient.DeleteEntityAsync(tenantId, upn);
            return true;
        }

        public async Task<bool> UpdateMemberPermissionsAsync(string tenantId, string upn, string role, bool canManageBootstrapTokens)
        {
            tenantId = tenantId.ToLowerInvariant();
            upn = upn.ToLowerInvariant();

            try
            {
                var result = await _tenantAdminsTableClient.GetEntityAsync<TenantAdminEntity>(tenantId, upn);
                var entity = result.Value;
                if (entity == null) return false;

                entity.Role = role;
                entity.CanManageBootstrapTokens = canManageBootstrapTokens;
                await _tenantAdminsTableClient.UpdateEntityAsync(entity, ETag.All);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
        }

        public async Task<bool> SetTenantMemberEnabledAsync(string tenantId, string upn, bool isEnabled)
        {
            tenantId = tenantId.ToLowerInvariant();
            upn = upn.ToLowerInvariant();

            try
            {
                var result = await _tenantAdminsTableClient.GetEntityAsync<TenantAdminEntity>(tenantId, upn);
                var entity = result.Value;
                if (entity == null) return false;

                entity.IsEnabled = isEnabled;
                await _tenantAdminsTableClient.UpdateEntityAsync(entity, ETag.All);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
        }

        public async Task<TenantMember?> GetTenantMemberAsync(string tenantId, string upn)
        {
            tenantId = tenantId.ToLowerInvariant();
            upn = upn.ToLowerInvariant();

            try
            {
                var result = await _tenantAdminsTableClient.GetEntityAsync<TenantAdminEntity>(tenantId, upn);
                var entity = result.Value;
                if (entity == null) return null;
                return MapToTenantMember(entity);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting tenant member {Upn} for tenant {TenantId}", upn, tenantId);
                return null;
            }
        }

        public async Task<bool> IsTenantAdminAsync(string tenantId, string upn)
        {
            var member = await GetTenantMemberAsync(tenantId, upn);
            if (member == null || !member.IsEnabled) return false;
            // Only true for Admin role (null Role = Admin for backward compat)
            return member.Role == null || member.Role == Constants.TenantRoles.Admin;
        }

        public async Task<bool> IsTenantMemberAsync(string tenantId, string upn)
        {
            var member = await GetTenantMemberAsync(tenantId, upn);
            if (member == null || !member.IsEnabled) return false;
            return member.Role != Constants.TenantRoles.Viewer;
        }

        // --- Helpers ---

        private static DelegatedAdminEntry MapToDelegatedEntry(DelegatedAdminEntity entity)
        {
            return new DelegatedAdminEntry
            {
                Upn = entity.Upn,
                TenantId = entity.TenantId,
                Role = entity.Role,
                IsEnabled = entity.IsEnabled,
                Status = entity.Status,
                Source = entity.Source,
                GrantedAt = entity.GrantedDate,
                GrantedBy = entity.GrantedBy
            };
        }

        private static TenantMember MapToTenantMember(TenantAdminEntity entity)
        {
            return new TenantMember
            {
                Upn = entity.Upn,
                TenantId = entity.TenantId,
                Role = entity.Role ?? Constants.TenantRoles.Admin,
                IsEnabled = entity.IsEnabled,
                CanManageBootstrapTokens = entity.CanManageBootstrapTokens,
                AddedAt = entity.AddedDate,
                AddedBy = entity.AddedBy
            };
        }

    }
}

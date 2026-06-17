using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Service for managing Tenant Admin permissions
/// Tenant Admins have full access to their specific tenant's configuration, sessions, and diagnostics
/// </summary>
public class TenantAdminsService
{
    private readonly IAdminRepository _adminRepo;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TenantAdminsService> _logger;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5);

    public TenantAdminsService(
        IAdminRepository adminRepo,
        IMemoryCache cache,
        ILogger<TenantAdminsService> logger)
    {
        _adminRepo = adminRepo;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Checks if a UPN is a Tenant Admin for a specific tenant
    /// Uses caching for performance
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="upn">User Principal Name (e.g., oliver@contoso.com)</param>
    /// <returns>True if the user is a Tenant Admin for this tenant</returns>
    public async Task<bool> IsTenantAdminAsync(string tenantId, string? upn)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(upn))
        {
            _logger.LogDebug("IsTenantAdminAsync: TenantId or UPN is null or empty");
            return false;
        }

        // Normalize for case-insensitive comparison
        tenantId = tenantId.ToLowerInvariant();
        upn = upn.ToLowerInvariant();

        // Check cache first
        var cacheKey = $"tenant-admin:{tenantId}:{upn}";
        if (_cache.TryGetValue<bool>(cacheKey, out var isAdmin))
        {
            _logger.LogDebug("Tenant Admin check (from cache): {Upn} @ {TenantId} -> {IsAdmin}", upn, tenantId, isAdmin);
            return isAdmin;
        }

        // Query via repository
        _logger.LogDebug("Querying repository for Tenant Admin: {Upn} @ {TenantId}", upn, tenantId);
        var member = await _adminRepo.GetTenantMemberAsync(tenantId, upn);
        // Only true for Admin role (null Role = Admin for backward compat)
        var result = member != null && member.IsEnabled
                     && (member.Role == null || member.Role == Constants.TenantRoles.Admin);

        _logger.LogDebug("Tenant Admin check result: {Upn} @ {TenantId} -> {Result}", upn, tenantId, result);

        _cache.Set(cacheKey, result, _cacheDuration);

        return result;
    }

    /// <summary>
    /// Adds a user as a Tenant Admin
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="upn">User Principal Name</param>
    /// <param name="addedBy">UPN of the admin who is adding this user</param>
    public virtual async Task<TenantAdminEntity> AddTenantAdminAsync(string tenantId, string upn, string addedBy)
    {
        tenantId = tenantId.ToLowerInvariant();
        upn = upn.ToLowerInvariant();
        addedBy = addedBy.ToLowerInvariant();

        await _adminRepo.AddTenantMemberAsync(tenantId, upn, addedBy, Constants.TenantRoles.Admin);

        InvalidateMemberCache(tenantId, upn);

        _logger.LogInformation($"Added Tenant Admin: {upn} to tenant {tenantId} by {addedBy}");

        return new TenantAdminEntity
        {
            PartitionKey = tenantId,
            RowKey = upn,
            TenantId = tenantId,
            Upn = upn,
            IsEnabled = true,
            AddedDate = DateTime.UtcNow,
            AddedBy = addedBy
        };
    }

    /// <summary>
    /// Removes a user from Tenant Admins
    /// </summary>
    public async Task RemoveTenantAdminAsync(string tenantId, string upn)
    {
        tenantId = tenantId.ToLowerInvariant();
        upn = upn.ToLowerInvariant();

        await _adminRepo.RemoveTenantMemberAsync(tenantId, upn);

        InvalidateMemberCache(tenantId, upn);

        _logger.LogInformation($"Removed Tenant Admin: {upn} from tenant {tenantId}");
    }

    /// <summary>
    /// Disables (but does not delete) a Tenant Admin
    /// </summary>
    public async Task DisableTenantAdminAsync(string tenantId, string upn)
    {
        tenantId = tenantId.ToLowerInvariant();
        upn = upn.ToLowerInvariant();

        await _adminRepo.SetTenantMemberEnabledAsync(tenantId, upn, false);

        InvalidateMemberCache(tenantId, upn);

        _logger.LogInformation($"Disabled Tenant Admin: {upn} for tenant {tenantId}");
    }

    /// <summary>
    /// Enables a Tenant Admin
    /// </summary>
    public async Task EnableTenantAdminAsync(string tenantId, string upn)
    {
        tenantId = tenantId.ToLowerInvariant();
        upn = upn.ToLowerInvariant();

        await _adminRepo.SetTenantMemberEnabledAsync(tenantId, upn, true);

        InvalidateMemberCache(tenantId, upn);

        _logger.LogInformation($"Enabled Tenant Admin: {upn} for tenant {tenantId}");
    }

    /// <summary>
    /// Gets all Tenant Admins for a specific tenant
    /// </summary>
    public virtual async Task<List<TenantAdminEntity>> GetTenantAdminsAsync(string tenantId)
    {
        SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

        tenantId = tenantId.ToLowerInvariant();

        var members = await _adminRepo.GetTenantMembersAsync(tenantId);

        _logger.LogInformation($"Retrieved {members.Count} Tenant Admins for tenant {tenantId}");

        return members.Select(m => new TenantAdminEntity
        {
            PartitionKey = m.TenantId,
            RowKey = m.Upn,
            TenantId = m.TenantId,
            Upn = m.Upn,
            IsEnabled = m.IsEnabled,
            AddedDate = m.AddedAt,
            AddedBy = m.AddedBy,
            Role = m.Role,
            CanManageBootstrapTokens = m.CanManageBootstrapTokens
        }).ToList();
    }

    /// <summary>
    /// Clears the cache for a specific tenant's admins
    /// Useful after bulk updates
    /// </summary>
    public void ClearCacheForTenant(string tenantId)
    {
        // Note: IMemoryCache doesn't have a clear all method by default
        // In production, consider using a distributed cache with better cache invalidation
        // For now, cache entries will expire after _cacheDuration
    }

    // -----------------------------------------------------------------------
    // Role-aware methods (Admin / Operator / Viewer)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Gets the role info for a tenant member. Returns null if the user has no enabled row
    /// (either no row at all or a disabled row). For Entra app-role reconciliation use
    /// <see cref="GetTableMembershipAsync"/>, which distinguishes "no row" from "disabled row".
    /// </summary>
    public virtual async Task<MemberRoleInfo?> GetMemberRoleAsync(string tenantId, string? upn)
    {
        var (state, role) = await GetTableMembershipAsync(tenantId, upn);
        return state == TableMemberState.Enabled ? role : null;
    }

    /// <summary>
    /// Gets the tri-state table membership for a user: whether a TenantAdmins row is absent,
    /// disabled, or enabled (with its role). The disabled/absent distinction is required by the
    /// Entra app-role resolver so a disabled row stays an explicit deny and is never silently
    /// re-authorized through an app-role claim.
    /// </summary>
    public virtual async Task<(TableMemberState State, MemberRoleInfo? Role)> GetTableMembershipAsync(string tenantId, string? upn)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(upn))
            return (TableMemberState.NotPresent, null);

        tenantId = tenantId.ToLowerInvariant();
        upn = upn.ToLowerInvariant();

        var cacheKey = $"tenant-member-state:{tenantId}:{upn}";
        if (_cache.TryGetValue<(TableMemberState, MemberRoleInfo?)>(cacheKey, out var cached))
            return cached;

        var member = await _adminRepo.GetTenantMemberAsync(tenantId, upn);

        (TableMemberState State, MemberRoleInfo? Role) result;
        if (member == null)
            result = (TableMemberState.NotPresent, null);
        else if (!member.IsEnabled)
            result = (TableMemberState.Disabled, null);
        else
            result = (TableMemberState.Enabled, new MemberRoleInfo
            {
                Role = member.Role ?? Constants.TenantRoles.Admin,
                CanManageBootstrapTokens = member.CanManageBootstrapTokens
            });

        _cache.Set(cacheKey, result, _cacheDuration);
        return result;
    }

    /// <summary>
    /// Checks if a user is a tenant member (Admin or Operator) and enabled.
    /// </summary>
    public async Task<bool> IsTenantMemberAsync(string tenantId, string? upn)
    {
        var role = await GetMemberRoleAsync(tenantId, upn);
        return role != null && role.Role != Constants.TenantRoles.Viewer;
    }

    /// <summary>
    /// Checks if a user can manage bootstrap tokens (Admin, or Operator with CanManageBootstrapTokens).
    /// </summary>
    public async Task<bool> CanManageBootstrapAsync(string tenantId, string? upn)
    {
        var role = await GetMemberRoleAsync(tenantId, upn);
        if (role == null) return false;
        if (role.Role == Constants.TenantRoles.Admin) return true;
        return role.Role == Constants.TenantRoles.Operator && role.CanManageBootstrapTokens;
    }

    /// <summary>
    /// Adds a tenant member with a specific role.
    /// </summary>
    public async Task<TenantAdminEntity> AddTenantMemberAsync(string tenantId, string upn, string addedBy, string role, bool canManageBootstrapTokens = false)
    {
        tenantId = tenantId.ToLowerInvariant();
        upn = upn.ToLowerInvariant();
        addedBy = addedBy.ToLowerInvariant();

        await _adminRepo.AddTenantMemberAsync(tenantId, upn, addedBy, role, canManageBootstrapTokens);

        InvalidateMemberCache(tenantId, upn);

        _logger.LogInformation("Added tenant member: {Upn} with role {Role} to tenant {TenantId} by {AddedBy}", upn, role, tenantId, addedBy);

        return new TenantAdminEntity
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
    }

    /// <summary>
    /// Updates role and permissions for an existing tenant member.
    /// </summary>
    public async Task<bool> UpdateMemberPermissionsAsync(string tenantId, string upn, string role, bool canManageBootstrapTokens)
    {
        tenantId = tenantId.ToLowerInvariant();
        upn = upn.ToLowerInvariant();

        var result = await _adminRepo.UpdateMemberPermissionsAsync(tenantId, upn, role, canManageBootstrapTokens);

        if (result)
        {
            InvalidateMemberCache(tenantId, upn);
            _logger.LogInformation("Updated member permissions: {Upn} -> role={Role}, canManageBootstrap={CanManageBootstrap} in tenant {TenantId}", upn, role, canManageBootstrapTokens, tenantId);
        }

        return result;
    }

    /// <summary>
    /// Invalidates both admin and member cache entries for a user.
    /// </summary>
    private void InvalidateMemberCache(string tenantId, string upn)
    {
        _cache.Remove($"tenant-admin:{tenantId}:{upn}");
        _cache.Remove($"tenant-member:{tenantId}:{upn}");
        _cache.Remove($"tenant-member-state:{tenantId}:{upn}");
    }
}

/// <summary>
/// Entity representing a tenant member (Admin, Operator, or Viewer) in Table Storage.
/// Stored in the TenantAdmins table for backward compatibility.
/// </summary>
public class TenantAdminEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty; // TenantId (lowercase)
    public string RowKey { get; set; } = string.Empty; // UPN in lowercase
    public DateTimeOffset? Timestamp { get; set; }
    public Azure.ETag ETag { get; set; }

    /// <summary>
    /// Tenant ID (lowercase)
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// User Principal Name (lowercase)
    /// </summary>
    public string Upn { get; set; } = string.Empty;

    /// <summary>
    /// Whether this member is currently enabled
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// When this member was added
    /// </summary>
    public DateTime AddedDate { get; set; }

    /// <summary>
    /// UPN of the admin who added this user
    /// </summary>
    public string AddedBy { get; set; } = string.Empty;

    /// <summary>
    /// Role: "Admin", "Operator", "Viewer".
    /// Null is treated as "Admin" for backward compatibility with existing entities.
    /// </summary>
    public string? Role { get; set; }

    /// <summary>
    /// Whether this Operator can manage bootstrap tokens.
    /// Only relevant for the Operator role.
    /// </summary>
    public bool CanManageBootstrapTokens { get; set; }
}

/// <summary>
/// Role information for a tenant member.
/// </summary>
public class MemberRoleInfo
{
    public string Role { get; set; } = Constants.TenantRoles.Admin;
    public bool CanManageBootstrapTokens { get; set; }
}

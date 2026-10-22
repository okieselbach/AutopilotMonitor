using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
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
    // Authorization state is cached per-process (IMemoryCache). On a scaled-out Flex Consumption
    // plan InvalidateMemberCache only clears the instance that handled the grant/revoke mutation;
    // other warm instances keep serving the stale role until their entry expires. A short TTL caps
    // that cross-instance divergence window so a revoked or changed role self-heals in seconds
    // instead of minutes. Only an ENABLED membership is cached: a missing or disabled row is re-read
    // on every call, because the grant that ends it may run where no invalidation reaches this
    // process (activation promotes the onboarding member on the queue worker) and a cached "no"
    // answered that new admin as a roleless user. The underlying lookup is a single Table Storage
    // point-read (PK+RK), so the extra reads are negligible. Do NOT raise this back to minutes
    // "for performance" — it reintroduces the role flip-flop. Matches the 30s cross-instance
    // revalidation convention used by BlockedDeviceService.
    private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(30);

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
    /// The shorter constructors stay for the tests that mock the service with positional arguments
    /// (Moq does not fill optional parameters); the container picks the longest satisfiable one.
    /// </summary>
    public TenantAdminsService(
        IAdminRepository adminRepo,
        IMemoryCache cache,
        ILogger<TenantAdminsService> logger,
        Push.IPushDeviceRevoker pushRevoker)
        : this(adminRepo, cache, logger)
    {
        _pushRevoker = pushRevoker;
    }

    /// <summary>The container's pick: the audit repository records every first-admin seat in the tenant's audit log.</summary>
    public TenantAdminsService(
        IAdminRepository adminRepo,
        IMemoryCache cache,
        ILogger<TenantAdminsService> logger,
        Push.IPushDeviceRevoker pushRevoker,
        IMaintenanceRepository auditRepo)
        : this(adminRepo, cache, logger, pushRevoker)
    {
        _auditRepo = auditRepo;
    }

    private readonly Push.IPushDeviceRevoker? _pushRevoker;
    private readonly IMaintenanceRepository? _auditRepo;

    /// <summary>
    /// Role-loss hook (K6): a member who is removed, disabled or demoted below Operator loses the
    /// paired push devices of this tenant — wipe push first, then the rows. Fail-soft.
    /// </summary>
    private Task RevokePushDevicesAsync(string tenantId, string upn, string reason)
        => _pushRevoker?.RevokeOwnerAsync(Notifications.NotificationScope.Tenant(tenantId), upn, reason) ?? Task.CompletedTask;

    /// <summary>
    /// Seats a user as Tenant Admin unless the tenant already holds a row for them: true when the row was
    /// created, false when one exists (left untouched, whatever it says). The two first-admin writers,
    /// activation seating the onboarding member and auth/me's first-user auto-admin, go through here, so
    /// neither can overwrite a row the tenant set (disabled, demoted) nor race the other for the same UPN.
    /// </summary>
    public virtual async Task<bool> TryAddTenantAdminAsync(string tenantId, string upn, string addedBy)
    {
        tenantId = tenantId.ToLowerInvariant();
        upn = upn.ToLowerInvariant();
        addedBy = addedBy.ToLowerInvariant();

        if (!await _adminRepo.TryAddTenantMemberAsync(tenantId, upn, addedBy, Constants.TenantRoles.Admin))
            return false;

        InvalidateMemberCache(tenantId, upn);

        _logger.LogInformation("Added Tenant Admin: {Upn} to tenant {TenantId} by {AddedBy}", upn, tenantId, addedBy);
        await RecordSeatAsync(tenantId, upn, addedBy);
        return true;
    }

    /// <summary>
    /// The tenant's audit log is where its admins review membership changes, so a seat written by
    /// activation or by a first sign-in shows up there like one written by an admin. Fail-soft: the
    /// row is already written, and a lost audit line must not fail a sign-in.
    /// </summary>
    private async Task RecordSeatAsync(string tenantId, string upn, string addedBy)
    {
        if (_auditRepo == null)
            return;

        try
        {
            await _auditRepo.LogAuditEntryAsync(tenantId, "CREATE", "TenantAdmin", upn, addedBy,
                new Dictionary<string, string>
                {
                    { "Role", Constants.TenantRoles.Admin },
                    { "CanManageBootstrapTokens", "False" },
                    { "PrincipalType", "User" },
                    { "Source", "first-admin seat" }
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audit entry for the first-admin seat of {Upn} in tenant {TenantId} was not written", upn, tenantId);
        }
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
        await RevokePushDevicesAsync(tenantId, upn, "member_removed");

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
        await RevokePushDevicesAsync(tenantId, upn, "member_disabled");

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
    public virtual async Task<List<TenantAdminRow>> GetTenantAdminsAsync(string tenantId)
    {
        SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

        tenantId = tenantId.ToLowerInvariant();

        var members = await _adminRepo.GetTenantMembersAsync(tenantId);

        _logger.LogInformation($"Retrieved {members.Count} Tenant Admins for tenant {tenantId}");

        return members.Select(m => new TenantAdminRow
        {
            TenantId = m.TenantId,
            Upn = m.Upn,
            IsEnabled = m.IsEnabled,
            AddedDate = m.AddedAt,
            AddedBy = m.AddedBy,
            Role = m.Role,
            CanManageBootstrapTokens = m.CanManageBootstrapTokens
        }).ToList();
    }

    // -----------------------------------------------------------------------
    // Role-aware methods (Admin / Operator / Viewer)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Gets the tri-state table membership for a user: whether a TenantAdmins row is absent,
    /// disabled, or enabled (with its role). The disabled/absent distinction is required by the
    /// Entra app-role resolver so a disabled row stays an explicit deny and is never silently
    /// re-authorized through an app-role claim. Only the enabled state is cached (see _cacheDuration).
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

        var result = ToMembership(await _adminRepo.GetTenantMemberAsync(tenantId, upn));
        if (result.State == TableMemberState.Enabled)
            _cache.Set(cacheKey, result, _cacheDuration);
        return result;
    }

    /// <summary>
    /// The caller's table membership and whether the tenant has any member row at all (disabled and
    /// application rows included), both from ONE uncached partition read. auth/me decides the first-user
    /// auto-admin from these two facts; read apart they disagree right after activation promotes the
    /// onboarding member, and the new admin is answered as a roleless user. Never cache this: a cached
    /// "no members" would make the next person who signs in the tenant's admin.
    /// </summary>
    public virtual async Task<(TableMemberState State, MemberRoleInfo? Role, bool TenantHasMembers)> GetMembershipSnapshotAsync(
        string tenantId, string upn)
    {
        SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

        var members = await _adminRepo.GetTenantMembersAsync(tenantId);
        var (state, role) = ToMembership(
            members.FirstOrDefault(m => string.Equals(m.Upn, upn, StringComparison.OrdinalIgnoreCase)));
        return (state, role, members.Count > 0);
    }

    private static (TableMemberState State, MemberRoleInfo? Role) ToMembership(TenantMember? member)
    {
        if (member == null)
            return (TableMemberState.NotPresent, null);
        if (!member.IsEnabled)
            return (TableMemberState.Disabled, null);
        return (TableMemberState.Enabled, new MemberRoleInfo
        {
            Role = member.Role ?? Constants.TenantRoles.Admin,
            CanManageBootstrapTokens = member.CanManageBootstrapTokens
        });
    }

    /// <summary>
    /// Adds a tenant member with a specific role.
    /// </summary>
    public async Task<TenantAdminRow> AddTenantMemberAsync(string tenantId, string upn, string addedBy, string role, bool canManageBootstrapTokens = false)
    {
        tenantId = tenantId.ToLowerInvariant();
        upn = upn.ToLowerInvariant();
        addedBy = addedBy.ToLowerInvariant();

        await _adminRepo.AddTenantMemberAsync(tenantId, upn, addedBy, role, canManageBootstrapTokens);

        InvalidateMemberCache(tenantId, upn);

        _logger.LogInformation("Added tenant member: {Upn} with role {Role} to tenant {TenantId} by {AddedBy}", upn, role, tenantId, addedBy);

        return new TenantAdminRow
        {
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
            if (role != Constants.TenantRoles.Admin && role != Constants.TenantRoles.Operator)
                await RevokePushDevicesAsync(tenantId, upn, "role_lost");
            _logger.LogInformation("Updated member permissions: {Upn} -> role={Role}, canManageBootstrap={CanManageBootstrap} in tenant {TenantId}", upn, role, canManageBootstrapTokens, tenantId);
        }

        return result;
    }

    /// <summary>
    /// Drops this process's cached membership of a user (other instances expire on their own).
    /// </summary>
    private void InvalidateMemberCache(string tenantId, string upn)
    {
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

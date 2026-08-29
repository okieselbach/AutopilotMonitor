using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Service for managing Global Admin permissions
/// Global Admins can access cross-tenant data and perform platform-wide operations.
/// <para>
/// Role RESOLUTION is keyed on the caller's full <see cref="AdminIdentity"/>, never on the UPN string alone:
/// the GlobalAdmins row is looked up by UPN, but it confers the role only when the caller's validated JWT
/// tid + oid match the UPN's <see cref="AdminIdentityBinding"/> (see <see cref="AdminIdentityBindingService"/>).
/// The API accepts tokens from any Entra tenant, and upn/preferred_username are mutable — a foreign-tenant
/// token with a matching UPN must resolve to no role.
/// </para>
/// </summary>
public class GlobalAdminService
{
    private readonly IAdminRepository _adminRepo;
    private readonly AdminIdentityBindingService _bindings;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GlobalAdminService> _logger;
    // Per-process cache: on scaled-out Flex Consumption, the _cache.Remove on add/remove/disable
    // only clears the mutating instance, so other instances serve a stale global role until expiry.
    // A short TTL caps that cross-instance window so a granted/revoked GlobalAdmin or GlobalReader
    // role self-heals in seconds. The lookup is a single Table Storage point-read. Do NOT raise this
    // back to minutes "for performance" — it reintroduces the role flip-flop (see TenantAdminsService).
    private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(30);

    // Sentinel stored in the role cache to represent "no global role" (row missing or disabled).
    // Lets us distinguish a cached negative from a cache miss without nullable boxing games.
    private const string NoRoleSentinel = "(none)";

    public GlobalAdminService(
        IAdminRepository adminRepo,
        AdminIdentityBindingService bindings,
        IMemoryCache cache,
        ILogger<GlobalAdminService> logger)
    {
        _adminRepo = adminRepo;
        _bindings = bindings;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Checks whether the caller is a Global Admin (the GlobalAdmin platform role, identity-bound).
    /// </summary>
    /// <param name="identity">The caller's validated identity; null (missing upn/tid/oid) ⇒ false.</param>
    public virtual async Task<bool> IsGlobalAdminAsync(AdminIdentity? identity)
    {
        // Single source of truth: GlobalAdmin == the GlobalAdmin platform role.
        return await GetGlobalRoleAsync(identity) == Constants.GlobalRoles.GlobalAdmin;
    }

    /// <summary>
    /// Resolves the caller's platform role: <see cref="Constants.GlobalRoles.GlobalAdmin"/>,
    /// <see cref="Constants.GlobalRoles.GlobalReader"/>, or <c>null</c> when the UPN has no enabled
    /// GlobalAdmins row OR the caller's tid/oid do not match the UPN's identity binding (fail-closed —
    /// an unbound row is inert). The row lookup is cached briefly (see _cacheDuration); the binding check
    /// runs only for UPNs that actually hold a row, so ordinary tenant users cost one cached read.
    /// </summary>
    public virtual async Task<string?> GetGlobalRoleAsync(AdminIdentity? identity)
    {
        if (identity == null)
        {
            _logger.LogDebug("GetGlobalRoleAsync: incomplete caller identity (upn/tid/oid)");
            return null;
        }

        var role = await GetRowRoleAsync(identity.Upn);
        if (role == null)
            return null;

        return await _bindings.IsBoundAsync(identity) ? role : null;
    }

    /// <summary>The role the GlobalAdmins ROW carries for a UPN, ignoring identity binding. Cached briefly.</summary>
    private async Task<string?> GetRowRoleAsync(string upn)
    {
        var cacheKey = $"global-role:{upn}";
        if (_cache.TryGetValue<string>(cacheKey, out var cached) && cached != null)
        {
            _logger.LogDebug("Global role check (from cache): {Upn} -> {Role}", upn, cached);
            return cached == NoRoleSentinel ? null : cached;
        }

        _logger.LogDebug("Querying repository for global role: {Upn}", upn);
        var role = await _adminRepo.GetGlobalRoleAsync(upn);

        _logger.LogDebug("Global role check result: {Upn} -> {Role}", upn, role ?? "(none)");

        _cache.Set(cacheKey, role ?? NoRoleSentinel, _cacheDuration);

        return role;
    }

    /// <summary>
    /// Adds a user as a Global Admin, binding the UPN to its home tenant (and object id, when known) FIRST —
    /// a role row without a binding is inert, and a binding conflict (UPN already homed elsewhere) aborts
    /// before any row is written.
    /// </summary>
    /// <param name="upn">User Principal Name</param>
    /// <param name="addedBy">UPN of the admin who is adding this user</param>
    /// <param name="homeTenantId">The Entra tenant the person signs in from (JWT tid).</param>
    /// <param name="objectId">The person's Entra object id, or null to pin it on their first sign-in.</param>
    /// <exception cref="IdentityBindingConflictException">The UPN is already bound to a different identity.</exception>
    public async Task<GlobalAdminEntity> AddGlobalAdminAsync(string upn, string addedBy, string homeTenantId, string? objectId)
    {
        upn = upn.ToLowerInvariant();
        addedBy = addedBy.ToLowerInvariant();

        await _bindings.EnsureBoundAsync(upn, homeTenantId, objectId, addedBy);
        await _adminRepo.AddGlobalAdminAsync(upn, addedBy);

        // Invalidate cache
        _cache.Remove($"global-role:{upn}");

        return new GlobalAdminEntity
        {
            PartitionKey = "GlobalAdmins",
            RowKey = upn,
            Upn = upn,
            IsEnabled = true,
            AddedDate = DateTime.UtcNow,
            AddedBy = addedBy,
            Role = Constants.GlobalRoles.GlobalAdmin
        };
    }

    /// <summary>
    /// Removes a user from Global Admins
    /// </summary>
    public async Task RemoveGlobalAdminAsync(string upn)
    {
        upn = upn.ToLowerInvariant();

        await _adminRepo.RemoveGlobalAdminAsync(upn);

        // Invalidate cache
        _cache.Remove($"global-role:{upn}");
    }

    /// <summary>
    /// Disables (but does not delete) a Global Admin
    /// </summary>
    public async Task DisableGlobalAdminAsync(string upn)
    {
        upn = upn.ToLowerInvariant();

        await _adminRepo.DisableGlobalAdminAsync(upn);

        // Invalidate cache
        _cache.Remove($"global-role:{upn}");
    }

    /// <summary>
    /// Gets all Global Admins
    /// </summary>
    public async Task<List<GlobalAdminEntity>> GetAllGlobalAdminsAsync()
    {
        var entries = await _adminRepo.GetAllGlobalAdminsAsync();

        return entries.Select(e => new GlobalAdminEntity
        {
            PartitionKey = "GlobalAdmins",
            RowKey = e.Upn,
            Upn = e.Upn,
            IsEnabled = e.IsEnabled,
            AddedDate = e.AddedAt,
            AddedBy = e.AddedBy,
            Role = string.IsNullOrEmpty(e.Role) ? Constants.GlobalRoles.GlobalAdmin : e.Role
        }).ToList();
    }

    /// <summary>
    /// Clears the cache for all Global Admins
    /// Useful after bulk updates
    /// </summary>
    public void ClearCache()
    {
        // Note: IMemoryCache doesn't have a clear all method by default
        // In production, consider using a distributed cache with better cache invalidation
        // For now, cache entries will expire after _cacheDuration
    }
}

/// <summary>
/// Entity representing a Global Admin in Table Storage
/// </summary>
public class GlobalAdminEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "GlobalAdmins";
    public string RowKey { get; set; } = string.Empty; // UPN in lowercase
    public DateTimeOffset? Timestamp { get; set; }
    public Azure.ETag ETag { get; set; }

    /// <summary>
    /// User Principal Name (lowercase)
    /// </summary>
    public string Upn { get; set; } = string.Empty;

    /// <summary>
    /// Whether this admin is currently enabled
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// When this admin was added
    /// </summary>
    public DateTime AddedDate { get; set; }

    /// <summary>
    /// UPN of the admin who added this user
    /// </summary>
    public string AddedBy { get; set; } = string.Empty;

    /// <summary>
    /// Platform role for this entry: <see cref="Constants.GlobalRoles.GlobalAdmin"/> (default) or
    /// <see cref="Constants.GlobalRoles.GlobalReader"/>. Empty/missing ⇒ GlobalAdmin (back-compat with
    /// rows created before the GlobalReader tier existed).
    /// </summary>
    public string Role { get; set; } = string.Empty;
}

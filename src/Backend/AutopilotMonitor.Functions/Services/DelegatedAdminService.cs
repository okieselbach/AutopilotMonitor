using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Resolves and manages <b>delegated admin</b> assignments — the "scoped global" tier between a
/// single-tenant member and a platform GlobalAdmin. A delegated admin may read (and later write) a
/// SUBSET of tenants: exactly the tenants for which it holds an Active, enabled DelegatedAdmins row.
/// Externally this is surfaced as "MSP mode".
///
/// The scope is keyed on UPN and is <b>tid-agnostic</b> — identical to <see cref="GlobalAdminService"/>:
/// the caller signs into their own home tenant, and their cross-tenant reach is resolved from this table
/// regardless of the JWT's tid. Resolution is cached for 5 minutes; every mutation invalidates the cache.
/// </summary>
public class DelegatedAdminService
{
    private readonly IAdminRepository _adminRepo;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DelegatedAdminService> _logger;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5);

    public DelegatedAdminService(
        IAdminRepository adminRepo,
        IMemoryCache cache,
        ILogger<DelegatedAdminService> logger)
    {
        _adminRepo = adminRepo;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the caller's effective delegated scope: the set of tenants it may access and the role per
    /// tenant. Only <see cref="Constants.DelegatedStatus.Active"/> + enabled rows with a recognized role
    /// contribute; pending/revoked/disabled/unknown-role rows are ignored (fail-closed). Cached 5 minutes.
    /// Returns an empty (never null) scope for a UPN with no effective assignments.
    /// </summary>
    public virtual async Task<DelegatedScope> GetScopeAsync(string? upn)
    {
        if (string.IsNullOrWhiteSpace(upn))
            return DelegatedScope.Empty;

        upn = upn.ToLowerInvariant();
        var cacheKey = $"delegated-scope:{upn}";
        if (_cache.TryGetValue<DelegatedScope>(cacheKey, out var cached) && cached != null)
            return cached;

        var rows = await _adminRepo.GetDelegatedTenantsAsync(upn);
        var tenantRoles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!row.IsEnabled || row.Status != Constants.DelegatedStatus.Active)
                continue;

            var role = NormalizeRole(row.Role, upn, row.TenantId);
            if (role == null)
                continue;

            // A duplicate (admin, tenant) RowKey is impossible in Table Storage, but if two rows ever
            // collide on tenantId casing, the stronger role wins (DelegatedAdmin > DelegatedReader).
            if (tenantRoles.TryGetValue(row.TenantId, out var existing) && IsStronger(existing, role))
                continue;
            tenantRoles[row.TenantId] = role;
        }

        var scope = new DelegatedScope(tenantRoles);
        _cache.Set(cacheKey, scope, _cacheDuration);
        return scope;
    }

    /// <summary>Creates or replaces an assignment, then invalidates the UPN's cached scope.</summary>
    public async Task<DelegatedAdminEntry> UpsertAsync(
        string upn, string tenantId, string role, string status, string source, string grantedBy)
    {
        upn = upn.ToLowerInvariant();
        tenantId = tenantId.ToLowerInvariant();
        grantedBy = grantedBy.ToLowerInvariant();

        await _adminRepo.UpsertDelegatedAdminAsync(upn, tenantId, role, status, source, grantedBy);
        Invalidate(upn);

        return new DelegatedAdminEntry
        {
            Upn = upn,
            TenantId = tenantId,
            Role = role,
            IsEnabled = true,
            Status = status,
            Source = source,
            GrantedAt = DateTime.UtcNow,
            GrantedBy = grantedBy
        };
    }

    /// <summary>Transitions an assignment's status (e.g. PendingApproval → Active on approval, → Revoked).</summary>
    public async Task<bool> SetStatusAsync(string upn, string tenantId, string status)
    {
        upn = upn.ToLowerInvariant();
        tenantId = tenantId.ToLowerInvariant();
        var ok = await _adminRepo.SetDelegatedAdminStatusAsync(upn, tenantId, status);
        Invalidate(upn);
        return ok;
    }

    public async Task<bool> SetEnabledAsync(string upn, string tenantId, bool isEnabled)
    {
        upn = upn.ToLowerInvariant();
        tenantId = tenantId.ToLowerInvariant();
        var ok = await _adminRepo.SetDelegatedAdminEnabledAsync(upn, tenantId, isEnabled);
        Invalidate(upn);
        return ok;
    }

    public async Task<bool> RemoveAsync(string upn, string tenantId)
    {
        upn = upn.ToLowerInvariant();
        tenantId = tenantId.ToLowerInvariant();
        var ok = await _adminRepo.RemoveDelegatedAdminAsync(upn, tenantId);
        Invalidate(upn);
        return ok;
    }

    /// <summary>All assignment rows for a UPN (any status) — for the operator/admin management UI.</summary>
    public Task<List<DelegatedAdminEntry>> GetAssignmentsForUpnAsync(string upn)
        => _adminRepo.GetDelegatedTenantsAsync(upn.ToLowerInvariant());

    /// <summary>All assignment rows targeting a tenant (any status) — for the customer "who manages me?" UI.</summary>
    public Task<List<DelegatedAdminEntry>> GetAssigneesForTenantAsync(string tenantId)
        => _adminRepo.GetDelegatedAssigneesAsync(tenantId.ToLowerInvariant());

    private void Invalidate(string upn) => _cache.Remove($"delegated-scope:{upn.ToLowerInvariant()}");

    /// <summary>Empty/missing role defaults to the least-privileged DelegatedReader; an unrecognized
    /// role is dropped (fail-closed) rather than silently granting access.</summary>
    private string? NormalizeRole(string role, string upn, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(role))
            return Constants.DelegatedRoles.DelegatedReader;
        if (role == Constants.DelegatedRoles.DelegatedReader || role == Constants.DelegatedRoles.DelegatedAdmin)
            return role;

        _logger.LogWarning("Unrecognized delegated Role '{Role}' for {Upn} on tenant {TenantId} — ignoring row",
            role, upn, tenantId);
        return null;
    }

    private static bool IsStronger(string existing, string candidate)
        => existing == Constants.DelegatedRoles.DelegatedAdmin
           && candidate == Constants.DelegatedRoles.DelegatedReader;
}

/// <summary>
/// Immutable resolved delegated scope: which tenants a UPN may access and at what role. Empty when the
/// caller is not a delegated admin. Consumed by the auth middleware to gate cross-tenant access against
/// a subset (vs. the all-or-nothing GlobalAdmin scope).
/// </summary>
public sealed class DelegatedScope
{
    public static readonly DelegatedScope Empty = new(new Dictionary<string, string>());

    private readonly IReadOnlyDictionary<string, string> _tenantRoles;

    public DelegatedScope(IReadOnlyDictionary<string, string> tenantRoles)
        => _tenantRoles = tenantRoles;

    /// <summary>Tenant IDs (lowercase) this scope grants access to.</summary>
    public IReadOnlyCollection<string> TenantIds => (IReadOnlyCollection<string>)_tenantRoles.Keys;

    public bool IsEmpty => _tenantRoles.Count == 0;

    /// <summary>True if the scope grants access to the given tenant (any delegated role).</summary>
    public bool Covers(string? tenantId)
        => !string.IsNullOrEmpty(tenantId) && _tenantRoles.ContainsKey(tenantId);

    /// <summary>The delegated role for a tenant, or null if not covered.</summary>
    public string? RoleFor(string? tenantId)
        => tenantId != null && _tenantRoles.TryGetValue(tenantId, out var r) ? r : null;

    /// <summary>True if the scope grants write (DelegatedAdmin) on the given tenant.</summary>
    public bool CanWrite(string? tenantId)
        => RoleFor(tenantId) == Constants.DelegatedRoles.DelegatedAdmin;
}

/// <summary>
/// Entity representing a delegated admin assignment in Table Storage.
/// PartitionKey = delegated-admin UPN (lowercase); RowKey = TenantId (lowercase).
/// </summary>
public class DelegatedAdminEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty; // UPN (lowercase)
    public string RowKey { get; set; } = string.Empty;       // TenantId (lowercase)
    public DateTimeOffset? Timestamp { get; set; }
    public Azure.ETag ETag { get; set; }

    /// <summary>The delegated admin's UPN (lowercase) — denormalized copy of PartitionKey.</summary>
    public string Upn { get; set; } = string.Empty;

    /// <summary>The managed tenant ID (lowercase) — denormalized copy of RowKey.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary><see cref="Constants.DelegatedRoles"/>: DelegatedReader (default) or DelegatedAdmin.</summary>
    public string Role { get; set; } = Constants.DelegatedRoles.DelegatedReader;

    /// <summary>Whether this assignment is currently enabled (soft toggle, independent of Status).</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary><see cref="Constants.DelegatedStatus"/>: Active / PendingApproval / Revoked.</summary>
    public string Status { get; set; } = Constants.DelegatedStatus.Active;

    /// <summary><see cref="Constants.DelegatedSource"/>: OperatorGranted / CustomerDelegated.</summary>
    public string Source { get; set; } = Constants.DelegatedSource.OperatorGranted;

    /// <summary>When this assignment was created.</summary>
    public DateTime GrantedDate { get; set; }

    /// <summary>UPN of the principal who created this assignment (operator GA, or customer tenant admin).</summary>
    public string GrantedBy { get; set; } = string.Empty;
}

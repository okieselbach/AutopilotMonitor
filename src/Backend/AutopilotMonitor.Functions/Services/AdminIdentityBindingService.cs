using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.DataAccess;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Single choke-point that decides whether a caller's <see cref="AdminIdentity"/> IS the identity a
/// cross-tenant-role UPN was granted for. Both role services (<see cref="GlobalAdminService"/> and
/// <see cref="DelegatedAdminService"/>) consult it BEFORE resolving any role row, so a token from a foreign
/// tenant — or from a different account inside the home tenant — that merely carries a matching UPN string
/// resolves no platform or delegated role at all.
/// <para>
/// Binding model: <c>TenantId</c> is mandatory at grant time (the operator states which tenant the person is
/// homed in). <c>ObjectId</c> is either supplied at grant time or <b>pinned on the first sign-in from the bound
/// tenant</b> and enforced from then on; a later token with the same UPN and tid but a different oid (the UPN
/// re-assigned to another account) is refused until an operator explicitly rebinds. No binding at all ⇒ no role
/// — legacy role rows without a binding are inert until bound.
/// </para>
/// <para>
/// Every non-match is logged at Warning (the one level the worker forwards to App Insights) with the reason,
/// because each one is either an attack signal or an operator action item. Matches are silent.
/// </para>
/// </summary>
public class AdminIdentityBindingService
{
    private readonly IAdminRepository _adminRepo;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AdminIdentityBindingService> _logger;
    // Same short per-process TTL as the role caches (see GlobalAdminService): a rebind/removal on one
    // scaled-out instance converges on the others within seconds. A single point-read per miss.
    private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(30);

    /// <summary>Cached stand-in for "no binding row" so a negative lookup is not re-read on every request.</summary>
    private static readonly AdminIdentityBinding NoBindingSentinel = new();

    public AdminIdentityBindingService(
        IAdminRepository adminRepo,
        IMemoryCache cache,
        ILogger<AdminIdentityBindingService> logger)
    {
        _adminRepo = adminRepo;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// True iff the caller's validated identity matches the binding stored for its UPN. Pins the object id on
    /// the first matching sign-in from the bound tenant (and then verifies against what was actually stored,
    /// so a concurrent pin by another account can never be mistaken for one's own).
    /// </summary>
    public virtual async Task<bool> IsBoundAsync(AdminIdentity? identity)
    {
        if (identity == null)
            return false;

        var binding = await GetCachedBindingAsync(identity.Upn);
        if (binding == null)
        {
            _logger.LogWarning("[IdentityBinding] {Upn} holds no identity binding — no cross-tenant role resolved (tid={TenantId})",
                identity.Upn, identity.TenantId);
            return false;
        }

        if (!string.Equals(binding.TenantId, identity.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[IdentityBinding] TENANT MISMATCH for {Upn}: token tid={TokenTenantId} bound tid={BoundTenantId} — no cross-tenant role resolved",
                identity.Upn, identity.TenantId, binding.TenantId);
            return false;
        }

        if (!binding.IsObjectIdPinned)
        {
            // First sign-in from the bound tenant: pin. The repository only pins onto an unpinned row homed
            // in this tenant and returns the stored row afterwards — verify against THAT, not our intent.
            var stored = await _adminRepo.TryPinIdentityObjectIdAsync(identity.Upn, identity.TenantId, identity.ObjectId);
            _cache.Remove(CacheKey(identity.Upn));
            if (stored == null)
                return false;
            if (string.Equals(stored.ObjectId, identity.ObjectId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("[IdentityBinding] Pinned object id for {Upn} on first sign-in from bound tenant {TenantId}",
                    identity.Upn, identity.TenantId);
                return true;
            }
            binding = stored;
        }

        if (!string.Equals(binding.ObjectId, identity.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[IdentityBinding] OBJECT-ID MISMATCH for {Upn} in tenant {TenantId}: token oid={TokenObjectId} bound oid={BoundObjectId} — no cross-tenant role resolved (rebind required if the account was legitimately re-created)",
                identity.Upn, identity.TenantId, identity.ObjectId, binding.ObjectId);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Creates the binding a grant requires. When a binding already exists it is kept if compatible (same
    /// tenant, and the supplied object id — if any — equals the pinned one) and the call is a no-op; an
    /// incompatible existing binding throws <see cref="IdentityBindingConflictException"/> so a grant can never
    /// silently re-home a UPN — that is an explicit, audited <see cref="RebindAsync"/>.
    /// </summary>
    public virtual async Task<AdminIdentityBinding> EnsureBoundAsync(string upn, string tenantId, string? objectId, string boundBy)
    {
        upn = upn.ToLowerInvariant();
        tenantId = tenantId.ToLowerInvariant();
        objectId = string.IsNullOrWhiteSpace(objectId) ? null : objectId.ToLowerInvariant();

        var existing = await _adminRepo.GetIdentityBindingAsync(upn);
        if (existing != null)
        {
            if (!string.Equals(existing.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                throw new IdentityBindingConflictException(
                    $"{upn} is already bound to tenant {existing.TenantId}; rebind explicitly to move it to {tenantId}");
            if (objectId != null && existing.IsObjectIdPinned
                && !string.Equals(existing.ObjectId, objectId, StringComparison.OrdinalIgnoreCase))
                throw new IdentityBindingConflictException(
                    $"{upn} is already pinned to a different object id; rebind explicitly to replace it");

            if (objectId == null || existing.IsObjectIdPinned)
                return existing;
            // Compatible upgrade: same tenant, previously unpinned, object id now supplied.
        }

        await _adminRepo.UpsertIdentityBindingAsync(upn, tenantId, objectId, boundBy);
        _cache.Remove(CacheKey(upn));
        return (await _adminRepo.GetIdentityBindingAsync(upn))!;
    }

    /// <summary>Replaces the binding unconditionally (operator rebind: tenant move, or re-pin after UPN re-assignment).</summary>
    public virtual async Task<AdminIdentityBinding> RebindAsync(string upn, string tenantId, string? objectId, string boundBy)
    {
        upn = upn.ToLowerInvariant();
        await _adminRepo.UpsertIdentityBindingAsync(upn, tenantId, objectId, boundBy);
        _cache.Remove(CacheKey(upn));
        return (await _adminRepo.GetIdentityBindingAsync(upn))!;
    }

    public virtual async Task RemoveAsync(string upn)
    {
        upn = upn.ToLowerInvariant();
        await _adminRepo.RemoveIdentityBindingAsync(upn);
        _cache.Remove(CacheKey(upn));
    }

    public Task<AdminIdentityBinding?> GetAsync(string upn) => _adminRepo.GetIdentityBindingAsync(upn.ToLowerInvariant());

    public Task<List<AdminIdentityBinding>> GetAllAsync() => _adminRepo.GetAllIdentityBindingsAsync();

    private async Task<AdminIdentityBinding?> GetCachedBindingAsync(string upn)
    {
        var key = CacheKey(upn);
        if (_cache.TryGetValue<AdminIdentityBinding>(key, out var cached) && cached != null)
            return ReferenceEquals(cached, NoBindingSentinel) ? null : cached;

        var binding = await _adminRepo.GetIdentityBindingAsync(upn);
        _cache.Set(key, binding ?? NoBindingSentinel, _cacheDuration);
        return binding;
    }

    private static string CacheKey(string upn) => $"identity-binding:{upn}";
}

/// <summary>A grant tried to bind a UPN that is already bound to a different tenant / object id (⇒ HTTP 409).</summary>
public sealed class IdentityBindingConflictException : InvalidOperationException
{
    public IdentityBindingConflictException(string message) : base(message) { }
}

/// <summary>
/// Row of <see cref="Shared.Constants.TableNames.AdminIdentityBindings"/>: PK = <see cref="Partition"/>, RK = UPN (lowercase).
/// </summary>
public class AdminIdentityBindingEntity : ITableEntity
{
    public const string Partition = "Bindings";

    public string PartitionKey { get; set; } = Partition;
    public string RowKey { get; set; } = string.Empty; // UPN (lowercase)
    public DateTimeOffset? Timestamp { get; set; }
    public Azure.ETag ETag { get; set; }

    /// <summary>The bound UPN (lowercase) — denormalized copy of RowKey.</summary>
    public string Upn { get; set; } = string.Empty;

    /// <summary>The admin's HOME Entra tenant id (lowercase). Offboarding that tenant purges the row (TenantId property wipe).</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The admin's Entra object id (lowercase); empty until pinned.</summary>
    public string ObjectId { get; set; } = string.Empty;

    /// <summary>UPN of the operator who created/replaced the binding ("Initial Setup" for the seed).</summary>
    public string BoundBy { get; set; } = string.Empty;

    public DateTime BoundDate { get; set; }

    /// <summary>Nullable on purpose: unpinned rows omit it (a default DateTime is below the Edm minimum and would fail the write).</summary>
    public DateTime? ObjectIdPinnedDate { get; set; }
}

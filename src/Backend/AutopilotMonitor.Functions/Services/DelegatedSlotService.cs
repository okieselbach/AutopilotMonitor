using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Slot usage of one HOME (managing / MSP) tenant: how many DISTINCT managed target tenants its users may
/// reach, against the tenant's slot limit. <see cref="Used"/> also counts pending self-service invitations
/// and 24-hour release holds — a slot is occupied from the moment it is promised until the hold lapses.
/// </summary>
public sealed record DelegatedSlotUsage(
    string HomeTenantId,
    string? HomeTenantDomain,
    int Limit,
    int CatalogLimit,
    int? OverrideLimit,
    IReadOnlySet<string> ManagedTenantIds,
    int PendingInvitations,
    IReadOnlyList<DelegationInvitation> Holds)
{
    public int ActiveHolds => Holds.Count;
    public int Used => ManagedTenantIds.Count + PendingInvitations + ActiveHolds;
    public int Free => Math.Max(0, Limit - Used);
}

/// <summary>A mutation that would push a home tenant over its slot limit — rendered as 409 <c>DelegatedSlotLimitReached</c>.</summary>
public sealed record DelegatedSlotViolation(string HomeTenantId, string? HomeTenantDomain, int Used, int Limit, int Required);

/// <summary>
/// Counts and enforces the <b>delegated tenant slots</b> of a managing (MSP) tenant: the number of DISTINCT
/// customer tenants that users HOMED in it may manage — direct DelegatedAdmins grants ∪ every tenant of every
/// Tenant Group assigned to any of those users ∪ the tenant's own self-service group (even before anyone is
/// assigned to it) — plus pending self-service invitations and 24-hour release holds. The limit is the
/// tenant's plan entitlement (<see cref="Security.EditionEntitlements.MaxDelegatedTenants"/>: Community 0,
/// Pro 2) unless a Global Admin set <see cref="Shared.Models.TenantConfiguration.MaxDelegatedTenantsOverride"/>.
///
/// Attribution rides on the identity binding: a UPN counts toward the tenant it is bound to. A grant for a
/// UPN WITHOUT a binding is not attributable — and inert anyway (DelegatedAdminService.GetScopeAsync confers
/// nothing without a binding). Disabled grants/assignments DO count (a paused relationship still holds its
/// slot); Revoked rows do not.
///
/// Two read paths: <see cref="GetUsageAsync"/> (30 s per-instance cache, read surfaces) and the Check*
/// methods (ALWAYS fresh — the limit is read from the config row uncached so a Global Admin's "raise the
/// limit, then retry" round trip lands on every instance at once). There is no global lock: two concurrent
/// grants can overshoot the limit by one — acceptable for an admin-scale surface.
/// </summary>
public class DelegatedSlotService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    /// <summary>A removed customer's slot stays occupied this long — no rotating customers through a small allowance.</summary>
    public static readonly TimeSpan ReleaseHold = TimeSpan.FromHours(24);

    private readonly IAdminRepository _adminRepo;
    private readonly IConfigRepository _configRepo;
    private readonly IDelegationInvitationRepository _invitations;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DelegatedSlotService> _logger;
    private readonly TimeProvider _time;

    public DelegatedSlotService(
        IAdminRepository adminRepo,
        IConfigRepository configRepo,
        IDelegationInvitationRepository invitations,
        IMemoryCache cache,
        ILogger<DelegatedSlotService> logger)
        : this(adminRepo, configRepo, invitations, cache, logger, TimeProvider.System)
    {
    }

    /// <summary>Test seam — inject a fake <see cref="TimeProvider"/> for deterministic edition/hold math.</summary>
    public DelegatedSlotService(
        IAdminRepository adminRepo,
        IConfigRepository configRepo,
        IDelegationInvitationRepository invitations,
        IMemoryCache cache,
        ILogger<DelegatedSlotService> logger,
        TimeProvider time)
    {
        _adminRepo = adminRepo;
        _configRepo = configRepo;
        _invitations = invitations;
        _cache = cache;
        _logger = logger;
        _time = time;
    }

    internal static string CacheKey(string homeTenantId) => $"delegated-slots:{homeTenantId.ToLowerInvariant()}";

    /// <summary>The home tenant's current slot usage. Cached briefly unless <paramref name="bypassCache"/>.</summary>
    public virtual async Task<DelegatedSlotUsage> GetUsageAsync(string homeTenantId, bool bypassCache = false)
    {
        var home = homeTenantId.ToLowerInvariant();
        var key = CacheKey(home);
        if (!bypassCache && _cache.TryGetValue<DelegatedSlotUsage>(key, out var cached) && cached != null)
            return cached;

        var nowUtc = _time.GetUtcNow().UtcDateTime;
        var (limit, catalogLimit, overrideLimit, domain) = await ResolveLimitAsync(home, nowUtc);
        var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var bindings = await _adminRepo.GetIdentityBindingsByHomeTenantAsync(home);
        // The tenant's own self-service group counts even before anyone is assigned to it: an accepted
        // customer occupies the slot from the moment it accepted.
        var groupIds = new HashSet<string>(StringComparer.Ordinal) { Constants.TenantGroupIds.ForHomeTenant(home) };
        foreach (var binding in bindings)
        {
            foreach (var row in await _adminRepo.GetDelegatedTenantsAsync(binding.Upn))
            {
                if (row.Status == Constants.DelegatedStatus.Revoked || string.IsNullOrWhiteSpace(row.TenantId))
                    continue;
                if (!string.Equals(row.TenantId, home, StringComparison.OrdinalIgnoreCase))
                    managed.Add(row.TenantId.ToLowerInvariant());
            }
            foreach (var assignment in await _adminRepo.GetGroupAssignmentsForUpnAsync(binding.Upn))
                groupIds.Add(assignment.GroupId);
        }

        foreach (var groupId in groupIds)
        {
            foreach (var tenantId in await _adminRepo.GetGroupTenantsAsync(groupId))
            {
                if (!string.IsNullOrWhiteSpace(tenantId) && !string.Equals(tenantId, home, StringComparison.OrdinalIgnoreCase))
                    managed.Add(tenantId.ToLowerInvariant());
            }
        }

        var rows = await _invitations.GetByHomeTenantAsync(home);
        var pending = rows.Count(r => IsPending(r, nowUtc));
        var holds = rows.Where(r => IsActiveHold(r, nowUtc)).OrderBy(r => r.HoldUntilUtc).ToList();

        var usage = new DelegatedSlotUsage(home, domain, limit, catalogLimit, overrideLimit, managed, pending, holds);
        _cache.Set(key, usage, CacheDuration);
        return usage;
    }

    /// <summary>
    /// Would granting the home tenant's users access to <paramref name="candidateTenantIds"/> exceed the limit?
    /// Only tenants NOT already managed (and not the home tenant itself) need a slot. Always a fresh read.
    /// </summary>
    public virtual async Task<DelegatedSlotViolation?> CheckAsync(string homeTenantId, IEnumerable<string> candidateTenantIds)
    {
        var usage = await GetUsageAsync(homeTenantId, bypassCache: true);
        return Evaluate(usage, RequiredSlots(usage, candidateTenantIds));
    }

    /// <summary>Would reserving <paramref name="count"/> new slots (a pending invitation) exceed the limit? Fresh read.</summary>
    public virtual async Task<DelegatedSlotViolation?> CheckReserveAsync(string homeTenantId, int count)
    {
        var usage = await GetUsageAsync(homeTenantId, bypassCache: true);
        return Evaluate(usage, count);
    }

    /// <summary>
    /// Accept-time check: the pending invitation being redeemed already holds a slot and converts into the
    /// managed tenant, so the net demand is zero — unless the limit was LOWERED after the invitation was sent
    /// (then Used already exceeds Limit). An already-managed tenant needs nothing.
    /// </summary>
    public virtual async Task<DelegatedSlotViolation?> CheckAcceptAsync(string homeTenantId, string tenantId)
    {
        var usage = await GetUsageAsync(homeTenantId, bypassCache: true);
        if (usage.ManagedTenantIds.Contains(tenantId.ToLowerInvariant()))
            return null;
        var usedWithoutThisInvitation = Math.Max(0, usage.Used - 1);
        return usedWithoutThisInvitation + 1 > usage.Limit
            ? new DelegatedSlotViolation(usage.HomeTenantId, usage.HomeTenantDomain, usedWithoutThisInvitation, usage.Limit, 1)
            : null;
    }

    /// <summary>
    /// Group-side check for "add tenant X to group G": every assignee's home tenant gains X. Returns the first
    /// home tenant that would exceed its limit (assignees without a binding are skipped — inert anyway).
    /// </summary>
    public virtual async Task<DelegatedSlotViolation?> CheckAddTenantToGroupAsync(IEnumerable<string> assigneeUpns, string tenantId)
    {
        foreach (var home in await ResolveHomeTenantsAsync(assigneeUpns))
        {
            var violation = await CheckAsync(home, new[] { tenantId });
            if (violation != null)
                return violation;
        }
        return null;
    }

    /// <summary>Distinct home tenants (lowercase) of the given UPNs, via their identity bindings.</summary>
    public virtual async Task<IReadOnlyCollection<string>> ResolveHomeTenantsAsync(IEnumerable<string> upns)
    {
        var homes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var upn in upns.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var binding = await _adminRepo.GetIdentityBindingAsync(upn.ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(binding?.TenantId))
                homes.Add(binding!.TenantId.ToLowerInvariant());
        }
        return homes;
    }

    /// <summary>
    /// A managed tenant left the home tenant's self-service group: its slot stays occupied for
    /// <see cref="ReleaseHold"/>. The Accepted invitation row that brought the tenant in becomes the hold;
    /// when none exists (the operator added the tenant by hand) a synthetic Released row carries the hold.
    /// </summary>
    public virtual async Task<DelegationInvitation> RecordReleaseAsync(string homeTenantId, string tenantId, string actor)
    {
        var home = homeTenantId.ToLowerInvariant();
        var managed = tenantId.ToLowerInvariant();
        var nowUtc = _time.GetUtcNow().UtcDateTime;
        var holdUntil = nowUtc.Add(ReleaseHold);

        var accepted = (await _invitations.GetByHomeTenantAsync(home))
            .Where(r => r.Status == Constants.DelegationInvitationStatus.Accepted
                        && string.Equals(r.TenantId, managed, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.AcceptedAt)
            .FirstOrDefault();

        DelegationInvitation hold;
        if (accepted != null && await _invitations.SetStatusAsync(home, accepted.InvitationId, Constants.DelegationInvitationStatus.Released, nowUtc, actor, holdUntil))
        {
            hold = accepted;
            hold.Status = Constants.DelegationInvitationStatus.Released;
            hold.ReleasedAt = nowUtc;
            hold.ReleasedBy = actor.ToLowerInvariant();
            hold.HoldUntilUtc = holdUntil;
        }
        else
        {
            hold = new DelegationInvitation
            {
                InvitationId = Guid.NewGuid().ToString("N"),
                HomeTenantId = home,
                Status = Constants.DelegationInvitationStatus.Released,
                Role = Constants.DelegatedRoles.DelegatedReader,
                Source = Constants.DelegatedSource.OperatorGranted,
                CreatedBy = actor.ToLowerInvariant(),
                CreatedAt = nowUtc,
                ExpiresAt = nowUtc,
                TenantId = managed,
                ReleasedAt = nowUtc,
                ReleasedBy = actor.ToLowerInvariant(),
                HoldUntilUtc = holdUntil,
            };
            await _invitations.CreateAsync(hold);
        }

        Invalidate(home);
        return hold;
    }

    /// <summary>Global Admin escape hatch: ends one hold (or every active hold) now. Returns how many were released.</summary>
    public virtual async Task<int> ReleaseHoldAsync(string homeTenantId, string? invitationId, bool releaseAll, string actor)
    {
        var home = homeTenantId.ToLowerInvariant();
        var nowUtc = _time.GetUtcNow().UtcDateTime;
        var holds = (await _invitations.GetByHomeTenantAsync(home))
            .Where(r => IsActiveHold(r, nowUtc) && (releaseAll || string.Equals(r.InvitationId, invitationId, StringComparison.Ordinal)))
            .ToList();

        var released = 0;
        foreach (var hold in holds)
        {
            if (await _invitations.SetStatusAsync(home, hold.InvitationId, Constants.DelegationInvitationStatus.Released, nowUtc, actor, holdUntilUtc: nowUtc))
                released++;
        }
        if (released > 0)
            Invalidate(home);
        return released;
    }

    public void Invalidate(string homeTenantId) => _cache.Remove(CacheKey(homeTenantId));

    /// <summary>Pure: a Pending row that has not expired.</summary>
    internal static bool IsPending(DelegationInvitation row, DateTime nowUtc)
        => row.Status == Constants.DelegationInvitationStatus.Pending && row.ExpiresAt > nowUtc;

    /// <summary>Pure: a Released row whose hold has not lapsed.</summary>
    internal static bool IsActiveHold(DelegationInvitation row, DateTime nowUtc)
        => row.Status == Constants.DelegationInvitationStatus.Released && row.HoldUntilUtc.HasValue && row.HoldUntilUtc.Value > nowUtc;

    /// <summary>Pure: the number of NEW slots the candidates need (unmanaged, not the home tenant, distinct).</summary>
    internal static int RequiredSlots(DelegatedSlotUsage usage, IEnumerable<string> candidateTenantIds)
        => candidateTenantIds
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(t => !string.Equals(t, usage.HomeTenantId, StringComparison.OrdinalIgnoreCase)
                        && !usage.ManagedTenantIds.Contains(t));

    /// <summary>Pure: a violation when the required slots do not fit; null when they do (or none are needed).</summary>
    internal static DelegatedSlotViolation? Evaluate(DelegatedSlotUsage usage, int required)
        => required > 0 && usage.Used + required > usage.Limit
            ? new DelegatedSlotViolation(usage.HomeTenantId, usage.HomeTenantDomain, usage.Used, usage.Limit, required)
            : null;

    /// <summary>
    /// The effective limit from the config row read UNCACHED (so a just-raised override is seen at once on every
    /// instance). No row or a storage error ⇒ catalog Community (0) — fail-closed, and the plan endpoint 404s
    /// for such a tenant anyway.
    /// </summary>
    private async Task<(int Limit, int CatalogLimit, int? OverrideLimit, string? Domain)> ResolveLimitAsync(string homeTenantId, DateTime nowUtc)
    {
        try
        {
            var config = await _configRepo.GetTenantConfigurationAsync(homeTenantId);
            if (config == null)
                return (0, 0, null, null);
            var catalog = Security.FeatureEntitlementCatalog.Get(TenantEntitlementService.ResolveEdition(config, nowUtc)).MaxDelegatedTenants;
            return (TenantEntitlementService.GetMaxDelegatedTenants(config, nowUtc), catalog, config.MaxDelegatedTenantsOverride,
                string.IsNullOrWhiteSpace(config.DomainName) ? null : config.DomainName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DelegatedSlots] Limit resolution failed for {HomeTenantId} — treating as 0 (fail-closed)", homeTenantId);
            return (0, 0, null, null);
        }
    }
}

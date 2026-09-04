using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Delegation;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>A failed self-service delegation step: HTTP status + machine-readable code + human message. A slot violation rides along for the 409 shape.</summary>
public sealed record DelegationFailure(int Status, string Code, string Message, DelegatedSlotViolation? SlotViolation = null);

/// <summary>Outcome of a self-service delegation step.</summary>
public sealed class DelegationResult<T>
{
    public T? Value { get; init; }
    public DelegationFailure? Failure { get; init; }
    public bool Ok => Failure == null;
    public static DelegationResult<T> Success(T value) => new() { Value = value };
    public static DelegationResult<T> Fail(int status, string code, string message, DelegatedSlotViolation? violation = null)
        => new() { Failure = new DelegationFailure(status, code, message, violation) };
}

public sealed record AcceptPreview(string HomeTenantId, string? HomeTenantDomain, DateTime ExpiresUtc, string Status, string TargetTenantId, string? TargetTenantDomain);
public sealed record AcceptOutcome(string HomeTenantId, string? HomeTenantDomain, string ManagedTenantId);
public sealed record ManagedTenantView(string TenantId, string Source, DateTime? SinceUtc, bool Removable);
public sealed record ManagedTenantsView(DelegatedSlotUsage Slots, IReadOnlyList<ManagedTenantView> Tenants);
public sealed record TenantManagerView(string? GroupId, string? OwnerTenantId, string? OwnerDomain, string Name, string Source,
    IReadOnlyList<TenantGroupAssignment> Assignees, DateTime? SinceUtc, bool Revocable);

/// <summary>
/// The customer-facing half of delegated ("MSP") administration — no Global Admin involved:
/// <list type="bullet">
///   <item>A Pro (managing) tenant owns exactly ONE implicit Tenant Group, <c>msp-{tenantId}</c>
///   (<see cref="Constants.TenantGroupIds"/>). Its members are the customer tenants that ACCEPTED an invitation;
///   its assignees are the managing tenant's own users. <c>DelegatedAdminService.GetScopeAsync</c>, the policy
///   middleware and the MCP server need no change — the group is an ordinary group to them.</item>
///   <item>Invitations are signed single-use links (<see cref="DelegationInviteTicket"/>); the row's Pending
///   status + ETag flip is the one-shot authority. The ACCEPTING tenant is always the caller's validated JWT
///   tenant — a link never names its target.</item>
///   <item>Removing a customer (by either side) frees the slot only after <see cref="DelegatedSlotService.ReleaseHold"/>
///   (24 h) — no rotating customers through a small allowance. A Global Admin can release a hold early.</item>
///   <item>Every access change is audited under BOTH tenants: the managed customer's trail records who can read
///   it, the managing tenant's trail records its own actions. Live streams of affected users are cut.</item>
/// </list>
/// Self-service grants are always <see cref="Constants.DelegatedRoles.DelegatedReader"/> (read-only).
/// </summary>
public class DelegationSelfService
{
    public const string SourceSelfService = "self-service";
    public const string SourceOperator = "operator";
    private const string AuditGroupAccess = "DelegatedGroupAccess";
    private const string AuditInvitation = "DelegationInvitation";
    private const string AuditManagedTenant = "DelegationManagedTenant";

    private readonly IAdminRepository _adminRepo;
    private readonly IDelegationInvitationRepository _invitations;
    private readonly DelegatedAdminService _delegatedAdmins;
    private readonly DelegatedSlotService _slots;
    private readonly TenantEntitlementService _entitlements;
    private readonly IConfigRepository _configRepo;
    private readonly IMaintenanceRepository _audit;
    private readonly ISignalRNotificationService _signalR;
    private readonly ProConferralService _proConferral;
    private readonly ILogger<DelegationSelfService> _logger;
    private readonly TimeProvider _time;

    public DelegationSelfService(
        IAdminRepository adminRepo,
        IDelegationInvitationRepository invitations,
        DelegatedAdminService delegatedAdmins,
        DelegatedSlotService slots,
        TenantEntitlementService entitlements,
        IConfigRepository configRepo,
        IMaintenanceRepository audit,
        ISignalRNotificationService signalR,
        ProConferralService proConferral,
        ILogger<DelegationSelfService> logger)
        : this(adminRepo, invitations, delegatedAdmins, slots, entitlements, configRepo, audit, signalR, proConferral, logger, TimeProvider.System)
    {
    }

    /// <summary>Test seam — inject a fake <see cref="TimeProvider"/>.</summary>
    public DelegationSelfService(
        IAdminRepository adminRepo,
        IDelegationInvitationRepository invitations,
        DelegatedAdminService delegatedAdmins,
        DelegatedSlotService slots,
        TenantEntitlementService entitlements,
        IConfigRepository configRepo,
        IMaintenanceRepository audit,
        ISignalRNotificationService signalR,
        ProConferralService proConferral,
        ILogger<DelegationSelfService> logger,
        TimeProvider time)
    {
        _adminRepo = adminRepo;
        _invitations = invitations;
        _delegatedAdmins = delegatedAdmins;
        _slots = slots;
        _entitlements = entitlements;
        _configRepo = configRepo;
        _audit = audit;
        _signalR = signalR;
        _proConferral = proConferral;
        _logger = logger;
        _time = time;
    }

    // ── Invitations (managing tenant) ────────────────────────────────────────────

    public async Task<DelegationResult<(DelegationInvitation Invitation, string Token)>> CreateInvitationAsync(string homeTenantId, string createdBy)
    {
        var home = homeTenantId.ToLowerInvariant();
        if (!(await _entitlements.GetEntitlementsAsync(home)).DelegatedAdminAllowed)
            return DelegationResult<(DelegationInvitation, string)>.Fail(403, Constants.DelegationCodes.DelegatedAdminNotAllowed,
                "Delegated administration is a Pro capability. Upgrade your plan to invite tenants.");

        var violation = await _slots.CheckReserveAsync(home, 1);
        if (violation != null)
            return DelegationResult<(DelegationInvitation, string)>.Fail(409, Constants.DelegatedSlots.LimitReachedCode,
                "No free delegated tenant slot: every slot is in use, promised to a pending invitation, or held after a removal.", violation);

        var nowUtc = _time.GetUtcNow().UtcDateTime;
        var row = new DelegationInvitation
        {
            InvitationId = Guid.NewGuid().ToString("N"),
            HomeTenantId = home,
            Status = Constants.DelegationInvitationStatus.Pending,
            Role = Constants.DelegatedRoles.DelegatedReader,
            Source = Constants.DelegatedSource.CustomerDelegated,
            CreatedBy = createdBy.ToLowerInvariant(),
            CreatedAt = nowUtc,
            ExpiresAt = nowUtc.Add(DelegationInviteTicket.DefaultTtl),
        };
        await _invitations.CreateAsync(row);
        var token = DelegationInviteTicket.Encode(home, row.InvitationId, new DateTimeOffset(nowUtc, TimeSpan.Zero));

        await _audit.LogAuditEntryAsync(home, "CREATE", AuditInvitation, row.InvitationId, row.CreatedBy,
            new Dictionary<string, string> { { "ExpiresUtc", row.ExpiresAt.ToString("O") } });
        _slots.Invalidate(home);
        _logger.LogInformation("[Delegation] Invitation {InvitationId} created by {By} for home tenant {Home}", row.InvitationId, row.CreatedBy, home);
        return DelegationResult<(DelegationInvitation, string)>.Success((row, token));
    }

    public async Task<DelegationResult<bool>> CancelInvitationAsync(string homeTenantId, string invitationId, string actor)
    {
        var home = homeTenantId.ToLowerInvariant();
        var row = await _invitations.GetAsync(home, invitationId);
        if (row == null || row.Status != Constants.DelegationInvitationStatus.Pending)
            return DelegationResult<bool>.Fail(404, Constants.DelegationCodes.InvitationNotFound, "No pending invitation with that id.");

        var nowUtc = _time.GetUtcNow().UtcDateTime;
        await _invitations.SetStatusAsync(home, invitationId, Constants.DelegationInvitationStatus.Cancelled, nowUtc, actor, holdUntilUtc: null);
        await _audit.LogAuditEntryAsync(home, "DELETE", AuditInvitation, invitationId, actor.ToLowerInvariant(),
            new Dictionary<string, string> { { "Reason", "cancelled" } });
        _slots.Invalidate(home);
        return DelegationResult<bool>.Success(true);
    }

    public Task<List<DelegationInvitation>> ListInvitationsAsync(string homeTenantId)
        => _invitations.GetByHomeTenantAsync(homeTenantId.ToLowerInvariant());

    /// <summary>Wire status of a row: a Pending row past its expiry reads as Expired (never written).</summary>
    public string EffectiveStatus(DelegationInvitation row)
        => row.Status == Constants.DelegationInvitationStatus.Pending && row.ExpiresAt <= _time.GetUtcNow().UtcDateTime
            ? Constants.DelegationInvitationStatus.Expired
            : row.Status;

    // ── Accept (customer tenant) ─────────────────────────────────────────────────

    public async Task<DelegationResult<AcceptPreview>> PreviewAsync(string token, string callerTenantId)
    {
        var located = await LocateAsync(token);
        if (!located.Ok)
            return DelegationResult<AcceptPreview>.Fail(located.Failure!.Status, located.Failure.Code, located.Failure.Message);
        var row = located.Value!;
        var target = callerTenantId.ToLowerInvariant();
        return DelegationResult<AcceptPreview>.Success(new AcceptPreview(
            row.HomeTenantId, await DomainAsync(row.HomeTenantId), row.ExpiresAt, EffectiveStatus(row), target, await DomainAsync(target)));
    }

    /// <summary>The accept chain. Every rejection is a distinct code so the accept page can explain it.</summary>
    public async Task<DelegationResult<AcceptOutcome>> AcceptAsync(string token, string callerTenantId, string callerUpn)
    {
        var located = await LocateAsync(token);
        if (!located.Ok)
            return DelegationResult<AcceptOutcome>.Fail(located.Failure!.Status, located.Failure.Code, located.Failure.Message);
        var row = located.Value!;
        var home = row.HomeTenantId;
        var nowUtc = _time.GetUtcNow().UtcDateTime;

        switch (row.Status)
        {
            case Constants.DelegationInvitationStatus.Cancelled:
                return DelegationResult<AcceptOutcome>.Fail(409, Constants.DelegationCodes.InvitationCancelled, "This invitation was cancelled by the managing organization. Ask them for a new link.");
            case Constants.DelegationInvitationStatus.Accepted:
            case Constants.DelegationInvitationStatus.Released:
                return DelegationResult<AcceptOutcome>.Fail(409, Constants.DelegationCodes.InvitationAlreadyUsed, "This invitation has already been used. Ask the managing organization for a new link.");
        }
        if (row.ExpiresAt <= nowUtc)
            return DelegationResult<AcceptOutcome>.Fail(409, Constants.DelegationCodes.InvitationExpired, "This invitation has expired. Ask the managing organization for a new link.");

        var target = callerTenantId.ToLowerInvariant();
        if (string.Equals(target, home, StringComparison.OrdinalIgnoreCase))
            return DelegationResult<AcceptOutcome>.Fail(409, Constants.DelegationCodes.CannotAcceptOwnInvitation, "An invitation must be accepted by an administrator of the tenant to be managed, not by the inviting tenant.");

        var groupId = Constants.TenantGroupIds.ForHomeTenant(home);
        if ((await _adminRepo.GetGroupTenantsAsync(groupId)).Any(t => string.Equals(t, target, StringComparison.OrdinalIgnoreCase)))
            return DelegationResult<AcceptOutcome>.Fail(409, Constants.DelegationCodes.AlreadyManaged, "Your tenant is already managed by this organization.");

        if (!(await _entitlements.GetEntitlementsAsync(home)).DelegatedAdminAllowed)
            return DelegationResult<AcceptOutcome>.Fail(409, Constants.DelegationCodes.ManagerNotEntitled, "The inviting organization is no longer on a plan that includes delegated administration.");

        var violation = await _slots.CheckAcceptAsync(home, target);
        if (violation != null)
            return DelegationResult<AcceptOutcome>.Fail(409, Constants.DelegatedSlots.LimitReachedCode,
                "The managing organization has no free delegated tenant slot. Ask them to raise their limit and try again.", violation);

        // One-shot: the ETag read above guards against a concurrent accept/cancel.
        if (!await _invitations.TryAcceptAsync(home, row.InvitationId, row.ETag ?? string.Empty, target, callerUpn, nowUtc))
            return DelegationResult<AcceptOutcome>.Fail(409, Constants.DelegationCodes.InvitationAlreadyUsed, "This invitation has just been used or cancelled. Ask the managing organization for a new link.");

        await EnsureOwnedGroupAsync(home);
        await _delegatedAdmins.AddTenantToGroupAsync(groupId, target);
        // Conferred Pro is projected from this membership — drop the caches so the customer sees "Pro (MSP)" now.
        await _proConferral.NotifyDelegationChangedAsync(target);

        var actor = callerUpn.ToLowerInvariant();
        var assignees = await _delegatedAdmins.GetGroupAssigneesAsync(groupId);
        foreach (var assignee in assignees)
        {
            await _audit.LogAuditEntryAsync(target, "CREATE", AuditGroupAccess, assignee.Upn, actor,
                new Dictionary<string, string>
                {
                    { "GroupId", groupId },
                    { "Reason", "customer-accepted-invitation" },
                    { "Source", Constants.DelegatedSource.CustomerDelegated },
                    { "HomeTenantId", home },
                });
        }
        await _audit.LogAuditEntryAsync(target, "ACCEPT", AuditInvitation, row.InvitationId, actor,
            new Dictionary<string, string> { { "HomeTenantId", home } });
        await _audit.LogAuditEntryAsync(home, "ACCEPT", AuditInvitation, row.InvitationId, actor,
            new Dictionary<string, string> { { "ManagedTenantId", target } });

        _slots.Invalidate(home);
        _logger.LogInformation("[Delegation] Tenant {Target} accepted invitation {InvitationId} of {Home}", target, row.InvitationId, home);
        return DelegationResult<AcceptOutcome>.Success(new AcceptOutcome(home, await DomainAsync(home), target));
    }

    // ── Managed tenants (managing side) ──────────────────────────────────────────

    public async Task<ManagedTenantsView> ListManagedAsync(string homeTenantId)
    {
        var home = homeTenantId.ToLowerInvariant();
        var usage = await _slots.GetUsageAsync(home);
        var selfService = new HashSet<string>(await _adminRepo.GetGroupTenantsAsync(Constants.TenantGroupIds.ForHomeTenant(home)), StringComparer.OrdinalIgnoreCase);
        var accepted = (await _invitations.GetByHomeTenantAsync(home))
            .Where(r => r.Status == Constants.DelegationInvitationStatus.Accepted && !string.IsNullOrEmpty(r.TenantId))
            .GroupBy(r => r.TenantId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(r => r.AcceptedAt), StringComparer.OrdinalIgnoreCase);

        var tenants = usage.ManagedTenantIds
            .OrderBy(t => t, StringComparer.Ordinal)
            .Select(t => selfService.Contains(t)
                ? new ManagedTenantView(t, SourceSelfService, accepted.TryGetValue(t, out var since) ? since : null, Removable: true)
                : new ManagedTenantView(t, SourceOperator, null, Removable: false))
            .ToList();
        return new ManagedTenantsView(usage, tenants);
    }

    /// <summary>The managing tenant removes a customer it invited. Access ends now; the slot is held 24 h.</summary>
    public Task<DelegationResult<bool>> RemoveManagedAsync(string homeTenantId, string tenantId, string actor)
        => EndSelfServiceAccessAsync(homeTenantId, tenantId, actor, "customer-removed-by-manager");

    /// <summary>The managed tenant's admin ends the managing tenant's access. Same effect, audited as a customer revoke.</summary>
    public Task<DelegationResult<bool>> RevokeManagerAsync(string managedTenantId, string homeTenantId, string actor)
        => EndSelfServiceAccessAsync(homeTenantId, managedTenantId, actor, "customer-revoked");

    private async Task<DelegationResult<bool>> EndSelfServiceAccessAsync(string homeTenantId, string tenantId, string actor, string reason)
    {
        var home = homeTenantId.ToLowerInvariant();
        var target = tenantId.ToLowerInvariant();
        var groupId = Constants.TenantGroupIds.ForHomeTenant(home);
        var assignees = await _delegatedAdmins.GetGroupAssigneesAsync(groupId);

        if (!await _delegatedAdmins.RemoveTenantFromGroupAsync(groupId, target))
            return DelegationResult<bool>.Fail(404, Constants.DelegationCodes.NotManagedBySelfService, "That tenant is not managed through a self-service delegation.");

        // The customer's conferred Pro ends with the membership: stamp its retention grace anchor.
        await _proConferral.RecordLossAsync(target, home, reason);
        var hold = await _slots.RecordReleaseAsync(home, target, actor);
        var by = actor.ToLowerInvariant();
        foreach (var assignee in assignees)
        {
            await _audit.LogAuditEntryAsync(target, "DELETE", AuditGroupAccess, assignee.Upn, by,
                new Dictionary<string, string> { { "GroupId", groupId }, { "Reason", reason }, { "HomeTenantId", home } });
            await _signalR.DisconnectUserAsync(assignee.Upn);
        }
        await _audit.LogAuditEntryAsync(home, "DELETE", AuditManagedTenant, target, by,
            new Dictionary<string, string> { { "Reason", reason }, { "SlotHeldUntilUtc", hold.HoldUntilUtc?.ToString("O") ?? string.Empty } });
        _logger.LogInformation("[Delegation] {Target} removed from {Home} by {By} ({Reason}); slot held until {HoldUntil}", target, home, by, reason, hold.HoldUntilUtc);
        return DelegationResult<bool>.Success(true);
    }

    // ── Assignees (managing tenant's own users) ──────────────────────────────────

    public Task<List<TenantGroupAssignment>> ListAssigneesAsync(string homeTenantId)
        => _delegatedAdmins.GetGroupAssigneesAsync(Constants.TenantGroupIds.ForHomeTenant(homeTenantId.ToLowerInvariant()));

    public async Task<DelegationResult<TenantGroupAssignment>> AssignAsync(string homeTenantId, string upn, string actor)
    {
        var home = homeTenantId.ToLowerInvariant();
        var normalizedUpn = upn.ToLowerInvariant();
        if (!(await _entitlements.GetEntitlementsAsync(home)).DelegatedAdminAllowed)
            return DelegationResult<TenantGroupAssignment>.Fail(403, Constants.DelegationCodes.DelegatedAdminNotAllowed, "Delegated administration is a Pro capability.");
        if (await _adminRepo.GetTenantMemberAsync(home, normalizedUpn) == null)
            return DelegationResult<TenantGroupAssignment>.Fail(400, Constants.DelegationCodes.NotATenantMember, "Only members of your own tenant (Access Management) can be assigned.");

        var groupId = await EnsureOwnedGroupAsync(home);
        try
        {
            // Bound to THIS tenant as home; the object id is pinned on the person's next sign-in. No slot
            // check: the group's tenants already occupy this tenant's slots regardless of who is assigned.
            await _delegatedAdmins.AssignGroupAsync(normalizedUpn, groupId, Constants.DelegatedRoles.DelegatedReader, true, actor.ToLowerInvariant(), home, objectId: null);
        }
        catch (IdentityBindingConflictException ex)
        {
            return DelegationResult<TenantGroupAssignment>.Fail(409, Constants.DelegationCodes.IdentityBindingConflict, ex.Message);
        }

        var tenants = await _adminRepo.GetGroupTenantsAsync(groupId);
        foreach (var tenant in tenants)
        {
            await _audit.LogAuditEntryAsync(tenant, "CREATE", AuditGroupAccess, normalizedUpn, actor.ToLowerInvariant(),
                new Dictionary<string, string> { { "GroupId", groupId }, { "Role", Constants.DelegatedRoles.DelegatedReader }, { "Reason", "self-service-assigned" }, { "HomeTenantId", home } });
        }
        var assignment = (await _delegatedAdmins.GetGroupAssigneesAsync(groupId)).FirstOrDefault(a => a.Upn == normalizedUpn)
            ?? new TenantGroupAssignment { Upn = normalizedUpn, GroupId = groupId, Role = Constants.DelegatedRoles.DelegatedReader, IsEnabled = true, AssignedBy = actor.ToLowerInvariant() };
        return DelegationResult<TenantGroupAssignment>.Success(assignment);
    }

    public async Task<DelegationResult<bool>> UnassignAsync(string homeTenantId, string upn, string actor)
    {
        var home = homeTenantId.ToLowerInvariant();
        var normalizedUpn = upn.ToLowerInvariant();
        var groupId = Constants.TenantGroupIds.ForHomeTenant(home);
        if (!await _delegatedAdmins.UnassignGroupAsync(normalizedUpn, groupId))
            return DelegationResult<bool>.Fail(404, Constants.DelegationCodes.AssigneeNotFound, "That person is not assigned.");

        await _signalR.DisconnectUserAsync(normalizedUpn);
        foreach (var tenant in await _adminRepo.GetGroupTenantsAsync(groupId))
        {
            await _audit.LogAuditEntryAsync(tenant, "DELETE", AuditGroupAccess, normalizedUpn, actor.ToLowerInvariant(),
                new Dictionary<string, string> { { "GroupId", groupId }, { "Reason", "self-service-unassigned" }, { "HomeTenantId", home } });
        }
        return DelegationResult<bool>.Success(true);
    }

    // ── Managers (managed tenant's side) ─────────────────────────────────────────

    public async Task<List<TenantManagerView>> ListManagersAsync(string managedTenantId)
    {
        var target = managedTenantId.ToLowerInvariant();
        var result = new List<TenantManagerView>();

        foreach (var groupId in await _adminRepo.GetGroupIdsContainingTenantAsync(target))
        {
            var group = await _adminRepo.GetTenantGroupAsync(groupId);
            if (group == null)
                continue;
            var owner = string.IsNullOrEmpty(group.OwnerTenantId) ? null : group.OwnerTenantId;
            DateTime? since = null;
            if (owner != null)
            {
                since = (await _invitations.GetByHomeTenantAsync(owner))
                    .Where(r => r.Status == Constants.DelegationInvitationStatus.Accepted && string.Equals(r.TenantId, target, StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.AcceptedAt)
                    .Max();
            }
            result.Add(new TenantManagerView(group.GroupId, owner, owner != null ? await DomainAsync(owner) : null,
                group.Name, owner != null ? SourceSelfService : SourceOperator, group.Assignees, since, Revocable: owner != null));
        }

        var direct = (await _adminRepo.GetDelegatedAssigneesAsync(target))
            .Where(r => r.Status == Constants.DelegatedStatus.Active)
            .Select(r => new TenantGroupAssignment { Upn = r.Upn, GroupId = string.Empty, Role = r.Role, IsEnabled = r.IsEnabled, AssignedBy = r.GrantedBy, AssignedAt = r.GrantedAt })
            .ToList();
        if (direct.Count > 0)
            result.Add(new TenantManagerView(null, null, null, "Platform operators", SourceOperator, direct, direct.Min(d => d.AssignedAt), Revocable: false));

        return result;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>Decodes the ticket and loads the row. 400 for anything the link itself is wrong about.</summary>
    private async Task<DelegationResult<DelegationInvitation>> LocateAsync(string token)
    {
        if (!DelegationInviteTicket.TryDecode(token ?? string.Empty, out var home, out var invitationId, out var reason, _time.GetUtcNow()))
        {
            _logger.LogWarning("[Delegation] Invitation ticket rejected ({Reason})", reason);
            return DelegationResult<DelegationInvitation>.Fail(400, Constants.DelegationCodes.InvalidInvitation, "This invitation link is not valid. Ask the managing organization for a new link.");
        }
        var row = await _invitations.GetAsync(home, invitationId);
        return row == null
            ? DelegationResult<DelegationInvitation>.Fail(400, Constants.DelegationCodes.InvalidInvitation, "This invitation link is not valid. Ask the managing organization for a new link.")
            : DelegationResult<DelegationInvitation>.Success(row);
    }

    /// <summary>The managing tenant's implicit group (meta row) — created on first use, named after its domain.</summary>
    private async Task<string> EnsureOwnedGroupAsync(string home)
    {
        var groupId = Constants.TenantGroupIds.ForHomeTenant(home);
        var label = await DomainAsync(home) ?? home;
        await _delegatedAdmins.EnsureOwnedGroupAsync(groupId, $"Customers of {label}", home);
        return groupId;
    }

    public async Task<string?> DomainAsync(string tenantId)
    {
        try
        {
            var config = await _configRepo.GetTenantConfigurationAsync(tenantId);
            return string.IsNullOrWhiteSpace(config?.DomainName) ? null : config!.DomainName;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Delegation] Domain lookup failed for {TenantId}", tenantId);
            return null;
        }
    }
}

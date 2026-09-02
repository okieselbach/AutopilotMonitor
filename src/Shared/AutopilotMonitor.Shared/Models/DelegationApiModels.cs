using System;
using System.Collections.Generic;
using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Shared.Models
{
    // Declaration order == wire order.

    /// <summary>
    /// 409 body when a delegated-admin mutation (grant, group assign, add tenant to group, self-service
    /// invitation or accept) would push a managing (MSP) tenant over its delegated tenant slot limit.
    /// <c>error</c> comes first so generic error rendering keeps working; <c>code</c> lets the GA UI offer the
    /// "raise the limit and retry" flow.
    /// </summary>
    public class DelegatedSlotLimitReachedResponse : IApiResponse
    {
        public string Error { get; set; } = default!;
        public string Code { get; set; } = Constants.DelegatedSlots.LimitReachedCode;
        /// <summary>The managing (home) tenant whose slots are exhausted.</summary>
        public string HomeTenantId { get; set; } = default!;
        /// <summary>Its display name; absent when the config row carries none.</summary>
        public string? HomeTenantDomain { get; set; }
        /// <summary>Slots in use (distinct managed tenants + pending invitations + release holds).</summary>
        public int Used { get; set; }
        /// <summary>The effective limit (plan entitlement or the Global Admin override).</summary>
        public int Limit { get; set; }
        /// <summary>New slots the rejected mutation needed.</summary>
        public int Required { get; set; }
    }

    /// <summary>Response of GET global/delegated-slots/{tenantId} and GET delegations/slots: a managing tenant's slot usage.</summary>
    public class DelegatedSlotUsageResponse : IApiResponse
    {
        public string HomeTenantId { get; set; } = default!;
        /// <summary>Effective limit (override when set, else the plan entitlement).</summary>
        public int Limit { get; set; }
        /// <summary>The plan entitlement (Community 0, Pro 2).</summary>
        public int CatalogLimit { get; set; }
        /// <summary>The Global Admin override; absent when the catalog value applies.</summary>
        public int? OverrideLimit { get; set; }
        public int Used { get; set; }
        /// <summary>Distinct managed tenant ids (lowercase) reachable by users homed in this tenant.</summary>
        public IReadOnlyList<string> ManagedTenantIds { get; set; } = default!;
        /// <summary>Pending self-service invitations (each holds a slot until accepted, cancelled or expired).</summary>
        public int PendingInvitations { get; set; }
        /// <summary>Release holds: slots freed by a removal that stay occupied for 24 hours.</summary>
        public IReadOnlyList<DelegatedSlotHold> Holds { get; set; } = default!;
    }

    /// <summary>One release hold nested in <see cref="DelegatedSlotUsageResponse"/>.</summary>
    public class DelegatedSlotHold
    {
        public string InvitationId { get; set; } = default!;
        /// <summary>The managed tenant that was removed; absent when unknown.</summary>
        public string? TenantId { get; set; }
        public DateTime HoldUntilUtc { get; set; }
        public string ReleasedBy { get; set; } = default!;
    }

    /// <summary>Response of POST global/delegated-slots/{tenantId}/release-hold: how many holds ended now.</summary>
    public class ReleaseDelegatedSlotHoldResponse : IApiResponse
    {
        public string HomeTenantId { get; set; } = default!;
        public int Released { get; set; }
    }

    // ---- Self-service delegation (the managing Pro tenant's own surface) ----------------

    /// <summary>One invitation row as the managing tenant sees it. Never carries the token.</summary>
    public class DelegationInvitationItem
    {
        public string InvitationId { get; set; } = default!;
        /// <summary>Pending | Accepted | Cancelled | Released | Expired (derived: pending past its expiry).</summary>
        public string Status { get; set; } = default!;
        public string CreatedBy { get; set; } = default!;
        public DateTime CreatedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public DateTime? AcceptedUtc { get; set; }
        public string? AcceptedBy { get; set; }
        /// <summary>The managed tenant (accepted / released rows).</summary>
        public string? TenantId { get; set; }
        public string? TenantDomain { get; set; }
        /// <summary>Released rows: while in the future the slot is still occupied.</summary>
        public DateTime? HoldUntilUtc { get; set; }
    }

    /// <summary>Response of GET delegations/invitations.</summary>
    public class DelegationInvitationListResponse : IApiResponse
    {
        public string HomeTenantId { get; set; } = default!;
        public IReadOnlyList<DelegationInvitationItem> Invitations { get; set; } = default!;
    }

    /// <summary>Response of POST delegations/invitations: the token is shown ONCE (the link is copy-only).</summary>
    public class CreateDelegationInvitationResponse : IApiResponse
    {
        public string InvitationId { get; set; } = default!;
        public string Token { get; set; } = default!;
        public DateTime ExpiresUtc { get; set; }
    }

    /// <summary>Response of GET delegations/accept?token=: what accepting would do — no mutation.</summary>
    public class DelegationAcceptPreviewResponse : IApiResponse
    {
        /// <summary>The inviting (managing) tenant.</summary>
        public string HomeTenantId { get; set; } = default!;
        public string? HomeTenantDomain { get; set; }
        public DateTime ExpiresUtc { get; set; }
        /// <summary>Pending | Accepted | Cancelled | Released | Expired.</summary>
        public string Status { get; set; } = default!;
        /// <summary>The caller's tenant — the one that would be managed.</summary>
        public string TargetTenantId { get; set; } = default!;
        public string? TargetTenantDomain { get; set; }
    }

    /// <summary>Response of POST delegations/accept.</summary>
    public class AcceptDelegationInvitationResponse : IApiResponse
    {
        public string HomeTenantId { get; set; } = default!;
        public string? HomeTenantDomain { get; set; }
        public string ManagedTenantId { get; set; } = default!;
    }

    /// <summary>MCP organization budget of a managed tenant, nested in <see cref="ManagedTenantItem"/>.</summary>
    public class ManagedTenantQuotaUsage
    {
        public string TenantPlan { get; set; } = default!;
        public int TenantDailyLimit { get; set; }
        public int TenantMonthlyLimit { get; set; }
        public long TenantDailyUsed { get; set; }
        public long TenantMonthlyUsed { get; set; }
    }

    /// <summary>One managed tenant as the managing tenant sees it.</summary>
    public class ManagedTenantItem
    {
        public string TenantId { get; set; } = default!;
        public string? Domain { get; set; }
        /// <summary>self-service (joined by invitation, removable here) | operator (provisioned by platform operators).</summary>
        public string Source { get; set; } = default!;
        public DateTime? SinceUtc { get; set; }
        public bool Removable { get; set; }
        /// <summary>Absent when not resolved (cap reached or read failure).</summary>
        public ManagedTenantQuotaUsage? Usage { get; set; }
    }

    /// <summary>Response of GET delegations/managed.</summary>
    public class ManagedTenantListResponse : IApiResponse
    {
        public string HomeTenantId { get; set; } = default!;
        public DelegatedSlotUsageResponse Slots { get; set; } = default!;
        public IReadOnlyList<ManagedTenantItem> Tenants { get; set; } = default!;
    }

    /// <summary>Response of GET delegations/assignees: the managing tenant's own users on its self-service group.</summary>
    public class DelegationAssigneeListResponse : IApiResponse
    {
        public string HomeTenantId { get; set; } = default!;
        public string GroupId { get; set; } = default!;
        public IReadOnlyList<TenantGroupAssignment> Assignees { get; set; } = default!;
    }

    /// <summary>Response of POST delegations/assignees.</summary>
    public class DelegationAssignResponse : IApiResponse
    {
        public TenantGroupAssignment Assignment { get; set; } = default!;
    }

    /// <summary>One party that can read the caller's tenant, nested in <see cref="TenantManagerListResponse"/>.</summary>
    public class TenantManagerItem
    {
        /// <summary>The Tenant Group conferring the access; absent for direct operator grants.</summary>
        public string? GroupId { get; set; }
        /// <summary>The managing tenant that owns the group (self-service); absent for operator-created groups and direct grants.</summary>
        public string? OwnerTenantId { get; set; }
        public string? OwnerDomain { get; set; }
        public string Name { get; set; } = default!;
        /// <summary>self-service | operator</summary>
        public string Source { get; set; } = default!;
        public IReadOnlyList<TenantManagerAssignee> Assignees { get; set; } = default!;
        public DateTime? SinceUtc { get; set; }
        /// <summary>True when the caller (the managed tenant's admin) may end this access here.</summary>
        public bool Revocable { get; set; }
    }

    /// <summary>One person with access, nested in <see cref="TenantManagerItem"/>.</summary>
    public class TenantManagerAssignee
    {
        public string Upn { get; set; } = default!;
        public string Role { get; set; } = default!;
        public bool IsEnabled { get; set; }
    }

    /// <summary>Response of GET delegations/managers: who manages the caller's tenant.</summary>
    public class TenantManagerListResponse : IApiResponse
    {
        public string TenantId { get; set; } = default!;
        public IReadOnlyList<TenantManagerItem> Managers { get; set; } = default!;
    }

    // ---- Requests -----------------------------------------------------------------------

    public class AcceptDelegationInvitationRequest
    {
        public string Token { get; set; } = string.Empty;
    }

    public class RemoveManagedTenantRequest
    {
        public string TenantId { get; set; } = string.Empty;
    }

    public class RevokeTenantManagerRequest
    {
        public string HomeTenantId { get; set; } = string.Empty;
    }

    public class DelegationAssignRequest
    {
        public string Upn { get; set; } = string.Empty;
    }

    public class ReleaseDelegatedSlotHoldRequest
    {
        public string? InvitationId { get; set; }
        public bool All { get; set; }
    }
}

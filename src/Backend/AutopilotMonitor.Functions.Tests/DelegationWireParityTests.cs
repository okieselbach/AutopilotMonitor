using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Wire parity for the delegated (MSP) slot DTOs: the 409 <c>DelegatedSlotLimitReached</c> body and the
/// GA slot-usage read. Anonymous literal on the left = the exact JSON the web/MCP consume.
/// </summary>
public class DelegationWireParityTests
{
    private const string Home = "7aa20c11-0002-4b7c-a1d2-52f3aaaa0001";
    private const string TenantA = "7aa20c11-0002-4b7c-a1d2-52f3aaaa0002";
    private const string TenantB = "7aa20c11-0002-4b7c-a1d2-52f3aaaa0003";

    [Fact]
    public void DelegatedSlotLimitReachedResponse_matches_the_409_shape()
    {
        var body = DelegatedSlotResponses.Build(new DelegatedSlotViolation(Home, "partner.example", 2, 2, 1));

        AssertParity(
            new
            {
                error = "Delegated tenant slot limit reached for partner.example: 2 of 2 slot(s) in use, 1 more needed. Raise the tenant's limit (plan package or Global Admin override) and retry.",
                code = "DelegatedSlotLimitReached",
                homeTenantId = Home,
                homeTenantDomain = "partner.example",
                used = 2,
                limit = 2,
                required = 1,
            },
            body);
    }

    [Fact]
    public void DelegatedSlotLimitReachedResponse_omits_a_null_domain_and_names_the_id()
    {
        var body = DelegatedSlotResponses.Build(new DelegatedSlotViolation(Home, null, 3, 3, 2));
        string? homeTenantDomain = null;

        AssertParity(
            new
            {
                error = $"Delegated tenant slot limit reached for {Home}: 3 of 3 slot(s) in use, 2 more needed. Raise the tenant's limit (plan package or Global Admin override) and retry.",
                code = "DelegatedSlotLimitReached",
                homeTenantId = Home,
                homeTenantDomain,
                used = 3,
                limit = 3,
                required = 2,
            },
            body);
    }

    [Fact]
    public void DelegatedSlotUsageResponse_matches_the_usage_shape()
    {
        var usage = new DelegatedSlotUsage(Home, "partner.example", 3, 2, 3,
            new HashSet<string>(new[] { TenantB, TenantA }, StringComparer.OrdinalIgnoreCase), 0, Array.Empty<DelegationInvitation>());

        AssertParity(
            new
            {
                homeTenantId = Home,
                limit = 3,
                catalogLimit = 2,
                overrideLimit = 3,
                used = 2,
                managedTenantIds = new[] { TenantA, TenantB },
                pendingInvitations = 0,
                holds = Array.Empty<DelegatedSlotHold>(),
            },
            DelegatedSlotManagementFunction.ToResponse(usage));
    }

    [Fact]
    public void DelegatedSlotUsageResponse_omits_a_null_override()
    {
        var usage = new DelegatedSlotUsage(Home, null, 2, 2, null, new HashSet<string>(), 0, Array.Empty<DelegationInvitation>());
        int? overrideLimit = null;

        AssertParity(
            new
            {
                homeTenantId = Home,
                limit = 2,
                catalogLimit = 2,
                overrideLimit,
                used = 0,
                managedTenantIds = Array.Empty<string>(),
                pendingInvitations = 0,
                holds = Array.Empty<DelegatedSlotHold>(),
            },
            DelegatedSlotManagementFunction.ToResponse(usage));
    }

    private static void AssertParity(object anonymousLiteral, IApiResponse typed)
        => ApiResponseWireParityTests.AssertWireIdentical(anonymousLiteral, typed);

    // ---- Self-service delegation ------------------------------------------------------------

    [Fact]
    public void ManagedTenantListResponse_matches_the_managed_shape_and_omits_null_usage()
    {
        var since = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var slots = new DelegatedSlotUsageResponse { HomeTenantId = Home, Limit = 2, CatalogLimit = 2, Used = 1, ManagedTenantIds = new[] { TenantA }, PendingInvitations = 0, Holds = Array.Empty<DelegatedSlotHold>() };
        string? domainB = null;
        ManagedTenantQuotaUsage? usageB = null;
        DateTime? sinceB = null;

        AssertParity(
            new
            {
                homeTenantId = Home,
                slots,
                tenants = new[]
                {
                    new { tenantId = TenantA, domain = (string?)"customer.example", source = "self-service", sinceUtc = (DateTime?)since, removable = true,
                          usage = (ManagedTenantQuotaUsage?)new ManagedTenantQuotaUsage { TenantPlan = "community", TenantDailyLimit = 300, TenantMonthlyLimit = 9000, TenantDailyUsed = 12, TenantMonthlyUsed = 340 } },
                    new { tenantId = TenantB, domain = domainB, source = "operator", sinceUtc = sinceB, removable = false, usage = usageB },
                },
            },
            new ManagedTenantListResponse
            {
                HomeTenantId = Home,
                Slots = slots,
                Tenants = new List<ManagedTenantItem>
                {
                    new() { TenantId = TenantA, Domain = "customer.example", Source = "self-service", SinceUtc = since, Removable = true,
                            Usage = new ManagedTenantQuotaUsage { TenantPlan = "community", TenantDailyLimit = 300, TenantMonthlyLimit = 9000, TenantDailyUsed = 12, TenantMonthlyUsed = 340 } },
                    new() { TenantId = TenantB, Source = "operator", Removable = false },
                },
            });
    }

    [Fact]
    public void DelegationInvitationListResponse_matches_and_omits_absent_optionals()
    {
        var created = new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc);
        DateTime? acceptedUtc = null; string? acceptedBy = null; string? tenantId = null; string? tenantDomain = null; DateTime? holdUntilUtc = null;

        AssertParity(
            new
            {
                homeTenantId = Home,
                invitations = new[]
                {
                    new { invitationId = "inv1", status = "Pending", createdBy = "admin@partner.example", createdUtc = created, expiresUtc = created.AddDays(7),
                          acceptedUtc, acceptedBy, tenantId, tenantDomain, holdUntilUtc },
                },
            },
            new DelegationInvitationListResponse
            {
                HomeTenantId = Home,
                Invitations = new List<DelegationInvitationItem>
                {
                    new() { InvitationId = "inv1", Status = "Pending", CreatedBy = "admin@partner.example", CreatedUtc = created, ExpiresUtc = created.AddDays(7) },
                },
            });
    }

    [Fact]
    public void CreateAndAcceptResponses_match()
    {
        var expires = new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc);
        AssertParity(
            new { invitationId = "inv1", token = "abc.def", expiresUtc = expires },
            new CreateDelegationInvitationResponse { InvitationId = "inv1", Token = "abc.def", ExpiresUtc = expires });
        AssertParity(
            new { homeTenantId = Home, homeTenantDomain = "partner.example", expiresUtc = expires, status = "Pending", targetTenantId = TenantA, targetTenantDomain = "customer.example" },
            new DelegationAcceptPreviewResponse { HomeTenantId = Home, HomeTenantDomain = "partner.example", ExpiresUtc = expires, Status = "Pending", TargetTenantId = TenantA, TargetTenantDomain = "customer.example" });
        AssertParity(
            new { homeTenantId = Home, homeTenantDomain = "partner.example", managedTenantId = TenantA },
            new AcceptDelegationInvitationResponse { HomeTenantId = Home, HomeTenantDomain = "partner.example", ManagedTenantId = TenantA });
        AssertParity(
            new { homeTenantId = Home, released = 2 },
            new ReleaseDelegatedSlotHoldResponse { HomeTenantId = Home, Released = 2 });
    }

    [Fact]
    public void TenantManagerListResponse_matches_owner_and_operator_entries()
    {
        var since = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        string? nullGroup = null; string? nullOwner = null; string? nullDomain = null;
        var assignees = new[] { new { upn = "analyst@partner.example", role = "DelegatedReader", isEnabled = true } };

        AssertParity(
            new
            {
                tenantId = TenantA,
                managers = new object[]
                {
                    new { groupId = (string?)("msp-" + Home), ownerTenantId = (string?)Home, ownerDomain = (string?)"partner.example", name = "Customers of partner.example", source = "self-service", assignees, sinceUtc = (DateTime?)since, revocable = true },
                    new { groupId = nullGroup, ownerTenantId = nullOwner, ownerDomain = nullDomain, name = "Platform operators", source = "operator", assignees, sinceUtc = (DateTime?)null, revocable = false },
                },
            },
            new TenantManagerListResponse
            {
                TenantId = TenantA,
                Managers = new List<TenantManagerItem>
                {
                    new() { GroupId = "msp-" + Home, OwnerTenantId = Home, OwnerDomain = "partner.example", Name = "Customers of partner.example", Source = "self-service",
                            Assignees = new List<TenantManagerAssignee> { new() { Upn = "analyst@partner.example", Role = "DelegatedReader", IsEnabled = true } }, SinceUtc = since, Revocable = true },
                    new() { Name = "Platform operators", Source = "operator",
                            Assignees = new List<TenantManagerAssignee> { new() { Upn = "analyst@partner.example", Role = "DelegatedReader", IsEnabled = true } }, Revocable = false },
                },
            });
    }
}

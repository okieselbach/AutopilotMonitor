using AutopilotMonitor.Functions.Functions.Metrics;
using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Unit tests for the cross-tenant guard on <c>GET /api/metrics/mcp-usage/user/{userId}</c>.
///
/// Background: the route is catalog-policy <c>TenantAdminOrGlobalReader</c> with no
/// <c>TenantScoping</c> (the path parameter is an Azure AD object id, not a tenant id). The
/// function hands the oid to <c>IUserUsageRepository.GetUsageByUserAsync</c> and then projects
/// the result down to the caller's tenant. Two invariants are pinned here:
/// (1) a non-global caller never receives a row that is not attributed to their own tenant —
/// foreign AND empty <c>TenantId</c> alike; (2) the projection is silent, so a foreign oid
/// and an unknown oid both yield an empty set (no cross-tenant existence oracle).
/// </summary>
public class UsageCrossTenantGuardTests
{
    private const string TenantA = "00000000-0000-0000-0000-aaaaaaaaaaaa";
    private const string TenantB = "00000000-0000-0000-0000-bbbbbbbbbbbb";

    private static UserUsageRecord Rec(string tid, string endpoint = "ep") => new()
    {
        UserId = "oid-1",
        UserPrincipalName = "alice@example.com",
        TenantId = tid,
        Endpoint = endpoint,
        Date = "20260505",
        RequestCount = 1,
    };

    [Fact]
    public void OwnTenantOnly_NonGlobal_AllReturned()
    {
        var records = new[] { Rec(TenantA), Rec(TenantA, "ep2") };

        var visible = UsageCrossTenantGuard.FilterForCaller(records, TenantA, hasGlobalScope: false);

        Assert.Equal(2, visible.Count);
        Assert.All(visible, r => Assert.Equal(TenantA, r.TenantId));
    }

    [Fact]
    public void ForeignTenantOnly_NonGlobal_EmptyLikeUnknownOid()
    {
        // Oracle closure: a foreign oid must look exactly like an oid with no usage at all.
        var foreign = UsageCrossTenantGuard.FilterForCaller(new[] { Rec(TenantB), Rec(TenantB, "ep2") }, TenantA, hasGlobalScope: false);
        var unknown = UsageCrossTenantGuard.FilterForCaller(Array.Empty<UserUsageRecord>(), TenantA, hasGlobalScope: false);

        Assert.Empty(foreign);
        Assert.Empty(unknown);
    }

    [Fact]
    public void MixedTenants_NonGlobal_OnlyOwnRowsReturned()
    {
        var records = new[] { Rec(TenantA), Rec(TenantB), Rec(TenantA, "ep2") };

        var visible = UsageCrossTenantGuard.FilterForCaller(records, TenantA, hasGlobalScope: false);

        Assert.Equal(2, visible.Count);
        Assert.DoesNotContain(visible, r => string.Equals(r.TenantId, TenantB, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForeignTenant_GlobalScope_AllReturned()
    {
        // GA / Global Reader can see usage across all tenants — including unattributed rows.
        var records = new[] { Rec(TenantB), Rec("") };

        var visible = UsageCrossTenantGuard.FilterForCaller(records, TenantA, hasGlobalScope: true);

        Assert.Equal(2, visible.Count);
    }

    [Fact]
    public void NullRecords_ReturnsEmpty()
    {
        var visible = UsageCrossTenantGuard.FilterForCaller(null, TenantA, hasGlobalScope: false);
        Assert.Empty(visible);
    }

    [Fact]
    public void RecordWithEmptyTenantId_NonGlobal_Excluded()
    {
        // Tenant-less rows exist by construction (legacy rows without the TenantId column,
        // tokens without a resolvable tid). They cannot be attributed to the caller's tenant,
        // so a tenant admin of ANY tenant must not receive them.
        var records = new[]
        {
            Rec(""),         // unattributed — dropped
            Rec(TenantA),    // own tenant — kept
        };

        var visible = UsageCrossTenantGuard.FilterForCaller(records, TenantA, hasGlobalScope: false);

        Assert.Single(visible);
        Assert.Equal(TenantA, visible[0].TenantId);
    }

    [Fact]
    public void CallerTenantIdEmpty_NonGlobal_ReturnsNothing()
    {
        // A caller without a tid cannot own any row — fail closed rather than open.
        var records = new[] { Rec(TenantA), Rec("") };

        var visible = UsageCrossTenantGuard.FilterForCaller(records, "", hasGlobalScope: false);

        Assert.Empty(visible);
    }

    [Fact]
    public void TenantIdComparison_IsCaseInsensitive()
    {
        // Azure AD tids are GUIDs; some sources upper-case them. Comparison must not drop those as foreign.
        var records = new[] { Rec(TenantA.ToUpperInvariant()) };

        var visible = UsageCrossTenantGuard.FilterForCaller(records, TenantA, hasGlobalScope: false);

        Assert.Single(visible);
    }
}

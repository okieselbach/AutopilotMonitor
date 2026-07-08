using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="SignalRGroupHelper"/>. Locks in the parsing rules for the four
/// supported group formats so a future refactor cannot accidentally break tenant
/// isolation in <see cref="AutopilotMonitor.Functions.Functions.Infrastructure.SignalRAddToGroupFunction"/>,
/// which uses <see cref="SignalRGroupHelper.ExtractTenantIdFromGroupName"/> for cross-tenant validation.
/// </summary>
public class SignalRGroupHelperTests
{
    private const string TenantId = "11111111-2222-3333-4444-555555555555";
    private const string SessionId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    // group name → extracted tenantId (or null for the roleless/unknown formats). Interpolated over the
    // const GUIDs, so MemberData rather than InlineData (string interpolation is not a compile-time const).
    public static IEnumerable<object?[]> ExtractTenantIdCases() => new[]
    {
        new object?[] { $"tenant-{TenantId}", TenantId },                    // tenant-wide broadcast
        new object?[] { $"tenant-{TenantId}-notify-member", TenantId },      // member-notify
        new object?[] { $"tenant-{TenantId}-notify-admin", TenantId },       // admin-notify
        new object?[] { $"session-{TenantId}-{SessionId}", TenantId },       // per-session
        new object?[] { "global-admins", null },                            // platform group → no tenant
        new object?[] { "foo-bar", null },                                  // unknown format → null
    };

    [Theory]
    [MemberData(nameof(ExtractTenantIdCases))]
    public void ExtractTenantIdFromGroupName_parses_supported_formats(string group, string? expected)
    {
        Assert.Equal(expected, SignalRGroupHelper.ExtractTenantIdFromGroupName(group));
    }

    [Fact]
    public void IsTenantNotifyAdminGroup_OnlyTrueForAdminSuffix()
    {
        Assert.True(SignalRGroupHelper.IsTenantNotifyAdminGroup($"tenant-{TenantId}-notify-admin"));
        Assert.False(SignalRGroupHelper.IsTenantNotifyAdminGroup($"tenant-{TenantId}-notify-member"));
        Assert.False(SignalRGroupHelper.IsTenantNotifyAdminGroup($"tenant-{TenantId}"));
        Assert.False(SignalRGroupHelper.IsTenantNotifyAdminGroup("global-admins"));
    }

    [Fact]
    public void GroupNameBuilders_RoundTrip()
    {
        Assert.Equal($"tenant-{TenantId}-notify-member", SignalRGroupHelper.TenantNotifyMemberGroup(TenantId));
        Assert.Equal($"tenant-{TenantId}-notify-admin", SignalRGroupHelper.TenantNotifyAdminGroup(TenantId));
    }

    // ── CheckNotifyGroupAccess ───────────────────────────────────────────────
    // The leak-critical gate: once a cross-tenant caller (a delegated "MSP" reader, or a Global Reader) is
    // admitted past the broadcast cross-tenant check, their HOME-tenant role must NOT authorize a DIFFERENT
    // tenant's notification group. RequestContext.IsTenantAdmin / IsTenantMemberRole describe the HOME tenant.

    private const string HomeTenant    = "11111111-1111-1111-1111-111111111111";
    private const string ManagedTenant = "22222222-2222-2222-2222-222222222222";
    private static string AdminGroup(string t)  => SignalRGroupHelper.TenantNotifyAdminGroup(t);
    private static string MemberGroup(string t) => SignalRGroupHelper.TenantNotifyMemberGroup(t);

    [Fact]
    public void NotifyAccess_DelegatedReader_HomeAdmin_DeniedOnManagedAdminGroup()
    {
        // THE FIX: a delegated reader of ManagedTenant who is Admin of their OWN (Home) tenant must NOT join
        // the MANAGED tenant's admin-notify group on the strength of that home-admin role.
        var ctx = new RequestContext
        {
            TenantId = HomeTenant,
            UserRole = Constants.TenantRoles.Admin,
            IsTenantAdmin = true, // Admin of HomeTenant
        };
        Assert.Equal(
            SignalRGroupHelper.NotifyGroupDenial.AdminTier,
            SignalRGroupHelper.CheckNotifyGroupAccess(AdminGroup(ManagedTenant), ManagedTenant, ctx));
    }

    [Fact]
    public void NotifyAccess_DelegatedReader_HomeMember_DeniedOnManagedMemberGroup()
    {
        // THE FIX (member tier): a delegated reader who is merely a member of their own tenant must NOT join
        // the managed tenant's member-notify group.
        var ctx = new RequestContext { TenantId = HomeTenant, UserRole = Constants.TenantRoles.Viewer };
        Assert.Equal(
            SignalRGroupHelper.NotifyGroupDenial.MemberTier,
            SignalRGroupHelper.CheckNotifyGroupAccess(MemberGroup(ManagedTenant), ManagedTenant, ctx));
    }

    [Fact]
    public void NotifyAccess_OwnTenantAdmin_AllowedOnOwnAdminGroup()
    {
        var ctx = new RequestContext { TenantId = HomeTenant, UserRole = Constants.TenantRoles.Admin, IsTenantAdmin = true };
        Assert.Equal(
            SignalRGroupHelper.NotifyGroupDenial.None,
            SignalRGroupHelper.CheckNotifyGroupAccess(AdminGroup(HomeTenant), HomeTenant, ctx));
    }

    [Fact]
    public void NotifyAccess_OwnTenantMember_AllowedOnOwnMemberGroup()
    {
        var ctx = new RequestContext { TenantId = HomeTenant, UserRole = Constants.TenantRoles.Operator };
        Assert.Equal(
            SignalRGroupHelper.NotifyGroupDenial.None,
            SignalRGroupHelper.CheckNotifyGroupAccess(MemberGroup(HomeTenant), HomeTenant, ctx));
    }

    [Fact]
    public void NotifyAccess_GlobalAdmin_AllowedCrossTenantOnBothTiers()
    {
        var ctx = new RequestContext { TenantId = HomeTenant, IsGlobalAdmin = true };
        Assert.Equal(SignalRGroupHelper.NotifyGroupDenial.None,
            SignalRGroupHelper.CheckNotifyGroupAccess(AdminGroup(ManagedTenant), ManagedTenant, ctx));
        Assert.Equal(SignalRGroupHelper.NotifyGroupDenial.None,
            SignalRGroupHelper.CheckNotifyGroupAccess(MemberGroup(ManagedTenant), ManagedTenant, ctx));
    }

    [Fact]
    public void NotifyAccess_GlobalReader_AllowedCrossTenantMember_DeniedCrossTenantAdmin()
    {
        // Global Reader keeps cross-tenant read of the MEMBER payload (HasGlobalScope), but is NOT a Global
        // Admin → excluded from another tenant's ADMIN-notify group even if home-admin (tightened, consistent).
        var ctx = new RequestContext { TenantId = HomeTenant, IsGlobalReader = true, IsTenantAdmin = true };
        Assert.Equal(SignalRGroupHelper.NotifyGroupDenial.None,
            SignalRGroupHelper.CheckNotifyGroupAccess(MemberGroup(ManagedTenant), ManagedTenant, ctx));
        Assert.Equal(SignalRGroupHelper.NotifyGroupDenial.AdminTier,
            SignalRGroupHelper.CheckNotifyGroupAccess(AdminGroup(ManagedTenant), ManagedTenant, ctx));
    }

    [Fact]
    public void NotifyAccess_NonNotifyGroup_AlwaysAllowed()
    {
        // The broadcast (session/event) group is not role-gated here — its cross-tenant admission is handled
        // upstream. CheckNotifyGroupAccess only governs the notification tiers.
        var ctx = new RequestContext { TenantId = HomeTenant };
        Assert.Equal(SignalRGroupHelper.NotifyGroupDenial.None,
            SignalRGroupHelper.CheckNotifyGroupAccess($"tenant-{ManagedTenant}", ManagedTenant, ctx));
    }

    // ── IsTenantBroadcastJoinDenied ──────────────────────────────────────────
    // The plain "tenant-{tid}" group streams MemberRead-tier telemetry, but the join route admits ANY
    // authenticated user of the tenant (the Progress Portal's roleless end users). A same-tenant join must
    // therefore require a member role; session groups stay open so the Progress Portal keeps working.

    [Fact]
    public void BroadcastJoin_RolelessSameTenantUser_Denied()
    {
        // THE FIX: a roleless authenticated employee (Progress Portal user) must NOT join their tenant's
        // broadcast group and passively stream org-wide enrollment activity they cannot fetch over REST.
        var ctx = new RequestContext { TenantId = HomeTenant };
        Assert.True(SignalRGroupHelper.IsTenantBroadcastJoinDenied($"tenant-{HomeTenant}", HomeTenant, ctx));
    }

    [Fact]
    public void BroadcastJoin_SameTenantMember_Allowed()
    {
        var ctx = new RequestContext { TenantId = HomeTenant, UserRole = Constants.TenantRoles.Viewer };
        Assert.False(SignalRGroupHelper.IsTenantBroadcastJoinDenied($"tenant-{HomeTenant}", HomeTenant, ctx));
    }

    [Fact]
    public void BroadcastJoin_GlobalScope_Allowed()
    {
        var ctx = new RequestContext { TenantId = HomeTenant, IsGlobalReader = true };
        Assert.False(SignalRGroupHelper.IsTenantBroadcastJoinDenied($"tenant-{HomeTenant}", HomeTenant, ctx));
    }

    [Fact]
    public void BroadcastJoin_CrossTenant_NotDecidedHere()
    {
        // A cross-tenant join only reaches this check after the upstream cross-tenant admission
        // (platform scope or delegated scope over the group's tenant) — the helper must not re-deny it.
        var ctx = new RequestContext { TenantId = HomeTenant };
        Assert.False(SignalRGroupHelper.IsTenantBroadcastJoinDenied($"tenant-{ManagedTenant}", ManagedTenant, ctx));
    }

    [Fact]
    public void BroadcastJoin_SessionAndNotifyGroups_NotCovered()
    {
        // Session groups stay joinable for roleless users (Progress Portal); notify groups have their own
        // gate (CheckNotifyGroupAccess).
        var ctx = new RequestContext { TenantId = HomeTenant };
        Assert.False(SignalRGroupHelper.IsTenantBroadcastJoinDenied(
            $"session-{HomeTenant}-{SessionId}", HomeTenant, ctx));
        Assert.False(SignalRGroupHelper.IsTenantBroadcastJoinDenied(MemberGroup(HomeTenant), HomeTenant, ctx));
        Assert.False(SignalRGroupHelper.IsTenantBroadcastJoinDenied(AdminGroup(HomeTenant), HomeTenant, ctx));
    }
}

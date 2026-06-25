using System.Net;
using AutopilotMonitor.Functions.Functions.Infrastructure;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for AuthFunction.BuildAuthResult — the pure decision logic behind GET /api/auth/me.
/// Covers all gate checks and edge cases to guard against regressions when parallelizing queries.
/// </summary>
public class AuthFunctionTests
{
    private const string TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string Upn = "user@contoso.com";
    private const string DisplayName = "Test User";
    private const string ObjectId = "oid-12345";

    private static TenantConfiguration DefaultConfig() => TenantConfiguration.CreateDefault(TenantId);

    private static McpAccessCheckResult McpAllowed() => McpAccessCheckResult.Allowed(Upn, "test-grant");
    private static McpAccessCheckResult McpDenied() => McpAccessCheckResult.Denied("not allowed");

    private static MemberRoleInfo AdminRole() => new() { Role = Constants.TenantRoles.Admin, CanManageBootstrapTokens = true };
    private static MemberRoleInfo OperatorRole() => new() { Role = Constants.TenantRoles.Operator, CanManageBootstrapTokens = false };
    private static MemberRoleInfo ViewerRole() => new() { Role = Constants.TenantRoles.Viewer, CanManageBootstrapTokens = false };

    // -------------------------------------------------------------------------
    // Happy Path
    // -------------------------------------------------------------------------

    [Fact]
    public void HappyPath_AdminUser_Returns200WithCorrectFields()
    {
        var result = AuthFunction.BuildAuthResult(
            DefaultConfig(), isGlobalAdmin: false, isGlobalReader: false, isPreviewApproved: true,
            memberRole: AdminRole(), mcpCheck: McpAllowed(),
            hasTenantAdmins: true,
            TenantId, Upn, DisplayName, ObjectId);

        Assert.True(result.IsSuccess);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.False(result.NeedsAutoAdmin);

        var body = ToDynamic(result.Body);
        Assert.Equal(TenantId, (string)body.tenantId);
        Assert.Equal(Upn, (string)body.upn);
        Assert.Equal(DisplayName, (string)body.displayName);
        Assert.Equal(ObjectId, (string)body.objectId);
        Assert.False((bool)body.isGlobalAdmin);
        Assert.True((bool)body.isTenantAdmin);
        Assert.Equal(Constants.TenantRoles.Admin, (string)body.role);
        Assert.True((bool)body.canManageBootstrapTokens);
        Assert.True((bool)body.hasMcpAccess);
    }

    [Fact]
    public void HappyPath_OperatorUser_ReturnsCorrectRole()
    {
        var result = AuthFunction.BuildAuthResult(
            DefaultConfig(), isGlobalAdmin: false, isGlobalReader: false, isPreviewApproved: true,
            memberRole: OperatorRole(), mcpCheck: McpDenied(),
            hasTenantAdmins: true,
            TenantId, Upn, DisplayName, ObjectId);

        Assert.True(result.IsSuccess);
        var body = ToDynamic(result.Body);
        Assert.Equal(Constants.TenantRoles.Operator, (string)body.role);
        Assert.False((bool)body.isTenantAdmin);
        Assert.False((bool)body.canManageBootstrapTokens);
        Assert.False((bool)body.hasMcpAccess);
    }

    [Fact]
    public void HappyPath_ViewerUser_ReturnsCorrectRole()
    {
        var result = AuthFunction.BuildAuthResult(
            DefaultConfig(), isGlobalAdmin: false, isGlobalReader: false, isPreviewApproved: true,
            memberRole: ViewerRole(), mcpCheck: McpDenied(),
            hasTenantAdmins: true,
            TenantId, Upn, DisplayName, ObjectId);

        Assert.True(result.IsSuccess);
        var body = ToDynamic(result.Body);
        Assert.Equal(Constants.TenantRoles.Viewer, (string)body.role);
        Assert.False((bool)body.isTenantAdmin);
    }

    [Fact]
    public void HappyPath_GlobalAdmin_ReturnsIsGlobalAdminTrue()
    {
        var result = AuthFunction.BuildAuthResult(
            DefaultConfig(), isGlobalAdmin: true, isGlobalReader: false, isPreviewApproved: true,
            memberRole: AdminRole(), mcpCheck: McpAllowed(),
            hasTenantAdmins: true,
            TenantId, Upn, DisplayName, ObjectId);

        Assert.True(result.IsSuccess);
        var body = ToDynamic(result.Body);
        Assert.True((bool)body.isGlobalAdmin);
    }

    [Fact]
    public void HappyPath_GlobalReader_ReturnsIsGlobalReaderTrue_NotGlobalAdmin()
    {
        var result = AuthFunction.BuildAuthResult(
            DefaultConfig(), isGlobalAdmin: false, isGlobalReader: true, isPreviewApproved: true,
            memberRole: null, mcpCheck: McpAllowed(),
            hasTenantAdmins: true,
            TenantId, Upn, DisplayName, ObjectId);

        Assert.True(result.IsSuccess);
        var body = ToDynamic(result.Body);
        Assert.False((bool)body.isGlobalAdmin);
        Assert.True((bool)body.isGlobalReader);
    }

    [Fact]
    public void PreviewGate_GlobalReaderBypassesPreviewGate()
    {
        // A read-only platform reader is staff and must bypass the private-preview gate, exactly like a GA.
        var result = AuthFunction.BuildAuthResult(
            DefaultConfig(), isGlobalAdmin: false, isGlobalReader: true, isPreviewApproved: false,
            memberRole: null, mcpCheck: McpAllowed(),
            hasTenantAdmins: true,
            TenantId, Upn, DisplayName, ObjectId);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void AutoAdmin_NeverTriggered_ForGlobalReader_EvenAsFirstUser()
    {
        // GlobalReader is additive (it never removes tenant rights) but must not SILENTLY acquire new
        // write power: the first-login auto-admin convenience is declined for a read-only-flagged
        // identity. (Explicit TenantAdmins membership would still grant write — that's the additive path.)
        var result = AuthFunction.BuildAuthResult(
            DefaultConfig(), isGlobalAdmin: false, isGlobalReader: true, isPreviewApproved: true,
            memberRole: null,
            mcpCheck: McpAllowed(),
            hasTenantAdmins: false, // first user in this tenant
            TenantId, Upn, DisplayName, ObjectId);

        Assert.True(result.IsSuccess);
        Assert.False(result.NeedsAutoAdmin);
        var body = ToDynamic(result.Body);
        Assert.False((bool)body.isTenantAdmin);
    }

    // -------------------------------------------------------------------------
    // Gate 1: Suspended Tenant
    // -------------------------------------------------------------------------

    [Fact]
    public void SuspendedTenant_Returns403WithTenantSuspendedError()
    {
        var config = DefaultConfig();
        config.Disabled = true;

        var result = AuthFunction.BuildAuthResult(
            config, isGlobalAdmin: false, isGlobalReader: false, isPreviewApproved: true,
            memberRole: AdminRole(), mcpCheck: McpAllowed(),
            hasTenantAdmins: true,
            TenantId, Upn, DisplayName, ObjectId);

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        var body = ToDynamic(result.Body);
        Assert.Equal("TenantSuspended", (string)body.error);
    }

    [Fact]
    public void SuspendedTenant_WithCustomReason_ReturnsCustomMessage()
    {
        var config = DefaultConfig();
        config.Disabled = true;
        config.DisabledReason = "Terms of service violation";

        var result = AuthFunction.BuildAuthResult(
            config, isGlobalAdmin: false, isGlobalReader: false, isPreviewApproved: true,
            memberRole: AdminRole(), mcpCheck: McpAllowed(),
            hasTenantAdmins: true,
            TenantId, Upn, DisplayName, ObjectId);

        Assert.False(result.IsSuccess);
        var body = ToDynamic(result.Body);
        Assert.Equal("Terms of service violation", (string)body.message);
    }

    [Fact]
    public void SuspendedTenant_BlocksEvenGlobalAdmin()
    {
        var config = DefaultConfig();
        config.Disabled = true;

        var result = AuthFunction.BuildAuthResult(
            config, isGlobalAdmin: true, isGlobalReader: false, isPreviewApproved: true,
            memberRole: AdminRole(), mcpCheck: McpAllowed(),
            hasTenantAdmins: true,
            TenantId, Upn, DisplayName, ObjectId);

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        var body = ToDynamic(result.Body);
        Assert.Equal("TenantSuspended", (string)body.error);
    }

    [Fact]
    public void SuspendedTenant_WithExpiredDisabledUntil_IsNotBlocked()
    {
        var config = DefaultConfig();
        config.Disabled = true;
        config.DisabledUntil = DateTime.UtcNow.AddHours(-1); // expired

        var result = AuthFunction.BuildAuthResult(
            config, isGlobalAdmin: false, isGlobalReader: false, isPreviewApproved: true,
            memberRole: AdminRole(), mcpCheck: McpAllowed(),
            hasTenantAdmins: true,
            TenantId, Upn, DisplayName, ObjectId);

        Assert.True(result.IsSuccess);
    }

    // -------------------------------------------------------------------------
    // Gate 2: Preview Gate
    // -------------------------------------------------------------------------

    [Fact]
    public void PreviewGate_NotApproved_Returns403PrivatePreview()
    {
        var result = AuthFunction.BuildAuthResult(
            DefaultConfig(), isGlobalAdmin: false, isGlobalReader: false, isPreviewApproved: false,
            memberRole: AdminRole(), mcpCheck: McpAllowed(),
            hasTenantAdmins: true,
            TenantId, Upn, DisplayName, ObjectId);

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        var body = ToDynamic(result.Body);
        Assert.Equal("PrivatePreview", (string)body.error);
    }

    [Fact]
    public void PreviewGate_GlobalAdminBypassesPreviewGate()
    {
        var result = AuthFunction.BuildAuthResult(
            DefaultConfig(), isGlobalAdmin: true, isGlobalReader: false, isPreviewApproved: false,
            memberRole: AdminRole(), mcpCheck: McpAllowed(),
            hasTenantAdmins: true,
            TenantId, Upn, DisplayName, ObjectId);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void PreviewGate_DelegatedAdminBypasses_AndSurfacesManagedTenants()
    {
        // An MSP admin whose own home tenant is NOT preview-approved still logs in (explicitly authorized
        // to manage OTHER tenants), and the response carries the managed tenant set + isDelegated flag.
        const string managed = "22222222-2222-2222-2222-222222222222";
        var result = AuthFunction.BuildAuthResult(
            DefaultConfig(), isGlobalAdmin: false, isGlobalReader: false, isPreviewApproved: false,
            memberRole: null, mcpCheck: McpDenied(),
            hasTenantAdmins: true,
            TenantId, Upn, DisplayName, ObjectId,
            delegatedTenantIds: new[] { managed });

        Assert.True(result.IsSuccess);
        var body = ToDynamic(result.Body);
        Assert.True((bool)body.isDelegated);
        Assert.Contains(managed, (IReadOnlyCollection<string>)body.delegatedTenantIds);
    }

    [Fact]
    public void NonDelegatedUser_HasEmptyDelegatedTenantIds()
    {
        var result = AuthFunction.BuildAuthResult(
            DefaultConfig(), isGlobalAdmin: false, isGlobalReader: false, isPreviewApproved: true,
            memberRole: AdminRole(), mcpCheck: McpAllowed(),
            hasTenantAdmins: true,
            TenantId, Upn, DisplayName, ObjectId);

        var body = ToDynamic(result.Body);
        Assert.False((bool)body.isDelegated);
        Assert.Empty((IReadOnlyCollection<string>)body.delegatedTenantIds);
    }

    [Fact]
    public void GatePriority_SuspendedTakesPrecedenceOverDelegatedBypass()
    {
        // Gate 1 (suspended home tenant) still wins over the delegated preview-gate bypass.
        var config = DefaultConfig();
        config.Disabled = true;

        var result = AuthFunction.BuildAuthResult(
            config, isGlobalAdmin: false, isGlobalReader: false, isPreviewApproved: false,
            memberRole: null, mcpCheck: McpDenied(),
            hasTenantAdmins: true,
            TenantId, Upn, DisplayName, ObjectId,
            delegatedTenantIds: new[] { "22222222-2222-2222-2222-222222222222" });

        Assert.False(result.IsSuccess);
        var body = ToDynamic(result.Body);
        Assert.Equal("TenantSuspended", (string)body.error);
    }

    [Fact]
    public void AutoAdmin_DelegatedFirstUser_DoesNotAutoAdminHomeTenant()
    {
        // A delegated ("MSP") admin logging into a home tenant with NO existing admins must NOT be silently
        // made its Tenant Admin — that would convert a read-only delegated assignment (for OTHER tenants)
        // into write authority over the home tenant. Regression for the preview-gate bypass opening the
        // auto-admin path to delegated callers.
        var result = AuthFunction.BuildAuthResult(
            DefaultConfig(), isGlobalAdmin: false, isGlobalReader: false, isPreviewApproved: false,
            memberRole: null, mcpCheck: McpDenied(),
            hasTenantAdmins: false,
            TenantId, Upn, DisplayName, ObjectId,
            delegatedTenantIds: new[] { "22222222-2222-2222-2222-222222222222" });

        Assert.True(result.IsSuccess);
        Assert.False(result.NeedsAutoAdmin);
        var body = ToDynamic(result.Body);
        Assert.False((bool)body.isTenantAdmin);
        Assert.Null((string?)body.role);
    }

    // -------------------------------------------------------------------------
    // Auto-Admin Logic
    // -------------------------------------------------------------------------

    [Fact]
    public void AutoAdmin_FirstUser_NoExistingAdmins_BecomesAdmin()
    {
        var result = AuthFunction.BuildAuthResult(
            DefaultConfig(), isGlobalAdmin: false, isGlobalReader: false, isPreviewApproved: true,
            memberRole: null, // no role yet
            mcpCheck: McpDenied(),
            hasTenantAdmins: false, // no existing admins
            TenantId, Upn, DisplayName, ObjectId);

        Assert.True(result.IsSuccess);
        Assert.True(result.NeedsAutoAdmin);
        var body = ToDynamic(result.Body);
        Assert.True((bool)body.isTenantAdmin);
        Assert.Equal(Constants.TenantRoles.Admin, (string)body.role);
        Assert.True((bool)body.canManageBootstrapTokens);
    }

    [Fact]
    public void AutoAdmin_NotTriggered_WhenAdminsExist()
    {
        var result = AuthFunction.BuildAuthResult(
            DefaultConfig(), isGlobalAdmin: false, isGlobalReader: false, isPreviewApproved: true,
            memberRole: null, // no role — new user but admins exist
            mcpCheck: McpDenied(),
            hasTenantAdmins: true, // admins already exist
            TenantId, Upn, DisplayName, ObjectId);

        Assert.True(result.IsSuccess);
        Assert.False(result.NeedsAutoAdmin);
        var body = ToDynamic(result.Body);
        Assert.False((bool)body.isTenantAdmin);
        Assert.Null((string?)body.role);
    }

    [Fact]
    public void AutoAdmin_NotTriggered_ForClaimDerivedOperator_WhenNoTableMembers()
    {
        // Regression guard: a claim-derived role (no table members yet) must NOT auto-promote the
        // user into the TenantAdmins table. needsAutoAdmin keys off memberRole == null, not on
        // "not admin", so an Entra app-role Operator in a claim-only tenant stays an Operator.
        var result = AuthFunction.BuildAuthResult(
            DefaultConfig(), isGlobalAdmin: false, isGlobalReader: false, isPreviewApproved: true,
            memberRole: OperatorRole(), // came from the "roles" claim
            mcpCheck: McpDenied(),
            hasTenantAdmins: false, // no explicit TenantAdmins table rows
            TenantId, Upn, DisplayName, ObjectId);

        Assert.True(result.IsSuccess);
        Assert.False(result.NeedsAutoAdmin);
        var body = ToDynamic(result.Body);
        Assert.False((bool)body.isTenantAdmin);
        Assert.Equal(Constants.TenantRoles.Operator, (string)body.role);
    }

    [Fact]
    public void AutoAdmin_NotTriggered_WhenUserAlreadyAdmin()
    {
        var result = AuthFunction.BuildAuthResult(
            DefaultConfig(), isGlobalAdmin: false, isGlobalReader: false, isPreviewApproved: true,
            memberRole: AdminRole(),
            mcpCheck: McpDenied(),
            hasTenantAdmins: true,
            TenantId, Upn, DisplayName, ObjectId);

        Assert.True(result.IsSuccess);
        Assert.False(result.NeedsAutoAdmin);
        var body = ToDynamic(result.Body);
        Assert.True((bool)body.isTenantAdmin);
    }

    // -------------------------------------------------------------------------
    // MCP Access
    // -------------------------------------------------------------------------

    [Fact]
    public void McpAccess_Allowed_ReturnsTrue()
    {
        var result = AuthFunction.BuildAuthResult(
            DefaultConfig(), isGlobalAdmin: false, isGlobalReader: false, isPreviewApproved: true,
            memberRole: AdminRole(), mcpCheck: McpAllowed(),
            hasTenantAdmins: true,
            TenantId, Upn, DisplayName, ObjectId);

        var body = ToDynamic(result.Body);
        Assert.True((bool)body.hasMcpAccess);
    }

    [Fact]
    public void McpAccess_Denied_ReturnsFalse()
    {
        var result = AuthFunction.BuildAuthResult(
            DefaultConfig(), isGlobalAdmin: false, isGlobalReader: false, isPreviewApproved: true,
            memberRole: AdminRole(), mcpCheck: McpDenied(),
            hasTenantAdmins: true,
            TenantId, Upn, DisplayName, ObjectId);

        var body = ToDynamic(result.Body);
        Assert.False((bool)body.hasMcpAccess);
    }

    // -------------------------------------------------------------------------
    // Gate Priority: Suspended takes precedence over Preview
    // -------------------------------------------------------------------------

    [Fact]
    public void GatePriority_SuspendedTakesPrecedenceOverPreviewGate()
    {
        var config = DefaultConfig();
        config.Disabled = true;

        var result = AuthFunction.BuildAuthResult(
            config, isGlobalAdmin: false, isGlobalReader: false, isPreviewApproved: false,
            memberRole: null, mcpCheck: McpDenied(),
            hasTenantAdmins: false,
            TenantId, Upn, DisplayName, ObjectId);

        Assert.False(result.IsSuccess);
        var body = ToDynamic(result.Body);
        Assert.Equal("TenantSuspended", (string)body.error);
    }

    // -------------------------------------------------------------------------
    // ExtractDomainFromUpn
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("user@contoso.com", "contoso.com")]
    [InlineData("admin@sub.domain.org", "sub.domain.org")]
    [InlineData("", "")]
    [InlineData("noemail", "")]
    public void ExtractDomainFromUpn_ReturnsExpected(string upn, string expected)
    {
        Assert.Equal(expected, AuthFunction.ExtractDomainFromUpn(upn));
    }

    // -------------------------------------------------------------------------
    // Helper
    // -------------------------------------------------------------------------

    /// <summary>
    /// Converts anonymous object to dynamic for property access in tests.
    /// </summary>
    private static dynamic ToDynamic(object obj)
    {
        var type = obj.GetType();
        var dict = new System.Dynamic.ExpandoObject() as IDictionary<string, object?>;
        foreach (var prop in type.GetProperties())
        {
            dict[prop.Name] = prop.GetValue(obj);
        }
        return dict;
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Azure.Core.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Endpoint-level tests for <see cref="TenantAdminManagementFunction"/> role handling:
/// the POST/PATCH role allow-list (Admin/Operator/Viewer) matches case-insensitively,
/// canonicalizes to the exact constant casing before anything reaches storage, and
/// rejects unknown roles with 400. Uses the same fake-HttpRequestData harness as
/// <c>SessionAnnotationFunctionsTests</c>.
/// </summary>
public class TenantAdminManagementFunctionTests
{
    private const string TenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string CallerUpn = "admin@contoso.com";

    // ── TryCanonicalizeRole ─────────────────────────────────────────────────

    [Theory]
    [InlineData("Admin", "Admin")]
    [InlineData("admin", "Admin")]
    [InlineData("OPERATOR", "Operator")]
    [InlineData("operator", "Operator")]
    [InlineData("viewer", "Viewer")]
    [InlineData("Viewer", "Viewer")]
    [InlineData("VIEWER", "Viewer")]
    [InlineData("Member", null)]
    [InlineData("GlobalAdmin", null)]
    [InlineData("", null)]
    public void TryCanonicalizeRole_matches_allow_list_case_insensitively(string input, string? expected)
    {
        Assert.Equal(expected, TenantAdminManagementFunction.TryCanonicalizeRole(input));
    }

    // ── POST /tenants/{tenantId}/admins ─────────────────────────────────────

    [Fact]
    public async Task Post_lowercase_viewer_is_persisted_as_canonical_Viewer()
    {
        var h = BuildHarness();
        var (req, ctx) = BuildRequest(new { upn = "user@contoso.com", role = "viewer" });

        var response = await h.Function.AddTenantAdmin(req, TenantId, ctx);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        h.AdminRepo.Verify(r => r.AddTenantMemberAsync(
            TenantId, "user@contoso.com", CallerUpn, Constants.TenantRoles.Viewer, false), Times.Once);
    }

    [Fact]
    public async Task Post_unknown_role_returns_400_and_never_writes()
    {
        var h = BuildHarness();
        var (req, ctx) = BuildRequest(new { upn = "user@contoso.com", role = "superuser" });

        var response = await h.Function.AddTenantAdmin(req, TenantId, ctx);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        h.AdminRepo.Verify(r => r.AddTenantMemberAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task Post_missing_role_defaults_to_Admin()
    {
        // Backward compat: older clients post only the UPN.
        var h = BuildHarness();
        var (req, ctx) = BuildRequest(new { upn = "user@contoso.com" });

        var response = await h.Function.AddTenantAdmin(req, TenantId, ctx);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        h.AdminRepo.Verify(r => r.AddTenantMemberAsync(
            TenantId, "user@contoso.com", CallerUpn, Constants.TenantRoles.Admin, false), Times.Once);
    }

    // ── PATCH /tenants/{tenantId}/admins/{adminUpn}/permissions ─────────────

    [Fact]
    public async Task Patch_lowercase_viewer_is_persisted_as_canonical_Viewer()
    {
        var h = BuildHarness();
        var (req, ctx) = BuildRequest(new { role = "viewer", canManageBootstrapTokens = false });

        var response = await h.Function.UpdateMemberPermissions(req, TenantId, "user@contoso.com", ctx);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        h.AdminRepo.Verify(r => r.UpdateMemberPermissionsAsync(
            TenantId, "user@contoso.com", Constants.TenantRoles.Viewer, false), Times.Once);
    }

    [Fact]
    public async Task Patch_unknown_role_returns_400_and_never_writes()
    {
        var h = BuildHarness();
        var (req, ctx) = BuildRequest(new { role = "root", canManageBootstrapTokens = true });

        var response = await h.Function.UpdateMemberPermissions(req, TenantId, "user@contoso.com", ctx);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        h.AdminRepo.Verify(r => r.UpdateMemberPermissionsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Patch_self_demotion_check_uses_canonical_role()
    {
        // Caller demotes THEMSELVES with a lowercase "admin" — canonicalization must
        // recognize it as Admin (no demotion) and not trip the last-admin guard.
        var h = BuildHarness();
        var (req, ctx) = BuildRequest(new { role = "admin", canManageBootstrapTokens = true });

        var response = await h.Function.UpdateMemberPermissions(req, TenantId, CallerUpn, ctx);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        h.AdminRepo.Verify(r => r.UpdateMemberPermissionsAsync(
            TenantId, CallerUpn, Constants.TenantRoles.Admin, true), Times.Once);
    }

    // ── revocation cuts live SignalR streams (join-time-only group authz) ────

    [Fact]
    public async Task Remove_disconnects_the_removed_members_signalr_connections()
    {
        var h = BuildHarness();
        var (req, ctx) = BuildRequest(new { });

        var response = await h.Function.RemoveTenantAdmin(req, TenantId, "Demoted@Contoso.com", ctx);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "demoted@contoso.com" }, h.SignalR.DisconnectedUsers);
    }

    [Fact]
    public async Task Disable_disconnects_the_disabled_members_signalr_connections()
    {
        var h = BuildHarness();
        var (req, ctx) = BuildRequest(new { });

        var response = await h.Function.DisableTenantAdmin(req, TenantId, "Demoted@Contoso.com", ctx);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "demoted@contoso.com" }, h.SignalR.DisconnectedUsers);
    }

    [Fact]
    public async Task Enable_does_not_disconnect()
    {
        var h = BuildHarness();
        var (req, ctx) = BuildRequest(new { });

        await h.Function.EnableTenantAdmin(req, TenantId, "user@contoso.com", ctx);

        Assert.Empty(h.SignalR.DisconnectedUsers);
    }

    [Fact]
    public async Task Demotion_below_admin_disconnects_but_promotion_to_admin_does_not()
    {
        var h = BuildHarness();

        var (demote, ctx1) = BuildRequest(new { role = "viewer", canManageBootstrapTokens = false });
        await h.Function.UpdateMemberPermissions(demote, TenantId, "User@Contoso.com", ctx1);
        Assert.Equal(new[] { "user@contoso.com" }, h.SignalR.DisconnectedUsers);

        var (promote, ctx2) = BuildRequest(new { role = "admin", canManageBootstrapTokens = false });
        await h.Function.UpdateMemberPermissions(promote, TenantId, "other@contoso.com", ctx2);
        Assert.Equal(new[] { "user@contoso.com" }, h.SignalR.DisconnectedUsers);
    }

    // ── harness ─────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public TenantAdminManagementFunction Function = default!;
        public Mock<IAdminRepository> AdminRepo = default!;
        public FakeSignalRNotificationService SignalR = default!;
    }

    private static Harness BuildHarness()
    {
        var adminRepo = new Mock<IAdminRepository>(MockBehavior.Loose);
        adminRepo.Setup(r => r.AddTenantMemberAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(true);
        adminRepo.Setup(r => r.UpdateMemberPermissionsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(true);

        var service = new TenantAdminsService(
            adminRepo.Object,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<TenantAdminsService>.Instance);

        var maintenanceRepo = new Mock<IMaintenanceRepository>(MockBehavior.Loose);
        var signalR = new FakeSignalRNotificationService();

        return new Harness
        {
            Function = new TenantAdminManagementFunction(
                NullLogger<TenantAdminManagementFunction>.Instance, service, maintenanceRepo.Object, signalR),
            AdminRepo = adminRepo,
            SignalR = signalR,
        };
    }

    private static (HttpRequestData Req, FunctionContext Ctx) BuildRequest(object body)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        // Production wire settings (camelCase policy binds camelCase test bodies to the
        // PascalCase request models exactly like real web requests do; absent-when-null and
        // string enums match what the deployed worker serializes).
        services.Configure<WorkerOptions>(o => o.Serializer =
            new JsonObjectSerializer(ApiJsonOptions.Create()));
        var provider = services.BuildServiceProvider();

        var requestContext = new RequestContext
        {
            TenantId = TenantId,
            TargetTenantId = TenantId,
            UserPrincipalName = CallerUpn,
            IsTenantAdmin = true,
            UserRole = Constants.TenantRoles.Admin,
        };

        var context = new Mock<FunctionContext>();
        context.SetupGet(c => c.Items).Returns(
            new Dictionary<object, object> { [RequestContext.ItemsKey] = requestContext });
        context.SetupGet(c => c.InstanceServices).Returns(provider);

        var req = new Mock<HttpRequestData>(context.Object);
        req.SetupGet(r => r.Headers).Returns(new HttpHeadersCollection());
        req.SetupGet(r => r.Body).Returns(
            new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body))));
        req.Setup(r => r.CreateResponse()).Returns(() => new FakeHttpResponseData(context.Object));
        return (req.Object, context.Object);
    }

    private sealed class FakeHttpResponseData : HttpResponseData
    {
        public FakeHttpResponseData(FunctionContext context) : base(context) { }
        public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public override HttpHeadersCollection Headers { get; set; } = new();
        public override Stream Body { get; set; } = new MemoryStream();
        public override HttpCookies Cookies { get; } = new Mock<HttpCookies>().Object;
    }
}

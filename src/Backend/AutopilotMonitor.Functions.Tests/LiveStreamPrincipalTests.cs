using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Functions.Infrastructure;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Azure.Core.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// A live SignalR connection is addressed by its principal (sign-in tenant plus principal key), and a revoke names the
/// tenant the revoked grant resolved under, so an action in one tenant never restarts the same UPN's connections in
/// another. Pins the ends of that chain: negotiate binds the pair from the validated request context,
/// <see cref="LiveStreamRevoker"/> finds the tenant a platform or delegated grant was exercised under, the
/// identity-binding routes cut the previous principal, and the call-site inventory keeps these the only paths.
/// </summary>
public class LiveStreamPrincipalTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string OtherTenantId = "22222222-2222-2222-2222-222222222222";
    private const string Upn = "person@partner.example";
    private const string OperatorUpn = "ga@vendor.example";

    // ── negotiate ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Negotiate_binds_the_connection_to_the_sign_in_tenant_and_principal_key()
    {
        var signalR = new FakeSignalRNotificationService();
        var req = BuildRequest(new RequestContext { TenantId = TenantId, UserPrincipalName = "Person@Partner.Example" });

        var response = await new SignalRNegotiateFunction(NullLogger<SignalRNegotiateFunction>.Instance, signalR).Negotiate(req);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new string?[] { SignalRGroupHelper.UserId(TenantId, Upn) }, signalR.NegotiatedUserIds);
    }

    [Fact]
    public async Task Negotiate_without_a_principal_key_binds_no_user_id()
    {
        // Every revocable join gate needs the key, so such a connection holds nothing a revoke would have to reach;
        // a login the portal keeps on token claims alone still gets its Progress Portal live updates.
        var signalR = new FakeSignalRNotificationService();
        var req = BuildRequest(new RequestContext { TenantId = TenantId, UserPrincipalName = string.Empty });

        var response = await new SignalRNegotiateFunction(NullLogger<SignalRNegotiateFunction>.Instance, signalR).Negotiate(req);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new string?[] { null }, signalR.NegotiatedUserIds);
    }

    [Fact]
    public async Task Negotiate_unauthenticated_is_refused_without_negotiating()
    {
        var signalR = new FakeSignalRNotificationService();
        var req = BuildRequest(new RequestContext { TenantId = TenantId, UserPrincipalName = Upn }, authenticated: false);

        var response = await new SignalRNegotiateFunction(NullLogger<SignalRNegotiateFunction>.Instance, signalR).Negotiate(req);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(signalR.NegotiatedUserIds);
    }

    // ── LiveStreamRevoker ────────────────────────────────────────────────────

    private static (LiveStreamRevoker Revoker, FakeSignalRNotificationService SignalR, StubAdminIdentityBindingService Bindings) BuildRevoker()
    {
        var signalR = new FakeSignalRNotificationService();
        var bindings = new StubAdminIdentityBindingService();
        return (new LiveStreamRevoker(signalR, bindings, NullLogger<LiveStreamRevoker>.Instance), signalR, bindings);
    }

    [Fact]
    public async Task Cut_addresses_exactly_the_named_tenant()
    {
        var (revoker, signalR, bindings) = BuildRevoker();
        bindings.BoundTenants[Upn] = OtherTenantId;

        await revoker.CutAsync(TenantId, Upn);

        Assert.Equal(new[] { SignalRGroupHelper.UserId(TenantId, Upn) }, signalR.DisconnectedUsers);
        Assert.Equal(0, bindings.GetCalls);
    }

    [Fact]
    public async Task CutBoundIdentity_addresses_the_tenant_of_the_binding()
    {
        var (revoker, signalR, bindings) = BuildRevoker();
        bindings.BoundTenants[Upn] = OtherTenantId;

        await revoker.CutBoundIdentityAsync(Upn);

        Assert.Equal(new[] { SignalRGroupHelper.UserId(OtherTenantId, Upn) }, signalR.DisconnectedUsers);
    }

    [Fact]
    public async Task CutBoundIdentity_without_a_binding_cuts_nothing()
    {
        // No binding ⇒ no platform or delegated grant of the UPN ever resolved ⇒ no connection holds its groups.
        var (revoker, signalR, _) = BuildRevoker();

        await revoker.CutBoundIdentityAsync(Upn);

        Assert.Empty(signalR.DisconnectedUsers);
    }

    [Fact]
    public async Task CutBoundIdentity_survives_a_failed_binding_read_without_cutting()
    {
        var (revoker, signalR, bindings) = BuildRevoker();
        bindings.GetThrows = new InvalidOperationException("storage unavailable");

        await revoker.CutBoundIdentityAsync(Upn);

        Assert.Empty(signalR.DisconnectedUsers);
    }

    // ── identity-binding routes cut the previous principal ───────────────────

    private static (IdentityBindingManagementFunction Fn, FakeSignalRNotificationService SignalR, StubAdminIdentityBindingService Bindings) BuildBindingFn()
    {
        var (revoker, signalR, bindings) = BuildRevoker();
        var fn = new IdentityBindingManagementFunction(
            NullLogger<IdentityBindingManagementFunction>.Instance, bindings, Mock.Of<IMaintenanceRepository>(), revoker);
        return (fn, signalR, bindings);
    }

    private static RequestContext Operator() => new() { TenantId = OtherTenantId, UserPrincipalName = OperatorUpn, IsGlobalAdmin = true };

    [Fact]
    public async Task Replacing_a_binding_cuts_the_connections_signed_in_under_the_previous_one()
    {
        var (fn, signalR, bindings) = BuildBindingFn();
        bindings.BoundTenants[Upn] = TenantId;
        var req = BuildRequest(Operator(), new { homeTenantId = OtherTenantId });

        var response = await fn.PutIdentityBinding(req, Upn, req.FunctionContext);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { SignalRGroupHelper.UserId(TenantId, Upn) }, signalR.DisconnectedUsers);
    }

    [Fact]
    public async Task Creating_a_binding_cuts_nothing()
    {
        var (fn, signalR, _) = BuildBindingFn();
        var req = BuildRequest(Operator(), new { homeTenantId = TenantId });

        var response = await fn.PutIdentityBinding(req, Upn, req.FunctionContext);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(signalR.DisconnectedUsers);
    }

    [Fact]
    public async Task Deleting_a_binding_cuts_the_connections_signed_in_under_it()
    {
        var (fn, signalR, bindings) = BuildBindingFn();
        bindings.BoundTenants[Upn] = TenantId;
        var req = BuildRequest(Operator());

        var response = await fn.DeleteIdentityBinding(req, Upn, req.FunctionContext);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { SignalRGroupHelper.UserId(TenantId, Upn) }, signalR.DisconnectedUsers);
    }

    [Fact]
    public async Task Deleting_a_missing_binding_is_404_and_cuts_nothing()
    {
        var (fn, signalR, _) = BuildBindingFn();
        var req = BuildRequest(Operator());

        var response = await fn.DeleteIdentityBinding(req, Upn, req.FunctionContext);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(signalR.DisconnectedUsers);
    }

    // ── call-site inventory ──────────────────────────────────────────────────

    [Fact]
    public void Only_the_revoker_cuts_streams_and_only_negotiate_binds_connections()
    {
        // A second cut site would have to decide the principal on its own — the decision this change put in one place.
        var functionsRoot = Path.Combine(FindRepoRoot(), "src", "Backend", "AutopilotMonitor.Functions");
        var cuts = new List<string>();
        var negotiations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(functionsRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(functionsRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = File.ReadAllText(file);
            if (text.Contains(".DisconnectUserAsync(", StringComparison.Ordinal))
                cuts.Add(relative);
            if (text.Contains(".NegotiateClientAsync(", StringComparison.Ordinal))
                negotiations.Add(relative);
        }

        Assert.Equal(new[] { "Services/LiveStreamRevoker.cs" }, cuts);
        Assert.Equal(new[] { "Functions/Infrastructure/SignalRNegotiateFunction.cs" }, negotiations);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static HttpRequestData BuildRequest(RequestContext requestContext, object? body = null, bool authenticated = true)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        // Production wire settings, so camelCase test bodies bind to the request models like real requests do.
        services.Configure<WorkerOptions>(o => o.Serializer = new JsonObjectSerializer(ApiJsonOptions.Create()));
        var provider = services.BuildServiceProvider();

        var items = new Dictionary<object, object> { [RequestContext.ItemsKey] = requestContext };
        if (authenticated)
            items["ClaimsPrincipal"] = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tid", requestContext.TenantId) }, "test"));

        var context = new Mock<FunctionContext>();
        context.SetupGet(c => c.Items).Returns(items);
        context.SetupGet(c => c.InstanceServices).Returns(provider);

        var req = new Mock<HttpRequestData>(context.Object);
        req.SetupGet(r => r.Headers).Returns(new HttpHeadersCollection());
        req.SetupGet(r => r.Body).Returns(new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body ?? new { }))));
        req.Setup(r => r.CreateResponse()).Returns(() => new FakeHttpResponseData(context.Object));
        return req.Object;
    }

    private sealed class FakeHttpResponseData : HttpResponseData
    {
        public FakeHttpResponseData(FunctionContext context) : base(context) { }
        public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public override HttpHeadersCollection Headers { get; set; } = new();
        public override Stream Body { get; set; } = new MemoryStream();
        public override HttpCookies Cookies { get; } = new Mock<HttpCookies>().Object;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

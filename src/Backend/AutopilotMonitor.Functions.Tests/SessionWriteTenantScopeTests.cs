using AutopilotMonitor.Functions.Functions.Sessions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// A write on one session acts in that session's tenant: the portal sends it as ?tenantId=, the
/// middleware validates it (TenantScoping.QueryParam), and a Global Admin on a foreign tenant's session
/// reaches that tenant. Before, mark-failed/-succeeded and actions took the caller's token tenant and
/// answered 404 for a Global Admin on a foreign session.
/// </summary>
public class SessionWriteTenantScopeTests
{
    private const string HomeTenant = "11111111-1111-1111-1111-111111111111";
    private const string OtherTenant = "22222222-2222-2222-2222-222222222222";
    private const string SessionId = "33333333-3333-3333-3333-333333333333";

    /// <summary>
    /// Every route on one session takes the tenant from ?tenantId=. The only exception, the report,
    /// carries the session's tenant in its body and checks it in the function itself.
    /// </summary>
    [Fact]
    public void Every_session_route_is_query_param_scoped()
    {
        var exceptions = new HashSet<string> { "POST sessions/{sessionId}/report" };

        var routes = EndpointAccessPolicyCatalog.Entries
            .Where(e => e.RouteTemplate.StartsWith("sessions/{sessionId}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(routes);

        var unscoped = routes
            .Where(e => e.TenantScoping != TenantScoping.QueryParam)
            .Select(e => $"{e.HttpMethod} {e.RouteTemplate}")
            .Where(r => !exceptions.Contains(r))
            .ToList();
        Assert.True(unscoped.Count == 0,
            "Session routes must take the session's tenant from ?tenantId= (TenantScoping.QueryParam):\n"
            + string.Join("\n", unscoped.Select(r => $"  - {r}")));

        foreach (var exception in exceptions)
            Assert.Contains(routes, e => $"{e.HttpMethod} {e.RouteTemplate}" == exception);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("succeeded")]
    public async Task Mark_by_a_global_admin_acts_in_the_session_tenant(string outcome)
    {
        var repo = SessionRepo();
        var req = Request(GlobalAdmin());

        await RunMark(outcome, repo, req);

        Assert.Equal(OtherTenant, StatusUpdateTenant(repo));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("succeeded")]
    public async Task Mark_by_a_tenant_admin_stays_in_the_validated_tenant(string outcome)
    {
        var repo = SessionRepo();
        var req = Request(TenantAdmin());

        await RunMark(outcome, repo, req);

        Assert.Equal(HomeTenant, StatusUpdateTenant(repo));
    }

    [Fact]
    public async Task Action_by_a_global_admin_is_queued_in_the_session_tenant()
    {
        var repo = SessionRepo();
        var req = Request(GlobalAdmin(), """{"type":"request_diagnostics"}""");

        await ActionFunction(repo).Run(req, SessionId);

        repo.Verify(r => r.QueueServerActionAsync(OtherTenant, SessionId, It.IsAny<ServerAction>()), Times.Once);
    }

    [Fact]
    public async Task Action_by_an_operator_stays_in_the_validated_tenant()
    {
        var repo = SessionRepo();
        var operatorContext = new RequestContext
        {
            TenantId = HomeTenant,
            TargetTenantId = HomeTenant,
            UserPrincipalName = "operator@contoso.com",
            UserRole = Constants.TenantRoles.Operator,
        };
        var req = Request(operatorContext, """{"type":"request_diagnostics"}""");

        await ActionFunction(repo).Run(req, SessionId);

        repo.Verify(r => r.QueueServerActionAsync(HomeTenant, SessionId, It.IsAny<ServerAction>()), Times.Once);
    }

    // The lookup always points at the other tenant, so only the caller's reach decides where the write lands.
    private static Mock<ISessionRepository> SessionRepo()
    {
        var repo = new Mock<ISessionRepository>(MockBehavior.Loose);
        repo.Setup(r => r.ResolveSessionTenantIdAsync(SessionId)).ReturnsAsync(OtherTenant);
        repo.Setup(r => r.GetSessionAsync(OtherTenant, SessionId))
            .ReturnsAsync(new SessionSummary { SessionId = SessionId, TenantId = OtherTenant });
        return repo;
    }

    private static RequestContext GlobalAdmin() => new()
    {
        TenantId = HomeTenant,
        TargetTenantId = HomeTenant,
        UserPrincipalName = "ga@contoso.com",
        IsGlobalAdmin = true,
        UserRole = Constants.GlobalRoles.GlobalAdmin,
    };

    private static RequestContext TenantAdmin() => new()
    {
        TenantId = HomeTenant,
        TargetTenantId = HomeTenant,
        UserPrincipalName = "admin@contoso.com",
        IsTenantAdmin = true,
        UserRole = Constants.TenantRoles.Admin,
    };

    private static HttpRequestData Request(RequestContext requestContext, string? body = null)
    {
        var (req, context) = EndpointHarness.Request(HomeTenant, jsonBody: body);
        context.Items["RequestContext"] = requestContext;
        return req;
    }

    private static async Task RunMark(string outcome, Mock<ISessionRepository> repo, HttpRequestData req)
    {
        var maintenance = Mock.Of<IMaintenanceRepository>();
        if (outcome == "failed")
            await new MarkSessionFailedFunction(NullLogger<MarkSessionFailedFunction>.Instance, repo.Object, maintenance).Run(req, SessionId);
        else
            await new MarkSessionSucceededFunction(NullLogger<MarkSessionSucceededFunction>.Instance, repo.Object, maintenance).Run(req, SessionId);
    }

    private static string? StatusUpdateTenant(Mock<ISessionRepository> repo)
    {
        var call = Assert.Single(repo.Invocations, i => i.Method.Name == nameof(ISessionRepository.UpdateSessionStatusAsync));
        return call.Arguments[0] as string;
    }

    private static QueueSessionActionFunction ActionFunction(Mock<ISessionRepository> repo)
    {
        var adminConfig = new Mock<AdminConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance,
            new MemoryCache(new MemoryCacheOptions())) { CallBase = false };
        var opsEvents = new OpsEventService(
            Mock.Of<IOpsEventRepository>(), NullLogger<OpsEventService>.Instance,
            TestNotifications.InertOpsAlertDispatch(adminConfig.Object));
        return new QueueSessionActionFunction(
            NullLogger<QueueSessionActionFunction>.Instance, repo.Object,
            new TelemetryClient(new TelemetryConfiguration { DisableTelemetry = true }), opsEvents);
    }
}

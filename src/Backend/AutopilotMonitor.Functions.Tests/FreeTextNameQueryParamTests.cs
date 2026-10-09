using System.Net;
using System.Security.Claims;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Functions.Apps;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// App names and serial numbers are free text and may contain '/', '%' and '+'. They travel as query
/// values: in a path segment the host decodes an escaped slash to a separator before routing (a 404
/// before any function runs), and a catalog placeholder never matches a '/'. The encoded forms below
/// are what the portal's URLSearchParams sends; req.Query decodes them exactly once.
/// </summary>
public class FreeTextNameQueryParamTests
{
    private const string TenantId = "00000000-0000-0000-0000-0000000000a7";

    public static TheoryData<string, string> Names => new()
    {
        { "a/b", "a%2Fb" },
        { "100%", "100%25" },
        { "x+y", "x%2By" },
        { "Company Portal", "Company+Portal" },
        // A literal escape sequence survives; the former second decode turned it into "a b".
        { "a%20b", "a%2520b" },
    };

    // ── Policy catalog ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "/api/apps/analytics", "apps/analytics", EndpointPolicy.MemberRead, TenantScoping.None)]
    [InlineData("GET", "/api/apps/sessions", "apps/sessions", EndpointPolicy.MemberRead, TenantScoping.None)]
    [InlineData("GET", "/api/global/apps/analytics", "global/apps/analytics", EndpointPolicy.GlobalReadOrAdmin, TenantScoping.QueryParam)]
    [InlineData("GET", "/api/global/apps/sessions", "global/apps/sessions", EndpointPolicy.GlobalReadOrAdmin, TenantScoping.QueryParam)]
    [InlineData("DELETE", "/api/devices/block", "devices/block", EndpointPolicy.GlobalAdminOnly, TenantScoping.None)]
    public void Free_name_routes_are_literal_and_keep_their_policy(
        string method, string path, string template, EndpointPolicy policy, TenantScoping scoping)
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy(method, path);

        Assert.NotNull(entry);
        Assert.Equal(template, entry!.RouteTemplate);
        Assert.Equal(policy, entry.Policy);
        Assert.Equal(scoping, entry.TenantScoping);
    }

    [Fact]
    public void No_route_carries_an_app_name_or_serial_number_in_the_path()
    {
        var offenders = EndpointAccessPolicyCatalog.Entries
            .Where(e => e.RouteTemplate.Contains("{appName}", StringComparison.OrdinalIgnoreCase)
                     || e.RouteTemplate.Contains("serialNumber}", StringComparison.OrdinalIgnoreCase))
            .Select(e => $"{e.HttpMethod} {e.RouteTemplate}")
            .ToList();

        Assert.Empty(offenders);
    }

    // ── Query path: the name reaches the handler unchanged ──────────────────

    [Theory]
    [MemberData(nameof(Names))]
    public async Task App_analytics_matches_the_exact_name(string name, string encoded)
    {
        var function = new GetAppAnalyticsFunction(
            NullLogger<GetAppAnalyticsFunction>.Instance, MetricsWith(name), Sessions(), RegressionTracker());
        var req = MemberRequest($"?appName={encoded}&days=30");

        var res = await function.Run(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var root = Json(res);
        Assert.Equal(name, root.GetProperty("appName").GetString());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("totalInstalls").GetInt32());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task App_sessions_match_the_exact_name(string name, string encoded)
    {
        var function = new GetAppSessionsFunction(NullLogger<GetAppSessionsFunction>.Instance, MetricsWith(name), Sessions());
        var req = MemberRequest($"?appName={encoded}&days=30");

        var res = await function.Run(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, Json(res).GetProperty("total").GetInt32());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Global_app_analytics_and_sessions_match_the_exact_name(string name, string encoded)
    {
        var analytics = new GetGlobalAppAnalyticsFunction(
            NullLogger<GetGlobalAppAnalyticsFunction>.Instance, MetricsWith(name), Sessions(), RegressionTracker());
        var sessions = new GetGlobalAppSessionsFunction(NullLogger<GetGlobalAppSessionsFunction>.Instance, MetricsWith(name), Sessions());
        var query = $"?appName={encoded}&days=30&tenantId={TenantId}";

        var analyticsRes = await analytics.Run(EndpointHarness.Request(TenantId, queryString: query).Req);
        var sessionsRes = await sessions.Run(EndpointHarness.Request(TenantId, queryString: query).Req);

        Assert.Equal(1, Json(analyticsRes).GetProperty("summary").GetProperty("totalInstalls").GetInt32());
        Assert.Equal(1, Json(sessionsRes).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task App_routes_without_a_name_are_a_bad_request()
    {
        var analytics = new GetAppAnalyticsFunction(
            NullLogger<GetAppAnalyticsFunction>.Instance, MetricsWith("x"), Sessions(), RegressionTracker());
        var globalSessions = new GetGlobalAppSessionsFunction(NullLogger<GetGlobalAppSessionsFunction>.Instance, MetricsWith("x"), Sessions());

        Assert.Equal(HttpStatusCode.BadRequest, (await analytics.Run(MemberRequest("?days=30"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await globalSessions.Run(EndpointHarness.Request(TenantId, queryString: "?appName=%20&days=30").Req)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Unblock_receives_the_exact_serial_number(string serial, string encoded)
    {
        var repo = new Mock<IDeviceSecurityRepository>();
        repo.Setup(r => r.UnblockDeviceAsync(TenantId, serial)).ReturnsAsync(Array.Empty<string>());
        var function = UnblockFunction(repo.Object);

        var res = await function.UnblockDevice(
            EndpointHarness.Request(TenantId, queryString: $"?serialNumber={encoded}&tenantId={TenantId}").Req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        repo.Verify(r => r.UnblockDeviceAsync(TenantId, serial), Times.Once);
    }

    [Fact]
    public async Task Unblock_without_a_serial_number_is_a_bad_request()
    {
        var repo = new Mock<IDeviceSecurityRepository>();
        var function = UnblockFunction(repo.Object);

        var res = await function.UnblockDevice(EndpointHarness.Request(TenantId, queryString: $"?tenantId={TenantId}").Req);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        repo.Verify(r => r.UnblockDeviceAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // ── Rig ──────────────────────────────────────────────────────────────────

    /// <summary>One finished install of <paramref name="name"/>, plus decoys named after its parts.</summary>
    private static IMetricsRepository MetricsWith(string name)
    {
        AppInstallSummary Install(string appName, string sessionId) => new()
        {
            TenantId = TenantId,
            SessionId = sessionId,
            AppName = appName,
            Status = "Succeeded",
            StartedAt = DateTime.UtcNow.AddHours(-2),
            DurationSeconds = 30,
        };

        var summaries = new List<AppInstallSummary> { Install(name, "s-1"), Install("a", "s-2"), Install("b", "s-3"), Install("a b", "s-4") };
        var repo = new Mock<IMetricsRepository>();
        repo.Setup(r => r.GetAppsDashboardSummariesAsync(It.IsAny<DateTime>(), It.IsAny<string?>()))
            .ReturnsAsync(() => summaries.ToList());
        return repo.Object;
    }

    private static ISessionRepository Sessions()
    {
        var repo = new Mock<ISessionRepository>();
        repo.Setup(r => r.GetSessionAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((SessionSummary?)null);
        return repo.Object;
    }

    private static IHardwareRejectionNotificationTracker RegressionTracker()
    {
        var tracker = new Mock<IHardwareRejectionNotificationTracker>();
        tracker.Setup(t => t.GetAppVersionRegressionsAsync(It.IsAny<string>())).ReturnsAsync(new List<AppVersionRegressionAlert>());
        return tracker.Object;
    }

    /// <summary>A member request: the JWT tenant comes from the principal the authentication middleware would set.</summary>
    private static HttpRequestData MemberRequest(string queryString)
    {
        var (req, context) = EndpointHarness.Request(TenantId, queryString: queryString);
        context.Items["ClaimsPrincipal"] = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tid", TenantId) }, "test"));
        return req;
    }

    private static DeviceBlockFunction UnblockFunction(IDeviceSecurityRepository securityRepo)
    {
        var maintenance = new Mock<IMaintenanceRepository>();
        maintenance
            .Setup(m => m.LogAuditEntryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()))
            .ReturnsAsync(true);
        var adminConfig = new Mock<AdminConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance,
            new MemoryCache(new MemoryCacheOptions())) { CallBase = false };
        var opsEvents = new OpsEventService(
            Mock.Of<IOpsEventRepository>(), NullLogger<OpsEventService>.Instance,
            TestNotifications.InertOpsAlertDispatch(adminConfig.Object));

        return new DeviceBlockFunction(
            NullLogger<DeviceBlockFunction>.Instance,
            new BlockedDeviceService(securityRepo, Mock.Of<ISessionRepository>(), NullLogger<BlockedDeviceService>.Instance),
            maintenance.Object,
            opsEvents);
    }

    private static JsonElement Json(HttpResponseData res)
    {
        res.Body.Position = 0;
        using var doc = JsonDocument.Parse(res.Body);
        return doc.RootElement.Clone();
    }
}

using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static AutopilotMonitor.Functions.Services.McpUsageOverviewService;
using static AutopilotMonitor.Functions.Services.McpUsageTelemetryQueries;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The pure fold behind GET global/metrics/mcp-usage/overview: which numbers come from the quota counters and
/// which from the telemetry, how platform admins drop out (but never out of a tenant's real organization
/// windows), what a call costs, MCP-only tools, the daily trend and the per-minute ranking.
/// </summary>
public class McpUsageOverviewAssemblyTests
{
    private const string TenantA = "aaaaaaaa-0000-0000-0000-00000000000a";
    private const string TenantB = "bbbbbbbb-0000-0000-0000-00000000000b";
    private const string Alice = "alice@contoso.com";
    private const string App = "app:11111111-2222-3333-4444-555555555555";
    private const string Operator = "ops@fabrikam.com";
    private const string Bob = "bob@fabrikam.com";
    private const string Carol = "carol@contoso.com";

    // Window 3 days (Nov 3–5); the month started Nov 1, so Nov 1 rows count for the quota, not for the window.
    private static readonly Dates ThreeDays = new("20261103", "20261105", "20261101");
    private static readonly DateTime At = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);

    private static TenantUsageRecord Counter(string tenant, string oid, string upn, string day, long requests)
        => new() { TenantId = tenant, UserId = oid, UserPrincipalName = upn, Date = day, RequestCount = requests, LastRequestAt = At };

    private static readonly TenantUsageRecord[] Counters =
    {
        Counter(TenantA, "oid-alice", Alice, "20261101", 10),
        Counter(TenantA, "oid-alice", Alice, "20261104", 20),
        Counter(TenantA, "oid-alice", Alice, "20261105", 30),
        Counter(TenantA, "oid-app", App, "20261105", 5),
        Counter(TenantA, "oid-ops", Operator, "20261105", 100),
        Counter(TenantB, "oid-bob", Bob, "20261103", 7),
    };

    private static CallRow Call(string day, string tool, string tenant, string principal, long calls, long served, long requests, string role)
        => new(day, tool, tenant, principal, calls, served, requests, At, role);

    private static readonly CallRow[] Calls =
    {
        Call("20261104", "get_session_summary", TenantA, Alice, 2, 2, 10, "Admin"),
        Call("20261105", "search_events", TenantA, Alice, 1, 1, 14, "Admin"),
        Call("20261105", "get_session_events", TenantA, Alice, 3, 3, 3, "Admin"),
        Call("20261105", "list_tenants", TenantA, Alice, 1, 1, 1, "Admin"),
        Call("20261105", "get_session_summary", TenantA, App, 1, 1, 5, "Viewer"),
        Call("20261105", "query_table", TenantA, Operator, 50, 50, 50, "GlobalAdmin"),
        Call("20261103", "search_sessions", TenantB, Bob, 2, 2, 7, "Admin"),
        Call("20261105", "get_time_attribution", TenantA, Carol, 4, 0, 0, "Admin"),
    };

    private static McpQuotaDecision State(string plan, int daily, int monthly, long dailyUsed, long monthlyUsed)
        => new() { Allowed = true, Plan = plan, DailyLimit = daily, MonthlyLimit = monthly, DailyUsed = dailyUsed, MonthlyUsed = monthlyUsed };

    private static TelemetryResult Telemetry(bool available = true) => !available
        ? new TelemetryResult(false, false, false, new(), new(), new(), new(), new(), new(), new ServerFacts(null, null))
        : new TelemetryResult(
            RequestsAvailable: true, McpLogAvailable: true, Partial: false,
            Calls: Calls.ToList(),
            Routes: new()
            {
                new("get_session_summary", "GET sessions/{id}/events", 3),
                new("get_session_summary", "GET sessions/{id}", 3),
            },
            Refusals: new()
            {
                new(RefusalQuota, "20261105", TenantA, Alice, "user", "daily", "community", 3, At, At.AddSeconds(9), 50, 50),
                new(RefusalMinute, "20261103", TenantB, Bob, "", "", "", 2, At, At, 120, 120),
            },
            Peaks: new()
            {
                new(PeakCalls, TenantA, Alice, 12, At),
                new(PeakRequests, TenantA, Alice, 40, At),
                new(PeakRequests, TenantB, Bob, 120, At),
                new(PeakCalls, TenantB, Bob, 5, At),
            },
            ToolQuality: new()
            {
                new("get_session_summary", 3, 0, 0, 0, 600, 19000.4),
                new("search_docs", 4, 1, 0, 0, 80, 5000),
            },
            McpRefusals: new() { new(Alice, 7, At) },
            ServerFacts: new ServerFacts(new DateTime(2026, 11, 4, 1, 0, 0, DateTimeKind.Utc), 60));

    private static GetGlobalMcpUsageOverviewResponse Overview(bool includePlatformAdmins = false, bool telemetry = true)
    {
        var result = Telemetry(telemetry);
        var facts = new Dictionary<(string Tenant, string Principal), AccountFacts>
        {
            [(TenantA, Alice)] = new("oid-alice", null, State("community", 50, 250, 30, 60)),
            [(TenantA, App)] = new("oid-app", null, State("community", 50, 250, 5, 5)),
            [(TenantA, Operator)] = new("oid-ops", "GlobalAdmin", State("community", 50, 250, 100, 100)),
            [(TenantB, Bob)] = new("oid-bob", null, State("pro", 500, 6000, 0, 7)),
        };
        var limits = new Dictionary<string, McpQuotaService.TenantPlanLimits>
        {
            [TenantA] = new("community", 100, 250),
            [TenantB] = new("pro", 1500, 15000),
        };
        // TenantB has no readable configuration: no name.
        var names = new Dictionary<string, string> { [TenantA] = "contoso.com" };
        var minute = new Dictionary<(string Tenant, string Principal), int> { [(TenantA, Alice)] = 120, [(TenantB, Bob)] = 150 };

        return Assemble(new OverviewInputs(ThreeDays, includePlatformAdmins, Counters, facts, limits, names, LatestRoles(result.Calls), minute, result));
    }

    [Fact]
    public void PlatformAdmins_DropOutOfEveryNumber_ButNotOutOfTheOrganizationWindows()
    {
        var overview = Overview();
        var tenantA = overview.Tenants.Single(t => t.TenantId == TenantA);

        Assert.DoesNotContain(tenantA.Accounts, a => a.Principal == Operator);
        Assert.DoesNotContain(overview.Tools, t => t.Tool == "query_table");
        Assert.Equal(55, tenantA.Requests); // alice 20 + 30, app 5 — the operator's 100 left out
        // The organization windows stay the tenant's real counters: today 30 + 5 + 100, the month 10 + 20 + 30 + 5 + 100.
        Assert.Equal(135, tenantA.Quota.DailyUsed);
        Assert.Equal(165, tenantA.Quota.MonthlyUsed);
        Assert.Equal("community", tenantA.Quota.TenantPlan);
    }

    [Fact]
    public void PlatformAdmins_WhenIncluded_AreMarked()
    {
        var tenantA = Overview(includePlatformAdmins: true).Tenants.Single(t => t.TenantId == TenantA);
        var op = tenantA.Accounts.Single(a => a.Principal == Operator);

        Assert.True(op.IsPlatformAdmin);
        Assert.Equal("GlobalAdmin", op.Role);
        Assert.Equal(155, tenantA.Requests);
    }

    [Fact]
    public void Accounts_RequestsComeFromTheCounters_CallsFromTheTelemetry()
    {
        var tenants = Overview().Tenants;
        Assert.Equal(new[] { TenantA, TenantB }, tenants.Select(t => t.TenantId)); // most calls first
        var accounts = tenants[0].Accounts;
        Assert.Equal(new[] { Alice, Carol, App }, accounts.Select(a => a.Principal));

        var alice = accounts[0];
        Assert.Equal("oid-alice", alice.UserId);
        Assert.Equal(7, alice.Calls);
        Assert.Equal(50, alice.Requests); // Nov 1 lies before the window
        Assert.Equal("20261105", alice.PeakDay);
        Assert.Equal(30, alice.PeakDayRequests);
        Assert.Equal(1, alice.QuotaHits);
        Assert.Equal("Admin", alice.Role);
        Assert.Equal(new[] { ("get_session_events", 3L), ("get_session_summary", 2L), ("list_tenants", 1L) },
            alice.TopTools.Select(t => (t.Tool, t.Calls)));
        Assert.Equal(("community", 50, 250, 30L, 60L),
            (alice.Quota!.Plan, alice.Quota.DailyLimit, alice.Quota.MonthlyLimit, alice.Quota.DailyUsed, alice.Quota.MonthlyUsed));
    }

    [Fact]
    public void AnAccountKnownOnlyFromTheTelemetry_HasNoObjectIdAndNoQuota()
    {
        // Every request refused: no counter row, but the calls happened.
        var carol = Overview().Tenants.Single(t => t.TenantId == TenantA).Accounts.Single(a => a.Principal == Carol);

        Assert.Null(carol.UserId);
        Assert.Null(carol.Quota);
        Assert.Equal(4, carol.Calls);
        Assert.Equal(0, carol.Requests);
        Assert.Null(carol.PeakDay);
    }

    [Fact]
    public void Tools_RankByCalls_CostPerServedCall_AndMcpOnlyToolsFromTheLog()
    {
        var tools = Overview().Tools;

        Assert.Equal(
            new[] { "get_time_attribution", "search_docs", "get_session_events", "get_session_summary", "search_sessions", "list_tenants", "search_events" },
            tools.Select(t => t.Tool));

        var summary = tools.Single(t => t.Tool == "get_session_summary");
        Assert.Equal(3, summary.Calls);
        Assert.Equal(2, summary.Accounts);
        Assert.Equal(1, summary.Tenants);
        Assert.Equal(5.0, summary.RequestsPerCall); // (10 + 5) requests over 3 served calls
        Assert.Equal(0.0, summary.ErrorRate);
        Assert.Equal(600, summary.P95DurationMs);
        Assert.Equal(19000, summary.AvgResultChars);
        Assert.Equal(new[] { "GET sessions/{id}", "GET sessions/{id}/events" }, summary.Routes.Select(r => r.Route));

        var docs = tools.Single(t => t.Tool == "search_docs");
        Assert.Equal(4, docs.Calls);
        Assert.Null(docs.Accounts);
        Assert.Null(docs.Tenants);
        Assert.Equal(0, docs.RequestsPerCall);
        Assert.Equal(0.25, docs.ErrorRate);

        // Every call refused: no served call, no cost to report.
        Assert.Equal(0, tools.Single(t => t.Tool == "get_time_attribution").RequestsPerCall);
    }

    [Fact]
    public void Daily_HasEveryDayOfTheWindow_WithCallsByPlan()
    {
        var daily = Overview().Daily;

        Assert.Equal(new[] { "20261103", "20261104", "20261105" }, daily.Select(d => d.Date));
        var today = daily[2];
        Assert.Equal(10, today.Calls);    // alice 5, app 1, carol 4 — the operator's 50 left out
        Assert.Equal(35, today.Requests); // alice 30, app 5
        Assert.Equal(3, today.Accounts);
        Assert.Equal(new[] { ("community", 10L) }, today.CallsByPlan.Select(p => (p.Plan, p.Calls)));
        Assert.Equal(new[] { ("pro", 2L) }, daily[0].CallsByPlan.Select(p => (p.Plan, p.Calls)));
    }

    [Fact]
    public void QuotaHits_AndMinuteLimits_RankTheAccountsClosestToALimit()
    {
        var overview = Overview();

        var hit = Assert.Single(overview.QuotaHits);
        Assert.Equal((Alice, "user", "daily", 50L, 50L, 3L), (hit.Principal, hit.Level, hit.Scope, hit.Limit, hit.Used, hit.Refused));

        Assert.Equal(60, overview.MinuteLimits.McpServerLimit);
        var minute = overview.MinuteLimits.Accounts;
        Assert.Equal(new[] { Bob, Alice }, minute.Select(m => m.Principal)); // 120/150 before 40/120 and 12/60
        Assert.Equal((120L, 150, 2L), (minute[0].PeakRequests, minute[0].BackendLimit, minute[0].BackendRefused));
        Assert.Equal((TenantA, 12L, 7L, 0L), (minute[1].TenantId, minute[1].PeakCalls, minute[1].McpRefused, minute[1].BackendRefused));
        // The share the list ranks by, in whole percent: the higher of calls/MCP limit and requests/backend limit.
        Assert.Equal(new[] { 80, 33 }, minute.Select(m => m.SharePercent)); // 120/150; max(12/60, 40/120)
    }

    [Fact]
    public void Totals_SumTheLists_OnceForEveryConsumer()
    {
        var totals = Overview().Totals;

        Assert.Equal(18, totals.Calls);       // every tool of the ranking, search_docs' 4 MCP-only calls included
        Assert.Equal(4, totals.McpOnlyCalls);
        Assert.Equal(7, totals.Tools);
        Assert.Equal(62, totals.Requests);    // tenant A 55, tenant B 7 — the operator's 100 left out
        Assert.Equal(2, totals.Tenants);
        Assert.Equal(4, totals.Accounts);     // alice, carol, the app, bob
        Assert.Equal(1, totals.ServicePrincipals);
        Assert.Equal(1, totals.QuotaHitAccounts);      // alice's daily window
        Assert.Equal(2, totals.MinuteRefusedAccounts); // alice at the MCP server, bob at the backend
        Assert.Equal(2, totals.LimitAccounts);
    }

    [Fact]
    public void Totals_WithoutTheirStore_AreAbsent_NotAPartialSum()
    {
        var totals = Overview(telemetry: false).Totals;

        Assert.Null(totals.Calls);
        Assert.Null(totals.McpOnlyCalls);
        Assert.Equal(0, totals.Tools);
        Assert.Equal(62, totals.Requests); // the counters still answer
        Assert.Equal(3, totals.Accounts);
    }

    [Fact]
    public void Tenants_CarryTheirDomain_WhenTheConfigurationNamesOne()
    {
        var tenants = Overview().Tenants;

        Assert.Equal("contoso.com", tenants.Single(t => t.TenantId == TenantA).DomainName);
        Assert.Null(tenants.Single(t => t.TenantId == TenantB).DomainName);
    }

    [Fact]
    public async Task AnUnreadableTenantConfiguration_LeavesOnlyTheNameOut()
    {
        var config = new Mock<TenantConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<TenantConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()))
        { CallBase = false };
        config.Setup(c => c.GetConfigurationIfExistsAsync(TenantA)).ThrowsAsync(new InvalidOperationException("storage down"));
        config.Setup(c => c.GetConfigurationIfExistsAsync(TenantB)).ReturnsAsync(new TenantConfiguration { TenantId = TenantB, DomainName = "fabrikam.com" });
        // Only the configuration read is exercised; the other collaborators stay unused.
        var service = new McpUsageOverviewService(
            null!, null!, null!, null!, config.Object, null!, null!, null!, NullLogger<McpUsageOverviewService>.Instance, TimeProvider.System);

        Assert.Null(await service.ReadTenantConfigurationAsync(TenantA));
        Assert.Equal("fabrikam.com", (await service.ReadTenantConfigurationAsync(TenantB))?.DomainName);
    }

    [Theory]
    [InlineData(null, null, 30, false, null)]
    [InlineData("7", "include", 7, true, null)]
    [InlineData("90", "exclude", 90, false, null)]
    [InlineData("91", null, 0, false, "days must be between 1 and 90")]
    [InlineData("0", null, 0, false, "days must be between 1 and 90")]
    [InlineData("-5", null, 0, false, "days must be between 1 and 90")]
    [InlineData("7.5", null, 0, false, "days must be an integer")]
    [InlineData("30", "all", 0, false, "platformAdmins must be include or exclude")]
    public void OverviewQuery_DefaultsAndBounds(string? days, string? platformAdmins, int expectedDays, bool expectedInclude, string? expectedError)
    {
        var parsed = AutopilotMonitor.Functions.Functions.Metrics.McpUsageMetricsFunction.ParseOverviewQuery(days, platformAdmins);

        Assert.Equal((expectedDays, expectedInclude, expectedError), parsed);
    }

    [Fact]
    public void Status_NamesTheMcpLogCoverage_AndATelemetryOutageEmptiesOnlyItsParts()
    {
        Assert.Equal("20261104", Overview().Telemetry.McpLogFrom);

        var dark = Overview(telemetry: false);
        Assert.False(dark.Telemetry.RequestsAvailable);
        Assert.False(dark.Telemetry.McpLogAvailable);
        Assert.Empty(dark.Tools);
        Assert.Empty(dark.QuotaHits);
        // The counters still carry tenants, accounts, requests and the quota.
        var tenantA = dark.Tenants.Single(t => t.TenantId == TenantA);
        Assert.Equal(new[] { Alice, App }, tenantA.Accounts.Select(a => a.Principal).OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal(0, tenantA.Calls);
        Assert.Equal(55, tenantA.Requests);
    }
}

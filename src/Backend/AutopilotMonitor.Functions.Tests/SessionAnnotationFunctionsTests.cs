using System.Net;
using System.Security.Claims;
using System.Text;
using AutopilotMonitor.Functions.Functions.Annotations;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure.Core.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the session-annotation authorization surface: the per-lane write matrix
/// (<see cref="UpsertSessionAnnotationFunction.IsLaneWritableByCaller"/>), the GA-lane
/// read filter (<see cref="GetSessionAnnotationsFunction.FilterLanesForCaller"/>), and —
/// endpoint-level, through the real request pipeline — that author identity is stamped
/// from the JWT and never from the body (same contract as the gather-rule PUT-upserts).
/// </summary>
public class SessionAnnotationFunctionsTests
{
    private const string TenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    // ── per-lane write matrix ───────────────────────────────────────────────
    // Callers reaching the function already passed TenantAdminOrOperator (tenant
    // Admin/Operator, or GA via bypass) — the matrix re-gates per lane.

    [Theory]
    // operator lane: Operator or Tenant Admin; GA (cross-tenant: not tenant admin) may not
    [InlineData(AnnotationLanes.Operator, "Operator", false, false, true)]
    [InlineData(AnnotationLanes.Operator, "Admin", true, false, true)]
    [InlineData(AnnotationLanes.Operator, "GlobalAdmin", false, true, false)]
    [InlineData(AnnotationLanes.Operator, "Viewer", false, false, false)]
    // tenantadmin lane: Tenant Admin only
    [InlineData(AnnotationLanes.TenantAdmin, "Admin", true, false, true)]
    [InlineData(AnnotationLanes.TenantAdmin, "Operator", false, false, false)]
    [InlineData(AnnotationLanes.TenantAdmin, "GlobalAdmin", false, true, false)]
    // globaladmin lane: Global Admin only
    [InlineData(AnnotationLanes.GlobalAdmin, "GlobalAdmin", false, true, true)]
    [InlineData(AnnotationLanes.GlobalAdmin, "Admin", true, false, false)]
    [InlineData(AnnotationLanes.GlobalAdmin, "Operator", false, false, false)]
    // unknown lane: never writable
    [InlineData("someotherlane", "GlobalAdmin", true, true, false)]
    public void IsLaneWritableByCaller_matrix(
        string lane, string userRole, bool isTenantAdmin, bool isGlobalAdmin, bool expected)
    {
        Assert.Equal(expected,
            UpsertSessionAnnotationFunction.IsLaneWritableByCaller(lane, userRole, isTenantAdmin, isGlobalAdmin));
    }

    [Fact]
    public void IsLaneWritableByCaller_ga_who_is_also_own_tenant_admin_may_write_tenant_lanes()
    {
        // A GA annotating a session of their OWN tenant (IsTenantAdmin=true there) may use
        // the tenant lanes — the GA-only restriction applies to foreign tenants, where
        // IsTenantAdmin is false.
        Assert.True(UpsertSessionAnnotationFunction.IsLaneWritableByCaller(
            AnnotationLanes.TenantAdmin, "GlobalAdmin", isTenantAdmin: true, isGlobalAdmin: true));
    }

    // ── GA-lane read filter ─────────────────────────────────────────────────

    private static List<SessionAnnotation> AllLanes() => new()
    {
        new SessionAnnotation { Lane = AnnotationLanes.Operator, SessionId = SessionId },
        new SessionAnnotation { Lane = AnnotationLanes.TenantAdmin, SessionId = SessionId },
        new SessionAnnotation { Lane = AnnotationLanes.GlobalAdmin, SessionId = SessionId },
    };

    [Fact]
    public void FilterLanesForCaller_hides_globaladmin_lane_without_global_scope()
    {
        var visible = GetSessionAnnotationsFunction.FilterLanesForCaller(AllLanes(), hasGlobalScope: false);

        Assert.Equal(2, visible.Count);
        Assert.DoesNotContain(visible, a => a.Lane == AnnotationLanes.GlobalAdmin);
    }

    [Fact]
    public void FilterLanesForCaller_returns_all_lanes_for_global_scope()
    {
        var visible = GetSessionAnnotationsFunction.FilterLanesForCaller(AllLanes(), hasGlobalScope: true);

        Assert.Equal(3, visible.Count);
    }

    // ── GET writableLanes: server-computed write matrix ─────────────────────
    // The web renders lanes writable exactly per this list (it holds no matrix copy),
    // so the GET must derive it from the SAME function the PUT re-gates with,
    // including the own-tenant binding of the tenant-role lanes.

    private static GetSessionAnnotationsFunction BuildGetFunction()
    {
        var annotationRepo = new Mock<ISessionAnnotationRepository>(MockBehavior.Loose);
        annotationRepo.Setup(r => r.GetForSessionAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<SessionAnnotation>());
        var sessionRepo = new Mock<ISessionRepository>(MockBehavior.Loose);
        sessionRepo.Setup(r => r.ResolveSessionTenantIdAsync(SessionId)).ReturnsAsync(TenantId);
        return new GetSessionAnnotationsFunction(
            NullLogger<GetSessionAnnotationsFunction>.Instance,
            annotationRepo.Object, sessionRepo.Object);
    }

    private static async Task<string[]> RunGetAndReadWritableLanes(RequestContext ctx)
    {
        var function = BuildGetFunction();
        var req = BuildRequest(Principal(("tid", ctx.TenantId)), ctx, new { });
        var res = await function.Run(req, SessionId);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        res.Body.Position = 0;
        using var reader = new StreamReader(res.Body);
        var json = JsonConvert.DeserializeAnonymousType(
            await reader.ReadToEndAsync(), new { writableLanes = Array.Empty<string>() });
        return json!.writableLanes;
    }

    [Fact]
    public async Task Get_writableLanes_tenant_admin_own_tenant()
    {
        var lanes = await RunGetAndReadWritableLanes(TenantAdminContext());
        Assert.Equal(new[] { AnnotationLanes.Operator, AnnotationLanes.TenantAdmin }, lanes);
    }

    [Fact]
    public async Task Get_writableLanes_operator_own_tenant()
    {
        var lanes = await RunGetAndReadWritableLanes(new RequestContext
        {
            TenantId = TenantId,
            TargetTenantId = TenantId,
            UserPrincipalName = "op@contoso.com",
            UserRole = Constants.TenantRoles.Operator,
        });
        Assert.Equal(new[] { AnnotationLanes.Operator }, lanes);
    }

    [Fact]
    public async Task Get_writableLanes_ga_on_foreign_session_gets_only_globaladmin_lane()
    {
        // GA is Admin of their HOME tenant; the session resolves to a foreign tenant —
        // the home-tenant role must not leak, exactly like the PUT's 403 test above.
        var lanes = await RunGetAndReadWritableLanes(new RequestContext
        {
            TenantId = "99999999-9999-9999-9999-999999999999",
            TargetTenantId = "99999999-9999-9999-9999-999999999999",
            UserPrincipalName = "ga@fabrikam.com",
            IsGlobalAdmin = true,
            IsTenantAdmin = true,
            UserRole = Constants.TenantRoles.Admin,
        });
        Assert.Equal(new[] { AnnotationLanes.GlobalAdmin }, lanes);
    }

    [Fact]
    public async Task Get_writableLanes_global_reader_gets_none()
    {
        var lanes = await RunGetAndReadWritableLanes(new RequestContext
        {
            TenantId = "99999999-9999-9999-9999-999999999999",
            TargetTenantId = "99999999-9999-9999-9999-999999999999",
            UserPrincipalName = "reader@fabrikam.com",
            IsGlobalReader = true,
        });
        Assert.Empty(lanes);
    }

    // ── global list: GA-lane exclusion follows global scope ─────────────────
    // The route is GlobalReadOrAdmin, but the delegated ("MSP") read rescue admits a
    // delegated caller on its managed ?tenantId= path — the platform-internal
    // globaladmin lane must be excluded server-side for anyone without global scope.

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GlobalList_derives_globaladmin_lane_exclusion_from_global_scope(
        bool hasGlobalScope, bool expectedExclude)
    {
        var annotationRepo = new Mock<ISessionAnnotationRepository>(MockBehavior.Loose);
        bool? capturedExclude = null;
        annotationRepo
            .Setup(r => r.QueryPageAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<bool>()))
            .Callback<string?, string?, string?, string?, DateTime?, DateTime?, int, string?, bool>(
                (_, _, _, _, _, _, _, _, exclude) => capturedExclude = exclude)
            .ReturnsAsync((new List<SessionAnnotation>(), (string?)null));

        var function = new ListSessionAnnotationsFunction(
            NullLogger<ListSessionAnnotationsFunction>.Instance, annotationRepo.Object);

        var requestCtx = new RequestContext
        {
            TenantId = TenantId,
            TargetTenantId = TenantId,
            UserPrincipalName = "reader@contoso.com",
            IsGlobalAdmin = hasGlobalScope,
        };
        var req = BuildRequest(Principal(("tid", TenantId)), requestCtx, new { });
        Mock.Get(req).SetupGet(r => r.Url)
            .Returns(new Uri("https://localhost/api/global/session-annotations"));

        var res = await function.Run(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(expectedExclude, capturedExclude);
    }

    // ── endpoint-level tests (fake HttpRequestData, same harness as
    //    GatherRulesFunctionAuthorEndpointTests) ──────────────────────────────

    private sealed class FakeHttpResponseData : HttpResponseData
    {
        public FakeHttpResponseData(FunctionContext context) : base(context) { }
        public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public override HttpHeadersCollection Headers { get; set; } = new();
        public override Stream Body { get; set; } = new MemoryStream();
        public override HttpCookies Cookies { get; } = new Mock<HttpCookies>().Object;
    }

    private static FunctionContext BuildContext(ClaimsPrincipal? principal, RequestContext requestContext)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<WorkerOptions>(o => o.Serializer = new JsonObjectSerializer());
        var provider = services.BuildServiceProvider();

        var items = new Dictionary<object, object> { ["RequestContext"] = requestContext };
        if (principal != null)
        {
            items["ClaimsPrincipal"] = principal;
        }

        var context = new Mock<FunctionContext>();
        context.SetupGet(c => c.Items).Returns(items);
        context.SetupGet(c => c.InstanceServices).Returns(provider);
        return context.Object;
    }

    private static HttpRequestData BuildRequest(
        ClaimsPrincipal? principal, RequestContext requestContext, object body)
    {
        var context = BuildContext(principal, requestContext);
        var req = new Mock<HttpRequestData>(context);
        req.SetupGet(r => r.Headers).Returns(new HttpHeadersCollection());
        req.SetupGet(r => r.Body).Returns(
            new MemoryStream(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body))));
        req.Setup(r => r.CreateResponse()).Returns(() => new FakeHttpResponseData(context));
        return req.Object;
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "TestAuth"));

    private static RequestContext TenantAdminContext() => new()
    {
        TenantId = TenantId,
        TargetTenantId = TenantId,
        UserPrincipalName = "alice@contoso.com",
        IsTenantAdmin = true,
        UserRole = Constants.TenantRoles.Admin,
    };

    private sealed class Harness
    {
        public UpsertSessionAnnotationFunction Function = default!;
        public Mock<ISessionAnnotationRepository> AnnotationRepo = default!;
        public List<SessionAnnotation> Stored = default!;
    }

    private static Harness BuildFunction(
        SessionAnnotation? existing = null, List<RuleResult>? ruleResults = null)
    {
        var annotationRepo = new Mock<ISessionAnnotationRepository>(MockBehavior.Loose);
        var stored = new List<SessionAnnotation>();
        annotationRepo.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(existing);
        annotationRepo.Setup(r => r.UpsertAsync(It.IsAny<SessionAnnotation>()))
            .Callback<SessionAnnotation>(stored.Add)
            .Returns(Task.CompletedTask);

        var sessionRepo = new Mock<ISessionRepository>(MockBehavior.Loose);
        sessionRepo.Setup(r => r.GetSessionAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new SessionSummary { SessionId = SessionId, TenantId = TenantId });
        sessionRepo.Setup(r => r.ResolveSessionTenantIdAsync(SessionId)).ReturnsAsync(TenantId);

        var ruleRepo = new Mock<IRuleRepository>(MockBehavior.Loose);
        ruleRepo.Setup(r => r.GetRuleResultsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(ruleResults ?? new List<RuleResult>());

        var maintenanceRepo = new Mock<IMaintenanceRepository>(MockBehavior.Loose);

        return new Harness
        {
            Function = new UpsertSessionAnnotationFunction(
                NullLogger<UpsertSessionAnnotationFunction>.Instance,
                annotationRepo.Object, sessionRepo.Object, ruleRepo.Object, maintenanceRepo.Object),
            AnnotationRepo = annotationRepo,
            Stored = stored,
        };
    }

    [Fact]
    public async Task Upsert_stamps_author_from_jwt_not_from_body()
    {
        var h = BuildFunction();
        // Body tries to smuggle author fields — the function must never read them.
        var req = BuildRequest(
            Principal(("tid", TenantId), ("name", "Alice Admin"), ("upn", "alice@contoso.com")),
            TenantAdminContext(),
            new
            {
                verdict = AnnotationVerdicts.RootCauseConfirmed,
                note = "checked on device",
                authorUpn = "spoofed@evil.example",
                authorDisplayName = "Spoofed Author",
                createdByUpn = "spoofed@evil.example",
            });

        var response = await h.Function.Run(req, SessionId, AnnotationLanes.TenantAdmin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var written = Assert.Single(h.Stored);
        Assert.Equal("Alice Admin", written.AuthorDisplayName);
        Assert.Equal("alice@contoso.com", written.AuthorUpn);
        Assert.Equal("alice@contoso.com", written.CreatedByUpn);
    }

    [Fact]
    public async Task Upsert_true_update_preserves_original_creator_and_createdAt()
    {
        var createdAt = new DateTime(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc);
        var h = BuildFunction(existing: new SessionAnnotation
        {
            TenantId = TenantId,
            SessionId = SessionId,
            Lane = AnnotationLanes.TenantAdmin,
            CreatedByUpn = "bob@contoso.com",
            CreatedAtUtc = createdAt,
        });
        var req = BuildRequest(
            Principal(("tid", TenantId), ("name", "Alice Admin"), ("upn", "alice@contoso.com")),
            TenantAdminContext(),
            new { verdict = AnnotationVerdicts.AnalysisWrong });

        var response = await h.Function.Run(req, SessionId, AnnotationLanes.TenantAdmin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var written = Assert.Single(h.Stored);
        Assert.Equal("bob@contoso.com", written.CreatedByUpn);   // first writer survives
        Assert.Equal(createdAt, written.CreatedAtUtc);
        Assert.Equal("alice@contoso.com", written.AuthorUpn);    // last editor stamped
    }

    [Fact]
    public async Task Upsert_snapshots_fired_rule_ids()
    {
        var h = BuildFunction(ruleResults: new List<RuleResult>
        {
            new RuleResult { SessionId = SessionId, RuleId = "ANALYZE-ESP-001" },
            new RuleResult { SessionId = SessionId, RuleId = "ANALYZE-ESP-001" }, // duplicate collapses
            new RuleResult { SessionId = SessionId, RuleId = "ANALYZE-CORR-003" },
        });
        var req = BuildRequest(
            Principal(("tid", TenantId), ("name", "Alice Admin"), ("upn", "alice@contoso.com")),
            TenantAdminContext(),
            new { verdict = AnnotationVerdicts.RootCauseConfirmed });

        await h.Function.Run(req, SessionId, AnnotationLanes.TenantAdmin);

        var written = Assert.Single(h.Stored);
        Assert.Equal(new[] { "ANALYZE-ESP-001", "ANALYZE-CORR-003" }, written.RuleIds);
    }

    [Fact]
    public async Task Upsert_empty_body_clears_the_lane()
    {
        var h = BuildFunction();
        var req = BuildRequest(
            Principal(("tid", TenantId), ("name", "Alice Admin"), ("upn", "alice@contoso.com")),
            TenantAdminContext(),
            new { verdict = (string?)null, note = "" });

        var response = await h.Function.Run(req, SessionId, AnnotationLanes.TenantAdmin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(h.Stored);
        h.AnnotationRepo.Verify(
            r => r.DeleteAsync(TenantId, SessionId, AnnotationLanes.TenantAdmin), Times.Once);
    }

    [Fact]
    public async Task Upsert_unknown_verdict_is_rejected()
    {
        var h = BuildFunction();
        var req = BuildRequest(
            Principal(("tid", TenantId), ("upn", "alice@contoso.com")),
            TenantAdminContext(),
            new { verdict = "totally_made_up" });

        var response = await h.Function.Run(req, SessionId, AnnotationLanes.TenantAdmin);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(h.Stored);
    }

    [Fact]
    public async Task Upsert_note_over_cap_is_rejected()
    {
        var h = BuildFunction();
        var req = BuildRequest(
            Principal(("tid", TenantId), ("upn", "alice@contoso.com")),
            TenantAdminContext(),
            new { note = new string('x', SessionAnnotation.MaxNoteLength + 1) });

        var response = await h.Function.Run(req, SessionId, AnnotationLanes.TenantAdmin);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(h.Stored);
    }

    [Fact]
    public async Task Upsert_forbids_lane_outside_caller_matrix()
    {
        var h = BuildFunction();
        var operatorContext = new RequestContext
        {
            TenantId = TenantId,
            TargetTenantId = TenantId,
            UserPrincipalName = "op@contoso.com",
            IsTenantAdmin = false,
            UserRole = Constants.TenantRoles.Operator,
        };
        var req = BuildRequest(
            Principal(("tid", TenantId), ("upn", "op@contoso.com")),
            operatorContext,
            new { verdict = AnnotationVerdicts.RootCauseConfirmed });

        var response = await h.Function.Run(req, SessionId, AnnotationLanes.TenantAdmin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(h.Stored);
    }

    [Fact]
    public async Task Upsert_ga_with_own_tenant_admin_role_cannot_write_foreign_tenant_lanes()
    {
        // The session belongs to TenantId (foreign); the GA is Admin of their OWN tenant.
        // Tenant-role lanes bind to the caller's own tenant, so this must 403 — otherwise a
        // GA's home-tenant admin role would leak into every other tenant's lanes.
        var h = BuildFunction();
        var gaContext = new RequestContext
        {
            TenantId = "99999999-9999-9999-9999-999999999999",
            TargetTenantId = "99999999-9999-9999-9999-999999999999",
            UserPrincipalName = "ga@fabrikam.com",
            IsGlobalAdmin = true,
            IsTenantAdmin = true, // admin of the HOME tenant, not of TenantId
            UserRole = Constants.TenantRoles.Admin,
        };
        var req = BuildRequest(
            Principal(("tid", gaContext.TenantId), ("upn", "ga@fabrikam.com")),
            gaContext,
            new { verdict = AnnotationVerdicts.RootCauseConfirmed });

        var response = await h.Function.Run(req, SessionId, AnnotationLanes.TenantAdmin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(h.Stored);
    }

    [Fact]
    public async Task Upsert_ga_writes_globaladmin_lane_on_foreign_tenant_session()
    {
        var h = BuildFunction();
        var gaContext = new RequestContext
        {
            TenantId = "99999999-9999-9999-9999-999999999999", // GA's home tenant
            TargetTenantId = "99999999-9999-9999-9999-999999999999",
            UserPrincipalName = "ga@fabrikam.com",
            IsGlobalAdmin = true,
            UserRole = "GlobalAdmin",
        };
        var req = BuildRequest(
            Principal(("tid", gaContext.TenantId), ("name", "Gerd Admin"), ("upn", "ga@fabrikam.com")),
            gaContext,
            new { verdict = AnnotationVerdicts.DifferentProblem, note = "actually a network issue" });

        var response = await h.Function.Run(req, SessionId, AnnotationLanes.GlobalAdmin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var written = Assert.Single(h.Stored);
        // Tenant resolved from the session index, NOT the GA's own tenant.
        Assert.Equal(TenantId, written.TenantId);
        Assert.Equal(AnnotationLanes.GlobalAdmin, written.Lane);
    }
}

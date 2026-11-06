using System.Security.Claims;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Telemetry;
using AutopilotMonitor.Shared;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The pieces of "view as tenant" outside the policy decision: the header parse, the data scope every handler
/// reads (<see cref="TenantHelper"/>), the recompute gate that guards two GET actions, and the places where the
/// real caller — not the viewed tenant — must count (presence, the request row, every other telemetry item).
/// </summary>
public class ViewAsTests
{
    private const string Home = "11111111-1111-1111-1111-111111111111";
    private const string Viewed = "22222222-2222-2222-2222-222222222222";
    private const string GaUpn = "ga@vendor.example";

    private static RequestContext ViewingContext(string role = Constants.TenantRoles.Admin) => new()
    {
        TenantId = Viewed,
        TargetTenantId = Viewed,
        UserPrincipalName = GaUpn,
        UserRole = role,
        IsTenantAdmin = role == Constants.TenantRoles.Admin,
        ViewAs = new ViewAsContext(Home, role),
    };

    // ── header parse ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void No_tenant_header_is_an_ordinary_request(string? tenantHeader)
        => Assert.Null(ViewAsTarget.FromHeaders(tenantHeader, "Viewer"));

    [Theory]
    [InlineData("not-a-guid", null)]
    [InlineData("00000000-0000-0000-0000-000000000000", null)]
    [InlineData(Viewed, "GlobalAdmin")]
    [InlineData(Viewed, "Member")]
    public void Unusable_headers_are_invalid(string tenantHeader, string? roleHeader)
        => Assert.Same(ViewAsTarget.Invalid, ViewAsTarget.FromHeaders(tenantHeader, roleHeader));

    [Theory]
    [InlineData(null, Constants.TenantRoles.Admin)]
    [InlineData("viewer", Constants.TenantRoles.Viewer)]
    [InlineData(" OPERATOR ", Constants.TenantRoles.Operator)]
    public void A_tenant_id_is_normalized_and_the_role_defaults_to_admin(string? roleHeader, string expectedRole)
    {
        var request = ViewAsTarget.FromHeaders(" 22222222-2222-2222-2222-22222222222A ", roleHeader);

        Assert.Equal(new ViewAsTarget("22222222-2222-2222-2222-22222222222a", expectedRole), request);
        Assert.True(request!.IsValid);
    }

    // ── data scope and identity ──────────────────────────────────────────────

    [Fact]
    public void TenantHelper_answers_the_viewed_tenant_under_view_as_and_the_token_tenant_otherwise()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tid", Home), new Claim("upn", GaUpn) }, "test"));

        Assert.Equal(Viewed, TenantHelper.GetTenantId(Request(principal, ViewingContext())));
        Assert.Equal(Home, TenantHelper.GetTenantId(Request(principal, new RequestContext { TenantId = Home })));
        Assert.Equal(Home, TenantHelper.GetTenantId(Request(principal, context: null)));
    }

    [Fact]
    public void The_identity_tenant_stays_the_home_tenant()
    {
        Assert.Equal(Home, ViewingContext().IdentityTenantId());
        Assert.Equal(Viewed, new RequestContext { TenantId = Viewed }.IdentityTenantId());
    }

    [Fact]
    public void View_as_never_triggers_a_recompute_although_its_admin_owns_the_viewed_tenant()
    {
        Assert.False(RecomputeTriggerGate.CanTriggerRecompute(ViewingContext(), Viewed));
        Assert.True(RecomputeTriggerGate.CanTriggerRecompute(ViewingContext() with { ViewAs = null }, Viewed));
    }

    // ── counting at the real caller ──────────────────────────────────────────

    [Fact]
    public void View_as_records_no_presence_in_the_viewed_tenant()
    {
        Assert.False(UserPresenceMiddleware.Records(ViewingContext()));
        Assert.True(UserPresenceMiddleware.Records(ViewingContext() with { ViewAs = null }));
    }

    [Fact]
    public void The_request_row_of_a_view_as_request_counts_at_the_home_tenant_as_global_admin()
    {
        var ctx = ViewingContext();
        var properties = new Dictionary<string, string>();

        RequestTelemetryMiddleware.StampCaller(properties, ctx,
            RequestTelemetryMiddleware.ResolveTenantId(ctx.IdentityTenantId(), new Dictionary<object, object>(), "/api/sessions", null));

        Assert.Equal(Home, properties[RequestRowDimensions.TenantId]);
        Assert.Equal(GaUpn, properties[RequestRowDimensions.UserId]);
        Assert.Equal(Constants.GlobalRoles.GlobalAdmin, properties[RequestRowDimensions.UserRole]);
        Assert.Equal(Viewed, properties[RequestRowDimensions.ViewAsTenantId]);
    }

    [Fact]
    public void An_ordinary_request_row_carries_no_view_as_dimension()
    {
        var ctx = ViewingContext() with { TenantId = Home, ViewAs = null };
        var properties = new Dictionary<string, string>();

        RequestTelemetryMiddleware.StampCaller(properties, ctx, ctx.TenantId);

        Assert.Equal(Home, properties[RequestRowDimensions.TenantId]);
        Assert.Equal(Constants.TenantRoles.Admin, properties[RequestRowDimensions.UserRole]);
        Assert.False(properties.ContainsKey(RequestRowDimensions.ViewAsTenantId));
    }

    [Fact]
    public async Task Telemetry_items_are_stamped_only_inside_the_view_as_request_flow()
    {
        var sink = new CapturingProcessor();
        var processor = new ViewAsTelemetryProcessor(sink);

        await InViewAsFlow(() => processor.Process(new EventTelemetry("inside")));
        processor.Process(new EventTelemetry("outside"));

        var inside = (EventTelemetry)sink.Items[0];
        var outside = (EventTelemetry)sink.Items[1];
        Assert.Equal(Viewed, inside.Properties[RequestRowDimensions.ViewAsTenantId]);
        Assert.False(outside.Properties.ContainsKey(RequestRowDimensions.ViewAsTenantId));
    }

    /// <summary>The scope opens inside an awaited method, like the middleware; it ends with that flow.</summary>
    private static async Task InViewAsFlow(Action emit)
    {
        await Task.Yield();
        ViewAsTelemetryProcessor.Enter(Viewed);
        emit();
    }

    private sealed class CapturingProcessor : ITelemetryProcessor
    {
        public List<ITelemetry> Items { get; } = new();
        public void Process(ITelemetry item) => Items.Add(item);
    }

    private static HttpRequestData Request(ClaimsPrincipal principal, RequestContext? context)
    {
        var items = new Dictionary<object, object> { ["ClaimsPrincipal"] = principal };
        if (context != null)
            items[RequestContext.ItemsKey] = context;
        var functionContext = new Mock<FunctionContext>();
        functionContext.SetupGet(c => c.Items).Returns(items);
        return new Mock<HttpRequestData>(functionContext.Object).Object;
    }
}

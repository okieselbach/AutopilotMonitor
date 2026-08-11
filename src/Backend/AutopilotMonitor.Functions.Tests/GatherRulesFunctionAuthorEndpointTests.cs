using System.Net;
using System.Security.Claims;
using System.Text;
using AutopilotMonitor.Functions.Functions.Rules;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure.Core.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Endpoint-level tests proving <see cref="GatherRulesFunction"/> stamps the rule
/// Author from the caller's JWT and never from the request body — through the real
/// request pipeline (body deserialization → ValidateScopeAndEmitMode → stamp →
/// service). Complements <see cref="GatherRuleAuthorStampingTests"/> (claim
/// precedence only) and GatherRuleUpdatePartialMergeTests (service-level
/// preservation): those pin the pieces, this pins the wiring.
/// Unlike most function tests in this project, it fakes HttpRequestData — the
/// stamp lives IN the endpoint method, so no smaller seam can prove it.
/// </summary>
public class GatherRulesFunctionAuthorEndpointTests
{
    private const string TenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string RuleId = "GATHER-CUSTOM-777";

    // ── request/response fakes ──────────────────────────────────────────────

    private sealed class FakeHttpResponseData : HttpResponseData
    {
        public FakeHttpResponseData(FunctionContext context) : base(context) { }
        public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public override HttpHeadersCollection Headers { get; set; } = new();
        public override Stream Body { get; set; } = new MemoryStream();
        public override HttpCookies Cookies { get; } = new Mock<HttpCookies>().Object;
    }

    private static FunctionContext BuildContext(ClaimsPrincipal? principal)
    {
        // WriteAsJsonAsync resolves the serializer from InstanceServices.
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<WorkerOptions>(o => o.Serializer = new JsonObjectSerializer());
        var provider = services.BuildServiceProvider();

        var items = new Dictionary<object, object>();
        if (principal != null)
        {
            items["ClaimsPrincipal"] = principal;
        }

        var context = new Mock<FunctionContext>();
        context.SetupGet(c => c.Items).Returns(items);
        context.SetupGet(c => c.InstanceServices).Returns(provider);
        return context.Object;
    }

    private static HttpRequestData BuildRequest(ClaimsPrincipal? principal, object body)
    {
        var context = BuildContext(principal);
        var req = new Mock<HttpRequestData>(context);
        req.SetupGet(r => r.Headers).Returns(new HttpHeadersCollection());
        req.SetupGet(r => r.Body).Returns(
            new MemoryStream(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body))));
        req.Setup(r => r.CreateResponse()).Returns(() => new FakeHttpResponseData(context));
        return req.Object;
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "TestAuth"));

    private static ClaimsPrincipal AlicePrincipal()
        => Principal(("tid", TenantId), ("name", "Alice Admin"), ("upn", "alice@contoso.com"));

    // ── function under test with a captured repository ─────────────────────

    private static (GatherRulesFunction function, List<GatherRule> stored) BuildFunction(
        GatherRule? existingTenantRule = null)
    {
        var repo = new Mock<IRuleRepository>(MockBehavior.Loose);
        var stored = new List<GatherRule>();

        repo.Setup(r => r.GatherRuleExistsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        repo.Setup(r => r.GetGatherRulesAsync("global")).ReturnsAsync(new List<GatherRule>());
        repo.Setup(r => r.GetGatherRulesAsync(TenantId)).ReturnsAsync(
            existingTenantRule == null ? new List<GatherRule>() : new List<GatherRule> { existingTenantRule });
        repo.Setup(r => r.StoreGatherRuleAsync(It.IsAny<GatherRule>(), TenantId))
            .Callback<GatherRule, string>((rule, _) => stored.Add(rule))
            .ReturnsAsync(true);

        var service = new GatherRuleService(repo.Object, NullLogger<GatherRuleService>.Instance);
        return (new GatherRulesFunction(NullLogger<GatherRulesFunction>.Instance, service), stored);
    }

    private static GatherRule SpoofedPayload() => new()
    {
        RuleId = RuleId,
        Title = "Collect BIOS Config",
        CollectorType = "registry",
        Target = "HKLM\\SOFTWARE\\RealmJoin\\Custom\\BIOS",
        Trigger = "startup",
        OutputEventType = "gather_bios_config",
        Enabled = true,
        Author = "Spoofed Author",
    };

    // ── CreateRule ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateRule_stamps_author_from_jwt_not_from_body()
    {
        var (function, stored) = BuildFunction();
        var req = BuildRequest(AlicePrincipal(), SpoofedPayload());

        var response = await function.CreateRule(req);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var written = Assert.Single(stored);
        Assert.Equal("Alice Admin", written.Author);
    }

    [Fact]
    public async Task CreateRule_falls_back_to_product_name_without_identifying_claims()
    {
        // App-only-shaped token: authenticated with a tenant but no user display claims.
        var (function, stored) = BuildFunction();
        var req = BuildRequest(Principal(("tid", TenantId)), SpoofedPayload());

        var response = await function.CreateRule(req);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var written = Assert.Single(stored);
        Assert.Equal("Autopilot Monitor", written.Author);
    }

    // ── UpdateRule ──────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateRule_upsert_of_unknown_rule_stamps_author_from_jwt()
    {
        // Full-payload PUT for a ruleId with no existing tenant row upserts a new row —
        // without the endpoint stamp the payload's author would be stored verbatim.
        var (function, stored) = BuildFunction(existingTenantRule: null);
        var req = BuildRequest(AlicePrincipal(), SpoofedPayload());

        var response = await function.UpdateRule(req, RuleId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var written = Assert.Single(stored);
        Assert.Equal("Alice Admin", written.Author);
    }

    [Fact]
    public async Task UpdateRule_true_update_keeps_original_author_over_editor_and_body()
    {
        var existing = new GatherRule
        {
            RuleId = RuleId,
            Title = "Collect BIOS Config",
            CollectorType = "registry",
            Target = "HKLM\\SOFTWARE\\RealmJoin\\Custom\\BIOS",
            Trigger = "startup",
            OutputEventType = "gather_bios_config",
            IsBuiltIn = false,
            IsCommunity = false,
            Author = "Original Creator",
        };
        var (function, stored) = BuildFunction(existing);
        // Bob edits Alice's rule; neither Bob nor the spoofed body author may win.
        var req = BuildRequest(
            Principal(("tid", TenantId), ("name", "Bob Editor")), SpoofedPayload());

        var response = await function.UpdateRule(req, RuleId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var written = Assert.Single(stored);
        Assert.Equal("Original Creator", written.Author);
    }
}

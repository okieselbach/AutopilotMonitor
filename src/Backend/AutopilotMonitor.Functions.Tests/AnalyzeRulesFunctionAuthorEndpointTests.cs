using System.Net;
using System.Security.Claims;
using System.Text;
using AutopilotMonitor.Functions.Functions.Rules;
using AutopilotMonitor.Functions.Helpers;
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
/// Analyze twin of <see cref="GatherRulesFunctionAuthorEndpointTests"/>: the rule Author is
/// stamped from the caller's JWT on create, on a PUT-upsert of an unknown rule and on the
/// template copy, and a true update keeps the original creator over editor and body.
/// Before this pin, custom analyze rules were created as "Autopilot Monitor" and the edit
/// path let the browser choose the author.
/// </summary>
public class AnalyzeRulesFunctionAuthorEndpointTests
{
    private const string TenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string RuleId = "ANALYZE-CUSTOM-777";
    private const string TemplateRuleId = "ANALYZE-SEC-004";

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
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<WorkerOptions>(o => o.Serializer =
            new JsonObjectSerializer(ApiJsonOptions.Create()));
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

    private static (AnalyzeRulesFunction function, List<AnalyzeRule> stored) BuildFunction(
        AnalyzeRule? existingTenantRule = null, AnalyzeRule? globalRule = null)
    {
        var repo = new Mock<IRuleRepository>(MockBehavior.Loose);
        var stored = new List<AnalyzeRule>();

        repo.Setup(r => r.AnalyzeRuleExistsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        repo.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(
            globalRule == null ? new List<AnalyzeRule>() : new List<AnalyzeRule> { globalRule });
        repo.Setup(r => r.GetAnalyzeRulesAsync(TenantId)).ReturnsAsync(
            existingTenantRule == null ? new List<AnalyzeRule>() : new List<AnalyzeRule> { existingTenantRule });
        repo.Setup(r => r.GetRuleStatesAsync(TenantId)).ReturnsAsync(new Dictionary<string, RuleState>());
        repo.Setup(r => r.StoreRuleStateAsync(TenantId, It.IsAny<string>(), It.IsAny<RuleState>())).ReturnsAsync(true);
        repo.Setup(r => r.StoreAnalyzeRuleAsync(It.IsAny<AnalyzeRule>(), TenantId))
            .Callback<AnalyzeRule, string>((rule, _) => stored.Add(rule))
            .ReturnsAsync(true);

        var service = new AnalyzeRuleService(repo.Object, NullLogger<AnalyzeRuleService>.Instance);
        return (new AnalyzeRulesFunction(NullLogger<AnalyzeRulesFunction>.Instance, service), stored);
    }

    private static AnalyzeRule SpoofedPayload() => new()
    {
        RuleId = RuleId,
        Title = "Proxy PAC unreachable",
        Description = "Custom",
        Severity = "warning",
        Category = "network",
        Explanation = "The PAC file could not be fetched.",
        IsBuiltIn = false,
        IsCommunity = false,
        Enabled = true,
        Author = "Spoofed Author",
        Conditions = new List<RuleCondition>
        {
            new() { Signal = "pac_failed", Source = "event_type", EventType = "network_pac_failed", Required = true }
        },
    };

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
        var (function, stored) = BuildFunction();
        var req = BuildRequest(Principal(("tid", TenantId)), SpoofedPayload());

        var response = await function.CreateRule(req);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var written = Assert.Single(stored);
        Assert.Equal("Autopilot Monitor", written.Author);
    }

    [Fact]
    public async Task UpdateRule_upsert_of_unknown_rule_stamps_author_from_jwt()
    {
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
        var existing = SpoofedPayload();
        existing.Author = "Original Creator";
        existing.CreatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var (function, stored) = BuildFunction(existing);
        var req = BuildRequest(
            Principal(("tid", TenantId), ("name", "Bob Editor")), SpoofedPayload());

        var response = await function.UpdateRule(req, RuleId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var written = Assert.Single(stored);
        Assert.Equal("Original Creator", written.Author);
        Assert.Equal(existing.CreatedAt, written.CreatedAt);
    }

    [Fact]
    public async Task CreateFromTemplate_stamps_the_copying_admin_not_the_template_author()
    {
        var template = new AnalyzeRule
        {
            RuleId = TemplateRuleId,
            Title = "AutoLogon enabled for an unexpected user",
            Description = "Template",
            Severity = "warning",
            Category = "security",
            Explanation = "Template",
            IsBuiltIn = true,
            Author = "Autopilot Monitor",
            Conditions = new List<RuleCondition>
            {
                new() { Signal = "autologon", Source = "event_type", EventType = "autologon_analysis", Operator = "equals", Value = "PLACEHOLDER", Required = true }
            },
            TemplateVariables = new List<TemplateVariable>
            {
                new() { Name = "allowed", Label = "Allowed", ConditionIndex = 0, Field = "value" }
            },
        };
        var (function, stored) = BuildFunction(globalRule: template);
        var req = BuildRequest(AlicePrincipal(), new { variables = new Dictionary<string, string> { ["allowed"] = "KIOSK-1" } });

        var response = await function.CreateFromTemplate(req, TemplateRuleId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var written = Assert.Single(stored);
        Assert.Equal("Alice Admin", written.Author);
        Assert.Equal(TemplateRuleId, written.DerivedFromTemplateRuleId);
    }
}

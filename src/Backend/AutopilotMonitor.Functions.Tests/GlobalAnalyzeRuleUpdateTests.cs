using System.Net;
using AutopilotMonitor.Functions.Functions.Rules;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The cross-tenant rule update answers like the tenant route: a rule the service rejects is a 400
/// with the reason, never a 500 through the global exception middleware.
/// </summary>
public class GlobalAnalyzeRuleUpdateTests
{
    private const string TenantId = "00000000-0000-4000-8000-000000000002";

    [Fact]
    public async Task An_invalid_presentation_is_a_bad_request_and_nothing_is_stored()
    {
        var repo = new Mock<IRuleRepository>();
        var function = new GlobalRulesFunction(
            NullLogger<GlobalRulesFunction>.Instance,
            new GatherRuleService(repo.Object, NullLogger<GatherRuleService>.Instance),
            new AnalyzeRuleService(repo.Object, NullLogger<AnalyzeRuleService>.Instance));
        // A custom rule as the editor sends it (isBuiltIn defaults to true on the model).
        const string body = "{\"ruleId\":\"ANALYZE-CUSTOM-901\",\"isBuiltIn\":false,\"title\":\"t\",\"severity\":\"high\",\"category\":\"apps\","
            + "\"explanation\":\"e\",\"conditions\":[],\"presentation\":{\"role\":\"bogus\"}}";
        var (req, _) = EndpointHarness.Request(TenantId, jsonBody: body, queryString: $"?tenantId={TenantId}");

        var response = await function.UpdateAnalyzeRule(req, "ANALYZE-CUSTOM-901");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        repo.Verify(r => r.StoreAnalyzeRuleAsync(It.IsAny<AnalyzeRule>(), It.IsAny<string>()), Times.Never);
    }
}

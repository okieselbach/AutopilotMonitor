using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Rules;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The per-instance caps on synchronous rule work (D-379, D-410) and the ops events behind them: a
/// caller past a gate's limit gets 429 with a retry hint, the first refusal of a window raises one
/// RuleEvaluationLimitReached naming the gate, and stopped rules reach the operators through a
/// reporter that a tenant's rule count cannot turn into an alarm storm.
/// </summary>
public class RuleWorkGateTests
{
    private const string Upn = "admin@example.com";

    private readonly List<OpsEventEntry> _opsEvents = new();

    [Theory]
    [InlineData("dry-run", 5)]
    [InlineData("re-analysis", 5)]
    [InlineData("pattern-test", 3)]
    public async Task Caller_past_the_limit_gets_429_with_a_retry_hint_and_one_ops_event_per_window(string gateName, int retryAfter)
    {
        var gate = Gate(gateName, limit: 1);
        using var busy = gate.TryEnter();
        Assert.NotNull(busy);
        Assert.Null(gate.TryEnter());

        var (req, _) = EndpointHarness.Request("tenant-a", Upn);
        var refused = await gate.RefuseAsync(req, "tenant-a", Upn);
        await gate.RefuseAsync(req, "tenant-a", Upn);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.RateLimited, EndpointHarness.ErrorCode(refused));
        Assert.True(refused.Headers.TryGetValues("Retry-After", out var header));
        Assert.Equal(retryAfter.ToString(), header!.Single());
        // The situation only: the portal and the MCP phrase the retry from Retry-After.
        Assert.EndsWith("running right now.", ErrorText(refused));

        var alert = Assert.Single(_opsEvents);
        Assert.Equal(OpsEventTypes.RuleEvaluationLimitReached, alert.EventType);
        Assert.Equal(OpsEventSeverity.Warning, alert.Severity);
        Assert.Equal("tenant-a", alert.TenantId);
        using var details = JsonDocument.Parse(alert.Details!);
        Assert.Equal(gateName, details.RootElement.GetProperty("gate").GetString());
    }

    [Fact]
    public void Default_limits_never_refuse_a_person()
    {
        var ops = TestNotifications.CapturingOpsEvents(_opsEvents);
        Assert.Equal(2, new RuleDryRunGate(ops, NullLogger<RuleDryRunGate>.Instance).Limit);
        Assert.Equal(4, new ReanalyzeGate(ops, NullLogger<ReanalyzeGate>.Instance).Limit);
    }

    // ===== RuleEvaluationStopReporter =====

    [Fact]
    public async Task A_stopped_custom_rule_is_a_warning_and_a_stopped_catalog_rule_an_error()
    {
        var reporter = new RuleEvaluationStopReporter(TestNotifications.CapturingOpsEvents(_opsEvents));
        var outcome = new AnalysisOutcome();
        outcome.Stops.Add(new RuleEvaluationStop("CUSTOM-1", EvaluationStopReasons.RegexTimeout, IsCatalogRule: false));
        outcome.Stops.Add(new RuleEvaluationStop("ANALYZE-APP-002", EvaluationStopReasons.TimeBudget, IsCatalogRule: true));
        outcome.SkippedRuleIds.AddRange(new[] { "CUSTOM-2", "CUSTOM-3" });

        await reporter.ReportAsync("tenant-a", "session-1", AnalyzeRunKind.Terminal, outcome);

        Assert.Equal(2, _opsEvents.Count);
        Assert.All(_opsEvents, e => Assert.Equal(OpsEventTypes.RuleEvaluationStopped, e.EventType));
        Assert.Equal(OpsEventSeverity.Warning, _opsEvents[0].Severity);
        Assert.Equal(OpsEventSeverity.Error, _opsEvents[1].Severity);
        using var budgetStop = JsonDocument.Parse(_opsEvents[1].Details!);
        Assert.Equal(2, budgetStop.RootElement.GetProperty("skippedRules").GetInt32());
        using var regexStop = JsonDocument.Parse(_opsEvents[0].Details!);
        Assert.Equal(0, regexStop.RootElement.GetProperty("skippedRules").GetInt32());
    }

    [Fact]
    public void One_event_per_tenant_and_rule_per_window()
    {
        var reporter = new RuleEvaluationStopReporter(TestNotifications.CapturingOpsEvents(_opsEvents));
        var start = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(reporter.Take("tenant-a", "CUSTOM-1", start));
        Assert.False(reporter.Take("tenant-a", "CUSTOM-1", start.AddMinutes(59)));
        Assert.True(reporter.Take("tenant-b", "CUSTOM-1", start.AddMinutes(1)));
        Assert.True(reporter.Take("tenant-a", "CUSTOM-1", start + RuleEvaluationStopReporter.Window));
    }

    [Fact]
    public void A_tenant_with_many_rules_cannot_flood_the_alarm()
    {
        var reporter = new RuleEvaluationStopReporter(TestNotifications.CapturingOpsEvents(_opsEvents));
        var start = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

        var taken = Enumerable.Range(0, 1000).Count(i => reporter.Take("tenant-a", $"CUSTOM-{i}", start.AddSeconds(i)));

        Assert.Equal(RuleEvaluationStopReporter.MaxEventsPerWindow, taken);
        Assert.True(reporter.Take("tenant-a", "CUSTOM-new", start + RuleEvaluationStopReporter.Window));
    }

    private RuleWorkGate Gate(string name, int limit)
    {
        var ops = TestNotifications.CapturingOpsEvents(_opsEvents);
        return name switch
        {
            "dry-run" => new RuleDryRunGate(limit, ops, NullLogger<RuleDryRunGate>.Instance),
            "re-analysis" => new ReanalyzeGate(limit, ops, NullLogger<ReanalyzeGate>.Instance),
            _ => new TestLogPatternGate(limit, ops, NullLogger<TestLogPatternGate>.Instance),
        };
    }

    private static string ErrorText(Microsoft.Azure.Functions.Worker.Http.HttpResponseData response)
    {
        response.Body.Position = 0;
        using var doc = JsonDocument.Parse(response.Body);
        return doc.RootElement.GetProperty("error").GetString()!;
    }
}

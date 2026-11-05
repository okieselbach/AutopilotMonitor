using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Rules;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The evaluation budget of tenant-authored rules (D-410): the first regex match timeout stops the
/// rule instead of costing one second per event, a spent run budget leaves the later rules
/// unevaluated, every scan without an operator call is checked too, a rule that did not finish keeps
/// its stored row, and the dry-run reports the stop as verdict "stopped".
/// </summary>
public class RuleEvaluationBudgetTests
{
    private const string TenantId = "tenant-budget";
    private const string SessionId = "session-budget";

    /// <summary>Nested quantifier over input it cannot match: catastrophic backtracking, ends at the 1 s match timeout.</summary>
    private const string CatastrophicPattern = @"^(\w+\s?)+$";
    private static readonly string NonMatchingInput = new string('a', 60) + "!";

    // ===== analysis run =====

    [Fact]
    public async Task Regex_timeout_stops_the_rule_after_one_match_and_the_next_rule_still_runs()
    {
        var events = Enumerable.Range(1, 200).Select(i => Event("app_install_failed", i, ("errorDetail", NonMatchingInput))).ToList();
        var redos = CustomRule("TEST-REDOS", new RuleCondition
        {
            Signal = "slow", Source = "event_data", EventType = "app_install_failed",
            DataField = "errorDetail", Operator = "regex", Value = CatastrophicPattern, Required = true,
        });
        var next = CustomRule("TEST-NEXT", ExistsCondition("app_install_failed"));

        var clock = Stopwatch.StartNew();
        var outcome = await Harness(events, redos, next).Engine().AnalyzeSessionAsync(TenantId, SessionId);
        clock.Stop();

        // One timed-out match, not 200 of them.
        Assert.True(clock.ElapsedMilliseconds < 4000, $"took {clock.ElapsedMilliseconds} ms");
        var stop = Assert.Single(outcome.Stops);
        Assert.Equal(new RuleEvaluationStop("TEST-REDOS", EvaluationStopReasons.RegexTimeout, IsCatalogRule: false), stop);
        Assert.Empty(outcome.SkippedRuleIds);
        Assert.Equal(new[] { "TEST-NEXT" }, outcome.Results.Select(r => r.RuleId));
    }

    [Fact]
    public async Task Spent_budget_stops_the_run_keeps_every_unfinished_row_and_resolves_nothing()
    {
        var events = new List<EnrollmentEvent> { Event("app_install_failed", 1, ("appId", "app-1")) };
        var first = CustomRule("TEST-A", new RuleCondition
        {
            Signal = "a", Source = "event_data", EventType = "app_install_failed",
            DataField = "appId", Operator = "equals", Value = "app-1", Required = true,
        });
        var second = CustomRule("TEST-B", ExistsCondition("app_install_failed"));
        var third = CustomRule("TEST-C", ExistsCondition("app_install_failed"));
        var interimA = StoredRow("TEST-A", isInterim: true);
        var interimB = StoredRow("TEST-B", isInterim: true);

        var harness = Harness(events, first, second, third);
        harness.ExistingResults.AddRange(new[] { interimA, interimB });
        var engine = harness.Engine(TimeSpan.Zero);

        var outcome = await engine.AnalyzeSessionAsync(TenantId, SessionId, AnalyzeRunContext.Terminal());

        var stop = Assert.Single(outcome.Stops);
        Assert.Equal("TEST-A", stop.RuleId);
        Assert.Equal(EvaluationStopReasons.TimeBudget, stop.Reason);
        Assert.Equal(new[] { "TEST-B", "TEST-C" }, outcome.SkippedRuleIds);
        Assert.Empty(outcome.Results);
        Assert.Empty(outcome.ResolvedResults); // a stop is no evidence that a finding went away
        Assert.Equal(new[] { interimA, interimB }, outcome.KeptResults);
        Assert.Null(interimA.ResolvedAt);
        Assert.True(interimA.IsInterim);
    }

    /// <summary>
    /// Each of these evaluators scans the session per matching event without calling an operator
    /// (quadratic in the session size); without its own check a spent budget would not stop it, and
    /// the rule would simply be evaluated.
    /// </summary>
    [Theory]
    [InlineData("event_correlation pairing")]
    [InlineData("suppressByEvent scan")]
    [InlineData("app_install_duration start search")]
    public async Task Scans_without_an_operator_call_check_the_budget_too(string scan)
    {
        var (condition, events) = scan switch
        {
            "event_correlation pairing" => (
                new RuleCondition
                {
                    Signal = "pair", Source = "event_correlation", EventType = "app_install_started",
                    CorrelateEventType = "app_install_failed", JoinField = "appId", Required = true,
                },
                new[] { Event("app_install_started", 1, ("appId", "x")), Event("app_install_failed", 2, ("appId", "x")) }),
            "suppressByEvent scan" => (
                new RuleCondition
                {
                    Signal = "failed", Source = "event_type", EventType = "app_install_failed", Operator = "exists", Required = true,
                    SuppressByEvent = new SuppressByEventConfig { EventType = "app_install_completed", JoinField = "appId" },
                },
                new[] { Event("app_install_failed", 1, ("appId", "x")) }),
            _ => (
                new RuleCondition { Signal = "slow", Source = "app_install_duration", Operator = "gt", Value = "0", Required = true },
                new[] { Event("app_install_completed", 1, ("appId", "x")) }),
        };

        var outcome = await Harness(events.ToList(), CustomRule("TEST-SCAN", condition)).Engine(TimeSpan.Zero)
            .AnalyzeSessionAsync(TenantId, SessionId);

        var stop = Assert.Single(outcome.Stops);
        Assert.Equal(EvaluationStopReasons.TimeBudget, stop.Reason);
        Assert.Empty(outcome.Results);
    }

    // ===== dry-run =====

    [Fact]
    public void Dry_run_regex_timeout_ends_with_verdict_stopped_and_a_note_naming_the_condition()
    {
        var rule = CustomRule("TEST-DRY",
            ExistsCondition("app_install_failed"),
            new RuleCondition
            {
                Signal = "slow", Source = "event_data", EventType = "app_install_failed",
                DataField = "errorDetail", Operator = "regex", Value = CatastrophicPattern, Required = true,
            });
        var events = Enumerable.Range(1, 50).Select(i => Event("app_install_failed", i, ("errorDetail", NonMatchingInput))).ToList();

        var dry = Harness(events).Engine().DryRunEvaluate(rule, events, new RuleEvaluationBudget(RuleEvaluationBudget.DryRun));

        Assert.Equal(RuleDryRunVerdict.Stopped, dry.Verdict);
        Assert.Equal(EvaluationStopReasons.RegexTimeout, dry.StopReason);
        Assert.StartsWith("conditions[1] ('slow'): a regex hit the 1 s match timeout", dry.StopNote);
        // Only what finished before the stop; no half-evaluated evidence map.
        Assert.Equal(new[] { "exists_app_install_failed" }, dry.Conditions.Select(c => c.Signal));
        Assert.Null(dry.MatchedConditions);
        Assert.Null(dry.FinalConfidence);
    }

    [Fact]
    public void Dry_run_spent_budget_stops_at_the_first_operator()
    {
        var rule = CustomRule("TEST-DRY", ExistsCondition("app_install_failed"));
        rule.Preconditions = new List<RulePrecondition>
        {
            new() { Source = "event_data", EventType = "os_info", DataField = "edition", Operator = "equals", Value = "Enterprise" },
        };
        var events = new List<EnrollmentEvent> { Event("os_info", 1, ("edition", "Enterprise")) };

        var dry = Harness(events).Engine().DryRunEvaluate(rule, events, new RuleEvaluationBudget(TimeSpan.Zero));

        Assert.Equal(RuleDryRunVerdict.Stopped, dry.Verdict);
        Assert.Equal(EvaluationStopReasons.TimeBudget, dry.StopReason);
        Assert.StartsWith("preconditions[0] (os_info): the dry-run used up its 0 s time budget on this session's 1 events", dry.StopNote);
        Assert.Empty(dry.Preconditions);
        Assert.Empty(dry.Conditions);
    }

    [Fact]
    public async Task Dry_run_of_a_caller_who_went_away_stops_as_cancelled()
    {
        var events = new List<EnrollmentEvent> { Event("app_install_failed", 1, ("appId", "x")) };
        var rule = CustomRule("TEST-DRY", new RuleCondition
        {
            Signal = "a", Source = "event_data", EventType = "app_install_failed", DataField = "appId", Operator = "equals", Value = "x", Required = true,
        });
        using var gone = new CancellationTokenSource();
        gone.Cancel();

        var dry = await Harness(events).Engine().DryRunRuleAsync(TenantId, SessionId, rule, gone.Token);

        Assert.Equal(RuleDryRunVerdict.Stopped, dry.Verdict);
        Assert.Equal(EvaluationStopReasons.Cancelled, dry.StopReason);
    }

    // ===== "Analyze now" =====

    [Fact]
    public async Task Reanalyze_with_a_full_gate_answers_429_and_raises_one_ops_event()
    {
        var opsEvents = new List<OpsEventEntry>();
        var harness = Harness(new List<EnrollmentEvent>());
        var gate = new ReanalyzeGate(1, TestNotifications.CapturingOpsEvents(opsEvents), NullLogger<ReanalyzeGate>.Instance);
        using var busy = gate.TryEnter();
        var function = harness.Function(gate, opsEvents);

        var (req, _) = EndpointHarness.Request(TenantId, queryString: "?reanalyze=true");
        var response = await function.Run(req, SessionId);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.RateLimited, EndpointHarness.ErrorCode(response));
        Assert.True(response.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.Equal("5", retryAfter!.Single());
        var alert = Assert.Single(opsEvents);
        Assert.Equal(OpsEventTypes.RuleEvaluationLimitReached, alert.EventType);
        Assert.Contains("re-analysis", alert.Details);
        harness.Maintenance.Verify(m => m.DeleteSessionRuleResultsAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Reanalyze_writes_back_the_row_of_a_stopped_rule_and_names_it()
    {
        var events = Enumerable.Range(1, 20).Select(i => Event("app_install_failed", i, ("errorDetail", NonMatchingInput))).ToList();
        var redos = CustomRule("TEST-REDOS", new RuleCondition
        {
            Signal = "slow", Source = "event_data", EventType = "app_install_failed",
            DataField = "errorDetail", Operator = "regex", Value = CatastrophicPattern, Required = true,
        });
        var earlierFinding = StoredRow("TEST-REDOS", isInterim: false);
        var harness = Harness(events, redos);
        harness.ExistingResults.Add(earlierFinding);
        var opsEvents = new List<OpsEventEntry>();
        var function = harness.Function(
            new ReanalyzeGate(TestNotifications.CapturingOpsEvents(opsEvents), NullLogger<ReanalyzeGate>.Instance), opsEvents);

        var (req, _) = EndpointHarness.Request(TenantId, queryString: "?reanalyze=true");
        var response = await function.Run(req, SessionId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        harness.Maintenance.Verify(m => m.DeleteSessionRuleResultsAsync(TenantId, SessionId), Times.Once);
        harness.Rules.Verify(r => r.StoreRuleResultAsync(earlierFinding), Times.Once);

        response.Body.Position = 0;
        using var body = JsonDocument.Parse(response.Body);
        Assert.Equal(new[] { "TEST-REDOS" }, body.RootElement.GetProperty("stoppedRuleIds").EnumerateArray().Select(e => e.GetString()));

        var stopped = Assert.Single(opsEvents);
        Assert.Equal(OpsEventTypes.RuleEvaluationStopped, stopped.EventType);
        Assert.Equal(OpsEventSeverity.Warning, stopped.Severity);
    }

    // ===== fixtures =====

    private static RuleCondition ExistsCondition(string eventType) => new()
    {
        Signal = "exists_" + eventType, Source = "event_type", EventType = eventType, Operator = "exists", Required = true,
    };

    private static AnalyzeRule CustomRule(string ruleId, params RuleCondition[] conditions) => new()
    {
        RuleId = ruleId,
        IsBuiltIn = false, // the model defaults to a built-in rule
        Title = ruleId,
        Severity = "warning",
        Category = "test",
        Enabled = true,
        BaseConfidence = 80,
        ConfidenceThreshold = 40,
        Conditions = conditions.ToList(),
        Explanation = "test",
    };

    private static RuleResult StoredRow(string ruleId, bool isInterim) => new()
    {
        RuleId = ruleId,
        ResultId = "result-" + ruleId,
        TenantId = TenantId,
        SessionId = SessionId,
        IsInterim = isInterim,
        DetectedAt = DateTime.UtcNow.AddMinutes(-30),
    };

    private static EnrollmentEvent Event(string eventType, int sequence, params (string Key, object Value)[] data) => new()
    {
        EventId = $"evt-{sequence}",
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = eventType,
        Sequence = sequence,
        Timestamp = DateTime.UtcNow.AddMinutes(sequence),
        Data = data.ToDictionary(d => d.Key, d => d.Value),
    };

    private static RuleHarness Harness(List<EnrollmentEvent> events, params AnalyzeRule[] customRules) => new(events, customRules);

    /// <summary>The tenant's custom rules behind a real AnalyzeRuleService, mocked storage.</summary>
    private sealed class RuleHarness
    {
        public Mock<IRuleRepository> Rules { get; } = new();
        public Mock<ISessionRepository> Sessions { get; } = new();
        public Mock<IMaintenanceRepository> Maintenance { get; } = new();
        public List<RuleResult> ExistingResults { get; } = new();

        public RuleHarness(List<EnrollmentEvent> events, AnalyzeRule[] customRules)
        {
            Rules.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(new List<AnalyzeRule>());
            Rules.Setup(r => r.GetAnalyzeRulesAsync(TenantId)).ReturnsAsync(customRules.ToList());
            Rules.Setup(r => r.GetRuleStatesAsync(It.IsAny<string>())).ReturnsAsync(new Dictionary<string, RuleState>());
            Rules.Setup(r => r.GetRuleResultsAsync(TenantId, SessionId)).ReturnsAsync(() => ExistingResults.ToList());
            Rules.Setup(r => r.StoreRuleResultAsync(It.IsAny<RuleResult>())).ReturnsAsync(true);
            Sessions.Setup(s => s.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>())).ReturnsAsync(events);
        }

        private AnalyzeRuleService Service() => new(Rules.Object, NullLogger<AnalyzeRuleService>.Instance);

        public RuleEngine Engine(TimeSpan? runBudget = null) =>
            new(Service(), Rules.Object, Sessions.Object, NullLogger<RuleEvaluationBudgetTests>.Instance)
            {
                RunBudget = runBudget ?? RuleEvaluationBudget.AnalysisRun,
            };

        public GetRuleResultsFunction Function(ReanalyzeGate gate, List<OpsEventEntry> opsEvents) =>
            new(NullLogger<GetRuleResultsFunction>.Instance, Rules.Object, Maintenance.Object, Sessions.Object, Service(),
                gate, new RuleEvaluationStopReporter(TestNotifications.CapturingOpsEvents(opsEvents)));
    }
}

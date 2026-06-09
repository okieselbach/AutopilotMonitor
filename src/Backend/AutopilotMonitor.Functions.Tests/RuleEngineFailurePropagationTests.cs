using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the queue-path retry contract: <see cref="RuleEngine.AnalyzeSessionAsync"/> must
/// propagate storage exceptions to the caller. The previous outer try/catch swallowed all
/// errors and returned an empty <see cref="AnalysisOutcome"/>, which caused
/// <c>AnalyzeOnEnrollmentEndQueueWorker</c> to delete the message as success and never retry —
/// rule results were silently lost on transient Table Storage failures.
/// </summary>
public class RuleEngineFailurePropagationTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    [Fact]
    public async Task GetSessionEventsStrictAsync_throwing_propagates_to_caller()
    {
        var ruleRepo = new Mock<IRuleRepository>();
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync(It.IsAny<string>())).ReturnsAsync(new List<AnalyzeRule>());
        ruleRepo.Setup(r => r.GetRuleStatesAsync(It.IsAny<string>())).ReturnsAsync(new Dictionary<string, RuleState>());

        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(s => s.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("simulated GetSessionEventsStrictAsync failure"));

        var ruleService = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<RuleEngineFailurePropagationTests>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.AnalyzeSessionAsync(TenantId, SessionId));

        Assert.Contains("simulated GetSessionEventsStrictAsync failure", ex.Message);
    }

    [Fact]
    public async Task GetAnalyzeRulesAsync_throwing_propagates_to_caller()
    {
        var ruleRepo = new Mock<IRuleRepository>();
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("simulated GetAnalyzeRulesAsync failure"));

        var sessionRepo = new Mock<ISessionRepository>();

        var ruleService = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<RuleEngineFailurePropagationTests>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.AnalyzeSessionAsync(TenantId, SessionId));

        Assert.Contains("simulated GetAnalyzeRulesAsync failure", ex.Message);
    }

    [Fact]
    public async Task GetRuleResultsAsync_throwing_propagates_to_caller()
    {
        // GetRuleResultsAsync is the dedup-lookup. A storage hiccup there must NOT be
        // silently treated as "no existing results" — that would re-fire every rule and
        // duplicate stored results. Propagate so the worker retries.
        var ruleRepo = new Mock<IRuleRepository>();
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync(It.IsAny<string>())).ReturnsAsync(new List<AnalyzeRule>());
        ruleRepo.Setup(r => r.GetRuleStatesAsync(It.IsAny<string>())).ReturnsAsync(new Dictionary<string, RuleState>());
        ruleRepo.Setup(r => r.GetRuleResultsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("simulated GetRuleResultsAsync failure"));

        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(s => s.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>()))
            .ReturnsAsync(new List<EnrollmentEvent>
            {
                new() { TenantId = TenantId, SessionId = SessionId, EventType = "phase_changed" }
            });

        var ruleService = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<RuleEngineFailurePropagationTests>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.AnalyzeSessionAsync(TenantId, SessionId));

        Assert.Contains("simulated GetRuleResultsAsync failure", ex.Message);
    }

    [Fact]
    public async Task Empty_event_list_returns_empty_outcome_without_throwing()
    {
        // Confirms the legitimate "no events" case is preserved: AnalyzeSessionAsync returns
        // an empty outcome cleanly. Since the engine reads via GetSessionEventsStrictAsync
        // (storage failures throw), an empty list here genuinely means "session without
        // events" — the historical fail-soft ambiguity is resolved.
        var ruleRepo = new Mock<IRuleRepository>();
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync(It.IsAny<string>())).ReturnsAsync(new List<AnalyzeRule>());
        ruleRepo.Setup(r => r.GetRuleStatesAsync(It.IsAny<string>())).ReturnsAsync(new Dictionary<string, RuleState>());

        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(s => s.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>()))
            .ReturnsAsync(new List<EnrollmentEvent>());

        var ruleService = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<RuleEngineFailurePropagationTests>.Instance);

        var outcome = await engine.AnalyzeSessionAsync(TenantId, SessionId);

        Assert.NotNull(outcome);
        Assert.Empty(outcome.Results);
        Assert.Empty(outcome.EvaluatedRules);
    }
}

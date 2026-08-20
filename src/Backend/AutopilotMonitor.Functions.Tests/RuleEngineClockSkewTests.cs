using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the clock_skew condition source (EvaluateClockSkewCondition) behind ANALYZE-DEV-008.
/// The evaluator measures d = Timestamp − ReceivedAt per event, reduces to per-upload-batch
/// medians (wide-spread batches = spool backlogs are dropped), then detects either a persistent
/// mid-session step between two flat plateaus (clock_jump) or a stable whole-session offset
/// (sustained_offset). IME-log-derived events are excluded so a CMTrace anchoring regression
/// (the e9753578 field incident) can never fire this customer-facing rule — that failure mode
/// belongs to the operator-side CmTraceSkewTripwire.
/// </summary>
public class RuleEngineClockSkewTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    private static readonly DateTime T0 = new(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc);

    // ── clock_jump: must fire ──────────────────────────────────────────────

    [Fact]
    public async Task ClockJump_ForwardStepBetweenStableFrames_Fires()
    {
        // 4 batches on frame 0, then 4 batches on frame +1800 s (+30 min) that hold to
        // session end — the canonical "clock corrected forward mid-session".
        var events = Batches(offsetsSeconds: new double[] { 0, 0, 0, 0, 1800, 1800, 1800, 1800 });

        var outcome = await RunAsync(MakeRule("clock_jump", "300"), events);

        var result = Assert.Single(outcome.Results);
        var evidence = Assert.IsAssignableFrom<IDictionary<string, object>>(result.MatchedConditions["clock_jump"]);
        Assert.Equal("forward", evidence["direction"]);
        Assert.Equal(1800, Convert.ToDouble(evidence["jumpSeconds"]), 0);
        Assert.Equal("skewSummary", evidence["field"]);
    }

    [Fact]
    public async Task ClockJump_BackwardStep_Fires()
    {
        var events = Batches(new double[] { 0, 0, 0, -1200, -1200, -1200 });

        var outcome = await RunAsync(MakeRule("clock_jump", "300"), events);

        var result = Assert.Single(outcome.Results);
        var evidence = Assert.IsAssignableFrom<IDictionary<string, object>>(result.MatchedConditions["clock_jump"]);
        Assert.Equal("backward", evidence["direction"]);
    }

    [Fact]
    public async Task ClockJump_WrongClockCorrectedMidSession_Fires()
    {
        // Device started 30 min behind, time sync fixed it: the CORRECTED frame is the
        // persistent change, so this must fire (direction forward).
        var events = Batches(new double[] { -1800, -1800, -1800, 0, 0, 0 });

        var outcome = await RunAsync(MakeRule("clock_jump", "300"), events);

        var result = Assert.Single(outcome.Results);
        var evidence = Assert.IsAssignableFrom<IDictionary<string, object>>(result.MatchedConditions["clock_jump"]);
        Assert.Equal("forward", evidence["direction"]);
    }

    // ── clock_jump: must stay silent ───────────────────────────────────────

    [Fact]
    public async Task ClockJump_ImeDerivedSkew_ExcludedFromMeasurement_Silent()
    {
        // e9753578 replica: IME-derived events carry grid-multiple wrong timestamps (−7 h /
        // +2 h writer eras) while the agent's own events are healthy. The IME events must be
        // excluded up front — a CMTrace anchoring regression may NEVER fire the customer rule.
        var events = Batches(new double[] { 0, 0, 0, 0, 0, 0 });
        var seq = 1000;
        foreach (var imeOffset in new double[] { -25200, -25200, 7200, 7200, -25200, 7200 })
            events.Add(MakeEvent(seq++, T0.AddMinutes(seq % 7), imeOffset, source: "ImeLogTracker"));

        var outcome = await RunAsync(MakeRule("clock_jump", "300"), events);

        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ClockJump_WideSpreadSpoolBatch_DroppedAndSilent()
    {
        // One upload batch whose events span 30 min of emission time (offline spool flushed
        // in a single request) — the internal spread disqualifies the batch entirely.
        var events = Batches(new double[] { 0, 0, 0, 0, 0, 0 });
        var flushReceivedAt = T0.AddMinutes(90);
        for (int i = 0; i < 20; i++)
            events.Add(MakeEvent(2000 + i, flushReceivedAt, offsetSeconds: -1800 + i * 90));

        var outcome = await RunAsync(MakeRule("clock_jump", "300"), events);

        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ClockJump_GradualBacklogDrainRamp_Silent()
    {
        // A chunked spool drain produces a RAMP of batch medians walking back to the live
        // frame. No flat pre/post plateau pair exists, so the step detector must reject every
        // candidate — this is a delivery artifact, not a clock change.
        var events = Batches(new double[] { -1800, -1500, -1200, -900, -600, -300, 0, 0 });

        var outcome = await RunAsync(MakeRule("clock_jump", "300"), events);

        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ClockJump_TransientExcursionReturnsToFrame_Silent()
    {
        // Frame dips for two batches and returns — whatever it was, it is not a SUSTAINED
        // clock change (end-state persistence fails).
        var events = Batches(new double[] { 0, 0, 0, -1800, -1800, 0, 0, 0 });

        var outcome = await RunAsync(MakeRule("clock_jump", "300"), events);

        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ClockJump_BelowThreshold_Silent()
    {
        var events = Batches(new double[] { 0, 0, 0, 240, 240, 240 });

        var outcome = await RunAsync(MakeRule("clock_jump", "300"), events);

        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ClockJump_TooFewBatches_Silent()
    {
        // 5 batches < 2×ConfirmBatches — not enough for two comparison windows.
        var events = Batches(new double[] { 0, 0, 1800, 1800, 1800 });

        var outcome = await RunAsync(MakeRule("clock_jump", "300"), events);

        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ClockJump_LegacyEventsWithoutReceivedAt_Silent()
    {
        var events = Batches(new double[] { 0, 0, 0, 1800, 1800, 1800 });
        foreach (var e in events)
            e.ReceivedAt = null;

        var outcome = await RunAsync(MakeRule("clock_jump", "300"), events);

        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ClockJump_ClampedEventsExcluded_Silent()
    {
        // The jumped frame exists only on clamped events (their OccurredUtc is already
        // server-substituted in production) — excluding them leaves a single stable frame.
        var events = Batches(new double[] { 0, 0, 0, 0, 0, 0 });
        var seq = 3000;
        foreach (var offset in new double[] { 1800, 1800, 1800 })
        {
            var evt = MakeEvent(seq, T0.AddMinutes(120 + seq % 5), offset);
            evt.TimestampClamped = true;
            events.Add(evt);
            seq++;
        }

        var outcome = await RunAsync(MakeRule("clock_jump", "300"), events);

        Assert.Empty(outcome.Results);
    }

    // ── sustained_offset ───────────────────────────────────────────────────

    [Fact]
    public async Task SustainedOffset_ClockBehindWholeSession_Fires()
    {
        var events = Batches(new double[] { -1800, -1795, -1805, -1800, -1798 });

        var outcome = await RunAsync(MakeRule("sustained_offset", "300"), events);

        var result = Assert.Single(outcome.Results);
        var evidence = Assert.IsAssignableFrom<IDictionary<string, object>>(result.MatchedConditions["sustained_offset"]);
        Assert.Equal("behind", evidence["direction"]);
        Assert.Equal(-30.0, Convert.ToDouble(evidence["offsetMinutes"]), 1);
    }

    [Fact]
    public async Task SustainedOffset_HealthyUploadLatencyBaseline_Silent()
    {
        // Healthy devices sit a few seconds NEGATIVE (upload latency) — far below threshold.
        var events = Batches(new double[] { -3, -5, -2, -4, -6 });

        var outcome = await RunAsync(MakeRule("sustained_offset", "300"), events);

        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task SustainedOffset_UnstableBatchOffsets_Silent()
    {
        // Medians all over the place: no honest single-offset statement exists, even though
        // the overall median magnitude clears the threshold.
        var events = Batches(new double[] { -2400, 900, -1800, 300, -3600, -600 });

        var outcome = await RunAsync(MakeRule("sustained_offset", "300"), events);

        Assert.Empty(outcome.Results);
    }

    // ── ANALYZE-DEV-008 shape: two optional conditions ─────────────────────

    [Fact]
    public async Task Dev008Shape_JumpOnlySession_FiresWithSingleMatchedCondition()
    {
        // Mirrors the shipped rule: both conditions required:false, baseConfidence 60,
        // threshold 50 — one matching metric must be enough to fire.
        var rule = MakeRule("clock_jump", "300");
        rule.Conditions[0].Required = false;
        rule.Conditions.Add(new RuleCondition
        {
            Signal = "sustained_offset",
            Source = "clock_skew",
            SkewMetric = "sustained_offset",
            Operator = "gte",
            Value = "300",
            Required = false,
        });

        var events = Batches(new double[] { 0, 0, 0, 0, 1800, 1800, 1800, 1800 });

        var outcome = await RunAsync(rule, events);

        var result = Assert.Single(outcome.Results);
        Assert.True(result.MatchedConditions.ContainsKey("clock_jump"));
    }

    // ===== Helpers =====

    private static AnalyzeRule MakeRule(string skewMetric, string thresholdSeconds) => new()
    {
        RuleId = "ANALYZE-TST-CLK",
        Title = "clock_skew test rule",
        Severity = "warning",
        Category = "device",
        Enabled = true,
        IsBuiltIn = false,
        BaseConfidence = 60,
        ConfidenceThreshold = 50,
        Conditions = new List<RuleCondition>
        {
            new()
            {
                Signal = skewMetric,
                Source = "clock_skew",
                SkewMetric = skewMetric,
                Operator = "gte",
                Value = thresholdSeconds,
                Required = true,
            }
        },
        Explanation = "test {{skewSummary}}",
    };

    /// <summary>
    /// One upload batch per entry: 5 events sharing the batch's ReceivedAt, each with
    /// Timestamp = ReceivedAt + offsetSeconds (±2 s in-batch jitter, well under the spread cap).
    /// </summary>
    private static List<EnrollmentEvent> Batches(double[] offsetsSeconds)
    {
        var events = new List<EnrollmentEvent>();
        long seq = 1;
        for (int b = 0; b < offsetsSeconds.Length; b++)
        {
            var receivedAt = T0.AddMinutes(b * 5);
            for (int i = 0; i < 5; i++)
                events.Add(MakeEvent(seq++, receivedAt, offsetsSeconds[b] + (i - 2)));
        }
        return events;
    }

    private static EnrollmentEvent MakeEvent(long sequence, DateTime receivedAt, double offsetSeconds,
        string source = "DecisionEngine") => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "status_update",
        Source = source,
        Timestamp = receivedAt.AddSeconds(offsetSeconds),
        ReceivedAt = receivedAt,
        Sequence = sequence,
        Data = new Dictionary<string, object>(),
    };

    private static async Task<AnalysisOutcome> RunAsync(AnalyzeRule rule, List<EnrollmentEvent> events)
    {
        var ruleRepo = new Mock<IRuleRepository>();
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(new List<AnalyzeRule> { rule });
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync(TenantId)).ReturnsAsync(new List<AnalyzeRule>());
        ruleRepo.Setup(r => r.GetRuleStatesAsync(It.IsAny<string>())).ReturnsAsync(new Dictionary<string, RuleState>());
        ruleRepo.Setup(r => r.GetRuleResultsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(new List<RuleResult>());

        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(s => s.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>())).ReturnsAsync(events);

        var ruleService = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<RuleEngineClockSkewTests>.Instance);

        return await engine.AnalyzeSessionAsync(TenantId, SessionId);
    }
}

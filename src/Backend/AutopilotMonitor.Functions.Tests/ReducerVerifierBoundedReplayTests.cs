using System.Diagnostics;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// DoS hardening of the reducer-verification replay: device-uploaded signal payloads are
/// attacker-shaped (chunk-stored up to ~1 MB each, 5000 per request). The verifier must
/// (a) decode-fold-discard so peak memory is one signal, not the whole stream, and
/// (b) finish in seconds on a forged RealmJoin package stream because
/// <see cref="FactStringBounds"/> bounds the dedupe keys inside the engine. The
/// signal/transition count-imbalance semantics of the pre-streaming implementation are pinned
/// here as well because the streaming rewrite changed how that count is produced.
/// </summary>
public class ReducerVerifierBoundedReplayTests
{
    private const string TenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";
    private static readonly DateTime T0 = new(2026, 8, 30, 10, 0, 0, DateTimeKind.Utc);
    private static string CurrentReducerVersion { get; } = new DecisionEngine().ReducerVersion;

    private static DecisionSignal MakeSignal(long ordinal, DecisionSignalKind kind, IReadOnlyDictionary<string, string>? payload = null) =>
        new(
            sessionSignalOrdinal: ordinal,
            sessionTraceOrdinal: ordinal,
            kind: kind,
            kindSchemaVersion: 1,
            occurredAtUtc: T0.AddSeconds(ordinal),
            sourceOrigin: "replay-test",
            evidence: new Evidence(EvidenceKind.Synthetic, $"replay:ord-{ordinal}", "test"),
            payload: payload);

    /// <summary>Fold <paramref name="sigs"/> through the live reducer and materialise the stored shapes.</summary>
    private static (SignalRecord[] signals, DecisionTransitionRecord[] transitions) Materialise(IReadOnlyList<DecisionSignal> sigs)
    {
        var engine = new DecisionEngine();
        var state = DecisionState.CreateInitial(SessionId, TenantId);
        var transitions = new DecisionTransitionRecord[sigs.Count];
        var signals = new SignalRecord[sigs.Count];

        for (var i = 0; i < sigs.Count; i++)
        {
            var step = engine.Reduce(state, sigs[i]);
            state = step.NewState;
            var t = step.Transition;
            transitions[i] = new DecisionTransitionRecord
            {
                TenantId = TenantId,
                SessionId = SessionId,
                StepIndex = t.StepIndex,
                SessionTraceOrdinal = t.SessionTraceOrdinal,
                SignalOrdinalRef = t.SignalOrdinalRef,
                OccurredAtUtc = t.OccurredAtUtc,
                Trigger = t.Trigger,
                FromStage = t.FromStage.ToString(),
                ToStage = t.ToStage.ToString(),
                Taken = t.Taken,
                DeadEndReason = t.DeadEndReason,
                ReducerVersion = t.ReducerVersion,
                PayloadJson = TransitionSerializer.Serialize(t),
            };
            signals[i] = new SignalRecord
            {
                TenantId = TenantId,
                SessionId = SessionId,
                SessionSignalOrdinal = sigs[i].SessionSignalOrdinal,
                SessionTraceOrdinal = sigs[i].SessionTraceOrdinal,
                Kind = sigs[i].Kind.ToString(),
                KindSchemaVersion = sigs[i].KindSchemaVersion,
                OccurredAtUtc = sigs[i].OccurredAtUtc,
                SourceOrigin = sigs[i].SourceOrigin,
                PayloadJson = SignalSerializer.Serialize(sigs[i]),
            };
        }

        return (signals, transitions);
    }

    [Fact]
    public void Forged_realmjoin_package_stream_replays_in_bounded_time_with_zero_divergence()
    {
        // Exploit shape: many equal-length package ids sharing all but the trailing digits.
        // Bounded to 256 chars inside the engine they collapse to a single package row, so the
        // per-signal dedupe scan compares one short key instead of 200 × 200 KB.
        const int packageSignals = 250;
        const int idLength = 200_000;
        var sigs = new List<DecisionSignal>(packageSignals + 2)
        {
            MakeSignal(0, DecisionSignalKind.SessionStarted),
            MakeSignal(1, DecisionSignalKind.RealmJoinDetected,
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100" }),
        };
        for (var i = 0; i < packageSignals; i++)
        {
            sigs.Add(MakeSignal(2 + i, DecisionSignalKind.RealmJoinPackageStarted, new Dictionary<string, string>
            {
                [DecisionEngine.RealmJoinPayloadKeys.PackageId] = new string('p', idLength - 8) + i.ToString("D8"),
                [DecisionEngine.RealmJoinPayloadKeys.Scope] = RealmJoinPackageFact.ScopeMachine,
            }));
        }
        var (signals, transitions) = Materialise(sigs);
        Assert.All(signals.Skip(2), s => Assert.True(s.PayloadJson.Length > idLength));

        var sw = Stopwatch.StartNew();
        var report = ReducerVerifier.Verify(TenantId, SessionId, signals, transitions, CurrentReducerVersion);
        sw.Stop();

        Assert.True(report.SemanticReplayPerformed);
        Assert.Equal(0, report.TransitionDivergenceCount);
        Assert.DoesNotContain(report.Issues, i => i.Kind == "replay_divergence");
        // Deserialising 250 × 200 KB JSON blobs is the dominant (linear) cost here; the
        // pre-fix quadratic scan alone was tens of seconds for this shape.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"verification took {sw.Elapsed}");
    }

    [Fact]
    public void Signal_surplus_beyond_the_journal_is_still_reported_as_count_imbalance()
    {
        var sigs = new[]
        {
            MakeSignal(0, DecisionSignalKind.SessionStarted),
            MakeSignal(1, DecisionSignalKind.AppInstallCompleted),
            MakeSignal(2, DecisionSignalKind.AppInstallCompleted),
        };
        var (signals, transitions) = Materialise(sigs);
        var shortJournal = new[] { transitions[0], transitions[1] };

        var report = ReducerVerifier.Verify(TenantId, SessionId, signals, shortJournal, CurrentReducerVersion);

        Assert.True(report.SemanticReplayPerformed);
        Assert.Contains(report.Issues, i => i.Kind == "replay_divergence" && i.Message.StartsWith("Signal count (3) != transition count (2)"));
    }

    [Fact]
    public void Mid_stream_deserialisation_failure_truncates_replay_and_counts_only_decoded_signals()
    {
        var sigs = new[]
        {
            MakeSignal(0, DecisionSignalKind.SessionStarted),
            MakeSignal(1, DecisionSignalKind.AppInstallCompleted),
            MakeSignal(2, DecisionSignalKind.AppInstallCompleted),
        };
        var (signals, transitions) = Materialise(sigs);
        signals[1].PayloadJson = "{not json";

        var report = ReducerVerifier.Verify(TenantId, SessionId, signals, transitions, CurrentReducerVersion);

        Assert.True(report.SemanticReplayPerformed);
        Assert.Contains(report.Issues, i => i.Kind == "replay_deserialization_error" && i.Message.Contains("ordinal 1"));
        Assert.Contains(report.Issues, i => i.Kind == "replay_divergence" && i.Message.StartsWith("Signal count (1) != transition count (3)"));
    }

    [Fact]
    public void Head_deserialisation_failure_skips_replay_silently()
    {
        var (signals, transitions) = Materialise(new[] { MakeSignal(0, DecisionSignalKind.SessionStarted) });
        signals[0].PayloadJson = "stub";

        var report = ReducerVerifier.Verify(TenantId, SessionId, signals, transitions, CurrentReducerVersion);

        Assert.False(report.SemanticReplayPerformed);
        Assert.Equal("deserialization_failure", report.SemanticReplaySkipReason);
        Assert.DoesNotContain(report.Issues, i => i.Kind == "replay_deserialization_error");
    }

    // ---- Repository payload budget ---------------------------------------------------

    [Fact]
    public void Payload_budget_exhaustion_mirrors_the_repository_stop_condition()
    {
        SignalRecord Row(int chars) => new() { PayloadJson = new string('x', chars) };

        Assert.False(SignalQueryLimits.IsPayloadBudgetExhausted(new[] { Row(10), Row(10) }, maxTotalPayloadChars: 100));
        Assert.True(SignalQueryLimits.IsPayloadBudgetExhausted(new[] { Row(60), Row(40) }, maxTotalPayloadChars: 100));
        Assert.True(SignalQueryLimits.IsPayloadBudgetExhausted(new[] { Row(60), Row(60) }, maxTotalPayloadChars: 100));
        Assert.False(SignalQueryLimits.IsPayloadBudgetExhausted(Array.Empty<SignalRecord>(), maxTotalPayloadChars: 100));
    }
}

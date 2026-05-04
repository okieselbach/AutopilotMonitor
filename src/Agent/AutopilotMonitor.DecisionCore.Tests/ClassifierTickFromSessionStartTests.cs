using System;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Plan §4.x M4.4.4 — <c>ClassifierTick</c> is armed on <c>SessionStarted</c> so the
    /// WhiteGlove classifier re-evaluates periodically from the start of every session,
    /// not only reactively on the first WG-relevant signal (which never fires for non-WG
    /// paths and closed the M3 re-trigger-lücke).
    /// </summary>
    public sealed class ClassifierTickFromSessionStartTests
    {
        private static readonly DateTime SessionStartUtc = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        // Pass SessionStartUtc as the agent-boot anchor so the EffectiveDeadlineBase guard
        // doesn't floor signal-time-based deadlines at the test runner's wall-clock now
        // (which would always be later than the deterministic SessionStartUtc constant).
        private static DecisionState FreshState() =>
            DecisionState.CreateInitial("s", "t", SessionStartUtc);

        [Fact]
        public void SessionStarted_from_fresh_state_arms_ClassifierTick_deadline()
        {
            var engine = new DecisionEngine();
            var state = FreshState();

            var step = engine.Reduce(state, MakeSessionStarted(ordinal: 0));

            Assert.True(step.Transition.Taken);
            Assert.Single(step.NewState.Deadlines);
            Assert.Equal(DeadlineNames.ClassifierTick, step.NewState.Deadlines[0].Name);
        }

        [Fact]
        public void Armed_ClassifierTick_dueAt_equals_signalOccurredAtUtc_plus_30s()
        {
            // Plan §2.6 — deadline dueAt is deterministic from the triggering signal's
            // OccurredAtUtc, not from clock.UtcNow. Replay must reproduce it.
            var engine = new DecisionEngine();
            var state = FreshState();

            var step = engine.Reduce(state, MakeSessionStarted(ordinal: 0));
            var tick = step.NewState.Deadlines[0];

            Assert.Equal(SessionStartUtc.AddSeconds(30), tick.DueAtUtc);
            Assert.Equal(DecisionSignalKind.DeadlineFired, tick.FiresSignalKind);
            Assert.NotNull(tick.FiresPayload);
            Assert.Equal(DeadlineNames.ClassifierTick, tick.FiresPayload![SignalPayloadKeys.Deadline]);
        }

        [Fact]
        public void SessionStarted_emits_ScheduleDeadline_effect_for_ClassifierTick()
        {
            var engine = new DecisionEngine();
            var state = FreshState();

            var step = engine.Reduce(state, MakeSessionStarted(ordinal: 0));

            Assert.Single(step.Effects);
            var effect = step.Effects[0];
            Assert.Equal(DecisionEffectKind.ScheduleDeadline, effect.Kind);
            Assert.NotNull(effect.Deadline);
            Assert.Equal(DeadlineNames.ClassifierTick, effect.Deadline!.Name);
            Assert.Equal(step.NewState.Deadlines[0].DueAtUtc, effect.Deadline.DueAtUtc);
        }

        [Fact]
        public void DeadEnd_SessionStarted_on_active_state_does_NOT_arm_ClassifierTick()
        {
            // Plan §2.5 — dead-end branch (replay of truncated log, session already mid-flight)
            // must not reset state; specifically, no new deadline.
            var engine = new DecisionEngine();
            var active = FreshState()
                .ToBuilder()
                .WithStage(SessionStage.AwaitingHello)
                .WithStepIndex(5)
                .WithLastAppliedSignalOrdinal(4)
                .Build();

            var step = engine.Reduce(active, MakeSessionStarted(ordinal: 5));

            Assert.False(step.Transition.Taken);
            Assert.Empty(step.NewState.Deadlines);
            Assert.Empty(step.Effects);
        }

        [Fact]
        public void ClassifierTick_from_session_start_persists_in_snapshot_and_fires_via_DeadlineFired()
        {
            // End-to-end: SessionStarted arms the tick; firing the deadline runs classifier +
            // re-arms. Wiring depends only on the generic DeadlineFired dispatcher + the
            // existing HandleClassifierTickDeadlineFired handler from M3.3+.
            var engine = new DecisionEngine();
            var state = FreshState();

            var startStep = engine.Reduce(state, MakeSessionStarted(ordinal: 0));
            Assert.Single(startStep.NewState.Deadlines);

            var firedSignal = MakeDeadlineFired(
                ordinal: 1,
                occurredAtUtc: SessionStartUtc.AddSeconds(30),
                deadlineName: DeadlineNames.ClassifierTick);

            var firedStep = engine.Reduce(startStep.NewState, firedSignal);

            Assert.True(firedStep.Transition.Taken);
            // After fire: the deadline is cancelled + re-armed (same name, new dueAt).
            Assert.Single(firedStep.NewState.Deadlines);
            Assert.Equal(DeadlineNames.ClassifierTick, firedStep.NewState.Deadlines[0].Name);
            Assert.Equal(SessionStartUtc.AddSeconds(60), firedStep.NewState.Deadlines[0].DueAtUtc);
        }

        private static DecisionSignal MakeSessionStarted(long ordinal) =>
            new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: DecisionSignalKind.SessionStarted,
                kindSchemaVersion: 1,
                occurredAtUtc: SessionStartUtc,
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Synthetic, "session:started", "test"));

        private static DecisionSignal MakeDeadlineFired(long ordinal, DateTime occurredAtUtc, string deadlineName) =>
            new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: DecisionSignalKind.DeadlineFired,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Synthetic, $"deadline:{deadlineName}:fired", "test"),
                payload: new System.Collections.Generic.Dictionary<string, string>
                {
                    [SignalPayloadKeys.Deadline] = deadlineName,
                });
    }
}

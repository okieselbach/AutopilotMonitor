using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// A <c>DeadlineFired</c> signal is stamped with the wall-clock firing time, not the due
    /// time: a deadline due during Modern Standby or a reboot fires only afterwards, and every
    /// effect of the resulting step (phase_transition, enrollment_complete) inherits the signal
    /// time — a due-time stamp would date the verdict into the outage (session ac5660b8,
    /// "Completed" drawn inside the Asleep block). The due time travels in the payload under
    /// <see cref="SignalPayloadKeys.DeadlineDueAtUtc"/> for the stale-fire guards that identify
    /// the deadline incarnation; without the key they fall back to the signal time (pre-key
    /// signal logs, where the two were equal by contract).
    /// </summary>
    public sealed class DeadlineFireTimeStampTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 9, 6, 31, 0, DateTimeKind.Utc);

        private static DecisionSignal MakeSignal(
            long ordinal,
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            IReadOnlyDictionary<string, string>? payload = null) =>
            new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Synthetic, $"t-{kind}-{ordinal}", $"synthetic {kind}"),
                payload: payload);

        /// <summary>What the host posts: name plus due time, OccurredAtUtc = firing clock.</summary>
        private static Dictionary<string, string> FiredPayload(string deadlineName, DateTime dueAtUtc) =>
            new Dictionary<string, string>
            {
                [SignalPayloadKeys.Deadline] = deadlineName,
                [SignalPayloadKeys.DeadlineDueAtUtc] = dueAtUtc.ToString("O", CultureInfo.InvariantCulture),
            };

        // ============================================================== helper

        [Fact]
        public void DeadlineDueAtUtc_reads_the_payload_and_falls_back_to_the_signal_time()
        {
            var due = T0.AddMinutes(5);
            var fired = T0.AddMinutes(9);

            var withKey = MakeSignal(1, DecisionSignalKind.DeadlineFired, fired, FiredPayload(DeadlineNames.HelloSafety, due));
            Assert.Equal(due, DecisionEngine.DeadlineDueAtUtc(withKey));
            Assert.Equal(DateTimeKind.Utc, DecisionEngine.DeadlineDueAtUtc(withKey).Kind);

            var withoutKey = MakeSignal(2, DecisionSignalKind.DeadlineFired, fired,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.HelloSafety });
            Assert.Equal(fired, DecisionEngine.DeadlineDueAtUtc(withoutKey));

            var garbage = MakeSignal(3, DecisionSignalKind.DeadlineFired, fired,
                new Dictionary<string, string> { [SignalPayloadKeys.DeadlineDueAtUtc] = "not-a-time" });
            Assert.Equal(fired, DecisionEngine.DeadlineDueAtUtc(garbage));
        }

        // ============================================================== Classic: standby at the Hello prompt

        [Fact]
        public void Late_HelloSafety_fire_stamps_the_resolution_and_the_grace_window_from_the_firing_clock()
        {
            // Session ac5660b8 shape: ESP exits, the user leaves the device at the Hello prompt, it
            // enters Modern Standby, HelloSafety is due during the sleep and fires ~3 min late after
            // wake. The Hello timeout fact, the Finalizing step and the FinalizingGrace due time must
            // all sit at the firing clock — never inside the sleep window.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-standby", "tenant", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.AccountSetupProvisioningComplete, T0.AddMinutes(2))).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DesktopArrived, T0.AddMinutes(3))).NewState;

            var espExit = T0.AddMinutes(6).AddSeconds(50.2144855);
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.EspExiting, espExit)).NewState;
            var helloSafety = Assert.Single(state.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            Assert.Equal(espExit.AddSeconds(300), helloSafety.DueAtUtc);

            // Timer fires after wake, 2m 54s past due.
            var firedAt = helloSafety.DueAtUtc.AddSeconds(174);
            var finalizing = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.DeadlineFired, firedAt,
                FiredPayload(DeadlineNames.HelloSafety, helloSafety.DueAtUtc)));

            Assert.True(finalizing.Transition.Taken);
            Assert.Equal(SessionStage.Finalizing, finalizing.NewState.Stage);
            Assert.Equal("Timeout", finalizing.NewState.HelloOutcome!.Value);
            Assert.Equal(firedAt, finalizing.NewState.HelloResolvedUtc!.Value);

            var grace = Assert.Single(finalizing.NewState.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);
            Assert.Equal(firedAt.AddSeconds(5), grace.DueAtUtc);

            // The chained grace fires on time and completes; nothing carries the old due time.
            var completed = engine.Reduce(finalizing.NewState, MakeSignal(6, DecisionSignalKind.DeadlineFired, grace.DueAtUtc,
                FiredPayload(DeadlineNames.FinalizingGrace, grace.DueAtUtc)));
            Assert.Equal(SessionStage.Completed, completed.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, completed.NewState.Outcome);
            Assert.Contains(completed.Effects, e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                && e.Parameters != null
                && e.Parameters.TryGetValue("eventType", out var et) && et == "enrollment_complete");
        }

        // ============================================================== SelfDeploying guard C

        [Fact]
        public void SelfDeploying_guard_C_matches_the_incarnation_by_payload_due_time_not_by_firing_clock()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sd-late", "t", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;
            var armed = Assert.Single(state.Deadlines, d => d.Name == DeadlineNames.DeviceOnlyEspDetection);
            Assert.Equal(T0.AddMinutes(8), armed.DueAtUtc);

            // A stale fire of an OLDER incarnation (due T+5) arriving late: dead-end, deadline kept.
            var stale = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DeadlineFired, T0.AddMinutes(12),
                FiredPayload(DeadlineNames.DeviceOnlyEspDetection, T0.AddMinutes(5))));
            Assert.False(stale.Transition.Taken);
            Assert.Equal("device_only_esp_detection_stale_due_at_mismatch", stale.Transition.DeadEndReason);
            Assert.Single(stale.NewState.Deadlines, d => d.Name == DeadlineNames.DeviceOnlyEspDetection);

            // The real fire, 4 min late (standby), carries the armed due time: terminal.
            var real = engine.Reduce(stale.NewState, MakeSignal(4, DecisionSignalKind.DeadlineFired, T0.AddMinutes(12),
                FiredPayload(DeadlineNames.DeviceOnlyEspDetection, armed.DueAtUtc)));
            Assert.True(real.Transition.Taken);
            Assert.Equal(SessionStage.Completed, real.NewState.Stage);
        }

        // ============================================================== RealmJoin re-arm guard

        [Fact]
        public void RealmJoin_rearm_guard_compares_the_payload_due_time_not_the_firing_clock()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("rj-late", "t", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.EspExiting, T0.AddMinutes(3))).NewState;
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.HelloResolved, T0.AddMinutes(4),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "Success" })).NewState;
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(5),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100" })).NewState;
            state = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.DesktopArrived, T0.AddMinutes(6))).NewState;
            var first = Assert.Single(state.Deadlines, d => d.Name == DeadlineNames.RealmJoinTimeout);

            // Simulate the activity-based re-arm: the armed incarnation is now due later.
            var rearmedDue = first.DueAtUtc.AddMinutes(25);
            var rearmed = state.ToBuilder()
                .CancelDeadline(DeadlineNames.RealmJoinTimeout)
                .AddDeadline(new ActiveDeadline(
                    name: DeadlineNames.RealmJoinTimeout,
                    dueAtUtc: rearmedDue,
                    firesSignalKind: DecisionSignalKind.DeadlineFired,
                    firesPayload: new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.RealmJoinTimeout }))
                .Build();

            // The OLD incarnation's fire arrives late — after the re-armed due time on the clock.
            // Only the payload due time identifies it as superseded.
            var firedAt = rearmedDue.AddMinutes(1);
            var stale = engine.Reduce(rearmed, MakeSignal(7, DecisionSignalKind.DeadlineFired, firedAt,
                FiredPayload(DeadlineNames.RealmJoinTimeout, first.DueAtUtc)));
            Assert.False(stale.Transition.Taken);
            Assert.Equal("realmjoin_timeout_stale_superseded_by_rearm", stale.Transition.DeadEndReason);
            Assert.Null(stale.NewState.RealmJoinFacts.Outcome);

            // The re-armed incarnation's own fire, same firing clock: timeout is applied.
            var real = engine.Reduce(stale.NewState, MakeSignal(8, DecisionSignalKind.DeadlineFired, firedAt,
                FiredPayload(DeadlineNames.RealmJoinTimeout, rearmedDue)));
            Assert.True(real.Transition.Taken);
            Assert.Equal("Timeout", real.NewState.RealmJoinFacts.Outcome!.Value);
            Assert.Equal(SessionStage.Finalizing, real.NewState.Stage);
        }
    }
}

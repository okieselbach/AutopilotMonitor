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
    /// Tenant-extendable Hello window. The agent stamps the tenant's <c>HelloWaitTimeoutSeconds</c>
    /// on <see cref="DecisionSignalKind.EnrollmentFactsObserved"/> when it exceeds the built-in
    /// 300 s; every <see cref="DeadlineNames.HelloSafety"/> arm site then uses it, clamped to
    /// 3600 s. Without the key the window stays exactly the built-in 300 s.
    /// </summary>
    public sealed class HelloSafetyTenantWindowTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime EspExit = T0.AddMinutes(6);

        [Theory]
        [InlineData(null, 300)]
        [InlineData("1800", 1800)]
        [InlineData("301", 301)]
        [InlineData("3600", 3600)]
        [InlineData("86400", 3600)] // beyond the ceiling — clamped
        [InlineData("300", 300)]    // not an extension — ignored (the agent never stamps it)
        [InlineData("120", 300)]
        public void Esp_exit_arms_hello_safety_with_the_tenant_window(string? helloWait, int expectedSeconds)
        {
            var engine = new DecisionEngine();
            var state = ReduceAll(engine, ClassicUntilEspExit(helloWait));

            Assert.Equal(SessionStage.AwaitingHello, state.Stage);
            var helloSafety = Assert.Single(state.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            Assert.Equal(EspExit.AddSeconds(expectedSeconds), helloSafety.DueAtUtc);
        }

        [Fact]
        public void Tenant_window_is_recorded_set_once_with_its_source_ordinal()
        {
            var engine = new DecisionEngine();
            var state = ReduceAll(engine, ClassicUntilEspExit("1800"));

            var fact = state.ScenarioObservations.HelloWaitTimeoutSeconds;
            Assert.NotNull(fact);
            Assert.Equal(1800, fact!.Value);
            Assert.Equal(1, fact.SourceSignalOrdinal);

            // A later re-post (every agent start posts the facts again) never re-bases the window.
            var repost = engine.Reduce(state, Facts(20, EspExit.AddSeconds(30), "3600"));
            Assert.Equal(1800, repost.NewState.ScenarioObservations.HelloWaitTimeoutSeconds!.Value);
            Assert.Equal(1, repost.NewState.ScenarioObservations.HelloWaitTimeoutSeconds!.SourceSignalOrdinal);
            Assert.Empty(repost.Effects);
            Assert.Equal(
                EspExit.AddSeconds(1800),
                Assert.Single(repost.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety).DueAtUtc);
        }

        [Fact]
        public void Census_surfaces_the_tenant_window_only_when_recorded()
        {
            var engine = new DecisionEngine();

            var extended = DecisionStateSignalCensus.Build(ReduceAll(engine, ClassicUntilEspExit("2700")));
            Assert.Contains("hello_wait_extended", extended.SignalsSeen);
            var evidence = Assert.IsAssignableFrom<IDictionary<string, object>>(extended.SignalEvidence["helloWaitExtended"]);
            Assert.Equal(2700, evidence["seconds"]);

            var builtIn = DecisionStateSignalCensus.Build(ReduceAll(engine, ClassicUntilEspExit(helloWait: null)));
            Assert.DoesNotContain("hello_wait_extended", builtIn.SignalsSeen);
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("")]
        [InlineData("0")]
        [InlineData("-5")]
        public void Malformed_tenant_window_is_ignored(string helloWait)
        {
            var engine = new DecisionEngine();
            var state = ReduceAll(engine, ClassicUntilEspExit(helloWait));

            Assert.Null(state.ScenarioObservations.HelloWaitTimeoutSeconds);
            Assert.Equal(
                EspExit.AddSeconds(300),
                Assert.Single(state.Deadlines, d => d.Name == DeadlineNames.HelloSafety).DueAtUtc);
        }

        [Fact]
        public void Late_tenant_window_extends_the_armed_hello_safety_from_its_original_anchor()
        {
            // A backfilled ESP exit armed the built-in window before the facts signal landed.
            var engine = new DecisionEngine();
            var state = ReduceAll(engine, ClassicUntilEspExit(helloWait: null));
            Assert.Equal(
                EspExit.AddSeconds(300),
                Assert.Single(state.Deadlines, d => d.Name == DeadlineNames.HelloSafety).DueAtUtc);

            var step = engine.Reduce(state, Facts(20, EspExit.AddSeconds(10), "1800"));

            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            var extended = Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            Assert.Equal(EspExit.AddSeconds(1800), extended.DueAtUtc);
            Assert.Equal(DecisionSignalKind.DeadlineFired, extended.FiresSignalKind);
            Assert.Equal(DeadlineNames.HelloSafety, extended.FiresPayload![SignalPayloadKeys.Deadline]);

            // The live scheduler is told to replace its timer (same name → replaced).
            var effect = Assert.Single(step.Effects);
            Assert.Equal(DecisionEffectKind.ScheduleDeadline, effect.Kind);
            Assert.Equal(EspExit.AddSeconds(1800), effect.Deadline!.DueAtUtc);
        }

        [Theory]
        [InlineData("300")]
        [InlineData("120")]
        public void Late_tenant_window_not_longer_than_the_built_in_leaves_the_deadline_alone(string helloWait)
        {
            var engine = new DecisionEngine();
            var state = ReduceAll(engine, ClassicUntilEspExit(helloWait: null));

            var step = engine.Reduce(state, Facts(20, EspExit.AddSeconds(10), helloWait));

            Assert.Empty(step.Effects);
            Assert.Null(step.NewState.ScenarioObservations.HelloWaitTimeoutSeconds);
            Assert.Equal(
                EspExit.AddSeconds(300),
                Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety).DueAtUtc);
        }

        [Fact]
        public void Fire_of_the_replaced_built_in_deadline_is_dead_ended_after_the_late_extension()
        {
            // The old timer fired while the facts signal was still queued ahead of it: the facts
            // step moves hello_safety out, then the queued fire of the replaced incarnation
            // arrives. It must not time Hello out at the built-in window.
            var engine = new DecisionEngine();
            var state = ReduceAll(engine, ClassicUntilEspExit(helloWait: null));
            var builtInDue = EspExit.AddSeconds(300);
            state = engine.Reduce(state, Facts(20, EspExit.AddSeconds(10), "1800")).NewState;

            var stale = engine.Reduce(state, MakeSignal(21, DecisionSignalKind.DeadlineFired, builtInDue.AddSeconds(2),
                Fired(DeadlineNames.HelloSafety, builtInDue)));

            Assert.False(stale.Transition.Taken);
            Assert.Equal("hello_safety_stale_superseded_by_rearm", stale.Transition.DeadEndReason);
            Assert.Empty(stale.Effects);
            Assert.Null(stale.NewState.HelloResolvedUtc);
            Assert.Equal(SessionStage.AwaitingHello, stale.NewState.Stage);
            var extendedDue = EspExit.AddSeconds(1800);
            Assert.Equal(
                extendedDue,
                Assert.Single(stale.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety).DueAtUtc);

            // The fire of the current incarnation still times Hello out (desktop is in → Finalizing).
            var current = engine.Reduce(stale.NewState, MakeSignal(22, DecisionSignalKind.DeadlineFired, extendedDue,
                Fired(DeadlineNames.HelloSafety, extendedDue)));
            Assert.Equal(SessionStage.Finalizing, current.NewState.Stage);
            Assert.Equal("Timeout", current.NewState.HelloOutcome!.Value);
        }

        [Fact]
        public void Late_tenant_window_after_the_built_in_expired_leaves_the_pending_fire_to_decide()
        {
            var engine = new DecisionEngine();
            var state = ReduceAll(engine, ClassicUntilEspExit(helloWait: null));

            var step = engine.Reduce(state, Facts(20, EspExit.AddSeconds(301), "1800"));

            Assert.Empty(step.Effects);
            Assert.Equal(
                EspExit.AddSeconds(300),
                Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety).DueAtUtc);
            // The fact is still recorded — later arm sites use it.
            Assert.Equal(1800, step.NewState.ScenarioObservations.HelloWaitTimeoutSeconds!.Value);
        }

        [Fact]
        public void Late_extension_is_replay_deterministic()
        {
            var engine = new DecisionEngine();
            var signals = ClassicUntilEspExit(helloWait: null)
                .Concat(new[] { Facts(20, EspExit.AddSeconds(10), "2400") })
                .ToList();

            var stepwise = ReduceAll(engine, signals);
            var replayed = ReducerReplay.Replay(engine, Seed(), signals);

            Assert.Equal(
                Assert.Single(stepwise.Deadlines, d => d.Name == DeadlineNames.HelloSafety).DueAtUtc,
                Assert.Single(replayed.Deadlines, d => d.Name == DeadlineNames.HelloSafety).DueAtUtc);
            Assert.Equal(EspExit.AddSeconds(2400), replayed.Deadlines.Single(d => d.Name == DeadlineNames.HelloSafety).DueAtUtc);
        }

        [Fact]
        public void Tenant_window_also_applies_to_the_AccountSetup_complete_deferred_promote()
        {
            // Strong gate arrives after the final exit and before the desktop: the deferred
            // promote arms hello_safety at the gate signal — with the tenant window.
            var engine = new DecisionEngine();
            var state = ReduceAll(engine, new[]
            {
                MakeSignal(0, DecisionSignalKind.SessionStarted, T0),
                Facts(1, T0.AddSeconds(5), "2400"),
                MakeSignal(2, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1), Phase("DeviceSetup")),
                MakeSignal(3, DecisionSignalKind.HelloPolicyDetected, T0.AddMinutes(1).AddSeconds(5),
                    new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = "true" }),
                MakeSignal(4, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(12), Phase("AccountSetup")),
                MakeSignal(5, DecisionSignalKind.EspExiting, T0.AddMinutes(24)),
            });
            Assert.Equal(SessionStage.EspAccountSetup, state.Stage);

            var gate = T0.AddMinutes(25);
            var step = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.AccountSetupProvisioningComplete, gate));

            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            Assert.Equal(
                gate.AddSeconds(2400),
                Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety).DueAtUtc);
        }

        [Theory]
        [InlineData(null, 30 * 60)]
        [InlineData("1200", 30 * 60)] // a shorter tenant window never shortens the WDP backstop
        [InlineData("3600", 3600)]
        public void Device_preparation_backstop_is_never_shorter_than_the_tenant_window(string? helloWait, int expectedSeconds)
        {
            // WDP has no ESP, so hello_safety never arms; the desktop-armed backstop is the last
            // net behind the tracker's Hello completion timer, which runs on the tenant budget.
            var engine = new DecisionEngine();
            var facts = new Dictionary<string, string>
            {
                [SignalPayloadKeys.EnrollmentType] = "v2",
                [SignalPayloadKeys.EnrollmentTypeDeterministic] = "true",
            };
            if (helloWait != null)
                facts[SignalPayloadKeys.HelloWaitTimeoutSeconds] = helloWait;
            var desktop = T0.AddMinutes(9);

            var state = ReduceAll(engine, new[]
            {
                MakeSignal(0, DecisionSignalKind.SessionStarted, T0),
                MakeSignal(1, DecisionSignalKind.EnrollmentFactsObserved, T0, facts),
                MakeSignal(2, DecisionSignalKind.DesktopArrived, desktop),
            });

            Assert.Equal(
                desktop.AddSeconds(expectedSeconds),
                Assert.Single(state.Deadlines, d => d.Name == DeadlineNames.DevicePrepCompletion).DueAtUtc);
        }

        [Fact]
        public void IsTenantHelloWaitPending_is_true_only_while_an_extended_window_is_armed_and_not_yet_due()
        {
            var engine = new DecisionEngine();
            var awaiting = ReduceAll(engine, ClassicUntilEspExit("1800"));
            var due = EspExit.AddSeconds(1800);

            Assert.True(DecisionEngine.IsTenantHelloWaitPending(awaiting, EspExit.AddMinutes(1)));
            Assert.True(DecisionEngine.IsTenantHelloWaitPending(awaiting, due.AddSeconds(-1)));
            Assert.False(DecisionEngine.IsTenantHelloWaitPending(awaiting, due));
            Assert.False(DecisionEngine.IsTenantHelloWaitPending(awaiting, due.AddSeconds(1)));
            Assert.False(DecisionEngine.IsTenantHelloWaitPending(null, EspExit));
            Assert.False(DecisionEngine.IsTenantHelloWaitPending(Seed(), EspExit));

            // The built-in window never counts: default tenants keep their stall reporting.
            var builtIn = ReduceAll(engine, ClassicUntilEspExit(helloWait: null));
            Assert.Contains(builtIn.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            Assert.False(DecisionEngine.IsTenantHelloWaitPending(builtIn, EspExit.AddMinutes(1)));

            // Hello resolved → the deadline is cancelled → nothing pending any more.
            var resolved = engine.Reduce(awaiting, MakeSignal(20, DecisionSignalKind.HelloResolved, EspExit.AddMinutes(10),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "completed" })).NewState;
            Assert.False(DecisionEngine.IsTenantHelloWaitPending(resolved, EspExit.AddMinutes(11)));
        }

        [Fact]
        public void IsTenantHelloWaitPending_covers_the_device_preparation_backstop()
        {
            var engine = new DecisionEngine();
            var desktop = T0.AddMinutes(9);
            var state = ReduceAll(engine, new[]
            {
                MakeSignal(0, DecisionSignalKind.SessionStarted, T0),
                MakeSignal(1, DecisionSignalKind.EnrollmentFactsObserved, T0, new Dictionary<string, string>
                {
                    [SignalPayloadKeys.EnrollmentType] = "v2",
                    [SignalPayloadKeys.EnrollmentTypeDeterministic] = "true",
                    [SignalPayloadKeys.HelloWaitTimeoutSeconds] = "3600",
                }),
                MakeSignal(2, DecisionSignalKind.DesktopArrived, desktop),
            });

            Assert.True(DecisionEngine.IsTenantHelloWaitPending(state, desktop.AddMinutes(59)));
            Assert.False(DecisionEngine.IsTenantHelloWaitPending(state, desktop.AddMinutes(60)));
        }

        // ============================================================ helpers

        private static DecisionState Seed() => DecisionState.CreateInitial("sess-hello-window", "tenant", T0);

        /// <summary>
        /// Classic user-driven flow up to the final ESP exit: strong gate and desktop in place,
        /// Hello policy unknown — so the exit promotes to AwaitingHello and arms hello_safety.
        /// </summary>
        private static List<DecisionSignal> ClassicUntilEspExit(string? helloWait)
        {
            var signals = new List<DecisionSignal> { MakeSignal(0, DecisionSignalKind.SessionStarted, T0) };
            if (helloWait != null)
                signals.Add(Facts(1, T0.AddSeconds(5), helloWait));
            signals.Add(MakeSignal(2, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1), Phase("AccountSetup")));
            signals.Add(MakeSignal(3, DecisionSignalKind.AccountSetupProvisioningComplete, T0.AddMinutes(2)));
            signals.Add(MakeSignal(4, DecisionSignalKind.DesktopArrived, T0.AddMinutes(3)));
            signals.Add(MakeSignal(5, DecisionSignalKind.EspExiting, EspExit));
            return signals;
        }

        private static DecisionState ReduceAll(DecisionEngine engine, IEnumerable<DecisionSignal> signals)
        {
            var state = Seed();
            foreach (var signal in signals)
                state = engine.Reduce(state, signal).NewState;
            return state;
        }

        private static DecisionSignal Facts(long ordinal, DateTime occurredAtUtc, string helloWait) =>
            MakeSignal(ordinal, DecisionSignalKind.EnrollmentFactsObserved, occurredAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.HelloWaitTimeoutSeconds] = helloWait });

        private static Dictionary<string, string> Fired(string deadlineName, DateTime dueAtUtc) =>
            new Dictionary<string, string>
            {
                [SignalPayloadKeys.Deadline] = deadlineName,
                [SignalPayloadKeys.DeadlineDueAtUtc] = dueAtUtc.ToString("O", CultureInfo.InvariantCulture),
            };

        private static Dictionary<string, string> Phase(string phase) =>
            new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = phase };

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
    }
}

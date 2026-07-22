using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Liveness plan PR1 — "no silent parking" tripwire. A non-terminal session that entered
    /// the post-AccountSetup dead-end zone (ESP final exit at-or-after AccountSetup entry, or
    /// an advisory-defanged failure) without a resolution-capable deadline armed must announce
    /// itself via a one-shot <c>session_parked_without_deadline</c> Warning. The parked states
    /// here are built artificially via the builder — production arming sites now prevent them,
    /// which is exactly why the tripwire exists (it catches the unknown variant 4).
    /// <para>
    /// M1 (delta review 2026-07-02): the tripwire is dwell-based — a parked step only ARMS a
    /// timer; the Warning fires (and consumes the one-shot) only if the session is still parked
    /// when the dwell elapses. A healthy transient park (HelloResolved → AwaitingDesktop,
    /// desktop arrives seconds later) must neither fire nor burn the one-shot.
    /// </para>
    /// </summary>
    [Collection("SerialThreading")] // timer-driven assertions — serialise against other timing-sensitive suites
    public sealed class DecisionStepProcessorParkedTripwireTests : IDisposable
    {
        private static DateTime At => new DateTime(2026, 6, 12, 10, 0, 0, DateTimeKind.Utc);

        /// <summary>Short dwell so fire-path tests complete quickly; suppression tests wait ≥10× this.</summary>
        private static readonly TimeSpan TestDwell = TimeSpan.FromMilliseconds(25);
        private static readonly TimeSpan FireWait = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan SuppressWait = TimeSpan.FromMilliseconds(400);

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly AgentLogger _logger;
        private readonly FakeJournalWriter _journal = new FakeJournalWriter();
        private readonly FakeEffectRunner _effects = new FakeEffectRunner();
        private readonly FakeSnapshotPersistence _snapshot = new FakeSnapshotPersistence();
        private readonly FakeQuarantineSink _quarantine = new FakeQuarantineSink();
        private readonly FakeSignalIngressSink _sink = new FakeSignalIngressSink();
        private readonly DecisionEngine _engine = new DecisionEngine();
        private DecisionStepProcessor? _sut;

        public DecisionStepProcessorParkedTripwireTests()
        {
            _logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
        }

        public void Dispose()
        {
            _sut?.Dispose();
            _tmp.Dispose();
        }

        private DecisionStepProcessor Build(DecisionState initialState, TimeSpan? dwell = null) =>
            _sut = new DecisionStepProcessor(
                initialState: initialState,
                journal: _journal,
                effectRunner: _effects,
                snapshot: _snapshot,
                quarantineSink: _quarantine,
                logger: _logger,
                informationalEvents: new InformationalEventPost(_sink, new FixedClock(At), _logger),
                parkedTripwireDwell: dwell ?? TestDwell);

        /// <summary>
        /// Baseline parked shape: AccountSetup entered, ESP exited finally AFTER that entry,
        /// no deadline armed. Variants override individual facts.
        /// </summary>
        private static DecisionState BuildParkedState(
            SessionStage stage = SessionStage.EspAccountSetup,
            bool accountSetupEntered = true,
            bool espFinalExitAfterAccountSetup = true,
            bool advisoryRecorded = false,
            params ActiveDeadline[] deadlines)
        {
            var builder = DecisionState.CreateInitial("S1", "T1", At).ToBuilder().WithStage(stage);
            if (accountSetupEntered)
                builder.AccountSetupEnteredUtc = new SignalFact<DateTime>(At.AddMinutes(10), 3);
            if (espFinalExitAfterAccountSetup)
                builder.EspFinalExitUtc = new SignalFact<DateTime>(At.AddMinutes(25), 7);
            if (advisoryRecorded)
                builder.WithEspAdvisoryFailureRecorded(At.AddMinutes(20), 6);
            foreach (var deadline in deadlines)
                builder.AddDeadline(deadline);
            return builder.Build();
        }

        private static ActiveDeadline Deadline(string name) =>
            new ActiveDeadline(name, At.AddMinutes(60), DecisionSignalKind.DeadlineFired);

        /// <summary>Reduces an InformationalEvent pass-through against the processor's current state.</summary>
        private (DecisionStep step, DecisionSignal signal) ReduceInformational(DecisionStepProcessor sut, long ordinal)
        {
            var payload = new Dictionary<string, string>
            {
                [SignalPayloadKeys.EventType] = "download_progress",
                [SignalPayloadKeys.Source] = "DeliveryOptimizationCollector",
            };
            var signal = TestSignals.Raw(ordinal, DecisionSignalKind.InformationalEvent, At, payload: payload);
            var step = _engine.Reduce(sut.CurrentState, signal);
            return (step, signal);
        }

        private FakeSignalIngressSink.PostedSignal? FindTripwirePost()
        {
            foreach (var posted in _sink.Posted)
            {
                if (posted.Payload != null
                    && posted.Payload.TryGetValue(SignalPayloadKeys.EventType, out var type)
                    && type == Constants.EventTypes.SessionParkedWithoutDeadline)
                {
                    return posted;
                }
            }
            return null;
        }

        /// <summary>The tripwire post, asserted present — for the assertions that only run
        /// after <c>WaitForTripwire()</c> has already confirmed one was emitted.</summary>
        private FakeSignalIngressSink.PostedSignal RequireTripwirePost()
        {
            var post = FindTripwirePost();
            Assert.NotNull(post);
            return post!;
        }

        private int CountTripwirePosts()
        {
            var count = 0;
            foreach (var posted in _sink.Posted)
            {
                if (posted.Payload != null
                    && posted.Payload.TryGetValue(SignalPayloadKeys.EventType, out var type)
                    && type == Constants.EventTypes.SessionParkedWithoutDeadline)
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>Blocks until at least one tripwire post landed (dwell elapsed) or times out.</summary>
        private bool WaitForTripwire() =>
            SpinWait.SpinUntil(() => CountTripwirePosts() >= 1, FireWait);

        /// <summary>Proves absence: waits well past the dwell, then asserts no post arrived.</summary>
        private void AssertNoTripwireAfterDwell()
        {
            Thread.Sleep(SuppressWait);
            Assert.Equal(0, CountTripwirePosts());
        }

        // ===================================================================== Fires

        [Fact]
        public void Parked_state_fires_tripwire_exactly_once_after_dwell()
        {
            var sut = Build(BuildParkedState());

            var (s1, sig1) = ReduceInformational(sut, 10);
            sut.ApplyStep(s1, sig1);

            Assert.True(WaitForTripwire(), "tripwire did not fire within the wait budget");

            // Further parked pass-throughs after the fire must not emit again (one-shot).
            var (s2, sig2) = ReduceInformational(sut, 11);
            sut.ApplyStep(s2, sig2);
            Thread.Sleep(SuppressWait);
            Assert.Equal(1, CountTripwirePosts());

            var post = RequireTripwirePost();
            Assert.Equal(DecisionSignalKind.InformationalEvent, post.Kind);
            Assert.Equal(nameof(SessionStage.EspAccountSetup), post.Payload!["stage"]);
            Assert.Equal("Warning", post.Payload[SignalPayloadKeys.Severity]);
            Assert.Equal("true", post.Payload[SignalPayloadKeys.ImmediateUpload]);
            Assert.Contains("esp_final_exit", post.Payload["signalsSeen"]);
            Assert.Contains("account_setup_entered", post.Payload["signalsSeen"]);
            Assert.Equal(string.Empty, post.Payload["armedDeadlines"]);
            Assert.True(post.Payload.ContainsKey("dwellSeconds"));
        }

        [Fact]
        public void Parked_passthrough_steps_do_not_rebase_the_dwell()
        {
            // A truly parked session still produces telemetry pass-throughs; if each of them
            // re-based the dwell the tripwire would never fire exactly when it matters most.
            var sut = Build(BuildParkedState(), dwell: TimeSpan.FromMilliseconds(150));

            var (s1, sig1) = ReduceInformational(sut, 10);
            sut.ApplyStep(s1, sig1);

            // Keep feeding parked pass-throughs faster than the dwell.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            long ordinal = 11;
            while (CountTripwirePosts() == 0 && DateTime.UtcNow < deadline)
            {
                var (s, sig) = ReduceInformational(sut, ordinal++);
                sut.ApplyStep(s, sig);
                Thread.Sleep(20);
            }

            Assert.Equal(1, CountTripwirePosts());
        }

        [Fact]
        public void Advisory_anchor_alone_fires_without_esp_final_exit()
        {
            var sut = Build(BuildParkedState(espFinalExitAfterAccountSetup: false, advisoryRecorded: true));

            var (step, signal) = ReduceInformational(sut, 10);
            sut.ApplyStep(step, signal);

            Assert.True(WaitForTripwire());
            Assert.Equal(1, CountTripwirePosts());
        }

        [Fact]
        public void Only_ClassifierTick_armed_still_fires()
        {
            // ClassifierTick re-arms itself and never resolves a session — it must not mask
            // the invariant.
            var sut = Build(BuildParkedState(deadlines: Deadline(DeadlineNames.ClassifierTick)));

            var (step, signal) = ReduceInformational(sut, 10);
            sut.ApplyStep(step, signal);

            Assert.True(WaitForTripwire());
            Assert.Equal(DeadlineNames.ClassifierTick, RequireTripwirePost().Payload!["armedDeadlines"]);
        }

        [Fact]
        public void AwaitingDesktop_parked_fires_with_stage_in_data()
        {
            // Deliberate design decision (plan PR1): AwaitingDesktop has no deadline today, so a
            // parked AwaitingDesktop session IS visible via the tripwire — but only after the
            // dwell, so the healthy Hello→Desktop window stays silent (see the dwell tests).
            var sut = Build(BuildParkedState(stage: SessionStage.AwaitingDesktop));

            var (step, signal) = ReduceInformational(sut, 10);
            sut.ApplyStep(step, signal);

            Assert.True(WaitForTripwire());
            Assert.Equal(nameof(SessionStage.AwaitingDesktop), RequireTripwirePost().Payload!["stage"]);
        }

        // ===================================================================== Dwell semantics

        [Fact]
        public void Transient_park_resolved_before_dwell_neither_fires_nor_burns_the_one_shot()
        {
            // The healthy Classic window: HelloResolved cancels HelloSafety → AwaitingDesktop
            // with no deadline; DesktopArrived (here: a resolution deadline appearing) follows
            // before the dwell elapses.
            var sut = Build(BuildParkedState(stage: SessionStage.AwaitingDesktop), dwell: TimeSpan.FromMilliseconds(250));

            var (s1, sig1) = ReduceInformational(sut, 10);
            sut.ApplyStep(s1, sig1);

            // Park resolves quickly: next step carries a resolution-capable deadline.
            var resolvedState = BuildParkedState(
                stage: SessionStage.AwaitingDesktop,
                deadlines: Deadline(DeadlineNames.AdvisoryCompletion));
            var (s2Template, sig2) = ReduceInformational(sut, 11);
            sut.ApplyStep(new DecisionStep(resolvedState, s2Template.Transition, Array.Empty<DecisionEffect>()), sig2);

            AssertNoTripwireAfterDwell(); // no false alarm

            // A REAL park later in the same run must still be able to fire — the one-shot was
            // not consumed by the transient window.
            var parkedAgain = BuildParkedState(stage: SessionStage.AwaitingDesktop);
            var (s3Template, sig3) = ReduceInformational(sut, 12);
            sut.ApplyStep(new DecisionStep(parkedAgain, s3Template.Transition, Array.Empty<DecisionEffect>()), sig3);

            Assert.True(WaitForTripwire(), "genuine park after a transient window must still fire");
            Assert.Equal(1, CountTripwirePosts());
        }

        [Fact]
        public void Dispose_cancels_a_pending_dwell()
        {
            var sut = Build(BuildParkedState());

            var (step, signal) = ReduceInformational(sut, 10);
            sut.ApplyStep(step, signal);
            sut.Dispose();

            AssertNoTripwireAfterDwell();
        }

        // ===================================================================== Suppressed

        [Fact]
        public void Resolution_capable_deadline_suppresses_tripwire()
        {
            var sut = Build(BuildParkedState(deadlines: Deadline(DeadlineNames.AdvisoryCompletion)));

            var (step, signal) = ReduceInformational(sut, 10);
            sut.ApplyStep(step, signal);

            AssertNoTripwireAfterDwell();
        }

        [Fact]
        public void Resolution_deadline_beside_ClassifierTick_suppresses_tripwire()
        {
            var sut = Build(BuildParkedState(
                deadlines: new[] { Deadline(DeadlineNames.ClassifierTick), Deadline(DeadlineNames.AdvisoryCompletion) }));

            var (step, signal) = ReduceInformational(sut, 10);
            sut.ApplyStep(step, signal);

            AssertNoTripwireAfterDwell();
        }

        [Fact]
        public void Terminal_stage_never_fires()
        {
            var sut = Build(BuildParkedState(stage: SessionStage.Completed));

            var (step, signal) = ReduceInformational(sut, 10);
            sut.ApplyStep(step, signal);

            AssertNoTripwireAfterDwell();
        }

        [Fact]
        public void WhiteGloveSealed_never_fires()
        {
            var sut = Build(BuildParkedState(stage: SessionStage.WhiteGloveSealed));

            var (step, signal) = ReduceInformational(sut, 10);
            sut.ApplyStep(step, signal);

            AssertNoTripwireAfterDwell();
        }

        [Fact]
        public void Pre_AccountSetup_never_fires()
        {
            // DeviceSetup hangs are the stall-probe path's responsibility, not this tripwire's.
            var sut = Build(BuildParkedState(stage: SessionStage.EspDeviceSetup, accountSetupEntered: false));

            var (step, signal) = ReduceInformational(sut, 10);
            sut.ApplyStep(step, signal);

            AssertNoTripwireAfterDwell();
        }

        [Fact]
        public void AccountSetup_active_before_final_exit_does_not_fire()
        {
            // Between AccountSetup entry and the final esp_exiting no deadline is armed by
            // design — ESP/IME registry signals are actively flowing. The dead-end zone is
            // only entered once the ESP side is gone (final exit) or has failed (advisory).
            var sut = Build(BuildParkedState(espFinalExitAfterAccountSetup: false));

            var (step, signal) = ReduceInformational(sut, 10);
            sut.ApplyStep(step, signal);

            AssertNoTripwireAfterDwell();
        }

        [Fact]
        public void Esp_final_exit_before_AccountSetup_entry_does_not_fire()
        {
            // A pre-AccountSetup handoff exit (DeviceSetup→AccountSetup transition reboot)
            // is not a dead-end anchor: the AccountSetup page is still ahead of the user.
            var state = DecisionState.CreateInitial("S1", "T1", At).ToBuilder()
                .WithStage(SessionStage.EspAccountSetup);
            state.AccountSetupEnteredUtc = new SignalFact<DateTime>(At.AddMinutes(10), 3);
            state.EspFinalExitUtc = new SignalFact<DateTime>(At.AddMinutes(5), 2);
            var sut = Build(state.Build());

            var (step, signal) = ReduceInformational(sut, 10);
            sut.ApplyStep(step, signal);

            AssertNoTripwireAfterDwell();
        }

        [Fact]
        public void Reboot_after_guard_blocked_exit_rebases_window_and_never_parks()
        {
            // Session 7443317c (2026-07-10): the pre-rebase reboot CANCEL left the dead-end
            // zone without a resolution-capable deadline and tripped this tripwire on a live
            // enrollment. Regression: drive the REAL engine through the field shape (premature
            // AccountSetup entry → guard-blocked exit arms the window → reboot) and prove the
            // resulting state carries the rebased AdvisoryCompletion deadline — not parked.
            var state = DecisionState.CreateInitial("S1", "T1", At);
            foreach (var signal in new[]
            {
                TestSignals.Raw(1, DecisionSignalKind.SessionStarted, At),
                TestSignals.Raw(2, DecisionSignalKind.EspConfigDetected, At.AddMinutes(1),
                    payload: new Dictionary<string, string>
                    {
                        [SignalPayloadKeys.SkipUserEsp] = "false",
                        [SignalPayloadKeys.SkipDeviceEsp] = "false",
                    }),
                TestSignals.Raw(3, DecisionSignalKind.EspPhaseChanged, At.AddMinutes(2),
                    payload: new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" }),
                TestSignals.Raw(4, DecisionSignalKind.EspPhaseChanged, At.AddMinutes(5),
                    payload: new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" }),
                TestSignals.Raw(5, DecisionSignalKind.EspExiting, At.AddMinutes(6)),
                TestSignals.Raw(6, DecisionSignalKind.SystemRebootObserved, At.AddMinutes(20)),
            })
            {
                state = _engine.Reduce(state, signal).NewState;
            }
            Assert.Contains(state.Deadlines, d => d.Name == DeadlineNames.AdvisoryCompletion);

            var sut = Build(state);
            var (step, signal2) = ReduceInformational(sut, 10);
            sut.ApplyStep(step, signal2);

            AssertNoTripwireAfterDwell();
        }

        [Fact]
        public void Without_informational_post_wired_parked_state_is_tolerated()
        {
            // Legacy ctor shape (no InformationalEventPost) — the tripwire is simply disabled.
            var sut = _sut = new DecisionStepProcessor(
                initialState: BuildParkedState(),
                journal: _journal,
                effectRunner: _effects,
                snapshot: _snapshot,
                quarantineSink: _quarantine,
                logger: _logger,
                parkedTripwireDwell: TestDwell);

            var (step, signal) = ReduceInformational(sut, 10);
            sut.ApplyStep(step, signal);

            Thread.Sleep(SuppressWait);
            Assert.Empty(_sink.Posted);
        }

        private sealed class FixedClock : IClock
        {
            private readonly DateTime _utcNow;
            public FixedClock(DateTime utcNow) => _utcNow = utcNow;
            public DateTime UtcNow => _utcNow;
            public System.Threading.Tasks.Task Delay(TimeSpan delay, System.Threading.CancellationToken cancellationToken)
                => System.Threading.Tasks.Task.CompletedTask;
        }
    }
}

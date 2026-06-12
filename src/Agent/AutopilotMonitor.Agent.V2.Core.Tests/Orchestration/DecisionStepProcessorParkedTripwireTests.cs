using System;
using System.Collections.Generic;
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
    /// </summary>
    public sealed class DecisionStepProcessorParkedTripwireTests : IDisposable
    {
        private static DateTime At => new DateTime(2026, 6, 12, 10, 0, 0, DateTimeKind.Utc);

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly AgentLogger _logger;
        private readonly FakeJournalWriter _journal = new FakeJournalWriter();
        private readonly FakeEffectRunner _effects = new FakeEffectRunner();
        private readonly FakeSnapshotPersistence _snapshot = new FakeSnapshotPersistence();
        private readonly FakeQuarantineSink _quarantine = new FakeQuarantineSink();
        private readonly FakeSignalIngressSink _sink = new FakeSignalIngressSink();
        private readonly DecisionEngine _engine = new DecisionEngine();

        public DecisionStepProcessorParkedTripwireTests()
        {
            _logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
        }

        public void Dispose() => _tmp.Dispose();

        private DecisionStepProcessor Build(DecisionState initialState) =>
            new DecisionStepProcessor(
                initialState: initialState,
                journal: _journal,
                effectRunner: _effects,
                snapshot: _snapshot,
                quarantineSink: _quarantine,
                logger: _logger,
                informationalEvents: new InformationalEventPost(_sink, new FixedClock(At), _logger));

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

        // ===================================================================== Fires

        [Fact]
        public void Parked_state_fires_tripwire_exactly_once()
        {
            var sut = Build(BuildParkedState());

            var (s1, sig1) = ReduceInformational(sut, 10);
            sut.ApplyStep(s1, sig1);
            var (s2, sig2) = ReduceInformational(sut, 11);
            sut.ApplyStep(s2, sig2);

            Assert.Equal(1, CountTripwirePosts());

            var post = FindTripwirePost()!;
            Assert.Equal(DecisionSignalKind.InformationalEvent, post.Kind);
            Assert.Equal(nameof(SessionStage.EspAccountSetup), post.Payload!["stage"]);
            Assert.Equal("Warning", post.Payload[SignalPayloadKeys.Severity]);
            Assert.Equal("true", post.Payload[SignalPayloadKeys.ImmediateUpload]);
            Assert.Contains("esp_final_exit", post.Payload["signalsSeen"]);
            Assert.Contains("account_setup_entered", post.Payload["signalsSeen"]);
            Assert.Equal(string.Empty, post.Payload["armedDeadlines"]);
        }

        [Fact]
        public void Advisory_anchor_alone_fires_without_esp_final_exit()
        {
            var sut = Build(BuildParkedState(espFinalExitAfterAccountSetup: false, advisoryRecorded: true));

            var (step, signal) = ReduceInformational(sut, 10);
            sut.ApplyStep(step, signal);

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

            Assert.Equal(1, CountTripwirePosts());
            Assert.Equal(DeadlineNames.ClassifierTick, FindTripwirePost()!.Payload!["armedDeadlines"]);
        }

        [Fact]
        public void AwaitingDesktop_parked_fires_with_stage_in_data()
        {
            // Deliberate design decision (plan PR1): AwaitingDesktop has no deadline today, so a
            // parked AwaitingDesktop session IS visible via the tripwire. The stage field keeps
            // it distinguishable in the field; a dedicated DesktopSafety deadline is a possible
            // later iteration — NOT part of PR1.
            var sut = Build(BuildParkedState(stage: SessionStage.AwaitingDesktop));

            var (step, signal) = ReduceInformational(sut, 10);
            sut.ApplyStep(step, signal);

            Assert.Equal(1, CountTripwirePosts());
            Assert.Equal(nameof(SessionStage.AwaitingDesktop), FindTripwirePost()!.Payload!["stage"]);
        }

        // ===================================================================== Suppressed

        [Fact]
        public void Resolution_capable_deadline_suppresses_tripwire()
        {
            var sut = Build(BuildParkedState(deadlines: Deadline(DeadlineNames.AdvisoryCompletion)));

            var (step, signal) = ReduceInformational(sut, 10);
            sut.ApplyStep(step, signal);

            Assert.Equal(0, CountTripwirePosts());
        }

        [Fact]
        public void Resolution_deadline_beside_ClassifierTick_suppresses_tripwire()
        {
            var sut = Build(BuildParkedState(
                deadlines: new[] { Deadline(DeadlineNames.ClassifierTick), Deadline(DeadlineNames.AdvisoryCompletion) }));

            var (step, signal) = ReduceInformational(sut, 10);
            sut.ApplyStep(step, signal);

            Assert.Equal(0, CountTripwirePosts());
        }

        [Fact]
        public void Terminal_stage_never_fires()
        {
            var sut = Build(BuildParkedState(stage: SessionStage.Completed));

            var (step, signal) = ReduceInformational(sut, 10);
            sut.ApplyStep(step, signal);

            Assert.Equal(0, CountTripwirePosts());
        }

        [Fact]
        public void WhiteGloveSealed_never_fires()
        {
            var sut = Build(BuildParkedState(stage: SessionStage.WhiteGloveSealed));

            var (step, signal) = ReduceInformational(sut, 10);
            sut.ApplyStep(step, signal);

            Assert.Equal(0, CountTripwirePosts());
        }

        [Fact]
        public void Pre_AccountSetup_never_fires()
        {
            // DeviceSetup hangs are the stall-probe path's responsibility, not this tripwire's.
            var sut = Build(BuildParkedState(stage: SessionStage.EspDeviceSetup, accountSetupEntered: false));

            var (step, signal) = ReduceInformational(sut, 10);
            sut.ApplyStep(step, signal);

            Assert.Equal(0, CountTripwirePosts());
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

            Assert.Equal(0, CountTripwirePosts());
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

            Assert.Equal(0, CountTripwirePosts());
        }

        [Fact]
        public void Without_informational_post_wired_parked_state_is_tolerated()
        {
            // Legacy ctor shape (no InformationalEventPost) — the tripwire is simply disabled.
            var sut = new DecisionStepProcessor(
                initialState: BuildParkedState(),
                journal: _journal,
                effectRunner: _effects,
                snapshot: _snapshot,
                quarantineSink: _quarantine,
                logger: _logger);

            var (step, signal) = ReduceInformational(sut, 10);
            sut.ApplyStep(step, signal);

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

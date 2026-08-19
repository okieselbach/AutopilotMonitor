#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring
{
    /// <summary>
    /// Scope of the Shell-Core replay: it recovers the Hello-wizard start and NOTHING else.
    /// <para>
    /// The replay had existed since session 772fe502 with no caller. Wiring it up (2026-08-19)
    /// activated three rails at once, and only one of them is safe to replay:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>62404 Hello-wizard start — replayed.</b> A conservative fact: it vetoes a
    ///     premature "Hello is disabled" skip and can never by itself complete a session, so
    ///     replaying it can only make the agent wait longer, never finish early.</item>
    ///   <item><b>62407 ESP exit — never replayed.</b> Windows writes the identical description
    ///     for the intermediate DeviceSetup-to-AccountSetup transition and the final exit, so the
    ///     record carries no evidence of its own position; the reducer orders exits by ingest
    ///     ordinal, which for a replay is fresher than reality; and arm C of
    ///     ShouldTransitionToAwaitingHello opens on any arriving exit once the restored state
    ///     carries AccountSetupEntered + a genuine IME user session + desktop arrival. See
    ///     ClassicEspExitingOnRestoredStateTests for the reducer half of that proof.</item>
    ///   <item><b>62407 ESP failure — never replayed.</b> Re-injecting a historic failure as
    ///     fresh can fail a session that recovered (ANALYZE-ESP-006).</item>
    /// </list>
    /// </summary>
    public sealed class ShellCoreTrackerReplayScopeTests
    {
        private static readonly DateTime ClockNow = new DateTime(2026, 8, 19, 8, 43, 16, DateTimeKind.Utc);

        // The REAL Shell-Core wording. Byte-identical for the intermediate DeviceSetup-to-
        // AccountSetup transition and for the final post-AccountSetup exit — that identity is the
        // whole reason a replayed exit cannot be classified.
        private const string EspExitDescription = "CommercialOOBE_ESPProgress_Page_Exiting";
        private const string EspFailureDescription = "CommercialOOBE_ESPProgress_Failure";
        private const string AadHelloDescription = "CloudExperienceHost web app activity: AADHello";
        private const string OtherWebAppDescription = "CloudExperienceHost web app activity: SomethingElse";

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public FakeSignalIngressSink PostSink { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(ClockNow);

            public ShellCoreTracker Build() => new ShellCoreTracker(
                sessionId: "S1",
                tenantId: "T1",
                post: new InformationalEventPost(PostSink, Clock),
                logger: new AgentLogger(Tmp.Path, AgentLogLevel.Info),
                helloTracker: null);

            public List<FakeSignalIngressSink.PostedSignal> AgentTraces() =>
                PostSink.Posted.Where(p =>
                    p.Payload != null
                    && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                    && et == "agent_trace").ToList();

            public void Dispose() => Tmp.Dispose();
        }

        private static (int, string, DateTime) Record(int id, string description, DateTime at)
            => (id, description, at);

        // ------------------------------------------------------------------ the wizard rail

        [Fact]
        public void The_hello_wizard_start_is_replayed_with_its_original_source_time()
        {
            // Session 772fe502: an agent restarted while the user sits inside the Hello wizard
            // would otherwise never learn the wizard ran. This is the observation the replay
            // exists for.
            using var f = new Fixture();
            using var tracker = f.Build();

            var order = new List<string>();
            HelloWizardStartedEventArgs? captured = null;
            tracker.HelloWizardStarted += (_, args) => { order.Add("wizard"); captured = args; };
            tracker.FinalizingSetupPhaseTriggered += (_, reason) => order.Add($"finalizing:{reason}");

            var duringDowntime = ClockNow.AddMinutes(-37);
            tracker.ReplayBackfillRecords(new List<(int, string, DateTime)>
            {
                Record(ShellCoreTracker.EventId_ShellCore_WebAppStarted, AadHelloDescription, duringDowntime),
            });

            Assert.Equal(new[] { "wizard", "finalizing:hello_wizard_started" }, order);
            Assert.NotNull(captured);
            // The historical time must survive the replay — collapsing to now would date the
            // wizard after the restart and skew every duration derived from it.
            Assert.Equal(duringDowntime, captured!.OccurredAtUtc);
        }

        [Fact]
        public void The_wizard_replay_is_single_shot_across_repeated_records()
        {
            using var f = new Fixture();
            using var tracker = f.Build();

            var starts = 0;
            tracker.HelloWizardStarted += (_, __) => starts++;

            tracker.ReplayBackfillRecords(new List<(int, string, DateTime)>
            {
                Record(ShellCoreTracker.EventId_ShellCore_WebAppStarted, AadHelloDescription, ClockNow.AddMinutes(-30)),
                Record(ShellCoreTracker.EventId_ShellCore_WebAppStarted, AadHelloDescription, ClockNow.AddMinutes(-20)),
            });

            Assert.Equal(1, starts);
        }

        [Fact]
        public void A_non_hello_web_app_start_is_ignored()
        {
            using var f = new Fixture();
            using var tracker = f.Build();

            var starts = 0;
            tracker.HelloWizardStarted += (_, __) => starts++;

            tracker.ReplayBackfillRecords(new List<(int, string, DateTime)>
            {
                Record(ShellCoreTracker.EventId_ShellCore_WebAppStarted, OtherWebAppDescription, ClockNow.AddMinutes(-5)),
            });

            Assert.Equal(0, starts);
            Assert.Empty(f.AgentTraces());
        }

        // ------------------------------------------------------------------ the exit rail

        [Fact]
        public void An_intermediate_only_window_raises_nothing_that_can_reach_the_reducer()
        {
            // THE regression this whole review chain is about: restart, and the only 62407 in the
            // window is the historic DeviceSetup-to-AccountSetup handoff. Nothing may leave the
            // tracker on a rail the decision engine listens to.
            using var f = new Fixture();
            using var tracker = f.Build();

            var espExits = 0;
            var finalizing = new List<string>();
            var failures = 0;
            tracker.EspExited += (_, __) => espExits++;
            tracker.FinalizingSetupPhaseTriggered += (_, reason) => finalizing.Add(reason);
            tracker.EspFailureDetected += (_, __) => failures++;

            tracker.ReplayBackfillRecords(new List<(int, string, DateTime)>
            {
                Record(ShellCoreTracker.EventId_ShellCore_WebAppEvent, EspExitDescription, ClockNow.AddMinutes(-52)),
            });

            Assert.Equal(0, espExits);
            Assert.Empty(finalizing);
            Assert.Equal(0, failures);
        }

        [Fact]
        public void Even_several_exits_in_the_window_stay_out_of_the_decision_stream()
        {
            // "Newest exit wins" was an earlier attempt at this problem. It does not help: if the
            // final exit has not happened yet, the newest record in the window is still the
            // intermediate one. Picking a record is the wrong lever — none of them is usable.
            using var f = new Fixture();
            using var tracker = f.Build();

            var espExits = 0;
            tracker.EspExited += (_, __) => espExits++;

            tracker.ReplayBackfillRecords(new List<(int, string, DateTime)>
            {
                Record(ShellCoreTracker.EventId_ShellCore_WebAppEvent, EspExitDescription, ClockNow.AddMinutes(-52)),
                Record(ShellCoreTracker.EventId_ShellCore_WebAppEvent, EspExitDescription, ClockNow.AddMinutes(-9)),
            });

            Assert.Equal(0, espExits);
        }

        [Fact]
        public void A_historic_esp_failure_is_never_re_injected()
        {
            // A session that failed and then recovered on retry (ANALYZE-ESP-006) must not be
            // failed again by its own history.
            using var f = new Fixture();
            using var tracker = f.Build();

            var failures = new List<string>();
            tracker.EspFailureDetected += (_, failureType) => failures.Add(failureType);

            tracker.ReplayBackfillRecords(new List<(int, string, DateTime)>
            {
                Record(ShellCoreTracker.EventId_ShellCore_WebAppEvent, EspFailureDescription, ClockNow.AddMinutes(-40)),
            });

            Assert.Empty(failures);
        }

        // ------------------------------------------------------------------ visibility

        [Fact]
        public void Skipped_records_are_reported_once_as_an_agent_trace()
        {
            // Dropping evidence silently is how this class of bug survives. The gap has to stay
            // visible to whoever debugs the session later — as an informational event, which is
            // decision-neutral by construction.
            using var f = new Fixture();
            using var tracker = f.Build();

            tracker.ReplayBackfillRecords(new List<(int, string, DateTime)>
            {
                Record(ShellCoreTracker.EventId_ShellCore_WebAppEvent, EspExitDescription, ClockNow.AddMinutes(-52)),
                Record(ShellCoreTracker.EventId_ShellCore_WebAppEvent, EspFailureDescription, ClockNow.AddMinutes(-40)),
                Record(ShellCoreTracker.EventId_ShellCore_WebAppEvent, EspExitDescription, ClockNow.AddMinutes(-9)),
            });

            var trace = Assert.Single(f.AgentTraces());
            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(trace.TypedPayload);
            Assert.Equal(2, data["skippedEspExits"]);
            Assert.Equal(1, data["skippedEspFailures"]);
            Assert.Equal("replayed_62407_not_orderable", data["reason"]);
            Assert.Equal(ClockNow.AddMinutes(-52).ToString("o"), data["oldestSkippedUtc"]);
            Assert.Equal(ClockNow.AddMinutes(-9).ToString("o"), data["newestSkippedUtc"]);
        }

        [Fact]
        public void A_wizard_only_window_reports_nothing_as_skipped()
        {
            using var f = new Fixture();
            using var tracker = f.Build();

            tracker.ReplayBackfillRecords(new List<(int, string, DateTime)>
            {
                Record(ShellCoreTracker.EventId_ShellCore_WebAppStarted, AadHelloDescription, ClockNow.AddMinutes(-30)),
            });

            Assert.Empty(f.AgentTraces());
        }

        [Fact]
        public void An_empty_batch_is_a_no_op()
        {
            using var f = new Fixture();
            using var tracker = f.Build();

            var raised = 0;
            tracker.EspExited += (_, __) => raised++;
            tracker.HelloWizardStarted += (_, __) => raised++;

            tracker.ReplayBackfillRecords(new List<(int, string, DateTime)>());

            Assert.Equal(0, raised);
            Assert.Empty(f.AgentTraces());
        }

        // ------------------------------------------------------------------ live path untouched

        [Fact]
        public void The_live_path_still_raises_the_exit_normally()
        {
            // Narrowing the REPLAY must not narrow live observation — that is the agent's primary
            // signal source and the one the completion recovery actually runs on.
            using var f = new Fixture();
            using var tracker = f.Build();

            var exits = new List<DateTime>();
            tracker.EspExited += (_, args) => exits.Add(args.OccurredAtUtc);

            tracker.ProcessEvent(
                eventId: ShellCoreTracker.EventId_ShellCore_WebAppEvent,
                description: EspExitDescription,
                timestamp: ClockNow,
                providerName: "Microsoft-Windows-Shell-Core",
                isBackfill: false);

            Assert.Equal(ClockNow, Assert.Single(exits));
        }

        [Fact]
        public void The_live_path_still_raises_esp_failures()
        {
            using var f = new Fixture();
            using var tracker = f.Build();

            var failures = new List<string>();
            tracker.EspFailureDetected += (_, failureType) => failures.Add(failureType);

            tracker.ProcessEvent(
                eventId: ShellCoreTracker.EventId_ShellCore_WebAppEvent,
                description: EspFailureDescription,
                timestamp: ClockNow,
                providerName: "Microsoft-Windows-Shell-Core",
                isBackfill: false);

            Assert.Single(failures);
        }

        // ------------------------------------------------------------------ window clamp

        [Theory]
        [InlineData(0, 1)]
        [InlineData(-42, 1)]
        [InlineData(5, 5)]
        [InlineData(ShellCoreTracker.BackfillLookbackMaxMinutes, ShellCoreTracker.BackfillLookbackMaxMinutes)]
        [InlineData(ShellCoreTracker.BackfillLookbackMaxMinutes + 1, ShellCoreTracker.BackfillLookbackMaxMinutes)]
        [InlineData(int.MaxValue, ShellCoreTracker.BackfillLookbackMaxMinutes)]
        public void Lookback_is_clamped_to_a_sane_window(int requested, int expected)
        {
            // int.MaxValue matters: the query multiplies minutes by 60_000, so an unclamped value
            // would overflow into a negative timediff and silently match nothing.
            Assert.Equal(expected, ShellCoreTracker.ClampLookbackMinutes(requested));
        }
    }
}

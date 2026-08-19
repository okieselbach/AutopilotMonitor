#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring
{
    /// <summary>
    /// sits-d Cloud-PC fix (2026-08-19) — Shell-Core ESP-exit backfill on restart.
    /// <para>
    /// The backfill has existed since session 772fe502 but no caller ever invoked it, so the
    /// Shell-Core watcher only saw events written after <c>Start()</c>. An agent restart
    /// therefore lost any ESP exit (62407) that happened while it was down — after a forced
    /// mid-ESP reboot that is exactly the window the AccountSetup completion lands in, and five
    /// sits-d Cloud-PC sessions hung until the server-side timeout as a result.
    /// </para>
    /// </summary>
    public sealed class ShellCoreTrackerEspExitBackfillTests
    {
        private static readonly DateTime ClockNow = new DateTime(2026, 8, 19, 8, 43, 16, DateTimeKind.Utc);

        // Shell-Core 62407 wording for the normal ESP teardown (matches OOBE_ESP.*Exiting).
        private const string EspExitDescription =
            "BootstrapStatus: OOBE_ESP - Exiting page due to Account Setup completion.";

        private static ShellCoreTracker MakeTracker(TempDirectory tmp, VirtualClock clock)
        {
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            return new ShellCoreTracker(
                sessionId: "S1",
                tenantId: "T1",
                post: new InformationalEventPost(new FakeSignalIngressSink(), clock),
                logger: logger,
                helloTracker: null);
        }

        [Fact]
        public void Backfilled_esp_exit_raises_EspExited_with_the_original_source_time()
        {
            using var tmp = new TempDirectory();
            using var tracker = MakeTracker(tmp, new VirtualClock(ClockNow));

            var order = new List<string>();
            EspExitedEventArgs? captured = null;
            tracker.FinalizingSetupPhaseTriggered += (_, reason) => order.Add($"finalizing:{reason}");
            tracker.EspExited += (_, args) => { order.Add("exited"); captured = args; };

            // The exit happened while the agent was dead across the reboot.
            var duringDowntime = ClockNow.AddMinutes(-11);
            tracker.HandleBackfillRecord(
                eventId: ShellCoreTracker.EventId_ShellCore_WebAppEvent,
                description: EspExitDescription,
                occurredAtUtc: duringDowntime);

            Assert.Equal(new[] { "finalizing:esp_exiting", "exited" }, order);
            Assert.NotNull(captured);
            // The historical time must survive the replay — collapsing to now would date the
            // completion after the reboot and skew every duration derived from it.
            Assert.Equal(duringDowntime, captured!.OccurredAtUtc);
        }

        [Fact]
        public void Backfill_is_single_shot_across_repeated_records()
        {
            // The replay walks every record in the window; only the first ESP exit may raise.
            using var tmp = new TempDirectory();
            using var tracker = MakeTracker(tmp, new VirtualClock(ClockNow));

            var exits = 0;
            tracker.EspExited += (_, __) => exits++;

            tracker.HandleBackfillRecord(
                eventId: ShellCoreTracker.EventId_ShellCore_WebAppEvent,
                description: EspExitDescription,
                occurredAtUtc: ClockNow.AddMinutes(-11));
            tracker.HandleBackfillRecord(
                eventId: ShellCoreTracker.EventId_ShellCore_WebAppEvent,
                description: EspExitDescription,
                occurredAtUtc: ClockNow.AddMinutes(-9));

            Assert.Equal(1, exits);
        }

        [Fact]
        public void Live_event_after_a_backfilled_exit_still_raises_by_design()
        {
            // The host starts the watcher BEFORE backfilling, so a record written in between is
            // observed twice. Pinning that this is BENIGN, not a defect: Shell-Core emits 62407
            // at every ESP phase transition anyway, the tracker deliberately does not dedup live
            // exits, and the reducer (ShouldTransitionToAwaitingHello) picks the genuine
            // post-AccountSetup occurrence. The fire-once guards that DO matter sit downstream —
            // the Hello-wizard rail and the user-apps-settled synthesis. Start-then-backfill is
            // still the right order: a duplicate costs nothing, a dropped record costs the
            // session's completion.
            using var tmp = new TempDirectory();
            using var tracker = MakeTracker(tmp, new VirtualClock(ClockNow));

            var exits = 0;
            tracker.EspExited += (_, __) => exits++;

            tracker.HandleBackfillRecord(
                eventId: ShellCoreTracker.EventId_ShellCore_WebAppEvent,
                description: EspExitDescription,
                occurredAtUtc: ClockNow.AddMinutes(-2));

            tracker.ProcessEvent(
                eventId: ShellCoreTracker.EventId_ShellCore_WebAppEvent,
                description: EspExitDescription,
                timestamp: ClockNow,
                providerName: "Microsoft-Windows-Shell-Core",
                isBackfill: false);

            Assert.Equal(2, exits);
        }

        [Fact]
        public void Backfilled_hello_wizard_start_is_single_shot_across_repeated_records()
        {
            // The 62404 rail matters just as much as the ESP exit: an agent restarted while the
            // user sits inside the Hello wizard would otherwise never observe the wizard start
            // (session 772fe502) — the original reason the backfill was written. Like the ESP
            // exit, the single-shot guard covers the REPLAY; a later live event raises again and
            // is absorbed downstream (HelloTracker once-guard, adapter dedup flag, engine
            // set-once fact).
            using var tmp = new TempDirectory();
            using var tracker = MakeTracker(tmp, new VirtualClock(ClockNow));

            var wizardStarts = 0;
            tracker.HelloWizardStarted += (_, __) => wizardStarts++;

            const string aadHello = "CloudExperienceHost web app activity started. CXID: 'AADHello'.";
            tracker.HandleBackfillRecord(
                eventId: ShellCoreTracker.EventId_ShellCore_WebAppStarted,
                description: aadHello,
                occurredAtUtc: ClockNow.AddMinutes(-3));
            tracker.HandleBackfillRecord(
                eventId: ShellCoreTracker.EventId_ShellCore_WebAppStarted,
                description: aadHello,
                occurredAtUtc: ClockNow.AddMinutes(-2));

            Assert.Equal(1, wizardStarts);
        }

        [Fact]
        public void Backfilled_unrelated_record_is_ignored()
        {
            using var tmp = new TempDirectory();
            using var tracker = MakeTracker(tmp, new VirtualClock(ClockNow));

            var exits = 0;
            tracker.EspExited += (_, __) => exits++;

            tracker.HandleBackfillRecord(
                eventId: ShellCoreTracker.EventId_ShellCore_WebAppEvent,
                description: "BootstrapStatus: some other page transition.",
                occurredAtUtc: ClockNow.AddMinutes(-1));

            Assert.Equal(0, exits);
        }

        // ------------------------------------------------------------------------------
        // Codex review P1 (2026-08-19): the reader walks oldest-first and the exit branch is
        // fire-once, so a naive replay hands over the FIRST match. With the downtime-sized
        // lookback the window routinely holds the intermediate DeviceSetup→AccountSetup exit
        // AND the final one — and the first match is the wrong edge, with the right one
        // swallowed by the fire-once guard.
        // ------------------------------------------------------------------------------

        [Fact]
        public void Replay_hands_over_the_newest_esp_exit_not_the_oldest()
        {
            using var tmp = new TempDirectory();
            using var tracker = MakeTracker(tmp, new VirtualClock(ClockNow));

            var exits = new List<DateTime>();
            tracker.EspExited += (_, args) => exits.Add(args.OccurredAtUtc);

            var intermediate = ClockNow.AddMinutes(-40);   // DeviceSetup → AccountSetup
            var final = ClockNow.AddMinutes(-9);           // AccountSetup → End (the one we need)

            tracker.ReplayBackfillRecords(new List<(int, string, DateTime)>
            {
                (ShellCoreTracker.EventId_ShellCore_WebAppEvent, EspExitDescription, intermediate),
                (ShellCoreTracker.EventId_ShellCore_WebAppEvent, EspExitDescription, final),
            });

            var raised = Assert.Single(exits);
            Assert.Equal(final, raised);
        }

        [Fact]
        public void Replay_keeps_chronological_order_for_non_exit_records()
        {
            // Only the exit branch is newest-wins; the Hello-wizard rail and ESP failures must
            // keep replaying exactly as before, oldest-first.
            using var tmp = new TempDirectory();
            using var tracker = MakeTracker(tmp, new VirtualClock(ClockNow));

            var order = new List<string>();
            tracker.HelloWizardStarted += (_, args) => order.Add($"wizard:{args.OccurredAtUtc:HH:mm}");
            tracker.EspExited += (_, args) => order.Add($"exit:{args.OccurredAtUtc:HH:mm}");

            const string aadHello = "CloudExperienceHost web app activity started. CXID: 'AADHello'.";
            tracker.ReplayBackfillRecords(new List<(int, string, DateTime)>
            {
                (ShellCoreTracker.EventId_ShellCore_WebAppEvent, EspExitDescription, ClockNow.AddMinutes(-40)),
                (ShellCoreTracker.EventId_ShellCore_WebAppStarted, aadHello, ClockNow.AddMinutes(-20)),
                (ShellCoreTracker.EventId_ShellCore_WebAppEvent, EspExitDescription, ClockNow.AddMinutes(-9)),
            });

            // Wizard replays at its own position; the exit fires once, for the newest record.
            Assert.Equal(new[] { "wizard:" + ClockNow.AddMinutes(-20).ToString("HH:mm"),
                                 "exit:" + ClockNow.AddMinutes(-9).ToString("HH:mm") }, order);
        }

        [Fact]
        public void Replay_of_a_single_exit_is_unchanged()
        {
            using var tmp = new TempDirectory();
            using var tracker = MakeTracker(tmp, new VirtualClock(ClockNow));

            var exits = new List<DateTime>();
            tracker.EspExited += (_, args) => exits.Add(args.OccurredAtUtc);

            var only = ClockNow.AddMinutes(-11);
            tracker.ReplayBackfillRecords(new List<(int, string, DateTime)>
            {
                (ShellCoreTracker.EventId_ShellCore_WebAppEvent, EspExitDescription, only),
            });

            Assert.Equal(only, Assert.Single(exits));
        }

        [Fact]
        public void Replay_of_an_empty_batch_is_a_no_op()
        {
            using var tmp = new TempDirectory();
            using var tracker = MakeTracker(tmp, new VirtualClock(ClockNow));

            var exits = 0;
            tracker.EspExited += (_, __) => exits++;

            tracker.ReplayBackfillRecords(new List<(int, string, DateTime)>());

            Assert.Equal(0, exits);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(-42, 1)]
        [InlineData(5, 5)]
        [InlineData(ShellCoreTracker.BackfillLookbackMaxMinutes, ShellCoreTracker.BackfillLookbackMaxMinutes)]
        [InlineData(ShellCoreTracker.BackfillLookbackMaxMinutes + 1, ShellCoreTracker.BackfillLookbackMaxMinutes)]
        [InlineData(int.MaxValue, ShellCoreTracker.BackfillLookbackMaxMinutes)]
        public void Lookback_is_clamped_to_a_sane_window(int requested, int expected)
        {
            // int.MaxValue matters: the query multiplies minutes by 60_000, so an unclamped
            // value would overflow into a negative timediff and silently match nothing.
            Assert.Equal(expected, ShellCoreTracker.ClampLookbackMinutes(requested));
        }
    }
}

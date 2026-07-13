#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring
{
    /// <summary>
    /// Session 772fe502 fix (2026-07-13) — the Shell-Core 62404 (CXID AADHello/NGC) wizard
    /// launch is promoted from an InformationalEvent-only observation to the dedicated
    /// <see cref="DecisionSignalKind.HelloWizardStarted"/> rail:
    /// ShellCoreTracker.HelloWizardStarted → coordinator forward → adapter emit (fire-once).
    /// The event must fire BEFORE FinalizingSetupPhaseTriggered so the engine records the
    /// wizard fact (and runs the un-skip cure) before EspPhaseChanged(FinalizingSetup) lands.
    /// </summary>
    public sealed class ShellCoreTrackerHelloWizardTests
    {
        private static readonly DateTime ClockNow = new DateTime(2026, 7, 13, 6, 33, 40, DateTimeKind.Utc);
        private const string AadHelloDescription =
            "Die CloudExperienceHost-Web-App-Aktivität wurde gestartet. CXID: 'AADHello'.";

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
        public void ProcessEvent_62404_AADHello_raises_HelloWizardStarted_before_Finalizing_with_source_timestamp()
        {
            using var tmp = new TempDirectory();
            using var tracker = MakeTracker(tmp, new VirtualClock(ClockNow));

            var order = new List<string>();
            HelloWizardStartedEventArgs? captured = null;
            tracker.HelloWizardStarted += (_, args) => { order.Add("wizard"); captured = args; };
            tracker.FinalizingSetupPhaseTriggered += (_, reason) => order.Add($"finalizing:{reason}");

            var sourceTime = ClockNow.AddSeconds(-8);
            tracker.ProcessEvent(
                eventId: ShellCoreTracker.EventId_ShellCore_WebAppStarted,
                description: AadHelloDescription,
                timestamp: sourceTime,
                providerName: "Microsoft-Windows-Shell-Core",
                isBackfill: false);

            Assert.Equal(new[] { "wizard", "finalizing:hello_wizard_started" }, order);
            Assert.NotNull(captured);
            Assert.Equal(sourceTime, captured!.OccurredAtUtc);
            // Mirror is cleared after the synchronous invoke chain.
            Assert.Null(tracker.LastEventOccurredAtUtc);
        }

        [Fact]
        public void ProcessEvent_62404_without_AADHello_raises_nothing()
        {
            using var tmp = new TempDirectory();
            using var tracker = MakeTracker(tmp, new VirtualClock(ClockNow));

            var raised = false;
            tracker.HelloWizardStarted += (_, _) => raised = true;
            tracker.FinalizingSetupPhaseTriggered += (_, _) => raised = true;

            tracker.ProcessEvent(
                eventId: ShellCoreTracker.EventId_ShellCore_WebAppStarted,
                description: "Die CloudExperienceHost-Web-App-Aktivität wurde gestartet. CXID: 'OtherApp'.",
                timestamp: ClockNow,
                providerName: "Microsoft-Windows-Shell-Core",
                isBackfill: false);

            Assert.False(raised);
        }

        [Fact]
        public void Backfill_62404_raises_once_with_historical_timestamp()
        {
            // Agent restart while the user sits inside the wizard: the backfill must replay
            // the 62404 observation exactly once with the original event time.
            using var tmp = new TempDirectory();
            using var tracker = MakeTracker(tmp, new VirtualClock(ClockNow));

            var captured = new List<HelloWizardStartedEventArgs>();
            tracker.HelloWizardStarted += (_, args) => captured.Add(args);

            var historical = ClockNow.AddMinutes(-3);
            tracker.HandleBackfillRecord(
                ShellCoreTracker.EventId_ShellCore_WebAppStarted, AadHelloDescription, historical);
            tracker.HandleBackfillRecord(
                ShellCoreTracker.EventId_ShellCore_WebAppStarted, AadHelloDescription, historical.AddSeconds(1));

            var args = Assert.Single(captured);
            Assert.Equal(historical, args.OccurredAtUtc);
        }

        [Fact]
        public void Backfill_62404_without_AADHello_is_ignored()
        {
            using var tmp = new TempDirectory();
            using var tracker = MakeTracker(tmp, new VirtualClock(ClockNow));

            var raised = false;
            tracker.HelloWizardStarted += (_, _) => raised = true;

            tracker.HandleBackfillRecord(
                ShellCoreTracker.EventId_ShellCore_WebAppStarted,
                "CXID: 'SomethingElse'",
                ClockNow.AddMinutes(-3));

            Assert.False(raised);
        }

        // -------------------------------------------- Coordinator + adapter (production wiring)

        [Fact]
        public void Coordinator_forward_posts_HelloWizardStarted_signal_with_source_timestamp()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var clock = new VirtualClock(ClockNow);
            var ingress = new FakeSignalIngressSink();
            using var coordinator = new EspAndHelloTracker(
                sessionId: "S1",
                tenantId: "T1",
                post: new InformationalEventPost(new FakeSignalIngressSink(), clock),
                logger: logger);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, ingress, clock);

            var sourceTime = ClockNow.AddSeconds(-8);
            coordinator.TriggerHelloWizardStartedForTest(sourceTime);

            var posted = Assert.Single(ingress.Posted, p => p.Kind == DecisionSignalKind.HelloWizardStarted);
            Assert.Equal("EspAndHelloTracker", posted.SourceOrigin);
            Assert.Equal(sourceTime, posted.OccurredAtUtc);
            Assert.Equal("ShellCoreTracker", posted.Evidence.DerivationInputs!["subSource"]);
            Assert.Equal("62404", posted.Evidence.DerivationInputs["eventId"]);
            Assert.False(posted.Evidence.DerivationInputs.ContainsKey("derivedTimestamp"));
            // Mirror is cleared after the forward.
            Assert.Null(coordinator.LastEventOccurredAtUtc);
        }

        [Fact]
        public void Adapter_dedupes_second_HelloWizardStarted()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var clock = new VirtualClock(ClockNow);
            var ingress = new FakeSignalIngressSink();
            using var coordinator = new EspAndHelloTracker(
                sessionId: "S1",
                tenantId: "T1",
                post: new InformationalEventPost(new FakeSignalIngressSink(), clock),
                logger: logger);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, ingress, clock);

            coordinator.TriggerHelloWizardStartedForTest(ClockNow.AddSeconds(-8));
            coordinator.TriggerHelloWizardStartedForTest(ClockNow.AddSeconds(-5));

            Assert.Single(ingress.Posted, p => p.Kind == DecisionSignalKind.HelloWizardStarted);
        }

        [Fact]
        public void Adapter_falls_back_to_clock_with_derivedTimestamp_tag_when_no_mirror()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var clock = new VirtualClock(ClockNow);
            var ingress = new FakeSignalIngressSink();
            using var coordinator = new EspAndHelloTracker(
                sessionId: "S1",
                tenantId: "T1",
                post: new InformationalEventPost(new FakeSignalIngressSink(), clock),
                logger: logger);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, ingress, clock);

            // Direct adapter trigger — no coordinator mirror set → clock fallback + tag.
            adapter.TriggerHelloWizardStartedFromTest();

            var posted = Assert.Single(ingress.Posted, p => p.Kind == DecisionSignalKind.HelloWizardStarted);
            Assert.Equal(ClockNow, posted.OccurredAtUtc);
            Assert.Equal("true", posted.Evidence.DerivationInputs!["derivedTimestamp"]);
        }
    }
}

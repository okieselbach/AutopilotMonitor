#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring
{
    /// <summary>
    /// The first run's update page backfill (<see cref="ShellCoreTracker.BackfillUpdatePageTelemetry"/>)
    /// and the page a stop belongs to across backfill and live records. The first run has no
    /// replay — its backfill is telemetry and nothing else, so a Hello-wizard start, an ESP exit
    /// or an ESP failure in the window must not leave the tracker on any rail.
    /// </summary>
    public sealed class ShellCoreTrackerUpdatePageBackfillTests : IDisposable
    {
        private static readonly DateTime At = new DateTime(2026, 10, 5, 13, 10, 0, DateTimeKind.Utc);

        private const string NdupStarted = "CloudExperienceHost Web App Activity started. CXID: 'OobeNDUP'.";
        private const string RebootNdupStarted = "CloudExperienceHost Web App Activity started. CXID: 'RebootNDUP'.";
        private const string AadHelloStarted = "CloudExperienceHost Web App Activity started. CXID: 'AADHello'.";
        private const string PageStopped = "CloudExperienceHost Web App Activity stopped. Result: 'success'.";
        private const string DownloadSucceeded =
            "CloudExperienceHost Web App Event 1. Name: 'SdxWebAppCloudNDUP_processStatusChangeFromHandler_downloadSucceeded'.";
        private const string EspExit = "CommercialOOBE_ESPProgress_Page_Exiting";
        private const string EspFailure = "CommercialOOBE_ESPProgress_Failure";
        private const string WhiteGloveSuccess = "CommercialOOBE_ESPProgress_WhiteGlove_Success";

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly FakeSignalIngressSink _sink = new FakeSignalIngressSink();
        private int _registryReads;

        public void Dispose() => _tmp.Dispose();

        private ShellCoreTracker Build() =>
            new ShellCoreTracker("S1", "T1",
                new InformationalEventPost(_sink, new VirtualClock(At)),
                new AgentLogger(_tmp.Path, AgentLogLevel.Info),
                helloTracker: null,
                oobeUpdateRegistryReader: () => { _registryReads++; return Array.Empty<OobeUpdateRegistrySnapshot.KeyState>(); },
                stateDirectory: Path.Combine(_tmp.Path, "state"));

        private static void Live(ShellCoreTracker tracker, int eventId, string description, DateTime at, long recordId) =>
            tracker.ProcessEvent(eventId, description, at, "Microsoft-Windows-Shell-Core", isBackfill: false, recordId: recordId);

        private static ShellCoreRecord Record(int eventId, string description, DateTime at, long recordId) =>
            new ShellCoreRecord(eventId, description, at, recordId);

        private IReadOnlyList<IReadOnlyDictionary<string, object>> Pages() =>
            _sink.Posted
                .Where(p => p.Kind == DecisionSignalKind.InformationalEvent
                    && p.Payload != null
                    && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                    && et == Constants.EventTypes.OobeUpdatePage)
                .Select(p => (IReadOnlyDictionary<string, object>)p.TypedPayload!)
                .ToList();

        // ------------------------------------------------------------ first run ----

        [Fact]
        public void FirstRunBackfill_ReportsTheUpdatePage_AndNothingElse()
        {
            using var tracker = Build();
            var raised = new List<string>();
            tracker.HelloWizardStarted += (_, _) => raised.Add("hello");
            tracker.FinalizingSetupPhaseTriggered += (_, reason) => raised.Add($"finalizing:{reason}");
            tracker.EspExited += (_, _) => raised.Add("exit");
            tracker.EspFailureDetected += (_, _) => raised.Add("failure");
            tracker.WhiteGloveCompleted += (_, _) => raised.Add("whiteglove");

            tracker.ReplayUpdatePageTelemetry(new[]
            {
                Record(ShellCoreTracker.EventId_ShellCore_WebAppEvent, EspExit, At.AddMinutes(-41), 95),
                Record(ShellCoreTracker.EventId_ShellCore_WebAppEvent, EspFailure, At.AddSeconds(-2430), 96),
                Record(ShellCoreTracker.EventId_ShellCore_WebAppEvent, WhiteGloveSuccess, At.AddSeconds(-2420), 97),
                Record(ShellCoreTracker.EventId_ShellCore_WebAppStarted, NdupStarted, At.AddMinutes(-40), 100),
                Record(ShellCoreTracker.EventId_ShellCore_WebAppEventName, DownloadSucceeded, At.AddMinutes(-30), 101),
                Record(ShellCoreTracker.EventId_ShellCore_WebAppStarted, AadHelloStarted, At.AddMinutes(-2), 110),
            });

            Assert.Empty(raised);
            Assert.False(tracker.IsEspExitedForTest);
            Assert.False(tracker.IsWhiteGloveDetectedForTest);
            var pages = Pages();
            Assert.Equal(new object[] { "page_started", "event_name" }, pages.Select(d => d["cxhEvent"]).ToArray());
            Assert.All(pages, d => Assert.Equal(true, d["backfill"]));
            // No agent_trace, no ESP or Hello event — only the two page rows.
            Assert.Equal(2, _sink.Posted.Count);
            // The registry shows the state now, not at the records' time.
            Assert.Equal(0, _registryReads);
        }

        [Fact]
        public void FirstRunBackfill_KeepsTheEventTimes()
        {
            using var tracker = Build();

            tracker.ReplayUpdatePageTelemetry(new[]
            {
                Record(ShellCoreTracker.EventId_ShellCore_WebAppStarted, NdupStarted, At.AddMinutes(-40), 100),
            });

            var posted = Assert.Single(_sink.Posted);
            Assert.Equal(At.AddMinutes(-40), posted.OccurredAtUtc);
        }

        [Fact]
        public void LiveStop_FindsThePageThatStartedBeforeTheAgent()
        {
            using var tracker = Build();
            tracker.ReplayUpdatePageTelemetry(new[]
            {
                Record(ShellCoreTracker.EventId_ShellCore_WebAppStarted, NdupStarted, At.AddMinutes(-40), 100),
            });

            Live(tracker, ShellCoreTracker.EventId_ShellCore_WebAppStopped, PageStopped, At.AddMinutes(5), 120);

            var stop = Pages().Last();
            Assert.Equal("page_stopped", stop["cxhEvent"]);
            Assert.Equal("OobeNDUP", stop["page"]);
            Assert.Equal(false, stop["backfill"]);
        }

        [Fact]
        public void SameRecord_FromTheLiveWatcherAndTheFirstRunBackfill_IsReportedOnce()
        {
            // The live watcher is armed before the backfill reads, so both can deliver a record.
            using var tracker = Build();
            Live(tracker, ShellCoreTracker.EventId_ShellCore_WebAppStarted, NdupStarted, At, 700);
            tracker.ReplayUpdatePageTelemetry(new[]
            {
                Record(ShellCoreTracker.EventId_ShellCore_WebAppStarted, NdupStarted, At, 700),
            });

            Assert.Single(Pages(), d => (string)d["cxhEvent"] == "page_started");
        }

        // ---------------------------------------------------- the page of a stop ----

        [Fact]
        public void ReplayedOlderStart_DoesNotTakeOverTheNextLiveStop()
        {
            // A restart replay reads records older than the live watcher's; before, its page start
            // overwrote the live page and the next live stop was attributed to the old page.
            using var tracker = Build();
            Live(tracker, ShellCoreTracker.EventId_ShellCore_WebAppStarted, RebootNdupStarted, At, 900);
            tracker.ReplayBackfillRecords(new[]
            {
                Record(ShellCoreTracker.EventId_ShellCore_WebAppStarted, NdupStarted, At.AddMinutes(-20), 850),
                Record(ShellCoreTracker.EventId_ShellCore_WebAppEventName, DownloadSucceeded, At.AddMinutes(-19), 851),
            });

            Live(tracker, ShellCoreTracker.EventId_ShellCore_WebAppStopped, PageStopped, At.AddMinutes(1), 901);

            var stop = Pages().Last();
            Assert.Equal("page_stopped", stop["cxhEvent"]);
            Assert.Equal("RebootNDUP", stop["page"]);
        }

        [Fact]
        public void BackfilledStop_BelongsToTheStartOfItsOwnBatch()
        {
            using var tracker = Build();
            Live(tracker, ShellCoreTracker.EventId_ShellCore_WebAppStarted, RebootNdupStarted, At, 900);

            tracker.ReplayUpdatePageTelemetry(new[]
            {
                Record(ShellCoreTracker.EventId_ShellCore_WebAppStarted, NdupStarted, At.AddMinutes(-20), 850),
                Record(ShellCoreTracker.EventId_ShellCore_WebAppStopped, PageStopped, At.AddMinutes(-18), 852),
            });

            var stop = Pages().Last();
            Assert.Equal("page_stopped", stop["cxhEvent"]);
            Assert.Equal("OobeNDUP", stop["page"]);
            Assert.Equal(true, stop["backfill"]);
        }

        [Fact]
        public void BackfilledStopWithoutItsStart_IsNotGivenTheLivePage()
        {
            using var tracker = Build();
            Live(tracker, ShellCoreTracker.EventId_ShellCore_WebAppStarted, NdupStarted, At, 900);

            tracker.ReplayUpdatePageTelemetry(new[]
            {
                Record(ShellCoreTracker.EventId_ShellCore_WebAppStopped, PageStopped, At.AddMinutes(-18), 852),
            });

            Assert.DoesNotContain(Pages(), d => (string)d["cxhEvent"] == "page_stopped");
        }

        [Fact]
        public void NewerBackfilledStart_MovesTheLivePageForward()
        {
            // Records written between arming the watcher and the backfill's read are newer than
            // the last live start the watcher delivered before them.
            using var tracker = Build();
            Live(tracker, ShellCoreTracker.EventId_ShellCore_WebAppStarted, NdupStarted, At, 900);
            tracker.ReplayUpdatePageTelemetry(new[]
            {
                Record(ShellCoreTracker.EventId_ShellCore_WebAppStarted, RebootNdupStarted, At.AddSeconds(30), 905),
            });

            Live(tracker, ShellCoreTracker.EventId_ShellCore_WebAppStopped, PageStopped, At.AddMinutes(1), 906);

            Assert.Equal("RebootNDUP", Pages().Last()["page"]);
        }

        [Fact]
        public void UpdatePageLookback_IsInsideTheReplayClamp()
        {
            Assert.Equal(ShellCoreTracker.UpdatePageBackfillLookbackMinutes,
                ShellCoreTracker.ClampLookbackMinutes(ShellCoreTracker.UpdatePageBackfillLookbackMinutes));
        }
    }
}

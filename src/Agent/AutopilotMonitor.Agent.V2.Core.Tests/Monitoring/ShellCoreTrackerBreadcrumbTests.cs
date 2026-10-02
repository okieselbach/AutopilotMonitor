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
using AutopilotMonitor.Shared;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring
{
    /// <summary>
    /// CloudExperienceHost navigation (D-310): one agent.log line per page start/stop and per
    /// update-page event name, and for the OOBE update page itself (<c>OobeNDUP</c>,
    /// <c>RebootNDUP</c>) the <c>oobe_update_page</c> telemetry plus the registry state at its live
    /// start and stop. Descriptions follow the Shell-Core manifest templates; a 62407 value
    /// (URLs with tenant ids) leaves the tracker only as a boolean, an integer or an HRESULT.
    /// </summary>
    public sealed class ShellCoreTrackerBreadcrumbTests : IDisposable
    {
        private static readonly DateTime At = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);

        private const string NdupStarted = "CloudExperienceHost Web App Activity started. CXID: 'OobeNDUP'.";
        private const string EspPageStarted = "CloudExperienceHost Web App Activity started. CXID: 'MDMProgressAfterAADRefactored'.";
        private const string PageStopped = "CloudExperienceHost Web App Activity stopped. Result: 'success'.";
        private const string ScanStartedName = "CloudExperienceHost Web App Event 1. Name: 'ExpeditedUpdate_startWUScanStarted'.";
        private const string NavigateWithTenantUrl =
            "CloudExperienceHost Web App Event 2. Name: 'Navigate', Value: '{\"httpMethod\":\"GET\",\"url\":\"https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/oauth2/authorize\"}'.";
        private const string DownloadSucceededWithValue =
            "CloudExperienceHost Web App Event 2. Name: 'ExpeditedUpdate_commitExpeditionDownloadInstallAsyncSucceeded', Value: '{\"url\":\"https://login.microsoftonline.com/x\"}'.";
        private const string CspGateTrue =
            "CloudExperienceHost Web App Event 2. Name: 'ExpeditedUpdate_isNDUPAllowedByCSPSucceeded', Value: 'true'.";

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly FakeSignalIngressSink _sink = new FakeSignalIngressSink();
        private IReadOnlyList<OobeUpdateRegistrySnapshot.KeyState> _registry = Array.Empty<OobeUpdateRegistrySnapshot.KeyState>();

        public void Dispose() => _tmp.Dispose();

        private ShellCoreTracker Build() =>
            new ShellCoreTracker("S1", "T1",
                new InformationalEventPost(_sink, new VirtualClock(At)),
                new AgentLogger(_tmp.Path, AgentLogLevel.Info),
                helloTracker: null,
                oobeUpdateRegistryReader: () => _registry);

        private IReadOnlyList<FakeSignalIngressSink.PostedSignal> ByType(string eventType) =>
            _sink.Posted.Where(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == eventType).ToList();

        private static IReadOnlyDictionary<string, object> Data(FakeSignalIngressSink.PostedSignal posted) =>
            (IReadOnlyDictionary<string, object>)posted.TypedPayload!;

        private static OobeUpdateRegistrySnapshot.KeyState NdupKey(string line) =>
            new OobeUpdateRegistrySnapshot.KeyState("setupOobeNdup", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\OOBE\NDUP",
                exists: true, hasValues: true, line: line);

        // ------------------------------------------------------------- breadcrumbs ----

        [Fact]
        public void PageStart_NamesTheCxid()
        {
            Assert.Equal("CXH page started: OobeNDUP",
                ShellCoreTracker.FormatCxhBreadcrumb(ShellCoreTracker.EventId_ShellCore_WebAppStarted, NdupStarted, lastPage: null));
        }

        [Fact]
        public void PageStop_IsAttributedToTheLastStartedPage()
        {
            // 62405 names no page — it ends the one started last.
            Assert.Equal("CXH page stopped: OobeNDUP (result=success)",
                ShellCoreTracker.FormatCxhBreadcrumb(ShellCoreTracker.EventId_ShellCore_WebAppStopped, PageStopped, lastPage: "OobeNDUP"));
            Assert.Equal("CXH page stopped: ? (result=success)",
                ShellCoreTracker.FormatCxhBreadcrumb(ShellCoreTracker.EventId_ShellCore_WebAppStopped, PageStopped, lastPage: null));
        }

        [Fact]
        public void UpdatePageEventNames_AreLogged_FromBothEventIds()
        {
            Assert.Equal("CXH event: ExpeditedUpdate_startWUScanStarted",
                ShellCoreTracker.FormatCxhBreadcrumb(ShellCoreTracker.EventId_ShellCore_WebAppEventName, ScanStartedName, lastPage: null));
            Assert.Equal("CXH event: ExpeditedUpdate_commitExpeditionDownloadInstallAsyncSucceeded",
                ShellCoreTracker.FormatCxhBreadcrumb(ShellCoreTracker.EventId_ShellCore_WebAppEvent, DownloadSucceededWithValue, lastPage: null));
        }

        [Fact]
        public void UnsafeEventValues_NeverReachTheLog()
        {
            var crumb = ShellCoreTracker.FormatCxhBreadcrumb(
                ShellCoreTracker.EventId_ShellCore_WebAppEvent, DownloadSucceededWithValue, lastPage: null);

            Assert.NotNull(crumb);
            Assert.DoesNotContain("microsoftonline", crumb);
            Assert.DoesNotContain("url", crumb!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void SafeEventValues_AreLogged()
        {
            Assert.Equal("CXH event: ExpeditedUpdate_isNDUPAllowedByCSPSucceeded (value=true)",
                ShellCoreTracker.FormatCxhBreadcrumb(ShellCoreTracker.EventId_ShellCore_WebAppEvent, CspGateTrue, lastPage: null));
        }

        [Fact]
        public void OtherEventNames_AreNotLogged()
        {
            // Ordinary page traffic (Navigate, Visible, ESP progress) stays out of the log.
            Assert.Null(ShellCoreTracker.FormatCxhBreadcrumb(
                ShellCoreTracker.EventId_ShellCore_WebAppEvent, NavigateWithTenantUrl, lastPage: "OobeNDUP"));
            Assert.Null(ShellCoreTracker.FormatCxhBreadcrumb(
                ShellCoreTracker.EventId_ShellCore_WebAppEvent, "CommercialOOBE_ESPProgress_Page_Exiting", lastPage: null));
        }

        [Theory]
        [InlineData("")]
        [InlineData("CloudExperienceHost Web App Activity started.")]
        public void PageStartWithoutCxid_IsNoBreadcrumb(string description)
        {
            Assert.Null(ShellCoreTracker.FormatCxhBreadcrumb(ShellCoreTracker.EventId_ShellCore_WebAppStarted, description, lastPage: null));
        }

        [Fact]
        public void LongTokens_AreBounded()
        {
            var longCxid = new string('X', 500);
            var crumb = ShellCoreTracker.FormatCxhBreadcrumb(
                ShellCoreTracker.EventId_ShellCore_WebAppStarted, $"CloudExperienceHost Web App Activity started. CXID: '{longCxid}'.", lastPage: null);

            Assert.NotNull(crumb);
            Assert.True(crumb!.Length < 200, crumb);
            Assert.EndsWith("…", crumb);
        }

        [Theory]
        [InlineData("OobeNDUP", true)]
        [InlineData("RebootNDUP", true)]
        [InlineData("EarlyRebootNDUP", true)]
        [InlineData("ExpeditedUpdate_startWUScanStarted", true)]
        [InlineData("AADHello", false)]
        [InlineData("MDMProgressAfterAADRefactored", false)]
        [InlineData(null, false)]
        public void UpdatePageTokens_AreRecognized(string? token, bool expected)
        {
            Assert.Equal(expected, ShellCoreTracker.IsUpdatePageToken(token!));
        }

        // ------------------------------------------------------ update page records ----

        [Fact]
        public void UpdatePageRecords_ArePageStartsStopsAndNames_OfTheUpdatePageOnly()
        {
            var start = ShellCoreTracker.ParseUpdatePageRecord(ShellCoreTracker.EventId_ShellCore_WebAppStarted, NdupStarted, lastPage: null);
            Assert.Equal(OobeUpdatePageRecord.PageStarted, start!.CxhEvent);
            Assert.Equal("OobeNDUP", start.Page);

            var stop = ShellCoreTracker.ParseUpdatePageRecord(ShellCoreTracker.EventId_ShellCore_WebAppStopped, PageStopped, lastPage: "RebootNDUP");
            Assert.Equal(OobeUpdatePageRecord.PageStopped, stop!.CxhEvent);
            Assert.Equal("RebootNDUP", stop.Page);
            Assert.Equal("success", stop.Result);

            var name = ShellCoreTracker.ParseUpdatePageRecord(ShellCoreTracker.EventId_ShellCore_WebAppEventName, ScanStartedName, lastPage: null);
            Assert.Equal(OobeUpdatePageRecord.EventName, name!.CxhEvent);
            Assert.Equal("ExpeditedUpdate_startWUScanStarted", name.Name);
            Assert.Null(name.Value);

            // Other pages and names are no update-page records.
            Assert.Null(ShellCoreTracker.ParseUpdatePageRecord(ShellCoreTracker.EventId_ShellCore_WebAppStarted, EspPageStarted, lastPage: null));
            Assert.Null(ShellCoreTracker.ParseUpdatePageRecord(ShellCoreTracker.EventId_ShellCore_WebAppStopped, PageStopped, lastPage: "MDMProgressAfterAADRefactored"));
            Assert.Null(ShellCoreTracker.ParseUpdatePageRecord(ShellCoreTracker.EventId_ShellCore_WebAppEvent, NavigateWithTenantUrl, lastPage: "OobeNDUP"));
        }

        [Fact]
        public void UpdatePageRecord_CarriesOnlyASafeValue()
        {
            var unsafeValue = ShellCoreTracker.ParseUpdatePageRecord(ShellCoreTracker.EventId_ShellCore_WebAppEvent, DownloadSucceededWithValue, lastPage: null);
            Assert.Null(unsafeValue!.Value);

            var gate = ShellCoreTracker.ParseUpdatePageRecord(ShellCoreTracker.EventId_ShellCore_WebAppEvent, CspGateTrue, lastPage: null);
            Assert.Equal("true", gate!.Value);
        }

        [Theory]
        [InlineData("true", "true")]
        [InlineData("FALSE", "FALSE")]
        [InlineData("0", "0")]
        [InlineData("-2147024891", "-2147024891")]
        [InlineData("0x80240022", "0x80240022")]
        [InlineData("12345678901", null)]          // more than ten digits
        [InlineData("0x123456789", null)]          // more than eight hex digits
        [InlineData("https://login.microsoftonline.com/x", null)]
        [InlineData("{\"result\":0}", null)]
        [InlineData("true and more", null)]
        [InlineData("", null)]
        public void SafeValues_AreBooleansIntegersAndHresults(string value, string? expected)
        {
            var description = $"CloudExperienceHost Web App Event 2. Name: 'ExpeditedUpdate_x', Value: '{value}'.";

            Assert.Equal(expected, ShellCoreTracker.ExtractSafeValue(description));
        }

        [Fact]
        public void LiveUpdatePage_IsReported_WithTheRegistryState_AndRaisesNothing()
        {
            _registry = new[] { NdupKey("EnableExpeditedUpdate=1") };
            using var tracker = Build();
            var raised = 0;
            tracker.EspExited += (_, _) => raised++;
            tracker.EspFailureDetected += (_, _) => raised++;
            tracker.FinalizingSetupPhaseTriggered += (_, _) => raised++;

            tracker.ProcessEvent(ShellCoreTracker.EventId_ShellCore_WebAppStarted, NdupStarted, At, "Microsoft-Windows-Shell-Core", isBackfill: false);
            tracker.ProcessEvent(ShellCoreTracker.EventId_ShellCore_WebAppEventName, ScanStartedName, At.AddMinutes(1), "Microsoft-Windows-Shell-Core", isBackfill: false);
            _registry = new[] { NdupKey("EnableExpeditedUpdate=1, EnableExpeditedUpdateSyncInstallCompleted=1") };
            tracker.ProcessEvent(ShellCoreTracker.EventId_ShellCore_WebAppStopped, PageStopped, At.AddMinutes(30), "Microsoft-Windows-Shell-Core", isBackfill: false);

            var pages = ByType(Constants.EventTypes.OobeUpdatePage);
            Assert.Equal(new object[] { "page_started", "event_name", "page_stopped" }, pages.Select(p => Data(p)["cxhEvent"]).ToArray());
            Assert.All(pages, p => Assert.Equal(false, Data(p)["backfill"]));

            var states = ByType(Constants.EventTypes.OobeUpdateState);
            Assert.Equal(new object[] { "update_page_started", "update_page_stopped" }, states.Select(s => Data(s)["moment"]).ToArray());
            Assert.All(states, s => Assert.Equal("OobeNDUP", Data(s)["page"]));

            // Telemetry only — no ESP signal, no other event.
            Assert.Equal(0, raised);
            Assert.False(tracker.IsEspExitedForTest);
            Assert.Equal(pages.Count + states.Count, _sink.Posted.Count);
        }

        [Fact]
        public void OtherPages_AreNotReported()
        {
            _registry = new[] { NdupKey("EnableExpeditedUpdate=1") };
            using var tracker = Build();

            tracker.ProcessEvent(ShellCoreTracker.EventId_ShellCore_WebAppStarted, EspPageStarted, At, "Microsoft-Windows-Shell-Core", isBackfill: false);
            tracker.ProcessEvent(ShellCoreTracker.EventId_ShellCore_WebAppStopped, PageStopped, At.AddMinutes(1), "Microsoft-Windows-Shell-Core", isBackfill: false);

            Assert.Empty(_sink.Posted);
        }

        [Fact]
        public void Backfill_ReportsTheUpdatePage_WithItsEventTimes_ButReadsNoRegistry()
        {
            // The registry shows the state now, not at the record's time.
            var reads = 0;
            using var tracker = new ShellCoreTracker("S1", "T1",
                new InformationalEventPost(_sink, new VirtualClock(At)),
                new AgentLogger(_tmp.Path, AgentLogLevel.Info),
                helloTracker: null,
                oobeUpdateRegistryReader: () => { reads++; return new[] { NdupKey("EnableExpeditedUpdate=1") }; });

            tracker.ReplayBackfillRecords(new[]
            {
                (ShellCoreTracker.EventId_ShellCore_WebAppStarted, NdupStarted, At.AddMinutes(-4)),
                (ShellCoreTracker.EventId_ShellCore_WebAppEventName, ScanStartedName, At.AddMinutes(-3)),
                (ShellCoreTracker.EventId_ShellCore_WebAppStopped, PageStopped, At.AddMinutes(-2)),
            });

            var pages = ByType(Constants.EventTypes.OobeUpdatePage);
            Assert.Equal(3, pages.Count);
            Assert.All(pages, p => Assert.Equal(true, Data(p)["backfill"]));
            Assert.Equal(At.AddMinutes(-4), pages[0].OccurredAtUtc);
            Assert.Equal(0, reads);
            // Nothing else: no ESP exit or failure was in the window, so no skipped-records trace.
            Assert.Equal(3, _sink.Posted.Count);
        }

        [Fact]
        public void AgentStartAndStop_ReportTheRegistryState_StopOnce()
        {
            _registry = new[] { NdupKey("EnableExpeditedUpdate=1") };
            var tracker = Build();

            tracker.Start();
            _registry = new[] { NdupKey("EnableExpeditedUpdate=1, ExpeditedUpdateStatus=2") };
            tracker.Stop();
            tracker.Stop();
            tracker.Dispose();

            var states = ByType(Constants.EventTypes.OobeUpdateState);
            Assert.Equal(new object[] { "agent_start", "agent_stop" }, states.Select(s => Data(s)["moment"]).ToArray());
        }

        [Fact]
        public void TrackerThatNeverStarted_ReportsNothingAtStop()
        {
            _registry = new[] { NdupKey("EnableExpeditedUpdate=1") };
            var tracker = Build();

            tracker.Dispose();

            Assert.Empty(_sink.Posted);
        }
    }
}

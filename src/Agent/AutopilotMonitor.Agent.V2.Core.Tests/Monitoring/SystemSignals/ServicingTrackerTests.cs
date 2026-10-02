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
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// ServicingTracker (D-310) — the CBS servicing steps of OS updates from the Setup log. The
    /// sequence and XML shapes are those Windows 11 writes for a cumulative update installed by
    /// <c>UpdateAgentLCU</c>: initiate Absent → Staged, Staged reached, initiate Staged → Installed,
    /// restart required, Installed reached after the restart.
    /// </summary>
    public sealed class ServicingTrackerTests : IDisposable
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 25, 18, 52, 37, DateTimeKind.Utc);
        private const string Kb = "KB5099999";

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly FakeSignalIngressSink _sink = new FakeSignalIngressSink();
        private readonly UpdateActivityTally _tally = new UpdateActivityTally();
        private readonly List<(string Step, string Update, DateTime At)> _osActivity = new List<(string, string, DateTime)>();
        private readonly ServicingTracker _tracker;
        private long _nextRecordId = 1200;

        public ServicingTrackerTests()
        {
            _tracker = NewTracker(stateDirectory: null);
        }

        public void Dispose() => _tmp.Dispose();

        private ServicingTracker NewTracker(string? stateDirectory) =>
            new ServicingTracker(
                sessionId: "sess-cbs",
                tenantId: "tenant-cbs",
                post: new InformationalEventPost(_sink, new VirtualClock(T0)),
                logger: new AgentLogger(_tmp.Path, AgentLogLevel.Info),
                backfillEnabled: false,
                stateDirectory: stateDirectory,
                tally: _tally,
                onOsUpdateActivity: (step, update, at) => _osActivity.Add((step, update, at)));

        private void Process(
            int eventId,
            DateTime at,
            string? initialState = null,
            string? targetState = "Installed",
            string? errorCode = null,
            string? client = "UpdateAgentLCU",
            string? package = Kb,
            long? recordId = null,
            ServicingTracker? tracker = null,
            bool isBackfill = false)
        {
            (tracker ?? _tracker).ProcessEvent(
                eventId: eventId,
                recordId: recordId ?? _nextRecordId++,
                timeCreatedUtc: at,
                package: package,
                initialState: initialState,
                targetState: targetState,
                errorCode: errorCode,
                client: client,
                isBackfill: isBackfill);
        }

        private IReadOnlyList<FakeSignalIngressSink.PostedSignal> Servicing() =>
            _sink.Posted.Where(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == Constants.EventTypes.WindowsUpdateServicing).ToList();

        private static IReadOnlyDictionary<string, object> Data(FakeSignalIngressSink.PostedSignal s) =>
            (IReadOnlyDictionary<string, object>)s.TypedPayload!;

        [Fact]
        public void CumulativeUpdateSequence_ListsEveryStep_AndReportsTheEngineSteps()
        {
            Process(ServicingTracker.EventId_InitiatingChanges, T0, initialState: "Absent", targetState: "Staged");
            Process(ServicingTracker.EventId_StateReached, T0.AddMinutes(4), targetState: "Staged", errorCode: "0x0");
            Process(ServicingTracker.EventId_InitiatingChanges, T0.AddMinutes(29), initialState: "Staged", targetState: "Installed");
            Process(ServicingTracker.EventId_RebootRequired, T0.AddMinutes(33), targetState: "Installed", errorCode: "0x0");
            // Written after the restart.
            Process(ServicingTracker.EventId_StateReached, T0.AddMinutes(41), targetState: "Installed", errorCode: "0x0");

            var rows = Servicing();
            Assert.Equal(
                new[] { "initiating", "state_reached", "initiating", "reboot_required", "state_reached" },
                rows.Select(s => (string)Data(s)["step"]).ToArray());
            Assert.All(rows, s => Assert.Equal("Info", s.Payload![SignalPayloadKeys.Severity]));
            Assert.All(rows, s => Assert.Equal("ServicingWatcher", s.Payload![SignalPayloadKeys.Source]));
            Assert.All(rows, s => Assert.Equal(Kb, Data(s)["package"]));
            Assert.All(rows, s => Assert.Equal("UpdateAgentLCU", Data(s)["client"]));
            // ErrorCode 0x0 is success, not a decoded HRESULT.
            Assert.All(rows, s => Assert.False(Data(s).ContainsKey("hresult")));
            Assert.Equal("Absent", Data(rows[0])["initialState"]);
            Assert.Equal("Staged", Data(rows[0])["targetState"]);
            Assert.Equal(T0.AddMinutes(33), rows[3].OccurredAtUtc);
            Assert.Contains("needs a restart", rows[3].Payload![SignalPayloadKeys.Message]);

            Assert.Equal(
                new[]
                {
                    OsUpdateSteps.StagingStarted, OsUpdateSteps.Staged, OsUpdateSteps.InstallStarted,
                    OsUpdateSteps.RebootRequired, OsUpdateSteps.Installed,
                },
                _osActivity.Select(a => a.Step).ToArray());
            Assert.All(_osActivity, a => Assert.Equal(Kb, a.Update));
            Assert.Equal(T0.AddMinutes(29), _osActivity[2].At);
        }

        [Theory]
        [InlineData("CbsTask")]
        [InlineData("DISM Package Manager Provider")]
        [InlineData("")]
        [InlineData(null)]
        public void NonOsClients_AreCountedNotListed(string? client)
        {
            // CBS maintenance writes hundreds of these in seconds; language packs and features on
            // demand installed by apps are not the OOBE update either.
            Process(ServicingTracker.EventId_InitiatingChanges, T0, initialState: "Staged", targetState: "Installed",
                client: client, package: "Microsoft-Windows-Telnet-Client-FOD-en-US");
            Process(ServicingTracker.EventId_StateReached, T0, targetState: "Installed", errorCode: "0x0",
                client: client, package: "Microsoft-Windows-Telnet-Client-FOD-en-US");

            Assert.Empty(_sink.Posted);
            Assert.Empty(_osActivity);
            var counts = _tally.Snapshot().ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.Equal(1, counts["servicing_other_1"]);
            Assert.Equal(1, counts["servicing_other_2"]);
        }

        [Fact]
        public void WindowsUpdateAgentClient_IsAnOsClient()
        {
            Process(ServicingTracker.EventId_RebootRequired, T0, client: "WindowsUpdateAgent", errorCode: "0x0");

            Assert.Single(Servicing());
            Assert.Equal(OsUpdateSteps.RebootRequired, Assert.Single(_osActivity).Step);
        }

        [Fact]
        public void ChangeFailed_EmitsWarning_WithDecodedHResult()
        {
            Process(ServicingTracker.EventId_ChangeFailed, T0, targetState: "Installed", errorCode: "0x800f0922");

            var s = Assert.Single(Servicing());
            Assert.Equal("Warning", s.Payload![SignalPayloadKeys.Severity]);
            var data = Data(s);
            Assert.Equal("failed", data["step"]);
            Assert.Equal("0x800F0922", data["hresult"]);
            Assert.Equal("CBS_E_INSTALLERS_FAILED", data["hresultSymbol"]);
            Assert.Contains("FAILED", s.Payload![SignalPayloadKeys.Message]);
            Assert.Equal(OsUpdateSteps.Failed, Assert.Single(_osActivity).Step);
        }

        [Fact]
        public void PartiallyInstalled_IsAFailure_AndSaysSo()
        {
            Process(ServicingTracker.EventId_PartiallyInstalled, T0, targetState: "Installed", errorCode: "0x800f0922");

            var s = Assert.Single(Servicing());
            Assert.Equal("failed", Data(s)["step"]);
            Assert.Contains("partially installed", s.Payload![SignalPayloadKeys.Message]);
            Assert.Equal(OsUpdateSteps.Failed, Assert.Single(_osActivity).Step);
        }

        [Fact]
        public void ChangeTowardsAnotherState_IsListed_ButIsNoEngineStep()
        {
            // Removing a superseded package is servicing, not progress of the update install.
            Process(ServicingTracker.EventId_InitiatingChanges, T0, initialState: "Installed", targetState: "Absent");

            Assert.Single(Servicing());
            Assert.Empty(_osActivity);
        }

        [Fact]
        public void BackfilledRecord_KeepsItsEventTime()
        {
            var earlier = T0.AddMinutes(-20);
            Process(ServicingTracker.EventId_InitiatingChanges, earlier, initialState: "Absent", targetState: "Staged", isBackfill: true);

            var s = Assert.Single(Servicing());
            Assert.Equal(earlier, s.OccurredAtUtc);
            Assert.Equal(true, Data(s)["backfilled"]);
            Assert.Equal(earlier, Assert.Single(_osActivity).At);
        }

        [Fact]
        public void Watermark_SkipsEmittedRecordsAfterRestart()
        {
            var first = NewTracker(_tmp.Path);
            Process(ServicingTracker.EventId_RebootRequired, T0, errorCode: "0x0", recordId: 1205, tracker: first);

            var second = NewTracker(_tmp.Path);
            second.LoadWatermark();
            Process(ServicingTracker.EventId_RebootRequired, T0, errorCode: "0x0", recordId: 1205, tracker: second, isBackfill: true);
            Process(ServicingTracker.EventId_StateReached, T0.AddMinutes(8), errorCode: "0x0", recordId: 1207, tracker: second);

            Assert.Equal(new[] { "reboot_required", "state_reached" }, Servicing().Select(s => (string)Data(s)["step"]).ToArray());
        }

        [Fact]
        public void SameRecordDeliveredTwice_IsListedOnce()
        {
            Process(ServicingTracker.EventId_RebootRequired, T0, errorCode: "0x0", recordId: 1205);
            Process(ServicingTracker.EventId_RebootRequired, T0, errorCode: "0x0", recordId: 1205);

            Assert.Single(Servicing());
            Assert.Single(_osActivity);
        }

        [Fact]
        public void ParseUserData_ReadsBothCbsPayloadShapes()
        {
            const string initiate =
                "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Servicing' Guid='{bd12f3b8-fc40-4a61-a307-b7a013a069c1}'/>" +
                "<EventID>1</EventID><Version>0</Version><Level>0</Level><Task>1</Task><Opcode>0</Opcode><Keywords>0x8000000000000000</Keywords>" +
                "<TimeCreated SystemTime='2026-09-25T19:21:42.4486782Z'/><EventRecordID>1203</EventRecordID><Channel>Setup</Channel><Computer>DESKTOP-TEST</Computer>" +
                "<Security UserID='S-1-5-18'/></System><UserData><CbsPackageInitiateChanges xmlns='http://manifests.microsoft.com/win/2004/08/windows/setup_provider'>" +
                "<PackageIdentifier>KB5099999</PackageIdentifier><InitialPackageState>5064</InitialPackageState><InitialPackageStateTextized>Staged</InitialPackageStateTextized>" +
                "<IntendedPackageState>5112</IntendedPackageState><IntendedPackageStateTextized>Installed</IntendedPackageStateTextized><Client>UpdateAgentLCU</Client>" +
                "</CbsPackageInitiateChanges></UserData></Event>";
            const string changeState =
                "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Servicing' Guid='{bd12f3b8-fc40-4a61-a307-b7a013a069c1}'/>" +
                "<EventID>4</EventID><TimeCreated SystemTime='2026-09-25T19:26:07.1381462Z'/><EventRecordID>1205</EventRecordID><Channel>Setup</Channel></System>" +
                "<UserData><CbsPackageChangeState xmlns='http://manifests.microsoft.com/win/2004/08/windows/setup_provider'><PackageIdentifier>KB5099999</PackageIdentifier>" +
                "<IntendedPackageState>5112</IntendedPackageState><IntendedPackageStateTextized>Installed</IntendedPackageStateTextized><ErrorCode>0x0</ErrorCode>" +
                "<Client>UpdateAgentLCU</Client></CbsPackageChangeState></UserData></Event>";

            var a = ServicingTracker.ParseUserData(initiate);
            Assert.Equal("KB5099999", a["PackageIdentifier"]);
            Assert.Equal("Staged", a["InitialPackageStateTextized"]);
            Assert.Equal("Installed", a["IntendedPackageStateTextized"]);
            Assert.Equal("UpdateAgentLCU", a["client"]); // case-insensitive
            Assert.False(a.ContainsKey("ErrorCode"));

            var b = ServicingTracker.ParseUserData(changeState);
            Assert.Equal("Installed", b["IntendedPackageStateTextized"]);
            Assert.Equal("0x0", b["ErrorCode"]);
            Assert.False(b.ContainsKey("InitialPackageStateTextized"));
        }

        [Fact]
        public void ParseUserData_MalformedOrWithoutUserData_ReturnsEmpty_NeverThrows()
        {
            Assert.Empty(ServicingTracker.ParseUserData(null));
            Assert.Empty(ServicingTracker.ParseUserData(""));
            Assert.Empty(ServicingTracker.ParseUserData("<not-xml"));
            Assert.Empty(ServicingTracker.ParseUserData("<Event><System><EventID>1</EventID></System></Event>"));
        }

        [Fact]
        public void Watermark_PersistsOnlyAfterTheEngineSignalWasHandedOver()
        {
            // "Reboot required" is written right before the update's restart: its engine signal
            // must be on its way before the watermark tells the next run's backfill to skip it.
            var path = System.IO.Path.Combine(_tmp.Path, ServicingTracker.WatermarkStateFileName);
            bool? watermarkOnDiskAtSignal = null;
            var tracker = new ServicingTracker(
                sessionId: "sess-cbs",
                tenantId: "tenant-cbs",
                post: new InformationalEventPost(_sink, new VirtualClock(T0)),
                logger: new AgentLogger(_tmp.Path, AgentLogLevel.Info),
                backfillEnabled: false,
                stateDirectory: _tmp.Path,
                tally: _tally,
                onOsUpdateActivity: (step, update, at) => watermarkOnDiskAtSignal = System.IO.File.Exists(path));

            Process(ServicingTracker.EventId_RebootRequired, T0, errorCode: "0x0", tracker: tracker);

            Assert.False(watermarkOnDiskAtSignal);
            Assert.True(System.IO.File.Exists(path));
        }
    }
}

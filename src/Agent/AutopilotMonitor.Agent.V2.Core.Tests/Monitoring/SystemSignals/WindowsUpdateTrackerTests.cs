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
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// WindowsUpdateTracker — surfaces quality/cumulative update activity during OOBE from the
    /// WindowsUpdateClient provider: install events from the System log, scan/download events from
    /// the Operational channel (D-310). Drives the primitive <see cref="WindowsUpdateTracker.ProcessEvent"/>
    /// test seam (no real EventRecord, which is abstract + Windows-only), mirroring the
    /// ModernDeploymentTracker test pattern. Update titles are the shapes the provider writes.
    /// </summary>
    public sealed class WindowsUpdateTrackerTests : IDisposable
    {
        private static readonly DateTime At = new DateTime(2026, 7, 8, 10, 15, 0, DateTimeKind.Utc);

        private const string Lcu = "2026-09 Cumulative Update for Windows 11, version 25H2 for x64-based Systems (KB5099999)";
        private const string StoreApp = "9WZDNCRFHVN5-MICROSOFT.WINDOWSCALCULATOR";
        private const string DefenderIntelligence = "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.459.505.0) - Current Channel (Broad)";

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly FakeSignalIngressSink _sink = new FakeSignalIngressSink();
        private readonly UpdateActivityTally _tally = new UpdateActivityTally();
        private readonly List<(string Step, string Update, DateTime At)> _osActivity = new List<(string, string, DateTime)>();
        private readonly WindowsUpdateTracker _system;
        private readonly WindowsUpdateTracker _operational;
        private long _nextRecordId = 1;

        public WindowsUpdateTrackerTests()
        {
            // In-memory dedup for most tests.
            _system = NewTracker(WindowsUpdateTracker.SystemChannel, stateDirectory: null);
            _operational = NewTracker(channel: null, stateDirectory: null);
        }

        public void Dispose() => _tmp.Dispose();

        private WindowsUpdateTracker NewTracker(string? channel, string? stateDirectory) =>
            new WindowsUpdateTracker(
                sessionId: "sess-wu",
                tenantId: "tenant-wu",
                post: new InformationalEventPost(_sink, new VirtualClock(At)),
                logger: new AgentLogger(_tmp.Path, AgentLogLevel.Info),
                backfillEnabled: false,
                stateDirectory: stateDirectory,
                channel: channel,
                tally: _tally,
                onOsUpdateActivity: (step, update, at) => _osActivity.Add((step, update, at)));

        private void Emit(
            WindowsUpdateTracker tracker,
            int eventId,
            string? updateTitle = Lcu,
            string? errorCode = null,
            long? recordId = null,
            string? serviceGuid = null,
            string? updateCount = null,
            DateTime? at = null,
            bool isBackfill = false)
        {
            tracker.ProcessEvent(
                eventId: eventId,
                level: 4,
                recordId: recordId ?? _nextRecordId++,
                timeCreatedUtc: at ?? At,
                updateTitle: updateTitle,
                updateGuid: "{8b1c8726-1111-2222-3333-444455556666}",
                updateRevisionNumber: "200",
                errorCode: errorCode,
                formattedDescription: null,
                isBackfill: isBackfill,
                serviceGuid: serviceGuid,
                updateCount: updateCount);
        }

        private IReadOnlyList<FakeSignalIngressSink.PostedSignal> ByType(string eventType) =>
            _sink.Posted.Where(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == eventType).ToList();

        private static IReadOnlyDictionary<string, object> Data(FakeSignalIngressSink.PostedSignal s) =>
            (IReadOnlyDictionary<string, object>)s.TypedPayload!;

        private int Counted(string key) =>
            _tally.Snapshot().Where(kv => kv.Key == key).Select(kv => kv.Value).SingleOrDefault();

        // ---------------------------------------------------------------------------
        // Install events (System log)
        // ---------------------------------------------------------------------------

        [Fact]
        public void FailureEvent_EmitsWindowsUpdateFailed_WithDecodedHResult_AndImmediateUpload()
        {
            Emit(_system, WindowsUpdateTracker.EventId_InstallFailure, errorCode: "0x80240022");

            var failed = ByType(Constants.EventTypes.WindowsUpdateFailed);
            var s = Assert.Single(failed);
            Assert.Equal("Error", s.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("true", s.Payload![SignalPayloadKeys.ImmediateUpload]);

            var data = Data(s);
            Assert.Equal("0x80240022", data["hresult"]);
            Assert.Equal("WU_E_ALL_UPDATES_FAILED", data["hresultSymbol"]);
            Assert.Equal(Lcu, data["updateTitle"]);
            Assert.Equal(20, data["wuEventId"]);
            Assert.Equal("system", data["wuChannel"]);
            Assert.Equal("install", data["wuPhase"]);
            Assert.Equal(WindowsUpdateClassifier.Os, data["updateClass"]);
        }

        [Fact]
        public void SuccessEvent_EmitsWindowsUpdateSucceeded_InfoSeverity()
        {
            Emit(_system, WindowsUpdateTracker.EventId_InstallSuccess);

            var ok = ByType(Constants.EventTypes.WindowsUpdateSucceeded);
            var s = Assert.Single(ok);
            Assert.Equal("Info", s.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("false", s.Payload![SignalPayloadKeys.ImmediateUpload]);
            Assert.Equal("installed", Data(s)["wuPhase"]);
        }

        [Fact]
        public void StartedAndDownloadEvents_EmitInfoStarted_WithTheirPhase()
        {
            // Info, not Debug: with only OS updates listed they are the visible progress of a
            // long OOBE update.
            Emit(_system, WindowsUpdateTracker.EventId_InstallStarted);
            Emit(_system, WindowsUpdateTracker.EventId_DownloadStarted);

            var started = ByType(Constants.EventTypes.WindowsUpdateStarted);
            Assert.Equal(2, started.Count);
            Assert.All(started, s => Assert.Equal("Info", s.Payload![SignalPayloadKeys.Severity]));
            Assert.Equal(new[] { "install", "download" }, started.Select(s => (string)Data(s)["wuPhase"]).ToArray());
        }

        [Fact]
        public void SameRecordId_ProcessedTwice_EmitsOnce()
        {
            Emit(_system, WindowsUpdateTracker.EventId_InstallSuccess, recordId: 42);
            Emit(_system, WindowsUpdateTracker.EventId_InstallSuccess, recordId: 42);

            Assert.Single(ByType(Constants.EventTypes.WindowsUpdateSucceeded));
        }

        [Fact]
        public void BackfillEventWithLowerRecordId_AfterLiveEvent_IsStillEmitted()
        {
            // Regression (Codex P1): the live watcher is armed BEFORE backfill runs, so a live event
            // with a higher RecordId can be processed first. An older, never-emitted backfill event
            // (lower RecordId) must NOT be suppressed as "already processed" — these early pre-agent
            // OOBE updates are the whole point of the feature. A high-water-mark dedup dropped them.
            Emit(_system, WindowsUpdateTracker.EventId_InstallSuccess, recordId: 100); // live, high RecordId
            Emit(_system, WindowsUpdateTracker.EventId_InstallSuccess, recordId: 50);  // backfill, older, never seen

            Assert.Equal(2, ByType(Constants.EventTypes.WindowsUpdateSucceeded).Count);
        }

        [Fact]
        public void BackfilledEvent_UsesEventTimeForTimelineTimestamp()
        {
            // Codex P1: a backfilled pre-agent update must land on the timeline at the WU event's own
            // time, not at agent start (the ctor's default UtcNow). InformationalEventPost forwards
            // EnrollmentEvent.Timestamp as the signal's OccurredAtUtc, which drives the timeline entry.
            var eventTime = new DateTime(2026, 7, 8, 7, 5, 0, DateTimeKind.Utc); // well before `At`
            Emit(_system, WindowsUpdateTracker.EventId_InstallFailure, errorCode: "0x80240022",
                recordId: 7, at: eventTime, isBackfill: true);

            var s = Assert.Single(ByType(Constants.EventTypes.WindowsUpdateFailed));
            Assert.Equal(eventTime, s.OccurredAtUtc);
            Assert.Equal(true, Data(s)["backfilled"]);
        }

        [Fact]
        public void NegativeRecordId_IsNeverDeduped()
        {
            Emit(_system, WindowsUpdateTracker.EventId_InstallSuccess, recordId: -1);
            Emit(_system, WindowsUpdateTracker.EventId_InstallSuccess, recordId: -1);

            Assert.Equal(2, ByType(Constants.EventTypes.WindowsUpdateSucceeded).Count);
        }

        // ---------------------------------------------------------------------------
        // Scan/download events (Operational channel)
        // ---------------------------------------------------------------------------

        [Fact]
        public void Downloaded_EmitsInfoStarted_WithDownloadedPhase()
        {
            Emit(_operational, WindowsUpdateTracker.EventId_Downloaded);

            var s = Assert.Single(ByType(Constants.EventTypes.WindowsUpdateStarted));
            Assert.Equal("Info", s.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("downloaded", Data(s)["wuPhase"]);
            Assert.Equal("operational", Data(s)["wuChannel"]);
        }

        [Fact]
        public void DownloadFailure_OfAnOsUpdate_EmitsWarning_WithDownloadPhase()
        {
            Emit(_operational, WindowsUpdateTracker.EventId_DownloadFailed, errorCode: "0x80248007");

            var s = Assert.Single(ByType(Constants.EventTypes.WindowsUpdateFailed));
            Assert.Equal("Warning", s.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("false", s.Payload![SignalPayloadKeys.ImmediateUpload]);
            var data = Data(s);
            Assert.Equal("download", data["wuPhase"]);
            Assert.Equal("0x80248007", data["hresult"]);
        }

        [Fact]
        public void ScanFound_EmitsOnlyChangedCounts_AndCountsTheRepeats()
        {
            foreach (var count in new[] { "3", "3", "5", "5", "0" })
                Emit(_operational, WindowsUpdateTracker.EventId_ScanFound, updateTitle: null, updateCount: count);

            var found = ByType(Constants.EventTypes.WindowsUpdateStarted);
            Assert.Equal(new[] { "3", "5", "0" }, found.Select(s => (string)Data(s)["updateCount"]).ToArray());
            Assert.All(found, s => Assert.Equal("Debug", s.Payload![SignalPayloadKeys.Severity]));
            Assert.All(found, s => Assert.Equal("scan", Data(s)["wuPhase"]));
            Assert.All(found, s => Assert.False(Data(s).ContainsKey("updateClass")));
            Assert.Contains("scan found 3 update(s)", found[0].Payload![SignalPayloadKeys.Message]);
            Assert.Equal(2, Counted("scan_found_repeat"));
            Assert.Empty(_osActivity); // a scan result is not an update step
        }

        [Fact]
        public void ScanFound_IsCappedPerRun()
        {
            for (var i = 0; i < WindowsUpdateTracker.MaxScanFoundEmissions + 2; i++)
                Emit(_operational, WindowsUpdateTracker.EventId_ScanFound, updateTitle: null, updateCount: i.ToString());

            Assert.Equal(WindowsUpdateTracker.MaxScanFoundEmissions, ByType(Constants.EventTypes.WindowsUpdateStarted).Count);
            Assert.Equal(2, Counted("scan_found_repeat"));
        }

        [Fact]
        public void ScanFailed_EmitsEachErrorCodeOnce()
        {
            Emit(_operational, WindowsUpdateTracker.EventId_ScanFailed, updateTitle: null, errorCode: "0x8024401c");
            Emit(_operational, WindowsUpdateTracker.EventId_ScanFailed, updateTitle: null, errorCode: "0x8024401c");
            Emit(_operational, WindowsUpdateTracker.EventId_ScanFailed, updateTitle: null, errorCode: "0x80072EE2");
            Emit(_operational, WindowsUpdateTracker.EventId_ScanFailed, updateTitle: null, errorCode: "0x8024401C");

            var failed = ByType(Constants.EventTypes.WindowsUpdateFailed);
            Assert.Equal(2, failed.Count);
            Assert.All(failed, s => Assert.Equal("Warning", s.Payload![SignalPayloadKeys.Severity]));
            Assert.All(failed, s => Assert.Equal("scan", Data(s)["wuPhase"]));
            Assert.Equal(2, Counted("scan_failed_repeat"));
            Assert.Empty(_osActivity);
        }

        [Fact]
        public void ScanFailed_IsCappedPerRun()
        {
            for (var i = 0; i < WindowsUpdateTracker.MaxScanFailedEmissions + 2; i++)
                Emit(_operational, WindowsUpdateTracker.EventId_ScanFailed, updateTitle: null, errorCode: $"0x8024000{i}");

            Assert.Equal(WindowsUpdateTracker.MaxScanFailedEmissions, ByType(Constants.EventTypes.WindowsUpdateFailed).Count);
            Assert.Equal(2, Counted("scan_failed_repeat"));
        }

        // ---------------------------------------------------------------------------
        // Classification — only OS updates are listed, the rest is counted
        // ---------------------------------------------------------------------------

        [Theory]
        [InlineData(StoreApp, null, WindowsUpdateTracker.EventId_InstallSuccess, "store_19")]
        [InlineData("Some App", "{855e8a7c-ecb4-4ca3-b045-1dfa50104289}", WindowsUpdateTracker.EventId_Downloaded, "store_41")]
        [InlineData(DefenderIntelligence, null, WindowsUpdateTracker.EventId_InstallStarted, "defender_43")]
        [InlineData("Intel net Driver Update (24.50.0.4)", null, WindowsUpdateTracker.EventId_DownloadStarted, "other_44")]
        // A real download failure names no update at all; it must not reach ANALYZE-DEV-004 as
        // a failed OS update.
        [InlineData("", null, WindowsUpdateTracker.EventId_DownloadFailed, "other_31")]
        [InlineData("Some App", "{855e8a7c-ecb4-4ca3-b045-1dfa50104289}", WindowsUpdateTracker.EventId_InstallFailure, "store_20")]
        public void NonOsUpdates_AreCountedNotEmitted(string title, string? serviceGuid, int eventId, string expectedKey)
        {
            var tracker = WindowsUpdateTracker.IsSystemChannelEventId(eventId) ? _system : _operational;
            Emit(tracker, eventId, updateTitle: title, serviceGuid: serviceGuid, errorCode: "0x80248007");

            Assert.Empty(_sink.Posted);
            Assert.Empty(_osActivity);
            Assert.Equal(1, Counted(expectedKey));
            // The census still sees the channel working.
            Assert.Equal(1, tracker.EmittedThisRun);
        }

        [Fact]
        public void CountedRecord_DeliveredTwice_IsCountedOnce()
        {
            // Live watcher and backfill overlap: the seen-set dedups counted records too.
            Emit(_system, WindowsUpdateTracker.EventId_InstallSuccess, updateTitle: StoreApp, recordId: 7);
            Emit(_system, WindowsUpdateTracker.EventId_InstallSuccess, updateTitle: StoreApp, recordId: 7);

            Assert.Equal(1, Counted("store_19"));
        }

        [Fact]
        public void OsUpdateSteps_AreReportedToTheEngineCallback()
        {
            var t0 = At;
            Emit(_system, WindowsUpdateTracker.EventId_DownloadStarted, at: t0);
            Emit(_operational, WindowsUpdateTracker.EventId_Downloaded, at: t0.AddMinutes(4));
            Emit(_system, WindowsUpdateTracker.EventId_InstallStarted, at: t0.AddMinutes(5));
            Emit(_system, WindowsUpdateTracker.EventId_InstallSuccess, at: t0.AddMinutes(31));
            Emit(_system, WindowsUpdateTracker.EventId_InstallFailure, errorCode: "0x800F0922", at: t0.AddMinutes(32));
            Emit(_operational, WindowsUpdateTracker.EventId_DownloadFailed, errorCode: "0x80248007", at: t0.AddMinutes(33));

            Assert.Equal(
                new[]
                {
                    OsUpdateSteps.DownloadStarted, OsUpdateSteps.Downloaded, OsUpdateSteps.InstallStarted,
                    OsUpdateSteps.Installed, OsUpdateSteps.Failed, OsUpdateSteps.Failed,
                },
                _osActivity.Select(a => a.Step).ToArray());
            Assert.All(_osActivity, a => Assert.Equal(Lcu, a.Update));
            Assert.Equal(t0.AddMinutes(31), _osActivity[3].At);
        }

        [Fact]
        public void ThrowingCallback_NeverCostsTheEvent()
        {
            var tracker = new WindowsUpdateTracker(
                "s", "t", new InformationalEventPost(_sink, new VirtualClock(At)),
                new AgentLogger(_tmp.Path, AgentLogLevel.Info),
                backfillEnabled: false,
                channel: WindowsUpdateTracker.SystemChannel,
                onOsUpdateActivity: (step, update, at) => throw new InvalidOperationException("boom"));

            Emit(tracker, WindowsUpdateTracker.EventId_InstallSuccess);

            Assert.Single(ByType(Constants.EventTypes.WindowsUpdateSucceeded));
        }

        // ---------------------------------------------------------------------------
        // Channels and XPath
        // ---------------------------------------------------------------------------

        [Fact]
        public void DefaultEventIds_FollowTheProviderManifest()
        {
            Assert.Equal(WindowsUpdateTracker.SystemChannel, _system.WatchedChannel);
            Assert.Equal(new[] { 19, 20, 43, 44 }, _system.TargetedEventIds.OrderBy(id => id).ToArray());
            Assert.Equal(WindowsUpdateTracker.Channel, _operational.WatchedChannel);
            Assert.Equal(new[] { 25, 26, 31, 41 }, _operational.TargetedEventIds.OrderBy(id => id).ToArray());
        }

        [Theory]
        [InlineData(16, true)]
        [InlineData(19, true)]
        [InlineData(20, true)]
        [InlineData(24, true)]
        [InlineData(27, true)]
        [InlineData(28, true)]
        [InlineData(32, true)]
        [InlineData(33, true)]
        [InlineData(43, true)]
        [InlineData(44, true)]
        [InlineData(212, true)]
        [InlineData(218, true)]
        [InlineData(25, false)]
        [InlineData(26, false)]
        [InlineData(31, false)]
        [InlineData(41, false)]
        [InlineData(15, false)]
        [InlineData(219, false)]
        public void IsSystemChannelEventId_MatchesTheManifest(int eventId, bool expected)
        {
            Assert.Equal(expected, WindowsUpdateTracker.IsSystemChannelEventId(eventId));
        }

        [Fact]
        public void BuildXPath_ContainsAllTargetedIds_Ordered()
        {
            var xpath = WindowsUpdateTracker.BuildXPath(new HashSet<int> { 44, 19, 20, 43 });
            Assert.Equal("*[System[(EventID=19 or EventID=20 or EventID=43 or EventID=44)]]", xpath);
        }

        [Fact]
        public void BuildXPath_SharedSystemLog_FiltersTheProvider_AndTheLookback()
        {
            // Verified against a real System log: the provider clause keeps the other providers'
            // EventIDs 19/20/43/44 out of the shared channel.
            var ids = new HashSet<int> { 44, 19, 20, 43 };

            Assert.Equal(
                "*[System[Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and (EventID=19 or EventID=20 or EventID=43 or EventID=44)]]",
                WindowsUpdateTracker.BuildXPath(ids, WindowsUpdateTracker.ProviderName));
            Assert.Equal(
                "*[System[Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and (EventID=19 or EventID=20 or EventID=43 or EventID=44) and TimeCreated[timediff(@SystemTime) <= 3600000]]]",
                WindowsUpdateTracker.BuildXPath(ids, WindowsUpdateTracker.ProviderName, lookbackMs: 3600000));
            Assert.Equal(
                "*[System[(EventID=25 or EventID=26) and TimeCreated[timediff(@SystemTime) <= 60000]]]",
                WindowsUpdateTracker.BuildXPath(new HashSet<int> { 26, 25 }, providerName: null, lookbackMs: 60000));
        }

        // ---------------------------------------------------------------------------
        // Watermark
        // ---------------------------------------------------------------------------

        [Fact]
        public void Watermark_PersistsAcrossTrackerInstances()
        {
            var first = NewTracker(WindowsUpdateTracker.SystemChannel, _tmp.Path);
            Emit(first, WindowsUpdateTracker.EventId_InstallSuccess, recordId: 500);

            // A fresh tracker (simulating an agent restart) must load the watermark and skip the
            // already-emitted record when the OOBE backfill re-reads it.
            var second = NewTracker(WindowsUpdateTracker.SystemChannel, _tmp.Path);
            second.LoadWatermark();
            Emit(second, WindowsUpdateTracker.EventId_InstallSuccess, recordId: 500, isBackfill: true);

            Assert.Single(ByType(Constants.EventTypes.WindowsUpdateSucceeded));
        }

        [Fact]
        public void Watermark_IsPerChannel()
        {
            // RecordIds are per channel: System record 500 and Operational record 500 are two
            // different events and must not dedup each other — before or after a restart.
            Emit(NewTracker(WindowsUpdateTracker.SystemChannel, _tmp.Path), WindowsUpdateTracker.EventId_InstallSuccess, recordId: 500);
            Emit(NewTracker(null, _tmp.Path), WindowsUpdateTracker.EventId_Downloaded, recordId: 500);

            Assert.Single(ByType(Constants.EventTypes.WindowsUpdateSucceeded));
            Assert.Single(ByType(Constants.EventTypes.WindowsUpdateStarted));
            Assert.Equal(500L, ReadWatermark(WindowsUpdateTracker.SystemWatermarkStateFileName));
            Assert.Equal(500L, ReadWatermark(WindowsUpdateTracker.WatermarkStateFileName));

            var system = NewTracker(WindowsUpdateTracker.SystemChannel, _tmp.Path);
            var operational = NewTracker(null, _tmp.Path);
            system.LoadWatermark();
            operational.LoadWatermark();
            Emit(system, WindowsUpdateTracker.EventId_InstallSuccess, recordId: 500, isBackfill: true);
            Emit(operational, WindowsUpdateTracker.EventId_Downloaded, recordId: 500, isBackfill: true);

            Assert.Single(ByType(Constants.EventTypes.WindowsUpdateSucceeded));
            Assert.Single(ByType(Constants.EventTypes.WindowsUpdateStarted));
        }

        [Fact]
        public void CountedRecords_WriteNoStateFile()
        {
            // A Store/Defender burst must not cost a state-file write per record; only emitted
            // records move the persisted watermark.
            var tracker = NewTracker(WindowsUpdateTracker.SystemChannel, _tmp.Path);
            Emit(tracker, WindowsUpdateTracker.EventId_InstallSuccess, updateTitle: StoreApp, recordId: 600);
            Emit(tracker, WindowsUpdateTracker.EventId_InstallStarted, updateTitle: DefenderIntelligence, recordId: 601);

            Assert.False(File.Exists(Path.Combine(_tmp.Path, WindowsUpdateTracker.SystemWatermarkStateFileName)));

            Emit(tracker, WindowsUpdateTracker.EventId_InstallStarted, recordId: 602);
            Assert.Equal(602L, ReadWatermark(WindowsUpdateTracker.SystemWatermarkStateFileName));
        }

        [Fact]
        public void Watermark_PersistsOnlyAfterTheEngineSignalWasHandedOver()
        {
            // A restart's backfill skips a persisted record, so the record's engine signal must be
            // on its way before the watermark reaches the disk — never the other way round.
            var path = Path.Combine(_tmp.Path, WindowsUpdateTracker.SystemWatermarkStateFileName);
            bool? watermarkOnDiskAtSignal = null;
            var tracker = new WindowsUpdateTracker(
                sessionId: "sess-wu",
                tenantId: "tenant-wu",
                post: new InformationalEventPost(_sink, new VirtualClock(At)),
                logger: new AgentLogger(_tmp.Path, AgentLogLevel.Info),
                backfillEnabled: false,
                stateDirectory: _tmp.Path,
                channel: WindowsUpdateTracker.SystemChannel,
                tally: _tally,
                onOsUpdateActivity: (step, update, at) => watermarkOnDiskAtSignal = File.Exists(path));

            Emit(tracker, WindowsUpdateTracker.EventId_InstallStarted, recordId: 700);

            Assert.False(watermarkOnDiskAtSignal);
            Assert.Equal(700L, ReadWatermark(WindowsUpdateTracker.SystemWatermarkStateFileName));
        }

        private long ReadWatermark(string fileName) =>
            JObject.Parse(File.ReadAllText(Path.Combine(_tmp.Path, fileName)))["LastRecordId"]!.Value<long>();

        // ---------------------------------------------------------------------------
        // Parsing and HRESULT decoding
        // ---------------------------------------------------------------------------

        [Fact]
        public void ParseEventData_ExtractsNamedFields_NamespaceAgnostic()
        {
            const string xml =
                "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>" +
                "<System><EventID>20</EventID></System>" +
                "<EventData>" +
                "<Data Name='updateTitle'>2026-07 Cumulative Update (KB5099999)</Data>" +
                "<Data Name='updateGuid'>{abcdef01-2345-6789-abcd-ef0123456789}</Data>" +
                "<Data Name='errorCode'>0x8024200B</Data>" +
                "</EventData></Event>";

            var data = WindowsUpdateTracker.ParseEventData(xml);
            Assert.Equal("2026-07 Cumulative Update (KB5099999)", data["updateTitle"]);
            Assert.Equal("{abcdef01-2345-6789-abcd-ef0123456789}", data["updateGuid"]);
            Assert.Equal("0x8024200B", data["errorCode"]);
            // Case-insensitive lookup
            Assert.True(data.ContainsKey("UPDATETITLE"));
        }

        [Fact]
        public void ParseEventData_ReadsTheScanFields()
        {
            // Event 26 (scan found N) and 25 (scan failed) carry no update title.
            const string xml =
                "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>" +
                "<System><EventID>26</EventID></System>" +
                "<EventData>" +
                "<Data Name='updateCount'>3</Data>" +
                "<Data Name='serviceGuid'>{8b24b027-1dee-babb-9a95-3517dfb9c552}</Data>" +
                "</EventData></Event>";

            var data = WindowsUpdateTracker.ParseEventData(xml);
            Assert.Equal("3", data["updateCount"]);
            Assert.Equal("{8b24b027-1dee-babb-9a95-3517dfb9c552}", data["serviceGuid"]);
            Assert.False(data.ContainsKey("updateTitle"));
        }

        [Fact]
        public void ParseEventData_MalformedXml_ReturnsEmpty_NeverThrows()
        {
            Assert.Empty(WindowsUpdateTracker.ParseEventData("<not-xml"));
            Assert.Empty(WindowsUpdateTracker.ParseEventData(null));
            Assert.Empty(WindowsUpdateTracker.ParseEventData(""));
        }

        [Theory]
        [InlineData("0x80240022", 0x80240022u, "0x80240022")]
        [InlineData("0X8024200b", 0x8024200Bu, "0x8024200B")]
        [InlineData("-2145124346", 0x80240006u, "0x80240006")] // signed decimal round-trips to unsigned hex
        [InlineData("0", 0u, "0x00000000")]
        public void TryNormalizeHResult_HandlesHexAndSignedDecimal(string input, uint expected, string expectedHex)
        {
            Assert.True(WindowsUpdateTracker.TryNormalizeHResult(input, out var value, out var hex));
            Assert.Equal(expected, value);
            Assert.Equal(expectedHex, hex);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not-a-number")]
        public void TryNormalizeHResult_RejectsUnparseable(string? input)
        {
            Assert.False(WindowsUpdateTracker.TryNormalizeHResult(input!, out _, out _));
        }

        [Fact]
        public void ResolveHResultSymbol_ReadsTheSharedCatalog()
        {
            Assert.Equal("WU_E_ALL_UPDATES_FAILED", WindowsUpdateTracker.ResolveHResultSymbol(0x80240022));
            Assert.Equal("CBS_E_INSTALLERS_FAILED", WindowsUpdateTracker.ResolveHResultSymbol(0x800F0922));
            Assert.Equal("ERROR_TIMEOUT", WindowsUpdateTracker.ResolveHResultSymbol(0x800705B4));
            Assert.Equal("S_OK", WindowsUpdateTracker.ResolveHResultSymbol(0x00000000));
            Assert.Equal("WU_E_UNKNOWN", WindowsUpdateTracker.ResolveHResultSymbol(0x8888DEAD));
        }

        [Fact]
        public void ResolveHResultSymbol_FallsBackWhenTheCatalogHasNoSymbolOrThrows()
        {
            // An entry without a symbol (IME/Intune paraphrases) still yields the sentinel.
            Assert.Equal("WU_E_UNKNOWN", WindowsUpdateTracker.ResolveHResultSymbol(0x87D1041C));
            // A catalog load failure is decoration lost, never an event lost.
            Assert.Equal("WU_E_UNKNOWN", WindowsUpdateTracker.ResolveHResultSymbol(0x80240022, _ => throw new InvalidOperationException("catalog")));
            Assert.Equal("S_OK", WindowsUpdateTracker.ResolveHResultSymbol(0, _ => throw new InvalidOperationException("catalog")));
        }

        // ---------------------------------------------------------------------------
        // Blind-spot channel census (session 7443317c)
        // ---------------------------------------------------------------------------

        private WindowsUpdateTracker BuildCensusTracker(
            bool censusEnabled,
            bool osBuildChanged,
            Func<string, int, WindowsUpdateTracker.ChannelCensusScan>? scanner = null)
        {
            var post = new InformationalEventPost(_sink, new VirtualClock(At));
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            var tracker = new WindowsUpdateTracker(
                sessionId: "sess-wu",
                tenantId: "tenant-wu",
                post: post,
                logger: logger,
                backfillEnabled: false,
                stateDirectory: null,
                channelCensusEnabled: censusEnabled,
                osBuildChangedProvider: () => osBuildChanged);
            tracker.CensusScannerOverride = scanner ?? ((channel, lookback) =>
                new WindowsUpdateTracker.ChannelCensusScan(
                    channel == WindowsUpdateTracker.Channel
                        ? new Dictionary<int, int> { { 25, 3 }, { 21, 1 } }
                        : new Dictionary<int, int> { { 200, 2 } },
                    truncated: false));
            return tracker;
        }

        [Fact]
        public void Census_BuildChanged_ZeroTargetedEmitted_EmitsHistogramEvent()
        {
            var tracker = BuildCensusTracker(censusEnabled: true, osBuildChanged: true);

            tracker.EmitChannelCensusIfBlind();

            var s = Assert.Single(ByType(Constants.EventTypes.WindowsUpdateChannelCensus));
            Assert.Equal("Debug", s.Payload![SignalPayloadKeys.Severity]);
            var data = Data(s);
            Assert.Equal("21=1,25=3", data["wuClientCensus"]);
            Assert.Equal("200=2", data["updateOrchestratorCensus"]);
            Assert.Equal("25,26,31,41", data["targetedEventIds"]);
            Assert.False(data.ContainsKey("censusTruncated"));
        }

        [Fact]
        public void Census_ListsTheTargetedIdsOfEveryChannel()
        {
            // The host runs the census on the Operational instance; the evidence must still say
            // the System-log install IDs were watched too.
            var tracker = BuildCensusTracker(censusEnabled: true, osBuildChanged: true);

            tracker.EmitChannelCensusIfBlind(otherChannelsEmitted: 0, otherTargetedEventIds: new[] { 44, 19, 20, 43 });

            var s = Assert.Single(ByType(Constants.EventTypes.WindowsUpdateChannelCensus));
            Assert.Equal("19,20,25,26,31,41,43,44", Data(s)["targetedEventIds"]);
        }

        [Fact]
        public void Census_BuildUnchanged_EmitsNothing()
        {
            var tracker = BuildCensusTracker(censusEnabled: true, osBuildChanged: false);

            tracker.EmitChannelCensusIfBlind();

            Assert.Empty(ByType(Constants.EventTypes.WindowsUpdateChannelCensus));
        }

        [Fact]
        public void Census_TargetedEventsWereEmittedThisRun_EmitsNothing()
        {
            // The watcher captured activity through its normal path — no blind spot to report.
            // Any class counts: a counted Store update proves the channel is read as well.
            var tracker = BuildCensusTracker(censusEnabled: true, osBuildChanged: true);
            Emit(tracker, WindowsUpdateTracker.EventId_Downloaded, updateTitle: StoreApp, recordId: 9);

            tracker.EmitChannelCensusIfBlind();

            Assert.Empty(ByType(Constants.EventTypes.WindowsUpdateChannelCensus));
        }

        [Fact]
        public void Census_AnotherChannelEmitted_EmitsNothing()
        {
            // The System-log watcher saw the install — the update was not missed.
            var tracker = BuildCensusTracker(censusEnabled: true, osBuildChanged: true);

            tracker.EmitChannelCensusIfBlind(otherChannelsEmitted: 1);

            Assert.Empty(ByType(Constants.EventTypes.WindowsUpdateChannelCensus));
        }

        [Fact]
        public void Census_DisabledByConfig_EmitsNothing()
        {
            var tracker = BuildCensusTracker(censusEnabled: false, osBuildChanged: true);

            tracker.EmitChannelCensusIfBlind();

            Assert.Empty(ByType(Constants.EventTypes.WindowsUpdateChannelCensus));
        }

        [Fact]
        public void Census_TruncatedScan_SaysSoInPayload()
        {
            // No silent caps: a capped scan must be visible in the evidence.
            var tracker = BuildCensusTracker(censusEnabled: true, osBuildChanged: true,
                scanner: (channel, lookback) => new WindowsUpdateTracker.ChannelCensusScan(
                    new Dictionary<int, int> { { 19, WindowsUpdateTracker.CensusRecordCap } }, truncated: true));

            tracker.EmitChannelCensusIfBlind();

            var s = Assert.Single(ByType(Constants.EventTypes.WindowsUpdateChannelCensus));
            Assert.Equal(true, Data(s)["censusTruncated"]);
        }

        [Fact]
        public void Census_ScannerThrows_IsSwallowed_NeverBreaksTheWatcher()
        {
            var tracker = BuildCensusTracker(censusEnabled: true, osBuildChanged: true,
                scanner: (channel, lookback) => throw new InvalidOperationException("boom"));

            tracker.EmitChannelCensusIfBlind();

            Assert.Empty(ByType(Constants.EventTypes.WindowsUpdateChannelCensus));
        }

        [Fact]
        public void FormatHistogram_OrdersByEventId()
        {
            Assert.Equal("19=2,25=1,43=7",
                WindowsUpdateTracker.FormatHistogram(new Dictionary<int, int> { { 43, 7 }, { 19, 2 }, { 25, 1 } }));
            Assert.Equal("", WindowsUpdateTracker.FormatHistogram(new Dictionary<int, int>()));
        }
    }
}

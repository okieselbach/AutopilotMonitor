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

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// WindowsUpdateTracker — surfaces quality/cumulative update activity during OOBE from the
    /// WindowsUpdateClient/Operational channel. Drives the primitive <see cref="WindowsUpdateTracker.ProcessEvent"/>
    /// test seam (no real EventRecord, which is abstract + Windows-only), mirroring the
    /// ModernDeploymentTracker test pattern.
    /// </summary>
    public sealed class WindowsUpdateTrackerTests : IDisposable
    {
        private static readonly DateTime At = new DateTime(2026, 7, 8, 10, 15, 0, DateTimeKind.Utc);

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly FakeSignalIngressSink _sink;
        private readonly WindowsUpdateTracker _tracker;
        private long _nextRecordId = 1;

        public WindowsUpdateTrackerTests()
        {
            _sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(_sink, new VirtualClock(At));
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            _tracker = new WindowsUpdateTracker(
                sessionId: "sess-wu",
                tenantId: "tenant-wu",
                post: post,
                logger: logger,
                backfillEnabled: false,
                stateDirectory: null); // in-memory dedup for most tests
        }

        public void Dispose() => _tmp.Dispose();

        private void Emit(int eventId, string? errorCode = null, string? updateTitle = "Cumulative Update KB5099999", long? recordId = null)
        {
            _tracker.ProcessEvent(
                eventId: eventId,
                level: 4,
                recordId: recordId ?? _nextRecordId++,
                timeCreatedUtc: At,
                updateTitle: updateTitle,
                updateGuid: "{8b1c8726-1111-2222-3333-444455556666}",
                updateRevisionNumber: "200",
                errorCode: errorCode,
                formattedDescription: null,
                isBackfill: false);
        }

        private IReadOnlyList<FakeSignalIngressSink.PostedSignal> ByType(string eventType) =>
            _sink.Posted.Where(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == eventType).ToList();

        private static IReadOnlyDictionary<string, object> Data(FakeSignalIngressSink.PostedSignal s) =>
            (IReadOnlyDictionary<string, object>)s.TypedPayload!;

        [Fact]
        public void FailureEvent_EmitsWindowsUpdateFailed_WithDecodedHResult_AndImmediateUpload()
        {
            Emit(WindowsUpdateTracker.EventId_InstallFailure, errorCode: "0x80240022");

            var failed = ByType(Constants.EventTypes.WindowsUpdateFailed);
            var s = Assert.Single(failed);
            Assert.Equal("Error", s.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("true", s.Payload![SignalPayloadKeys.ImmediateUpload]);

            var data = Data(s);
            Assert.Equal("0x80240022", data["hresult"]);
            Assert.Equal("WU_E_ALL_UPDATES_FAILED", data["hresultSymbol"]);
            Assert.Equal("Cumulative Update KB5099999", data["updateTitle"]);
            Assert.Equal(20, data["wuEventId"]);
        }

        [Fact]
        public void SuccessEvent_EmitsWindowsUpdateSucceeded_InfoSeverity()
        {
            Emit(WindowsUpdateTracker.EventId_InstallSuccess);

            var ok = ByType(Constants.EventTypes.WindowsUpdateSucceeded);
            var s = Assert.Single(ok);
            Assert.Equal("Info", s.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("false", s.Payload![SignalPayloadKeys.ImmediateUpload]);
        }

        [Fact]
        public void StartedAndDownloadEvents_EmitDebugStarted()
        {
            Emit(WindowsUpdateTracker.EventId_InstallStarted);
            Emit(WindowsUpdateTracker.EventId_DownloadStarted);

            var started = ByType(Constants.EventTypes.WindowsUpdateStarted);
            Assert.Equal(2, started.Count);
            Assert.All(started, s => Assert.Equal("Debug", s.Payload![SignalPayloadKeys.Severity]));
        }

        [Fact]
        public void SameRecordId_ProcessedTwice_EmitsOnce()
        {
            Emit(WindowsUpdateTracker.EventId_InstallSuccess, recordId: 42);
            Emit(WindowsUpdateTracker.EventId_InstallSuccess, recordId: 42);

            Assert.Single(ByType(Constants.EventTypes.WindowsUpdateSucceeded));
        }

        [Fact]
        public void BackfillEventWithLowerRecordId_AfterLiveEvent_IsStillEmitted()
        {
            // Regression (Codex P1): the live watcher is armed BEFORE backfill runs, so a live event
            // with a higher RecordId can be processed first. An older, never-emitted backfill event
            // (lower RecordId) must NOT be suppressed as "already processed" — these early pre-agent
            // OOBE updates are the whole point of the feature. A high-water-mark dedup dropped them.
            Emit(WindowsUpdateTracker.EventId_InstallSuccess, recordId: 100); // live, high RecordId
            Emit(WindowsUpdateTracker.EventId_InstallSuccess, recordId: 50);  // backfill, older, never seen

            Assert.Equal(2, ByType(Constants.EventTypes.WindowsUpdateSucceeded).Count);
        }

        [Fact]
        public void BackfilledEvent_UsesEventTimeForTimelineTimestamp()
        {
            // Codex P1: a backfilled pre-agent update must land on the timeline at the WU event's own
            // time, not at agent start (the ctor's default UtcNow). InformationalEventPost forwards
            // EnrollmentEvent.Timestamp as the signal's OccurredAtUtc, which drives the timeline entry.
            var eventTime = new DateTime(2026, 7, 8, 7, 5, 0, DateTimeKind.Utc); // well before `At`
            _tracker.ProcessEvent(
                eventId: WindowsUpdateTracker.EventId_InstallFailure,
                level: 4,
                recordId: 7,
                timeCreatedUtc: eventTime,
                updateTitle: "KB5099999",
                updateGuid: null,
                updateRevisionNumber: null,
                errorCode: "0x80240022",
                formattedDescription: null,
                isBackfill: true);

            var s = Assert.Single(ByType(Constants.EventTypes.WindowsUpdateFailed));
            Assert.Equal(eventTime, s.OccurredAtUtc);
        }

        [Fact]
        public void NegativeRecordId_IsNeverDeduped()
        {
            Emit(WindowsUpdateTracker.EventId_InstallSuccess, recordId: -1);
            Emit(WindowsUpdateTracker.EventId_InstallSuccess, recordId: -1);

            Assert.Equal(2, ByType(Constants.EventTypes.WindowsUpdateSucceeded).Count);
        }

        [Fact]
        public void Watermark_PersistsAcrossTrackerInstances()
        {
            var post = new InformationalEventPost(_sink, new VirtualClock(At));
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);

            var first = new WindowsUpdateTracker("s", "t", post, logger, backfillEnabled: false, stateDirectory: _tmp.Path);
            first.ProcessEvent(WindowsUpdateTracker.EventId_InstallSuccess, 4, recordId: 500, timeCreatedUtc: At,
                updateTitle: "KB1", updateGuid: null, updateRevisionNumber: null, errorCode: null,
                formattedDescription: null, isBackfill: false);

            // A fresh tracker (simulating an agent restart) must load the watermark and skip the
            // already-emitted record when the OOBE backfill re-reads it.
            var second = new WindowsUpdateTracker("s", "t", post, logger, backfillEnabled: false, stateDirectory: _tmp.Path);
            second.LoadWatermark();
            second.ProcessEvent(WindowsUpdateTracker.EventId_InstallSuccess, 4, recordId: 500, timeCreatedUtc: At,
                updateTitle: "KB1", updateGuid: null, updateRevisionNumber: null, errorCode: null,
                formattedDescription: null, isBackfill: true);

            Assert.Single(ByType(Constants.EventTypes.WindowsUpdateSucceeded));
        }

        [Fact]
        public void BuildXPath_ContainsAllTargetedIds_Ordered()
        {
            var xpath = WindowsUpdateTracker.BuildXPath(new HashSet<int> { 44, 19, 20, 43 });
            Assert.Equal("*[System[(EventID=19 or EventID=20 or EventID=43 or EventID=44)]]", xpath);
        }

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
        public void DecodeHResult_KnownAndUnknown()
        {
            Assert.Equal("WU_E_ALL_UPDATES_FAILED", WindowsUpdateTracker.DecodeHResult(0x80240022));
            Assert.Equal("CBS_E_INSTALLERS_FAILED", WindowsUpdateTracker.DecodeHResult(0x800F0922));
            Assert.Equal("S_OK", WindowsUpdateTracker.DecodeHResult(0x00000000));
            Assert.Equal("WU_E_UNKNOWN", WindowsUpdateTracker.DecodeHResult(0x8888DEAD));
        }
    }
}

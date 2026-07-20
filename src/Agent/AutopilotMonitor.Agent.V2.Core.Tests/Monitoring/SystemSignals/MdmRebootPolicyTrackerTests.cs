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
    /// MdmRebootPolicyTracker — attributes the "unexpected reboot + second sign-in" pattern to the
    /// device-assigned policy URIs that forced the coalesced reboot (DM-Enterprise EventID 2800).
    /// Drives the primitive <see cref="MdmRebootPolicyTracker.ProcessEvent"/> test seam (no real
    /// EventRecord, which is abstract + Windows-only), mirroring the WindowsUpdateTracker pattern.
    /// The registry probe is always overridden — tests never touch the live registry.
    /// </summary>
    public sealed class MdmRebootPolicyTrackerTests : IDisposable
    {
        private static readonly DateTime At = new DateTime(2026, 7, 20, 9, 30, 0, DateTimeKind.Utc);
        private const string SampleUri = "./Device/Vendor/MSFT/Policy/Config/DeviceGuard/EnableVirtualizationBasedSecurity";

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly FakeSignalIngressSink _sink;
        private readonly MdmRebootPolicyTracker _tracker;
        private long _nextRecordId = 1;

        public MdmRebootPolicyTrackerTests()
        {
            _sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(_sink, new VirtualClock(At));
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            _tracker = new MdmRebootPolicyTracker(
                sessionId: "sess-mdm",
                tenantId: "tenant-mdm",
                post: post,
                logger: logger,
                backfillEnabled: false,
                stateDirectory: null); // in-memory dedup for most tests
            _tracker.RebootRequiredFlagProbe = () => null; // registry untouched by default
        }

        public void Dispose() => _tmp.Dispose();

        private void Emit(string? rebootUri = SampleUri, string? description = null, long? recordId = null, bool isBackfill = false, DateTime? timeCreatedUtc = null)
        {
            _tracker.ProcessEvent(
                eventId: MdmRebootPolicyTracker.EventId_PolicyRebootRequired,
                recordId: recordId ?? _nextRecordId++,
                timeCreatedUtc: timeCreatedUtc ?? At,
                rebootUri: rebootUri,
                formattedDescription: description,
                isBackfill: isBackfill);
        }

        private IReadOnlyList<FakeSignalIngressSink.PostedSignal> Emitted() =>
            _sink.Posted.Where(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == Constants.EventTypes.MdmPolicyRebootRequired).ToList();

        private static IReadOnlyDictionary<string, object> Data(FakeSignalIngressSink.PostedSignal s) =>
            (IReadOnlyDictionary<string, object>)s.TypedPayload!;

        [Fact]
        public void ProcessEvent_EmitsWarning_WithUri_AndImmediateUpload()
        {
            Emit();

            var s = Assert.Single(Emitted());
            Assert.Equal("Warning", s.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("true", s.Payload![SignalPayloadKeys.ImmediateUpload]);

            var data = Data(s);
            Assert.Equal(SampleUri, data["rebootUri"]);
            Assert.Equal(MdmRebootPolicyTracker.EventId_PolicyRebootRequired, data["windowsEventId"]);
            Assert.Equal(MdmRebootPolicyTracker.Channel, data["eventLogChannel"]);
        }

        [Fact]
        public void ProcessEvent_NoUri_StillEmits_WithoutRebootUriField()
        {
            // Tolerant contract: an unparsed URI must stay timeline-visible (with the captured
            // description telling us how to fix the extraction) — the analyze rule keys on
            // "rebootUri exists" and simply stays silent.
            Emit(rebootUri: null, description: "Some unexpected 2800 wording");

            var s = Assert.Single(Emitted());
            var data = Data(s);
            Assert.False(data.ContainsKey("rebootUri"));
            Assert.Equal("Some unexpected 2800 wording", data["description"]);
        }

        [Fact]
        public void BackfilledEvent_UsesEventTimeForTimelineTimestamp_AndMarksBackfilled()
        {
            var eventTime = At.AddMinutes(-25); // policy applied well before agent start
            Emit(isBackfill: true, timeCreatedUtc: eventTime);

            var s = Assert.Single(Emitted());
            Assert.Equal(eventTime, s.OccurredAtUtc);
            Assert.Equal(true, Data(s)["backfilled"]);
        }

        [Fact]
        public void SameRecordId_ProcessedTwice_EmitsOnce()
        {
            Emit(recordId: 42);
            Emit(recordId: 42);

            Assert.Single(Emitted());
        }

        [Fact]
        public void BackfillEventWithLowerRecordId_AfterLiveEvent_IsStillEmitted()
        {
            // Same contract as WindowsUpdateTracker: the live watcher is armed BEFORE the backfill
            // scan, so a live record with a higher RecordId can arrive first — the older,
            // never-emitted backfill record must not be suppressed by a high-water mark.
            Emit(recordId: 100);
            Emit(recordId: 50, isBackfill: true);

            Assert.Equal(2, Emitted().Count);
        }

        [Fact]
        public void NegativeRecordId_IsNeverDeduped()
        {
            Emit(recordId: -1);
            Emit(recordId: -1);

            Assert.Equal(2, Emitted().Count);
        }

        [Fact]
        public void Watermark_PersistsAcrossTrackerInstances()
        {
            // The coalesced reboot restarts the agent and the post-reboot backfill re-reads the
            // same 2800 records — the persisted watermark must make that re-read a no-op.
            var post = new InformationalEventPost(_sink, new VirtualClock(At));
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);

            var first = new MdmRebootPolicyTracker("s", "t", post, logger, backfillEnabled: false, stateDirectory: _tmp.Path);
            first.RebootRequiredFlagProbe = () => null;
            first.ProcessEvent(MdmRebootPolicyTracker.EventId_PolicyRebootRequired, recordId: 500,
                timeCreatedUtc: At, rebootUri: SampleUri, formattedDescription: null, isBackfill: false);

            var second = new MdmRebootPolicyTracker("s", "t", post, logger, backfillEnabled: false, stateDirectory: _tmp.Path);
            second.RebootRequiredFlagProbe = () => null;
            second.LoadWatermark();
            second.ProcessEvent(MdmRebootPolicyTracker.EventId_PolicyRebootRequired, recordId: 500,
                timeCreatedUtc: At, rebootUri: SampleUri, formattedDescription: null, isBackfill: true);

            Assert.Single(Emitted());
        }

        [Fact]
        public void RebootRequiredFlagProbe_True_AddsField_Null_OmitsIt()
        {
            _tracker.RebootRequiredFlagProbe = () => true;
            Emit(recordId: 1);
            _tracker.RebootRequiredFlagProbe = () => null;
            Emit(recordId: 2);

            var emitted = Emitted();
            Assert.Equal(2, emitted.Count);
            Assert.Equal(true, Data(emitted[0])["omadmRebootRequiredFlag"]);
            Assert.False(Data(emitted[1]).ContainsKey("omadmRebootRequiredFlag"));
        }

        [Fact]
        public void RebootRequiredFlagProbe_Throwing_IsFailSoft()
        {
            _tracker.RebootRequiredFlagProbe = () => throw new InvalidOperationException("boom");
            Emit();

            var s = Assert.Single(Emitted());
            Assert.False(Data(s).ContainsKey("omadmRebootRequiredFlag"));
        }

        [Fact]
        public void BuildXPath_Targets2800()
        {
            Assert.Equal("*[System[(EventID=2800)]]", MdmRebootPolicyTracker.BuildXPath());
        }

        // ------------------------------------------------------------------ URI extraction

        [Fact]
        public void ExtractRebootUri_PrefersEventDataValue_OverDescription()
        {
            var eventData = new Dictionary<string, string> { ["Data0"] = " " + SampleUri + " " };
            var uri = MdmRebootPolicyTracker.ExtractRebootUri(eventData, "unrelated ./Other/Uri text");
            Assert.Equal(SampleUri, uri);
        }

        [Fact]
        public void ExtractRebootUri_RegexFallback_FromDescription()
        {
            var uri = MdmRebootPolicyTracker.ExtractRebootUri(
                new Dictionary<string, string> { ["Data0"] = "not a uri" },
                $"The following URI has triggered a reboot: {SampleUri}.");
            Assert.Equal(SampleUri, uri);
        }

        [Fact]
        public void ExtractRebootUri_ReturnsNull_WhenNothingUriLike()
        {
            Assert.Null(MdmRebootPolicyTracker.ExtractRebootUri(
                new Dictionary<string, string> { ["Data0"] = "plain" }, "no uri here"));
            Assert.Null(MdmRebootPolicyTracker.ExtractRebootUri(null, null));
        }

        [Fact]
        public void ParseEventData_CollectsNamedAndPositionalData()
        {
            const string xml =
                "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>" +
                "<System><EventID>2800</EventID></System>" +
                "<EventData><Data Name='Uri'>./Device/Vendor/MSFT/X</Data><Data>positional</Data></EventData>" +
                "</Event>";

            var parsed = MdmRebootPolicyTracker.ParseEventData(xml);
            Assert.Equal("./Device/Vendor/MSFT/X", parsed["Uri"]);
            Assert.Equal("positional", parsed["Data0"]);
        }
    }
}

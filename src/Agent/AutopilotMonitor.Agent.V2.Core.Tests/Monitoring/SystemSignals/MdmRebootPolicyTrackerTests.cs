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
    /// MdmRebootPolicyTracker — aggregates DM-Enterprise EventID 2800 bursts into ONE neutral
    /// <c>mdm_policy_reboot_required</c> event (session b2e890c1 rework: per-record events were
    /// spam, and the raw event must not CLAIM a reboot — ANALYZE-ESP-005 does, gated on an
    /// observed system_reboot_detected). Drives the primitive ProcessEvent seam + explicit
    /// FlushPending (debounce 0 = manual-flush mode; no timers, no SerialThreading needed).
    /// The registry probe is always overridden — tests never touch the live registry.
    /// </summary>
    public sealed class MdmRebootPolicyTrackerTests : IDisposable
    {
        private static readonly DateTime At = new DateTime(2026, 7, 20, 16, 20, 57, DateTimeKind.Utc);
        private const string UriSvchost = "./Device/Vendor/MSFT/Policy/Config/ServiceControlManager/SvchostProcessMitigation";
        private const string UriSystemGuard = "./Device/Vendor/MSFT/Policy/Config/DeviceGuard/ConfigureSystemGuardLaunch";

        // Verified real 2800 description shape (session b2e890c1, 2026-07-20).
        private const string RealDescription =
            "The following URI has triggered a reboot: (./Device/Vendor/MSFT/Policy/Config/ServiceControlManager/SvchostProcessMitigation).";

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly FakeSignalIngressSink _sink;
        private readonly MdmRebootPolicyTracker _tracker;
        private long _nextRecordId = 1;

        public MdmRebootPolicyTrackerTests()
        {
            _sink = new FakeSignalIngressSink();
            _tracker = MakeTracker(stateDirectory: null);
        }

        public void Dispose() => _tmp.Dispose();

        private MdmRebootPolicyTracker MakeTracker(string? stateDirectory)
        {
            var post = new InformationalEventPost(_sink, new VirtualClock(At));
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            var tracker = new MdmRebootPolicyTracker(
                sessionId: "sess-mdm",
                tenantId: "tenant-mdm",
                post: post,
                logger: logger,
                backfillEnabled: false,
                stateDirectory: stateDirectory,
                debounceMilliseconds: 0); // manual-flush mode — tests call FlushPending explicitly
            tracker.RebootRequiredFlagProbe = () => null; // registry untouched by default
            return tracker;
        }

        private void Buffer(string? rebootUri, long? recordId = null, bool isBackfill = false, DateTime? timeCreatedUtc = null, string? description = null)
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
        public void Burst_FlushesAsOneAggregatedEvent_InfoSeverity_ImmediateUpload()
        {
            Buffer(UriSvchost, timeCreatedUtc: At);
            Buffer(UriSystemGuard, timeCreatedUtc: At.AddMilliseconds(120));
            _tracker.FlushPending();

            var s = Assert.Single(Emitted());
            Assert.Equal("Info", s.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("true", s.Payload![SignalPayloadKeys.ImmediateUpload]);

            var data = Data(s);
            Assert.Equal(2, data["uriCount"]);
            Assert.Equal(2, data["recordCount"]);
            Assert.Equal(UriSvchost, data["firstRebootUri"]);
            var uris = Assert.IsType<List<string>>(data["rebootUris"]);
            Assert.Equal(2, uris.Count);
            Assert.Contains(UriSvchost, uris);
            Assert.Contains(UriSystemGuard, uris);
        }

        [Fact]
        public void Message_IsNeutralObservation_NoRebootClaim()
        {
            // Session b2e890c1: flags raised during AccountSetup have no effect — the raw event
            // must state the observation only, never a future consequence.
            Buffer(UriSvchost);
            _tracker.FlushPending();

            var s = Assert.Single(Emitted());
            var message = s.Payload![SignalPayloadKeys.Message];
            Assert.Contains("flagged", message);
            Assert.DoesNotContain("will restart", message);
            Assert.DoesNotContain("second sign-in", message);
        }

        [Fact]
        public void FlushPending_Empty_EmitsNothing()
        {
            _tracker.FlushPending();
            Assert.Empty(Emitted());
        }

        [Fact]
        public void AggregateTimestamp_IsEarliestRecordTime()
        {
            var early = At.AddMinutes(-30);
            Buffer(UriSystemGuard, timeCreatedUtc: At, isBackfill: true);
            Buffer(UriSvchost, timeCreatedUtc: early, isBackfill: true);
            _tracker.FlushPending();

            var s = Assert.Single(Emitted());
            Assert.Equal(early, s.OccurredAtUtc);
            Assert.Equal(true, Data(s)["backfilled"]);
            // firstRebootUri follows record-time order, not buffer order.
            Assert.Equal(UriSvchost, Data(s)["firstRebootUri"]);
        }

        [Fact]
        public void Backfilled_False_WhenAnyRecordIsLive()
        {
            Buffer(UriSvchost, isBackfill: true);
            Buffer(UriSystemGuard, isBackfill: false);
            _tracker.FlushPending();

            Assert.Equal(false, Data(Assert.Single(Emitted()))["backfilled"]);
        }

        [Fact]
        public void DuplicateUris_AreDistinctInAggregate()
        {
            Buffer(UriSvchost);
            Buffer(UriSvchost);
            _tracker.FlushPending();

            var data = Data(Assert.Single(Emitted()));
            Assert.Equal(1, data["uriCount"]);
            Assert.Equal(2, data["recordCount"]);
        }

        [Fact]
        public void UnparsedRecords_CountedWithSampleDescription_StillEmitted()
        {
            Buffer(rebootUri: null, description: "Some unexpected 2800 wording");
            _tracker.FlushPending();

            var s = Assert.Single(Emitted());
            var data = Data(s);
            Assert.False(data.ContainsKey("rebootUris"));
            Assert.False(data.ContainsKey("firstRebootUri"));
            Assert.Equal(0, data["uriCount"]);
            Assert.Equal(1, data["unparsedCount"]);
            Assert.Equal("Some unexpected 2800 wording", data["sampleDescription"]);
        }

        [Fact]
        public void SameRecordId_BufferedTwice_CountsOnce()
        {
            Buffer(UriSvchost, recordId: 42);
            Buffer(UriSvchost, recordId: 42);
            _tracker.FlushPending();

            Assert.Equal(1, Data(Assert.Single(Emitted()))["recordCount"]);
        }

        [Fact]
        public void RecordAfterFlush_GoesIntoNextAggregate()
        {
            Buffer(UriSvchost, recordId: 10);
            _tracker.FlushPending();
            Buffer(UriSystemGuard, recordId: 11);
            _tracker.FlushPending();

            Assert.Equal(2, Emitted().Count);
        }

        [Fact]
        public void BackfillRecordWithLowerRecordId_AfterLiveRecord_IsStillBuffered()
        {
            // Live watcher is armed BEFORE the backfill scan — an older, never-seen backfill
            // record must not be suppressed by a high-water mark.
            Buffer(UriSvchost, recordId: 100);
            Buffer(UriSystemGuard, recordId: 50, isBackfill: true);
            _tracker.FlushPending();

            Assert.Equal(2, Data(Assert.Single(Emitted()))["recordCount"]);
        }

        [Fact]
        public void NegativeRecordId_IsNeverDeduped()
        {
            Buffer(UriSvchost, recordId: -1);
            Buffer(UriSvchost, recordId: -1);
            _tracker.FlushPending();

            Assert.Equal(2, Data(Assert.Single(Emitted()))["recordCount"]);
        }

        [Fact]
        public void Watermark_PersistedAtFlush_SkipsReReadAcrossInstances()
        {
            // The coalesced reboot restarts the agent; the post-reboot backfill re-reads the same
            // records — a FLUSHED record must be skipped by the next instance.
            var first = MakeTracker(stateDirectory: _tmp.Path);
            first.ProcessEvent(MdmRebootPolicyTracker.EventId_PolicyRebootRequired, recordId: 500,
                timeCreatedUtc: At, rebootUri: UriSvchost, formattedDescription: null, isBackfill: false);
            first.FlushPending();

            var second = MakeTracker(stateDirectory: _tmp.Path);
            second.LoadWatermark();
            second.ProcessEvent(MdmRebootPolicyTracker.EventId_PolicyRebootRequired, recordId: 500,
                timeCreatedUtc: At, rebootUri: UriSvchost, formattedDescription: null, isBackfill: true);
            second.FlushPending();

            Assert.Single(Emitted());
        }

        [Fact]
        public void Watermark_NotPersisted_WhenBufferedButNeverFlushed()
        {
            // Process killed by the very reboot the records announce: nothing was flushed, so the
            // next instance must re-read and emit the records.
            var first = MakeTracker(stateDirectory: _tmp.Path);
            first.ProcessEvent(MdmRebootPolicyTracker.EventId_PolicyRebootRequired, recordId: 500,
                timeCreatedUtc: At, rebootUri: UriSvchost, formattedDescription: null, isBackfill: false);
            // no FlushPending — simulated reboot kill

            var second = MakeTracker(stateDirectory: _tmp.Path);
            second.LoadWatermark();
            second.ProcessEvent(MdmRebootPolicyTracker.EventId_PolicyRebootRequired, recordId: 500,
                timeCreatedUtc: At, rebootUri: UriSvchost, formattedDescription: null, isBackfill: true);
            second.FlushPending();

            var s = Assert.Single(Emitted());
            Assert.Equal(true, Data(s)["backfilled"]);
        }

        [Fact]
        public void RebootRequiredFlagProbe_True_AddsField_Null_OmitsIt()
        {
            _tracker.RebootRequiredFlagProbe = () => true;
            Buffer(UriSvchost, recordId: 1);
            _tracker.FlushPending();

            _tracker.RebootRequiredFlagProbe = () => null;
            Buffer(UriSystemGuard, recordId: 2);
            _tracker.FlushPending();

            var emitted = Emitted();
            Assert.Equal(2, emitted.Count);
            Assert.Equal(true, Data(emitted[0])["omadmRebootRequiredFlag"]);
            Assert.False(Data(emitted[1]).ContainsKey("omadmRebootRequiredFlag"));
        }

        [Fact]
        public void RebootRequiredFlagProbe_Throwing_IsFailSoft()
        {
            _tracker.RebootRequiredFlagProbe = () => throw new InvalidOperationException("boom");
            Buffer(UriSvchost);
            _tracker.FlushPending();

            Assert.False(Data(Assert.Single(Emitted())).ContainsKey("omadmRebootRequiredFlag"));
        }

        [Fact]
        public void BuildXPath_Targets2800()
        {
            Assert.Equal("*[System[(EventID=2800)]]", MdmRebootPolicyTracker.BuildXPath());
        }

        // ------------------------------------------------------------------ URI extraction

        [Fact]
        public void ExtractRebootUri_FromVerifiedRealDescription()
        {
            // Real 2800 text wraps the URI in parentheses and ends with a period — both must be
            // excluded from the extracted URI.
            var uri = MdmRebootPolicyTracker.ExtractRebootUri(null, RealDescription);
            Assert.Equal(UriSvchost, uri);
        }

        [Fact]
        public void ExtractRebootUri_PrefersEventDataValue_OverDescription()
        {
            var eventData = new Dictionary<string, string> { ["Data0"] = " " + UriSystemGuard + " " };
            var uri = MdmRebootPolicyTracker.ExtractRebootUri(eventData, RealDescription);
            Assert.Equal(UriSystemGuard, uri);
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

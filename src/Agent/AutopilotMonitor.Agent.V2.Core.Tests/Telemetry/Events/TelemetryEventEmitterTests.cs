using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Events;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Telemetry.Events
{
    public sealed class TelemetryEventEmitterTests
    {
        private static readonly DateTime At = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private static TelemetryEventEmitter Build(
            FakeTelemetryTransport transport,
            out EventSequenceCounter counter,
            string sessionId = "sess-1",
            string tenantId = "tenant-1",
            Func<bool>? traceEventsEnabled = null)
        {
            var tmp = new TempDirectory();
            counter = new EventSequenceCounter(new EventSequencePersistence(tmp.File("seq.json")));
            return new TelemetryEventEmitter(transport, counter, sessionId, tenantId, traceEventsEnabled);
        }

        private static EnrollmentEvent NewTraceEvent(string eventType = "outbound_ip") =>
            new EnrollmentEvent
            {
                EventType = eventType,
                Severity = EventSeverity.Trace,
                Source = "test",
                Timestamp = At,
                Phase = EnrollmentPhase.Unknown,
                Message = "trace message",
            };

        private static EnrollmentEvent NewTestEvent(string eventType = "enrollment_complete") =>
            new EnrollmentEvent
            {
                EventType = eventType,
                Severity = EventSeverity.Info,
                Source = "test",
                Timestamp = At,
                Phase = EnrollmentPhase.Unknown,
                Message = "test message",
            };

        [Fact]
        public void Emit_assigns_Sequence_RowKey_and_routes_through_transport()
        {
            var transport = new FakeTelemetryTransport();
            var emitter = Build(transport, out _);

            var evt = NewTestEvent();
            var item = emitter.Emit(evt);

            Assert.Equal(1, evt.Sequence);
            Assert.Equal("20260420100000000_0000000001", evt.RowKey);
            Assert.Equal("sess-1", evt.SessionId);
            Assert.Equal("tenant-1", evt.TenantId);

            Assert.NotNull(item);
            Assert.Equal(TelemetryItemKind.Event, item.Kind);
            Assert.Equal("tenant-1_sess-1", item.PartitionKey);
            Assert.Equal(evt.RowKey, item.RowKey);
            Assert.Equal(1, transport.EnqueueCount);
        }

        [Fact]
        public void Emit_Sequences_are_monotonic_across_calls()
        {
            var transport = new FakeTelemetryTransport();
            var emitter = Build(transport, out _);

            for (int i = 0; i < 5; i++)
            {
                emitter.Emit(NewTestEvent($"evt-{i}"));
            }

            Assert.Equal(5, transport.EnqueueCount);
            for (int i = 0; i < 5; i++)
            {
                var parsed = JObject.Parse(transport.Enqueued[i].PayloadJson);
                Assert.Equal(i + 1, (long)parsed["Sequence"]!);
            }
        }

        [Fact]
        public void PayloadJson_matches_legacy_wire_format_fields()
        {
            // Newtonsoft JsonConvert.SerializeObject — PascalCase from property names, STJ attributes
            // ignored. Backend ingestor uses PropertyNameCaseInsensitive so PascalCase is accepted.
            var transport = new FakeTelemetryTransport();
            var emitter = Build(transport, out _);

            emitter.Emit(NewTestEvent("whiteglove_complete"));

            var parsed = JObject.Parse(transport.Enqueued[0].PayloadJson);
            Assert.Equal("whiteglove_complete", (string?)parsed["EventType"]);
            Assert.Equal("sess-1", (string?)parsed["SessionId"]);
            Assert.Equal("tenant-1", (string?)parsed["TenantId"]);
            Assert.Equal(1, (long)parsed["Sequence"]!);
            Assert.False(string.IsNullOrEmpty((string?)parsed["EventId"]));
            Assert.False(string.IsNullOrEmpty((string?)parsed["RowKey"]));
        }

        [Fact]
        public void Emit_defaults_SessionId_TenantId_when_caller_leaves_empty()
        {
            var transport = new FakeTelemetryTransport();
            var emitter = Build(transport, out _, sessionId: "S-CTOR", tenantId: "T-CTOR");

            var evt = NewTestEvent();
            evt.SessionId = null;
            evt.TenantId = null;
            emitter.Emit(evt);

            Assert.Equal("S-CTOR", evt.SessionId);
            Assert.Equal("T-CTOR", evt.TenantId);
            Assert.Equal("T-CTOR_S-CTOR", transport.Enqueued[0].PartitionKey);
        }

        [Fact]
        public void Emit_preserves_caller_SessionId_if_set()
        {
            var transport = new FakeTelemetryTransport();
            var emitter = Build(transport, out _, sessionId: "S-CTOR", tenantId: "T-CTOR");

            var evt = NewTestEvent();
            evt.SessionId = "S-EXPLICIT";
            evt.TenantId = "T-EXPLICIT";
            emitter.Emit(evt);

            Assert.Equal("S-EXPLICIT", evt.SessionId);
            Assert.Equal("T-EXPLICIT", evt.TenantId);
            // PartitionKey is determined by the ctor values (session-scoping). Caller-explicit
            // overrides only the payload fields — the transport bucket stays stable.
            Assert.Equal("T-CTOR_S-CTOR", transport.Enqueued[0].PartitionKey);
        }

        [Fact]
        public void RequiresImmediateFlush_forwards_ImmediateUpload_flag()
        {
            var transport = new FakeTelemetryTransport();
            var emitter = Build(transport, out _);

            var immediate = NewTestEvent();
            immediate.ImmediateUpload = true;
            emitter.Emit(immediate);

            var deferred = NewTestEvent();
            deferred.ImmediateUpload = false;
            emitter.Emit(deferred);

            Assert.True(transport.Enqueued[0].RequiresImmediateFlush);
            Assert.False(transport.Enqueued[1].RequiresImmediateFlush);
        }

        [Fact]
        public void Emit_rejects_null_event_and_empty_EventType()
        {
            var transport = new FakeTelemetryTransport();
            var emitter = Build(transport, out _);

            Assert.Throws<ArgumentNullException>(() => emitter.Emit(null!));

            var evt = NewTestEvent();
            evt.EventType = string.Empty;
            Assert.Throws<ArgumentException>(() => emitter.Emit(evt));
            Assert.Equal(0, transport.EnqueueCount);
        }

        [Fact]
        public void Data_dict_is_serialized_into_payload()
        {
            var transport = new FakeTelemetryTransport();
            var emitter = Build(transport, out _);

            var evt = NewTestEvent();
            evt.Data = new Dictionary<string, object> { ["reason"] = "test-reason", ["wgConfidence"] = "Strong" };
            emitter.Emit(evt);

            var parsed = JObject.Parse(transport.Enqueued[0].PayloadJson);
            var data = (JObject)parsed["Data"]!;
            Assert.Equal("test-reason", (string?)data["reason"]);
            Assert.Equal("Strong", (string?)data["wgConfidence"]);
        }

        // ============================================================ Trace-event gate

        [Fact]
        public void Trace_event_is_dropped_when_gate_is_off()
        {
            var transport = new FakeTelemetryTransport();
            var emitter = Build(transport, out _, traceEventsEnabled: () => false);

            var item = emitter.Emit(NewTraceEvent());

            Assert.Null(item);
            Assert.Equal(0, transport.EnqueueCount);
        }

        [Fact]
        public void Trace_event_flows_when_gate_is_on()
        {
            var transport = new FakeTelemetryTransport();
            var emitter = Build(transport, out _, traceEventsEnabled: () => true);

            Assert.NotNull(emitter.Emit(NewTraceEvent()));
            Assert.Equal(1, transport.EnqueueCount);
        }

        [Fact]
        public void Trace_event_flows_when_no_gate_is_wired()
        {
            var transport = new FakeTelemetryTransport();
            var emitter = Build(transport, out _);

            Assert.NotNull(emitter.Emit(NewTraceEvent()));
            Assert.Equal(1, transport.EnqueueCount);
        }

        [Fact]
        public void Gate_off_does_not_suppress_non_trace_events()
        {
            var transport = new FakeTelemetryTransport();
            var emitter = Build(transport, out _, traceEventsEnabled: () => false);

            foreach (var severity in new[] { EventSeverity.Info, EventSeverity.Warning, EventSeverity.Error })
            {
                var evt = NewTestEvent();
                evt.Severity = severity;
                Assert.NotNull(emitter.Emit(evt));
            }

            Assert.Equal(3, transport.EnqueueCount);
        }

        [Fact]
        public void Dropped_trace_event_does_not_consume_a_sequence_number()
        {
            var transport = new FakeTelemetryTransport();
            var traceAllowed = true;
            // ReSharper disable once AccessToModifiedClosure — deliberate: the gate is read
            // per event, mirroring a mid-session remote-config merge.
            var emitter = Build(transport, out _, traceEventsEnabled: () => traceAllowed);

            emitter.Emit(NewTestEvent());          // seq 1
            traceAllowed = false;
            emitter.Emit(NewTraceEvent());         // dropped — must not burn seq 2
            emitter.Emit(NewTestEvent());          // seq 2

            Assert.Equal(2, transport.EnqueueCount);
            var first = JObject.Parse(transport.Enqueued[0].PayloadJson);
            var second = JObject.Parse(transport.Enqueued[1].PayloadJson);
            Assert.Equal(1, (long)first["Sequence"]!);
            Assert.Equal(2, (long)second["Sequence"]!);
        }

        [Fact]
        public void Throwing_gate_fails_open_so_diagnostics_are_never_silently_lost()
        {
            var transport = new FakeTelemetryTransport();
            var emitter = Build(transport, out _, traceEventsEnabled: () => throw new InvalidOperationException("boom"));

            Assert.NotNull(emitter.Emit(NewTraceEvent()));
            Assert.Equal(1, transport.EnqueueCount);
        }
    }
}

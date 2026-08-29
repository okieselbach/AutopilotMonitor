using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Events;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Telemetry.Events
{
    public sealed class EventTimelineEmitterTests
    {
        private static readonly DateTime At = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public FakeTelemetryTransport Transport { get; } = new FakeTelemetryTransport();
            public EventSequenceCounter Counter { get; }
            public TelemetryEventEmitter Inner { get; }
            public EventTimelineEmitter Sut { get; }

            public Rig()
            {
                Counter = new EventSequenceCounter(new EventSequencePersistence(Tmp.File("seq.json")));
                Inner = new TelemetryEventEmitter(Transport, Counter, "S1", "T1");
                Sut = new EventTimelineEmitter(Inner);
            }

            public void Dispose() => Tmp.Dispose();
        }

        private static DecisionState State() => DecisionState.CreateInitial("S1", "T1");

        [Fact]
        public void Emit_builds_EnrollmentEvent_with_eventType_and_routes_to_transport()
        {
            using var r = new Rig();
            var parameters = new Dictionary<string, string> { ["eventType"] = "enrollment_complete" };

            r.Sut.Emit(parameters, State(), At);

            Assert.Equal(1, r.Transport.EnqueueCount);
            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            Assert.Equal("enrollment_complete", (string?)parsed["EventType"]);
            Assert.Equal("S1", (string?)parsed["SessionId"]);
            Assert.Equal("T1", (string?)parsed["TenantId"]);
            Assert.Equal(1, (long)parsed["Sequence"]!);
        }

        [Fact]
        public void Emit_uses_occurredAtUtc_not_wallclock_for_Timestamp()
        {
            using var r = new Rig();
            var deterministicTime = new DateTime(2030, 1, 15, 12, 34, 56, 789, DateTimeKind.Utc);
            var parameters = new Dictionary<string, string> { ["eventType"] = "enrollment_complete" };

            r.Sut.Emit(parameters, State(), deterministicTime);

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            var ts = parsed["Timestamp"]!.ToObject<DateTime>();
            Assert.Equal(deterministicTime.ToUniversalTime(), ts.ToUniversalTime());

            // RowKey format {yyyyMMddHHmmssfff}_{Sequence:D10}
            Assert.Equal("20300115123456789_0000000001", r.Transport.Enqueued[0].RowKey);
        }

        [Fact]
        public void EventType_with_failed_suffix_is_Error_severity()
        {
            using var r = new Rig();
            r.Sut.Emit(
                new Dictionary<string, string> { ["eventType"] = "enrollment_failed", ["reason"] = "esp_terminal" },
                State(),
                At);

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            Assert.Equal("Error", (string?)parsed["SeverityString"]);
        }

        [Fact]
        public void EventType_without_special_suffix_defaults_to_Info()
        {
            using var r = new Rig();
            r.Sut.Emit(
                new Dictionary<string, string> { ["eventType"] = "whiteglove_complete" },
                State(),
                At);

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            Assert.Equal("Info", (string?)parsed["SeverityString"]);
        }

        [Fact]
        public void Reason_parameter_is_appended_to_message_and_kept_in_Data()
        {
            using var r = new Rig();
            r.Sut.Emit(
                new Dictionary<string, string>
                {
                    ["eventType"] = "enrollment_failed",
                    ["reason"] = "hello_timeout",
                    ["wgConfidence"] = "None",
                },
                State(),
                At);

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            Assert.Equal("enrollment_failed: hello_timeout", (string?)parsed["Message"]);
            var data = (JObject)parsed["Data"]!;
            Assert.Equal("hello_timeout", (string?)data["reason"]);
            Assert.Equal("None", (string?)data["wgConfidence"]);
            // eventType must NOT be duplicated inside Data.
            Assert.Null(data["eventType"]);
        }

        [Fact]
        public void Phase_stays_Unknown_for_terminal_events()
        {
            // feedback_phase_strategy: terminal events do not declare a phase.
            using var r = new Rig();
            r.Sut.Emit(
                new Dictionary<string, string> { ["eventType"] = "enrollment_complete" },
                State(),
                At);

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            Assert.Equal((int)EnrollmentPhase.Unknown, (int)parsed["PhaseNumber"]!);
            Assert.Equal("Unknown", (string?)parsed["PhaseName"]);
        }

        [Fact]
        public void Phase_parameter_with_valid_enum_name_is_applied_to_event()
        {
            // Plan §1.4 — opt-in override: phase-declaration events may set the Phase via the
            // "phase" parameter on the EmitEventTimelineEntry effect.
            using var r = new Rig();
            r.Sut.Emit(
                new Dictionary<string, string>
                {
                    ["eventType"] = "esp_phase_changed",
                    ["phase"] = "DeviceSetup",
                },
                State(),
                At);

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            Assert.Equal((int)EnrollmentPhase.DeviceSetup, (int)parsed["PhaseNumber"]!);
            // PhaseName is the display form ("Device Setup") derived by EnrollmentEvent.GetPhaseName,
            // not the raw enum name. Emitter-side we only control Phase (enum); the display form
            // follows from that deterministically.
            Assert.Equal("Device Setup", (string?)parsed["PhaseName"]);
        }

        [Fact]
        public void Emit_publishes_eventType_and_parsed_phase_to_TimelineEventStream()
        {
            // Post-reduce observer surface: gather-rule phase/event triggers subscribe here so
            // they fire in step with what the timeline shows (session 32312a32 regression).
            using var r = new Rig();
            var stream = new AutopilotMonitor.Agent.V2.Core.Orchestration.TimelineEventStream();
            var published = new List<(string EventType, EnrollmentPhase Phase)>();
            stream.EventEmitted += (eventType, phase, _) => published.Add((eventType, phase));
            var sut = new EventTimelineEmitter(r.Inner, stream);

            sut.Emit(
                new Dictionary<string, string>
                {
                    ["eventType"] = "phase_transition",
                    ["phase"] = "FinalizingSetup",
                },
                State(),
                At);
            sut.Emit(
                new Dictionary<string, string> { ["eventType"] = "enrollment_complete" },
                State(),
                At);

            Assert.Equal(2, published.Count);
            Assert.Equal(("phase_transition", EnrollmentPhase.FinalizingSetup), published[0]);
            Assert.Equal(("enrollment_complete", EnrollmentPhase.Unknown), published[1]);
            // Publish happens AFTER the transport enqueue — wire ordering is authoritative.
            Assert.Equal(2, r.Transport.EnqueueCount);
        }

        [Fact]
        public void TimelineEventStream_subscriber_exception_does_not_break_the_emit_path()
        {
            using var r = new Rig();
            var stream = new AutopilotMonitor.Agent.V2.Core.Orchestration.TimelineEventStream();
            var secondSubscriberCalls = 0;
            stream.EventEmitted += (_, _, _) => throw new InvalidOperationException("subscriber bug");
            stream.EventEmitted += (_, _, _) => secondSubscriberCalls++;
            var sut = new EventTimelineEmitter(r.Inner, stream);

            sut.Emit(
                new Dictionary<string, string> { ["eventType"] = "enrollment_complete" },
                State(),
                At);

            // Emit neither threw (would surface as an EffectRunner retry = duplicated event)
            // nor skipped the remaining subscribers.
            Assert.Equal(1, r.Transport.EnqueueCount);
            Assert.Equal(1, secondSubscriberCalls);
        }

        [Fact]
        public void Phase_parameter_with_invalid_value_falls_back_to_Unknown()
        {
            // Parse-failure MUST fall back to Unknown, never throw — the emitter path must not
            // break telemetry on a malformed reducer-case. Deterministic & safe by default.
            using var r = new Rig();
            r.Sut.Emit(
                new Dictionary<string, string>
                {
                    ["eventType"] = "esp_phase_changed",
                    ["phase"] = "Bogus",
                },
                State(),
                At);

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            Assert.Equal((int)EnrollmentPhase.Unknown, (int)parsed["PhaseNumber"]!);
            Assert.Equal("Unknown", (string?)parsed["PhaseName"]);
        }

        [Fact]
        public void Phase_parameter_match_is_case_sensitive_and_non_matching_casing_falls_back_to_Unknown()
        {
            // The phase contract is strict Ordinal match against the enum name. Lower/upper
            // mismatches are rejected so the wire format stays deterministic.
            using var r = new Rig();
            r.Sut.Emit(
                new Dictionary<string, string>
                {
                    ["eventType"] = "esp_phase_changed",
                    ["phase"] = "devicesetup",
                },
                State(),
                At);

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            Assert.Equal((int)EnrollmentPhase.Unknown, (int)parsed["PhaseNumber"]!);
        }

        [Fact]
        public void Phase_parameter_is_not_duplicated_in_Data_dict()
        {
            // Like eventType, the phase parameter is a top-level EnrollmentEvent field and
            // must not leak into Data — otherwise the Data dictionary drifts from the contract.
            using var r = new Rig();
            r.Sut.Emit(
                new Dictionary<string, string>
                {
                    ["eventType"] = "esp_phase_changed",
                    ["phase"] = "AccountSetup",
                    ["subcategory"] = "WorkplaceJoin",
                },
                State(),
                At);

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            var data = (JObject)parsed["Data"]!;
            Assert.Null(data["phase"]);
            Assert.Null(data["eventType"]);
            Assert.Equal("WorkplaceJoin", (string?)data["subcategory"]);
        }

        [Fact]
        public void ImmediateUpload_is_forced_true_for_reducer_terminal_events()
        {
            using var r = new Rig();
            r.Sut.Emit(
                new Dictionary<string, string> { ["eventType"] = "whiteglove_complete" },
                State(),
                At);

            Assert.True(r.Transport.Enqueued[0].RequiresImmediateFlush);
        }

        [Fact]
        public void Emit_rejects_missing_or_empty_eventType()
        {
            using var r = new Rig();

            Assert.Throws<ArgumentException>(() => r.Sut.Emit(null, State(), At));
            Assert.Throws<ArgumentException>(() =>
                r.Sut.Emit(new Dictionary<string, string>(), State(), At));
            Assert.Throws<ArgumentException>(() =>
                r.Sut.Emit(new Dictionary<string, string> { ["eventType"] = string.Empty }, State(), At));

            Assert.Equal(0, r.Transport.EnqueueCount);
        }

        [Fact]
        public void Emit_rejects_null_state()
        {
            using var r = new Rig();
            Assert.Throws<ArgumentNullException>(() =>
                r.Sut.Emit(new Dictionary<string, string> { ["eventType"] = "x" }, null!, At));
        }

        [Fact]
        public void Source_is_DecisionEngine()
        {
            using var r = new Rig();
            r.Sut.Emit(
                new Dictionary<string, string> { ["eventType"] = "enrollment_complete" },
                State(),
                At);

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            Assert.Equal("DecisionEngine", (string?)parsed["Source"]);
        }

        [Fact]
        public void Source_parameter_overrides_default_so_single_rail_events_keep_origin_label()
        {
            // Plan §1.3 — InformationalEvent carries the originating collector's source label.
            using var r = new Rig();
            r.Sut.Emit(
                new Dictionary<string, string>
                {
                    ["eventType"] = "ntp_time_check",
                    ["source"] = "Network",
                },
                State(),
                At);

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            Assert.Equal("Network", (string?)parsed["Source"]);
        }

        [Fact]
        public void Empty_source_parameter_falls_back_to_default()
        {
            using var r = new Rig();
            r.Sut.Emit(
                new Dictionary<string, string>
                {
                    ["eventType"] = "enrollment_complete",
                    ["source"] = string.Empty,
                },
                State(),
                At);

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            Assert.Equal("DecisionEngine", (string?)parsed["Source"]);
        }

        [Fact]
        public void Severity_parameter_overrides_suffix_derived_default()
        {
            using var r = new Rig();
            r.Sut.Emit(
                new Dictionary<string, string>
                {
                    ["eventType"] = "ntp_time_check",
                    ["severity"] = "Warning",
                },
                State(),
                At);

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            Assert.Equal("Warning", (string?)parsed["SeverityString"]);
        }

        [Fact]
        public void Invalid_severity_parameter_falls_back_to_suffix_derived_default()
        {
            using var r = new Rig();
            r.Sut.Emit(
                new Dictionary<string, string>
                {
                    ["eventType"] = "enrollment_failed",
                    ["severity"] = "NotAnEnumValue",
                },
                State(),
                At);

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            // "_failed" suffix still drives the default → Error.
            Assert.Equal("Error", (string?)parsed["SeverityString"]);
        }

        [Fact]
        public void Message_parameter_overrides_reason_based_default()
        {
            using var r = new Rig();
            r.Sut.Emit(
                new Dictionary<string, string>
                {
                    ["eventType"] = "ntp_time_check",
                    ["message"] = "NTP offset -124.02s from time.windows.com",
                    // reason is ignored when an explicit message is provided.
                    ["reason"] = "should_not_appear_in_message",
                },
                State(),
                At);

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            Assert.Equal("NTP offset -124.02s from time.windows.com", (string?)parsed["Message"]);
            // reason still stays in Data even when unused by the message.
            var data = (JObject)parsed["Data"]!;
            Assert.Equal("should_not_appear_in_message", (string?)data["reason"]);
        }

        [Fact]
        public void ImmediateUpload_parameter_false_opts_out_of_immediate_flush()
        {
            using var r = new Rig();
            r.Sut.Emit(
                new Dictionary<string, string>
                {
                    ["eventType"] = "performance_snapshot",
                    ["immediateUpload"] = "false",
                },
                State(),
                At);

            Assert.False(r.Transport.Enqueued[0].RequiresImmediateFlush);
        }

        [Fact]
        public void ImmediateUpload_default_remains_true_for_reducer_effects_without_override()
        {
            // Regression guard — today's reducer cases do not pass immediateUpload, and the
            // wire contract keeps immediate flush for reducer-emitted events.
            using var r = new Rig();
            r.Sut.Emit(
                new Dictionary<string, string> { ["eventType"] = "whiteglove_complete" },
                State(),
                At);

            Assert.True(r.Transport.Enqueued[0].RequiresImmediateFlush);
        }

        [Fact]
        public void Top_level_parameters_are_not_duplicated_in_Data()
        {
            // Plan §1.3 — every top-level EnrollmentEvent field (eventType, phase, source,
            // severity, message, immediateUpload) is stripped from the Data dictionary so the
            // wire format is not self-contradicting.
            using var r = new Rig();
            r.Sut.Emit(
                new Dictionary<string, string>
                {
                    ["eventType"] = "ntp_time_check",
                    ["phase"] = "DeviceSetup",
                    ["source"] = "Network",
                    ["severity"] = "Warning",
                    ["message"] = "NTP offset -124.02s",
                    ["immediateUpload"] = "false",
                    ["offsetSeconds"] = "-124.02",
                },
                State(),
                At);

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            var data = (JObject)parsed["Data"]!;
            Assert.Null(data["eventType"]);
            Assert.Null(data["phase"]);
            Assert.Null(data["source"]);
            Assert.Null(data["severity"]);
            Assert.Null(data["message"]);
            Assert.Null(data["immediateUpload"]);
            Assert.Equal("-124.02", (string?)data["offsetSeconds"]);
        }

        /// <summary>
        /// Codex Finding 2 — the typed-sidecar fast path. When a caller supplies a structured
        /// <c>Dictionary&lt;string, object&gt;</c> as <c>typedPayload</c>, the emitter uses it
        /// directly as <c>EnrollmentEvent.Data</c>. Newtonsoft then serializes nested Dict/List
        /// values as real JSON objects / arrays on the wire — no string round-trip, no marker,
        /// no type-name degeneracies.
        /// </summary>
        [Fact]
        public void TypedPayload_dictionary_becomes_EnrollmentEvent_Data_verbatim_on_wire()
        {
            using var r = new Rig();

            var adapters = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["description"] = "Intel Wireless",
                    ["macAddress"] = "AA:BB:CC:DD:EE:FF",
                },
                new Dictionary<string, object>
                {
                    ["description"] = "Loopback",
                },
            };
            var typed = new Dictionary<string, object>
            {
                ["adapterCount"] = 2,
                ["adapters"] = adapters,
            };

            r.Sut.Emit(
                new Dictionary<string, string>
                {
                    ["eventType"] = "network_adapters",
                    ["source"] = "DeviceInfoCollector",
                },
                State(),
                At,
                typedPayload: typed);

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            var data = (JObject)parsed["Data"]!;
            // Scalar int → JSON number (not "2" string).
            Assert.Equal(JTokenType.Integer, data["adapterCount"]!.Type);
            Assert.Equal(2, (int)data["adapterCount"]!);
            // Nested list → JSON array of JSON objects. Full structural fidelity.
            var adaptersToken = data["adapters"];
            Assert.NotNull(adaptersToken);
            Assert.Equal(JTokenType.Array, adaptersToken!.Type);
            var array = (JArray)adaptersToken;
            Assert.Equal(2, array.Count);
            Assert.Equal("Intel Wireless", (string?)array[0]["description"]);
            Assert.Equal("AA:BB:CC:DD:EE:FF", (string?)array[0]["macAddress"]);
            Assert.Equal("Loopback", (string?)array[1]["description"]);
        }

        [Fact]
        public void TypedPayload_missing_falls_back_to_string_parameter_reconstruction()
        {
            // Non-informational signals (e.g. reducer-synthesised classifier verdicts) do not
            // supply a typed payload. The emitter must still build Data from the string-only
            // effect parameters — same behaviour as before the sidecar existed.
            using var r = new Rig();

            r.Sut.Emit(
                new Dictionary<string, string>
                {
                    ["eventType"] = "decision_classifier_verdict",
                    ["verdictId"] = "v-123",
                },
                State(),
                At,
                typedPayload: null);

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            var data = (JObject)parsed["Data"]!;
            Assert.Equal("v-123", (string?)data["verdictId"]);
        }

        // ============================================================ Codex #3 forward-link

        [Fact]
        public void Emit_populates_CausedByTransitionStepIndex_from_currentState_StepIndex()
        {
            // Codex follow-up #3: every EmitEventTimelineEntry runs as part of a reducer
            // step; the event carries the StepIndex forward so Inspector queries can locate
            // all events emitted by a specific transition without needing the journal-side
            // EmittedEventSequences (which stays empty because event Sequences are assigned
            // after the journal record is on disk).
            using var r = new Rig();
            var state = DecisionState.CreateInitial("S1", "T1")
                .ToBuilder()
                .WithStepIndex(7)
                .WithLastAppliedSignalOrdinal(12)
                .Build();

            r.Sut.Emit(
                new Dictionary<string, string> { ["eventType"] = "enrollment_complete" },
                state,
                At);

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            Assert.Equal(7L, (long?)parsed["CausedByTransitionStepIndex"]);
            Assert.Equal(12L, (long?)parsed["CausedBySignalOrdinal"]);
        }

        [Fact]
        public void Emit_CausedBySignalOrdinal_is_null_when_state_has_no_applied_signal_yet()
        {
            // Fresh CreateInitial state has LastAppliedSignalOrdinal=-1 (sentinel for "no
            // signal applied yet"). The forward-link is then written as null rather than -1
            // so the nullable column stays absent instead of carrying a meaningless value.
            using var r = new Rig();
            var state = DecisionState.CreateInitial("S1", "T1"); // LastAppliedSignalOrdinal == -1

            r.Sut.Emit(
                new Dictionary<string, string> { ["eventType"] = "agent_started" },
                state,
                At);

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            Assert.Equal(0L, (long?)parsed["CausedByTransitionStepIndex"]); // initial state's StepIndex is 0, still captured
            Assert.Null((long?)parsed["CausedBySignalOrdinal"]);
        }
    }
}

using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pure parsing tests for <see cref="TelemetryPayloadParser"/>. Verifies the wire → storage
/// mapping the <see cref="IngestTelemetryFunction"/> relies on: defensive null handling, typed
/// column extraction, and the index-discriminator projection (IsTerminal, ClassifierVerdictId,
/// ClassifierHypothesisLevel).
/// </summary>
public class TelemetryPayloadParserTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    private static TelemetryItemDto Dto(string kind, string payloadJson, long? traceOrdinal = null)
        => new TelemetryItemDto
        {
            Kind                   = kind,
            PartitionKey           = $"{TenantId}_{SessionId}",
            RowKey                 = "0000000001",
            TelemetryItemId        = 1,
            SessionTraceOrdinal    = traceOrdinal,
            PayloadJson            = payloadJson,
            RequiresImmediateFlush = false,
            EnqueuedAtUtc          = new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc),
            RetryCount             = 0,
        };

    // ============================================================ Defensive parsing

    [Theory]
    [InlineData("")]
    [InlineData("{ this is not json }")]
    [InlineData("null")]
    public void ParseEvent_returns_null_for_missing_or_malformed_payload(string payload)
    {
        var result = TelemetryPayloadParser.ParseEvent(Dto("Event", payload), TenantId, SessionId);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ this is not json }")]
    public void ParseSignal_returns_null_for_missing_or_malformed_payload(string payload)
    {
        var result = TelemetryPayloadParser.ParseSignal(Dto("Signal", payload), TenantId, SessionId);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ this is not json }")]
    public void ParseTransition_returns_null_for_missing_or_malformed_payload(string payload)
    {
        var result = TelemetryPayloadParser.ParseTransition(Dto("DecisionTransition", payload), TenantId, SessionId);
        Assert.Null(result);
    }

    // ============================================================ Event

    [Fact]
    public void ParseEvent_deserialises_enrollment_event_shape()
    {
        var payload =
            "{" +
                "\"EventId\":\"evt-123\"," +
                "\"EventType\":\"test_event\"," +
                "\"Severity\":1," +
                "\"Source\":\"test\"," +
                "\"Phase\":2," +
                "\"Message\":\"hello\"," +
                "\"Sequence\":42," +
                "\"Timestamp\":\"2026-04-21T10:00:00Z\"," +
                "\"ReceivedAt\":\"2026-04-21T10:00:01Z\"" +
            "}";

        var evt = TelemetryPayloadParser.ParseEvent(Dto("Event", payload), TenantId, SessionId);

        Assert.NotNull(evt);
        Assert.Equal("evt-123", evt!.EventId);
        Assert.Equal("test_event", evt.EventType);
        Assert.Equal(42, evt.Sequence);
    }

    // ============================================================ Signal

    [Fact]
    public void ParseSignal_extracts_typed_columns_and_preserves_full_payload()
    {
        var payload =
            "{" +
                "\"SessionSignalOrdinal\":17," +
                "\"SessionTraceOrdinal\":117," +
                "\"Kind\":\"EspPhaseChanged\"," +
                "\"KindSchemaVersion\":2," +
                "\"OccurredAtUtc\":\"2026-04-21T10:00:00Z\"," +
                "\"SourceOrigin\":\"EspAndHelloTrackerAdapter\"," +
                "\"Evidence\":{\"Kind\":\"Raw\"}" +
            "}";

        var rec = TelemetryPayloadParser.ParseSignal(Dto("Signal", payload), TenantId, SessionId);

        Assert.NotNull(rec);
        Assert.Equal(TenantId, rec!.TenantId);
        Assert.Equal(SessionId, rec.SessionId);
        Assert.Equal(17L, rec.SessionSignalOrdinal);
        Assert.Equal(117L, rec.SessionTraceOrdinal);
        Assert.Equal("EspPhaseChanged", rec.Kind);
        Assert.Equal(2, rec.KindSchemaVersion);
        Assert.Equal(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc), rec.OccurredAtUtc.ToUniversalTime());
        Assert.Equal("EspAndHelloTrackerAdapter", rec.SourceOrigin);
        Assert.Equal(payload, rec.PayloadJson);
    }

    [Fact]
    public void ParseSignal_falls_back_to_envelope_SessionTraceOrdinal_when_payload_missing_field()
    {
        // Payload intentionally omits SessionTraceOrdinal; the envelope carries it.
        var payload = "{\"SessionSignalOrdinal\":1,\"Kind\":\"SessionStarted\"}";

        var rec = TelemetryPayloadParser.ParseSignal(Dto("Signal", payload, traceOrdinal: 99), TenantId, SessionId);

        Assert.NotNull(rec);
        Assert.Equal(99L, rec!.SessionTraceOrdinal);
    }

    [Theory]
    [InlineData("{\"Kind\":\"SessionStarted\"}")]                                       // key absent
    [InlineData("{\"SessionSignalOrdinal\":null,\"Kind\":\"SessionStarted\"}")]         // key present but null
    public void ParseSignal_drops_item_when_SessionSignalOrdinal_is_missing_or_null(string payload)
    {
        // A missing ordinal would silently default to 0 → collide with a legitimate ordinal-0
        // row on UpsertReplace. The parser must reject it so the caller loop drops the item.
        var rec = TelemetryPayloadParser.ParseSignal(Dto("Signal", payload), TenantId, SessionId);
        Assert.Null(rec);
    }

    [Fact]
    public void ParseSignal_accepts_explicit_ordinal_zero()
    {
        // 0 is a legal value (the agent seeds ordinals from 0 on a fresh SignalLog). Only
        // absent/null is rejected — an explicit 0 must round-trip.
        var payload = "{\"SessionSignalOrdinal\":0,\"Kind\":\"SessionStarted\"}";

        var rec = TelemetryPayloadParser.ParseSignal(Dto("Signal", payload), TenantId, SessionId);

        Assert.NotNull(rec);
        Assert.Equal(0L, rec!.SessionSignalOrdinal);
    }

    // ============================================================ DecisionTransition

    [Fact]
    public void ParseTransition_extracts_core_columns_and_flags_taken_false_when_dead_end()
    {
        var payload =
            "{" +
                "\"StepIndex\":3," +
                "\"SessionTraceOrdinal\":10," +
                "\"SignalOrdinalRef\":9," +
                "\"OccurredAtUtc\":\"2026-04-21T10:00:00Z\"," +
                "\"Trigger\":\"EspExiting\"," +
                "\"FromStage\":\"EspInProgress\"," +
                "\"ToStage\":\"EspInProgress\"," +
                "\"Taken\":false," +
                "\"DeadEndReason\":\"hybrid_reboot_gate_blocking\"," +
                "\"ReducerVersion\":\"1.0.0\"" +
            "}";

        var rec = TelemetryPayloadParser.ParseTransition(Dto("DecisionTransition", payload), TenantId, SessionId);

        Assert.NotNull(rec);
        Assert.False(rec!.Taken);
        Assert.Equal("hybrid_reboot_gate_blocking", rec.DeadEndReason);
        Assert.False(rec.IsTerminal);
        Assert.Null(rec.ClassifierVerdictId);
    }

    [Fact]
    public void ParseTransition_projects_IsTerminal_true_for_terminal_stages()
    {
        var terminalStages = new[] { "Completed", "Failed", "WhiteGloveSealed" };

        foreach (var stage in terminalStages)
        {
            var payload =
                "{\"StepIndex\":1,\"SessionTraceOrdinal\":1,\"SignalOrdinalRef\":1," +
                $"\"Trigger\":\"t\",\"FromStage\":\"X\",\"ToStage\":\"{stage}\",\"Taken\":true," +
                "\"ReducerVersion\":\"1.0.0\",\"OccurredAtUtc\":\"2026-04-21T10:00:00Z\"}";

            var rec = TelemetryPayloadParser.ParseTransition(Dto("DecisionTransition", payload), TenantId, SessionId);

            Assert.NotNull(rec);
            Assert.True(rec!.IsTerminal, $"Expected {stage} to be terminal");
        }
    }

    [Theory]
    [InlineData("{\"SessionTraceOrdinal\":1,\"ToStage\":\"EspInProgress\"}")]                       // key absent
    [InlineData("{\"StepIndex\":null,\"SessionTraceOrdinal\":1,\"ToStage\":\"EspInProgress\"}")]   // key present but null
    public void ParseTransition_drops_item_when_StepIndex_is_missing_or_null(string payload)
    {
        // Same defense as ParseSignal: a missing StepIndex would default to 0 → collide with the
        // legitimate step-0 row on UpsertReplace.
        var rec = TelemetryPayloadParser.ParseTransition(Dto("DecisionTransition", payload), TenantId, SessionId);
        Assert.Null(rec);
    }

    [Fact]
    public void ParseTransition_accepts_explicit_stepIndex_zero()
    {
        // Step indices start at 0 (the first reducer call produces StepIndex=0). An explicit 0
        // must not be confused with a missing key.
        var payload =
            "{\"StepIndex\":0,\"SessionTraceOrdinal\":1,\"SignalOrdinalRef\":1," +
            "\"Trigger\":\"t\",\"FromStage\":\"Pending\",\"ToStage\":\"EspInProgress\",\"Taken\":true," +
            "\"ReducerVersion\":\"1.0.0\",\"OccurredAtUtc\":\"2026-04-21T10:00:00Z\"}";

        var rec = TelemetryPayloadParser.ParseTransition(Dto("DecisionTransition", payload), TenantId, SessionId);

        Assert.NotNull(rec);
        Assert.Equal(0, rec!.StepIndex);
    }

    [Fact]
    public void ParseTransition_extracts_classifier_verdict_nested_fields()
    {
        var payload =
            "{\"StepIndex\":1,\"SessionTraceOrdinal\":1,\"SignalOrdinalRef\":1," +
            "\"Trigger\":\"t\",\"FromStage\":\"X\",\"ToStage\":\"WhiteGloveSealed\",\"Taken\":true," +
            "\"ReducerVersion\":\"1.0.0\",\"OccurredAtUtc\":\"2026-04-21T10:00:00Z\"," +
            "\"ClassifierVerdict\":{\"ClassifierId\":\"whiteglove-sealing\",\"HypothesisLevel\":\"Strong\"}}";

        var rec = TelemetryPayloadParser.ParseTransition(Dto("DecisionTransition", payload), TenantId, SessionId);

        Assert.NotNull(rec);
        Assert.Equal("whiteglove-sealing", rec!.ClassifierVerdictId);
        Assert.Equal("Strong", rec.ClassifierHypothesisLevel);
    }

    // ============================================================ IsTerminalStage

    [Theory]
    [InlineData("Completed",                 true)]
    [InlineData("Failed",                    true)]
    [InlineData("WhiteGloveSealed",          true)]
    [InlineData("EspInProgress",             false)]
    [InlineData("AccountSetup",              false)]
    [InlineData("",                          false)]
    [InlineData("completed",                 false)] // case-sensitive — enum names are PascalCase
    public void IsTerminalStage_matches_DecisionCore_authoritative_list(string stage, bool expected)
    {
        Assert.Equal(expected, TelemetryPayloadParser.IsTerminalStage(stage));
    }
}

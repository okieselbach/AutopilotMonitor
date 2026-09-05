using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// JSON round-trip tests for <see cref="AnalyzeOnEnrollmentEndEnvelope"/>. The producer
/// (<c>AzureQueueAnalyzeOnEnrollmentEndProducer</c>) serializes with Newtonsoft and the
/// queue worker (<c>AnalyzeOnEnrollmentEndQueueFunction</c>) deserializes the same shape —
/// pinning the contract here keeps the queue from silently breaking when fields are added.
/// </summary>
public class AnalyzeOnEnrollmentEndEnvelopeSerializationTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    private static readonly DateTime EnqueuedAt =
        new(2026, 4, 28, 10, 15, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("enrollment_complete")]
    [InlineData("enrollment_failed")]
    [InlineData("vulnerability_correlated")]
    [InlineData("whiteglove_sealed")]
    [InlineData("interim_trigger")]
    public void Envelope_round_trips_through_newtonsoft_for_all_reasons(string reason)
    {
        var original = new AnalyzeOnEnrollmentEndEnvelope
        {
            EnvelopeVersion = "1",
            TenantId        = TenantId,
            SessionId       = SessionId,
            Reason          = reason,
            EnqueuedAt      = EnqueuedAt,
        };

        var json = JsonConvert.SerializeObject(original);
        var restored = JsonConvert.DeserializeObject<AnalyzeOnEnrollmentEndEnvelope>(json);

        Assert.NotNull(restored);
        Assert.Equal("1",        restored!.EnvelopeVersion);
        Assert.Equal(TenantId,   restored.TenantId);
        Assert.Equal(SessionId,  restored.SessionId);
        Assert.Equal(reason,     restored.Reason);
        Assert.Equal(EnqueuedAt, restored.EnqueuedAt.ToUniversalTime());
    }

    [Fact]
    public void Envelope_trigger_event_types_round_trip_and_default_to_null()
    {
        // interim_trigger envelopes carry the batch's matched trigger types; every other
        // reason leaves the field null so legacy consumers see an unchanged shape.
        var original = new AnalyzeOnEnrollmentEndEnvelope
        {
            TenantId          = TenantId,
            SessionId         = SessionId,
            Reason            = "interim_trigger",
            TriggerEventTypes = new List<string> { "hybrid_login_pending", "custom_probe_signal" },
            EnqueuedAt        = EnqueuedAt,
        };

        var restored = JsonConvert.DeserializeObject<AnalyzeOnEnrollmentEndEnvelope>(
            JsonConvert.SerializeObject(original));

        Assert.NotNull(restored);
        Assert.Equal(new List<string> { "hybrid_login_pending", "custom_probe_signal" }, restored!.TriggerEventTypes);

        // A legacy envelope without the field deserializes to null, not empty.
        var legacy = JsonConvert.DeserializeObject<AnalyzeOnEnrollmentEndEnvelope>(
            $"{{\"EnvelopeVersion\":\"1\",\"TenantId\":\"{TenantId}\",\"SessionId\":\"{SessionId}\",\"Reason\":\"enrollment_complete\"}}");
        Assert.NotNull(legacy);
        Assert.Null(legacy!.TriggerEventTypes);
    }

    [Fact]
    public void Envelope_default_envelope_version_is_one()
    {
        var envelope = new AnalyzeOnEnrollmentEndEnvelope();
        Assert.Equal("1", envelope.EnvelopeVersion);
    }

    [Fact]
    public void Envelope_default_strings_are_empty_not_null()
    {
        var envelope = new AnalyzeOnEnrollmentEndEnvelope();
        Assert.Equal(string.Empty, envelope.TenantId);
        Assert.Equal(string.Empty, envelope.SessionId);
        Assert.Equal(string.Empty, envelope.Reason);
    }

    [Fact]
    public void Envelope_serializes_reason_as_field()
    {
        // Pin the JSON wire-format so a property rename can't slip through unnoticed.
        var envelope = new AnalyzeOnEnrollmentEndEnvelope
        {
            TenantId   = TenantId,
            SessionId  = SessionId,
            Reason     = "enrollment_complete",
            EnqueuedAt = EnqueuedAt,
        };

        var json = JsonConvert.SerializeObject(envelope);

        Assert.Contains("\"TenantId\"", json);
        Assert.Contains("\"SessionId\"", json);
        Assert.Contains("\"Reason\"", json);
        Assert.Contains("\"EnqueuedAt\"", json);
        Assert.Contains("\"EnvelopeVersion\"", json);
        Assert.Contains("enrollment_complete", json);
    }
}

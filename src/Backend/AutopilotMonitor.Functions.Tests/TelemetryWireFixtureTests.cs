using System.Text;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The agent → backend wire contract of <c>POST /api/agent/telemetry</c>, proven from the
/// READER side. The agent's <c>TelemetryWireContractTests</c> freezes the real batch body its
/// serialiser produces (one item per <see cref="TelemetryItemKind"/>) under
/// <c>tests/fixtures/telemetry-wire/</c>; every frozen body must deserialise and dispatch here
/// with zero rejections. Old fixtures stay: a body an older agent line still sends must keep
/// parsing after a backend-side change. Together the two suites make a rename on either side a
/// test failure instead of a silently dropped item.
/// </summary>
public class TelemetryWireFixtureTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    private static readonly DateTime At = new(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

    private static string FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "tests", "fixtures", "telemetry-wire");
    }

    public static IEnumerable<object[]> FrozenBodies() =>
        Directory.EnumerateFiles(FixtureDir(), "*.json").OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => new object[] { Path.GetFileName(p) });

    [Fact]
    public void At_least_one_frozen_body_exists()
    {
        Assert.NotEmpty(FrozenBodies());
    }

    [Theory]
    [MemberData(nameof(FrozenBodies))]
    public async Task Frozen_agent_body_dispatches_every_kind_with_zero_rejections(string fileName)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), fileName));

        var (exceeded, items) = await IngestTelemetryFunction.ReadBodyWithSizeCapAsync(new MemoryStream(bytes), 5 * 1024 * 1024);
        Assert.False(exceeded);
        Assert.NotNull(items);

        var kinds = items!.Select(i => i.Kind).ToHashSet(StringComparer.Ordinal);
        foreach (var kind in TelemetryItemKinds.All)
            Assert.Contains(kind.ToString(), kinds);

        var batch = IngestTelemetryFunction.PartitionBatch(items, TenantId, SessionId);

        Assert.True(batch.Rejected.Count == 0,
            $"{fileName}: the ingest rejects items the agent serialiser produced — " +
            string.Join("; ", batch.Rejected.Select(r => $"id={r.TelemetryItemId} kind='{r.Kind}' cause={r.Cause}")));
        Assert.Single(batch.Events);
        Assert.Single(batch.Signals);
        Assert.Single(batch.Transitions);
        Assert.Equal(items.Count, batch.Events.Count + batch.Signals.Count + batch.Transitions.Count);
    }

    // ============================================================ Dispatch is total

    /// <summary>Minimal payload the parser accepts per kind — a new kind needs its sample here.</summary>
    private static string SamplePayload(TelemetryItemKind kind) => kind switch
    {
        TelemetryItemKind.Event => "{\"EventType\":\"agent_started\",\"Timestamp\":\"2026-04-20T10:00:00Z\"}",
        TelemetryItemKind.Signal => "{\"SessionSignalOrdinal\":1}",
        TelemetryItemKind.DecisionTransition => "{\"StepIndex\":1}",
        _ => throw new Xunit.Sdk.XunitException($"No sample payload for TelemetryItemKind.{kind} — add one here and an ingest arm in PartitionBatch"),
    };

    private static TelemetryItemDto Dto(string kind, string payload, long id = 1) => new()
    {
        Kind = kind,
        PartitionKey = $"{TenantId}_{SessionId}",
        RowKey = $"rk-{id}",
        TelemetryItemId = id,
        SessionTraceOrdinal = id,
        PayloadJson = payload,
        EnqueuedAtUtc = At,
    };

    [Fact]
    public void Every_defined_kind_has_an_ingest_arm()
    {
        var items = TelemetryItemKinds.All.Select((k, i) => Dto(k.ToString(), SamplePayload(k), i + 1)).ToList();

        var batch = IngestTelemetryFunction.PartitionBatch(items, TenantId, SessionId);

        Assert.True(batch.Rejected.Count == 0,
            "PartitionBatch has no arm for: " + string.Join(", ", batch.Rejected.Select(r => r.Kind)));
        Assert.Equal(items.Count, batch.Events.Count + batch.Signals.Count + batch.Transitions.Count);
    }

    [Theory]
    [InlineData("Bogus")]
    [InlineData("event")]   // names are the contract — case matters
    [InlineData("0")]       // ordinals are not
    [InlineData("")]
    public void Unknown_kind_is_rejected_not_dropped(string kind)
    {
        var items = new List<TelemetryItemDto> { Dto("Event", SamplePayload(TelemetryItemKind.Event), 1), Dto(kind, "{}", 2) };

        var batch = IngestTelemetryFunction.PartitionBatch(items, TenantId, SessionId);

        var rejected = Assert.Single(batch.Rejected);
        Assert.Equal(IngestTelemetryFunction.RejectionCause.UnknownKind, rejected.Cause);
        Assert.Equal("rk-2", rejected.RowKey);
        Assert.Equal(2, rejected.TelemetryItemId);
        Assert.Single(batch.Events);
    }

    [Theory]
    [InlineData("Event", "")]
    [InlineData("Event", "not json")]
    [InlineData("Signal", "{}")]                   // SessionSignalOrdinal missing
    [InlineData("DecisionTransition", "{\"x\":1}")] // StepIndex missing
    public void Unparseable_payload_is_rejected_not_dropped(string kind, string payload)
    {
        var items = new List<TelemetryItemDto> { Dto(kind, payload, 7) };

        var batch = IngestTelemetryFunction.PartitionBatch(items, TenantId, SessionId);

        var rejected = Assert.Single(batch.Rejected);
        Assert.Equal(IngestTelemetryFunction.RejectionCause.UnparseablePayload, rejected.Cause);
        Assert.Equal(7, rejected.TelemetryItemId);
        Assert.Empty(batch.Events);
        Assert.Empty(batch.Signals);
        Assert.Empty(batch.Transitions);
    }

    // ============================================================ Poison body

    /// <summary>
    /// Byte-for-byte pin of the 422 body. The agent's <c>TelemetryWireContractTests</c> feeds this
    /// exact string to its uploader and expects an item-level poison result — change both together.
    /// </summary>
    [Fact]
    public void Poison_body_pins_the_shape_the_agent_parses()
    {
        var body = new TelemetryItemsRejectedResponse
        {
            Error = "2 of 3 telemetry item(s) cannot be ingested (unknown Kind or unusable payload); they were not stored.",
            CorrelationId = "cid-1",
            RejectedRowKeys = new List<string> { "rk-1", "rk-3" },
            Reason = "unknown_kind=1;unparseable=1",
            Received = 3,
            Rejected = 2,
        };

        Assert.Equal(
            "{\"error\":\"2 of 3 telemetry item(s) cannot be ingested (unknown Kind or unusable payload); they were not stored.\"," +
            "\"code\":\"TelemetryItemsRejected\",\"correlationId\":\"cid-1\",\"poison\":true,\"rejectedRowKeys\":[\"rk-1\",\"rk-3\"]," +
            "\"reason\":\"unknown_kind=1;unparseable=1\",\"received\":3,\"rejected\":2}",
            JsonSerializer.Serialize(body, ApiJsonOptions.Create()));
        Assert.Equal(Constants.ApiErrorCodes.TelemetryItemsRejected, body.Code);
    }
}

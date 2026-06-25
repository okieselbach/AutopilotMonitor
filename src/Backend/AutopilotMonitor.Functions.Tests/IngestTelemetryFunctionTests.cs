using System.IO;
using System.Text;
using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the pure helpers on <see cref="IngestTelemetryFunction"/>. The HTTP-trigger end
/// of the function needs a live runtime harness to exercise; this file covers the deterministic
/// bits (PartitionKey parsing, body size-cap guard).
/// </summary>
public class IngestTelemetryFunctionTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    [Fact]
    public void TryParsePartitionKey_splits_tenant_and_session_on_single_underscore()
    {
        var ok = IngestTelemetryFunction.TryParsePartitionKey(
            $"{TenantId}_{SessionId}", out var tenant, out var session);

        Assert.True(ok);
        Assert.Equal(TenantId, tenant);
        Assert.Equal(SessionId, session);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("no-underscore-here")]
    [InlineData("_missing-tenant")]
    [InlineData("missing-session_")]
    [InlineData("too_many_parts_here")]
    public void TryParsePartitionKey_rejects_malformed_shapes(string? input)
    {
        var ok = IngestTelemetryFunction.TryParsePartitionKey(
            input!, out var tenant, out var session);

        Assert.False(ok);
        Assert.Equal(string.Empty, tenant);
        Assert.Equal(string.Empty, session);
    }

    // ============================================================ Payload size-cap guard

    [Fact]
    public async Task ReadBodyWithSizeCapAsync_accepts_body_under_cap()
    {
        var payload = Encoding.UTF8.GetBytes("[{\"Kind\":\"Event\"}]");
        using var stream = new MemoryStream(payload);

        var (exceeded, items) = await IngestTelemetryFunction.ReadBodyWithSizeCapAsync(stream, maxBytes: 1024);

        Assert.False(exceeded);
        Assert.NotNull(items);
        var item = Assert.Single(items!);
        Assert.Equal("Event", item.Kind);
    }

    [Fact]
    public async Task ReadBodyWithSizeCapAsync_preserves_PayloadJson_as_raw_string()
    {
        // Hot-path contract: the inner payload stays an opaque escaped string on the envelope —
        // the batch parse must NOT re-parse/re-serialise it (that's TelemetryPayloadParser's job,
        // exactly once, downstream). Guards against an accidental round-trip that would mangle it.
        var payload = Encoding.UTF8.GetBytes(
            "[{\"Kind\":\"Event\",\"PayloadJson\":\"{\\\"EventType\\\":\\\"x\\\",\\\"n\\\":1}\"}]");
        using var stream = new MemoryStream(payload);

        var (exceeded, items) = await IngestTelemetryFunction.ReadBodyWithSizeCapAsync(stream, maxBytes: 1024);

        Assert.False(exceeded);
        var item = Assert.Single(items!);
        Assert.Equal("{\"EventType\":\"x\",\"n\":1}", item.PayloadJson);
    }

    [Fact]
    public async Task ReadBodyWithSizeCapAsync_accepts_body_exactly_at_cap()
    {
        // Strict greater-than semantics (matches legacy NdjsonParser): equal-to is OK. Pad a valid
        // batch with trailing whitespace (ignored by the parser) to land exactly on the cap.
        var json = "[{\"Kind\":\"Signal\",\"PayloadJson\":\"{}\"}]".PadRight(100);
        var payload = Encoding.UTF8.GetBytes(json);
        Assert.Equal(100, payload.Length); // ASCII → 1 byte/char
        using var stream = new MemoryStream(payload);

        var (exceeded, items) = await IngestTelemetryFunction.ReadBodyWithSizeCapAsync(stream, maxBytes: 100);

        Assert.False(exceeded);
        Assert.Single(items!);
    }

    [Fact]
    public async Task ReadBodyWithSizeCapAsync_rejects_body_over_cap_without_draining_full_stream()
    {
        // 1 MB payload with a 100-byte cap → short-circuits after a single buffer read, before
        // the deserialiser is ever invoked.
        var payload = new byte[1_000_000];
        using var stream = new MemoryStream(payload);

        var (exceeded, items) = await IngestTelemetryFunction.ReadBodyWithSizeCapAsync(stream, maxBytes: 100);

        Assert.True(exceeded);
        Assert.Null(items);
        // Stream position advanced past the cap but not to EOF — the helper bails early to
        // bound memory on hostile senders.
        Assert.True(stream.Position < stream.Length);
    }

    [Fact]
    public async Task ReadBodyWithSizeCapAsync_empty_stream_returns_null_items()
    {
        using var stream = new MemoryStream(Array.Empty<byte>());

        var (exceeded, items) = await IngestTelemetryFunction.ReadBodyWithSizeCapAsync(stream, maxBytes: 1024);

        Assert.False(exceeded);
        Assert.Null(items);
    }

    [Fact]
    public async Task ReadBodyWithSizeCapAsync_throws_on_malformed_json()
    {
        // Malformed JSON surfaces as JsonException so the HTTP trigger maps it to 400 — the cap
        // path must not swallow it as an empty/successful parse.
        var payload = Encoding.UTF8.GetBytes("[{\"Kind\":\"Event\"");
        using var stream = new MemoryStream(payload);

        await Assert.ThrowsAnyAsync<Newtonsoft.Json.JsonException>(
            () => IngestTelemetryFunction.ReadBodyWithSizeCapAsync(stream, maxBytes: 1024));
    }

    // ============================================================ Batch PartitionKey uniformity

    private static TelemetryItemDto Item(string partitionKey)
        => new TelemetryItemDto
        {
            Kind                = "Signal",
            PartitionKey        = partitionKey,
            RowKey              = "0000000001",
            TelemetryItemId     = 1,
            PayloadJson         = "{}",
            EnqueuedAtUtc       = DateTime.UtcNow,
        };

    [Fact]
    public void FindMismatchingPartitionKey_returns_false_for_empty_batch()
    {
        var result = IngestTelemetryFunction.FindMismatchingPartitionKey(
            new List<TelemetryItemDto>(), out var idx, out var value);

        Assert.False(result);
        Assert.Equal(-1, idx);
        Assert.Null(value);
    }

    [Fact]
    public void FindMismatchingPartitionKey_returns_false_for_single_item_batch()
    {
        var result = IngestTelemetryFunction.FindMismatchingPartitionKey(
            new[] { Item($"{TenantId}_{SessionId}") }, out var idx, out var value);

        Assert.False(result);
        Assert.Equal(-1, idx);
        Assert.Null(value);
    }

    [Fact]
    public void FindMismatchingPartitionKey_returns_false_when_all_items_share_PartitionKey()
    {
        var pk = $"{TenantId}_{SessionId}";
        var items = new[] { Item(pk), Item(pk), Item(pk) };

        var result = IngestTelemetryFunction.FindMismatchingPartitionKey(items, out var idx, out var value);

        Assert.False(result);
        Assert.Equal(-1, idx);
        Assert.Null(value);
    }

    [Fact]
    public void FindMismatchingPartitionKey_returns_index_and_value_on_mismatch()
    {
        // Session-mismatch in the middle of the batch — what a misbehaving client could send.
        var first  = $"{TenantId}_{SessionId}";
        var second = $"{TenantId}_c3d4e5f6-a7b8-9012-cdef-123456789012"; // same tenant, different session
        var items  = new[] { Item(first), Item(first), Item(second) };

        var result = IngestTelemetryFunction.FindMismatchingPartitionKey(items, out var idx, out var value);

        Assert.True(result);
        Assert.Equal(2, idx);
        Assert.Equal(second, value);
    }

    [Fact]
    public void FindMismatchingPartitionKey_is_case_sensitive()
    {
        // PartitionKeys are GUIDs — matching is ordinal. A lowercase/uppercase-swapped second
        // item is treated as a different partition (safe-by-default; the agent emits lowercase).
        var first  = $"{TenantId}_{SessionId}";
        var second = first.ToUpperInvariant();
        var items  = new[] { Item(first), Item(second) };

        var result = IngestTelemetryFunction.FindMismatchingPartitionKey(items, out var idx, out var value);

        Assert.True(result);
        Assert.Equal(1, idx);
        Assert.Equal(second, value);
    }
}

using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// RowKey → business-time decoders. These are the read-path compensation for rows whose
/// system Timestamp was reset by the 2026-07-18 storage migration: the RowKey is the only
/// time carrier that survives a row rewrite, so the decode must round-trip exactly and
/// must reject every foreign format (falling back instead of mis-dating rows).
/// </summary>
public class BusinessTimestampDecodeTests
{
    private static readonly DateTime Ts = new DateTime(2026, 6, 15, 10, 30, 45, 123, DateTimeKind.Utc).AddTicks(4567);

    [Fact]
    public void Audit_rowkey_roundtrips_tick_exact()
    {
        var rowKey = TableStorageService.BuildAuditLogRowKey(Ts, Guid.NewGuid());
        Assert.True(BusinessTimestamp.TryDecodeAuditRowKey(rowKey, out var decoded));
        Assert.Equal(Ts, decoded);
        Assert.Equal(DateTimeKind.Utc, decoded.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!")]
    [InlineData("!123")] // too short
    [InlineData("!123456789012345678x_guid")] // non-digit in revtick
    [InlineData("d6f6dd90f41c4b16b290aac042e5b466")] // legacy bare GUID (:N)
    [InlineData("0000000000000000000")] // ops-style bare revtick is not an audit key
    public void Audit_decoder_rejects_foreign_formats(string? rowKey)
    {
        Assert.False(BusinessTimestamp.TryDecodeAuditRowKey(rowKey, out _));
    }

    [Fact]
    public void Ops_rowkey_roundtrips_tick_exact()
    {
        var rowKey = $"{DateTime.MaxValue.Ticks - Ts.Ticks:D19}";
        Assert.True(BusinessTimestamp.TryDecodeOpsRowKey(rowKey, out var decoded));
        Assert.Equal(Ts, decoded);
        Assert.Equal(DateTimeKind.Utc, decoded.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("123456789012345678")] // 18 digits
    [InlineData("12345678901234567890")] // 20 digits
    [InlineData("123456789012345678x")] // non-digit
    [InlineData("!1234567890123456789")] // audit-prefixed
    public void Ops_decoder_rejects_foreign_formats(string? rowKey)
    {
        Assert.False(BusinessTimestamp.TryDecodeOpsRowKey(rowKey, out _));
    }

    [Fact]
    public void Event_rowkey_prefix_decodes_to_millisecond_truncated_agent_time()
    {
        var rowKey = $"{Ts:yyyyMMddHHmmssfff}_{42L:D10}";
        Assert.True(BusinessTimestamp.TryDecodeEventRowKeyPrefix(rowKey, out var decoded));
        // The RowKey format truncates sub-millisecond ticks at write time.
        var expected = new DateTime(2026, 6, 15, 10, 30, 45, 123, DateTimeKind.Utc);
        Assert.Equal(expected, decoded);
        Assert.Equal(DateTimeKind.Utc, decoded.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("20260615103045123")] // no separator/sequence
    [InlineData("2026061510304512_0000000042")] // 16-digit prefix
    [InlineData("202606151030451234_0000000042")] // 18-digit prefix
    [InlineData("2026061510304512x_0000000042")] // non-digit in prefix
    [InlineData("20261315103045123_0000000042")] // month 13
    [InlineData("d6f6dd90f41c4b16b290aac042e5b466")] // GUID
    public void Event_decoder_rejects_foreign_formats(string? rowKey)
    {
        Assert.False(BusinessTimestamp.TryDecodeEventRowKeyPrefix(rowKey, out _));
    }
}

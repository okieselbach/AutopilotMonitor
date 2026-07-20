using Azure.Data.Tables;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Read-mapper resolution order for the three affected tables:
/// OccurredUtc column → RowKey decode → system Timestamp. The last hop is the
/// migration-corrupted value (a row rewrite resets it), so it must only ever win when
/// the row carries no better source (audit legacy bare-GUID rows).
/// </summary>
public class BusinessTimestampPrecedenceTests
{
    private static readonly DateTime Occurred = new(2026, 5, 10, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime RowKeyTime = new(2026, 6, 20, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset SystemTs = new(2026, 7, 18, 13, 58, 0, TimeSpan.Zero); // migration moment

    private static TableEntity WithSystemTimestamp(TableEntity e)
    {
        e["Timestamp"] = SystemTs;
        return e;
    }

    // ===== Audit =====

    [Fact]
    public void Audit_occurredutc_wins_over_rowkey_and_system()
    {
        var e = WithSystemTimestamp(new TableEntity("t", TableStorageService.BuildAuditLogRowKey(RowKeyTime, Guid.NewGuid()))
        {
            [BusinessTimestamp.OccurredUtcColumn] = new DateTimeOffset(Occurred),
        });
        Assert.Equal(Occurred, TableStorageService.ResolveAuditTimestamp(e));
    }

    [Fact]
    public void Audit_rowkey_decode_wins_over_system_when_column_missing()
    {
        var e = WithSystemTimestamp(new TableEntity("t", TableStorageService.BuildAuditLogRowKey(RowKeyTime, Guid.NewGuid())));
        Assert.Equal(RowKeyTime, TableStorageService.ResolveAuditTimestamp(e));
    }

    [Fact]
    public void Audit_legacy_guid_row_falls_back_to_system_timestamp()
    {
        var e = WithSystemTimestamp(new TableEntity("t", Guid.NewGuid().ToString("N")));
        Assert.Equal(SystemTs.UtcDateTime, TableStorageService.ResolveAuditTimestamp(e));
    }

    // ===== Ops =====

    [Fact]
    public void Ops_occurredutc_wins_over_rowkey_and_system()
    {
        var e = WithSystemTimestamp(new TableEntity("Security", $"{DateTime.MaxValue.Ticks - RowKeyTime.Ticks:D19}")
        {
            [BusinessTimestamp.OccurredUtcColumn] = new DateTimeOffset(Occurred),
        });
        Assert.Equal(Occurred, TableOpsEventRepository.ResolveTimestamp(e));
    }

    [Fact]
    public void Ops_rowkey_decode_wins_over_system_when_column_missing()
    {
        var e = WithSystemTimestamp(new TableEntity("Security", $"{DateTime.MaxValue.Ticks - RowKeyTime.Ticks:D19}"));
        Assert.Equal(RowKeyTime, TableOpsEventRepository.ResolveTimestamp(e));
    }

    [Fact]
    public void Ops_undecodable_rowkey_falls_back_to_system_timestamp()
    {
        var e = WithSystemTimestamp(new TableEntity("Security", "not-a-revtick"));
        Assert.Equal(SystemTs.UtcDateTime, TableOpsEventRepository.ResolveTimestamp(e));
    }

    // ===== Events =====

    private static string EventRowKey(DateTime ts, long seq = 42) => $"{ts:yyyyMMddHHmmssfff}_{seq:D10}";

    [Fact]
    public void Event_occurredutc_wins_over_rowkey_and_system()
    {
        var e = WithSystemTimestamp(new TableEntity("tenant_session", EventRowKey(RowKeyTime))
        {
            [BusinessTimestamp.OccurredUtcColumn] = new DateTimeOffset(Occurred),
        });
        Assert.Equal(Occurred, TableStorageService.ResolveEventTimestamp(e));
    }

    [Fact]
    public void Event_rowkey_prefix_wins_over_system_when_column_missing()
    {
        // The pre-cutover case: no OccurredUtc column, system Timestamp = migration moment.
        var e = WithSystemTimestamp(new TableEntity("tenant_session", EventRowKey(RowKeyTime)));
        Assert.Equal(RowKeyTime, TableStorageService.ResolveEventTimestamp(e));
    }

    [Fact]
    public void Event_undecodable_rowkey_falls_back_to_system_timestamp()
    {
        var e = WithSystemTimestamp(new TableEntity("tenant_session", "weird-rowkey"));
        Assert.Equal(SystemTs.UtcDateTime, TableStorageService.ResolveEventTimestamp(e));
    }

    [Fact]
    public void Event_occurredutc_read_tolerates_datetime_materialization()
    {
        // SDK reads surface Edm.DateTime as DateTimeOffset or DateTime depending on payload
        // shape — the resolver must accept both (dual-reader lesson).
        var e = new TableEntity("tenant_session", EventRowKey(RowKeyTime))
        {
            [BusinessTimestamp.OccurredUtcColumn] = Occurred, // plain DateTime, not DateTimeOffset
        };
        Assert.Equal(Occurred, TableStorageService.ResolveEventTimestamp(e));
    }
}

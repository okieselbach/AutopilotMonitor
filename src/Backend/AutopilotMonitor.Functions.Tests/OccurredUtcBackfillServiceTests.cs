using Azure.Data.Tables;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Per-row backfill decision (pure function): rows that already carry OccurredUtc are
/// skipped (idempotent re-runs), decodable RowKeys yield the deterministic decoded value,
/// anything else is reported undecodable — never guessed.
/// </summary>
public class OccurredUtcBackfillServiceTests
{
    private static readonly DateTime Ts = new(2026, 6, 20, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Audit_row_with_existing_column_is_skipped()
    {
        var e = new TableEntity("t", TableStorageService.BuildAuditLogRowKey(Ts, Guid.NewGuid()))
        {
            [BusinessTimestamp.OccurredUtcColumn] = new DateTimeOffset(Ts),
        };
        Assert.Equal(OccurredUtcBackfillService.RowDecision.AlreadySet,
            OccurredUtcBackfillService.DecideRow(OccurredUtcBackfillService.TableAudit, e, out _));
    }

    [Fact]
    public void Audit_time_encoded_row_yields_decoded_write()
    {
        var e = new TableEntity("t", TableStorageService.BuildAuditLogRowKey(Ts, Guid.NewGuid()));
        var decision = OccurredUtcBackfillService.DecideRow(OccurredUtcBackfillService.TableAudit, e, out var occurred);
        Assert.Equal(OccurredUtcBackfillService.RowDecision.Write, decision);
        Assert.Equal(Ts, occurred);
    }

    [Fact]
    public void Audit_legacy_guid_row_is_undecodable_not_guessed()
    {
        var e = new TableEntity("t", Guid.NewGuid().ToString("N"));
        Assert.Equal(OccurredUtcBackfillService.RowDecision.Undecodable,
            OccurredUtcBackfillService.DecideRow(OccurredUtcBackfillService.TableAudit, e, out _));
    }

    [Fact]
    public void Ops_row_yields_decoded_write_and_existing_column_is_skipped()
    {
        var rowKey = $"{DateTime.MaxValue.Ticks - Ts.Ticks:D19}";

        var bare = new TableEntity("Security", rowKey);
        var decision = OccurredUtcBackfillService.DecideRow(OccurredUtcBackfillService.TableOps, bare, out var occurred);
        Assert.Equal(OccurredUtcBackfillService.RowDecision.Write, decision);
        Assert.Equal(Ts, occurred);

        var backfilled = new TableEntity("Security", rowKey)
        {
            [BusinessTimestamp.OccurredUtcColumn] = new DateTimeOffset(Ts),
        };
        Assert.Equal(OccurredUtcBackfillService.RowDecision.AlreadySet,
            OccurredUtcBackfillService.DecideRow(OccurredUtcBackfillService.TableOps, backfilled, out _));
    }
}

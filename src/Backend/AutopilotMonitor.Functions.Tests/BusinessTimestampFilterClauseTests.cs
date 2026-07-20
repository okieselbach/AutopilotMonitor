using System.Text.RegularExpressions;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// RowKey-range date-window / retention clauses. Date filtering moved off the system
/// Timestamp (reset by storage migrations) onto the reverse-tick RowKey; these tests pin
/// (a) tick-exact inclusive/exclusive boundary semantics identical to the old
/// `Timestamp ge/le` clauses, and (b) the GUID-safety invariant: legacy bare-GUID audit
/// rows sort AFTER all '!'-rows, so any lower RowKey bound without the `RowKey lt '"'`
/// guard would match — and a retention sweep would DELETE — every legacy row.
/// </summary>
public class BusinessTimestampFilterClauseTests
{
    private static readonly DateTime T = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc).AddTicks(789);
    private static readonly string Tenant = "11111111-1111-1111-1111-111111111111";

    private static string AuditRk(DateTime ts) => TableStorageService.BuildAuditLogRowKey(ts, Guid.NewGuid());
    private static string OpsRk(DateTime ts) => $"{DateTime.MaxValue.Ticks - ts.Ticks:D19}";

    /// <summary>Evaluates a clause of the form "RowKey op 'literal' [and RowKey op 'literal']…" against a RowKey.</summary>
    private static bool Matches(string clause, string rowKey)
    {
        var parts = Regex.Matches(clause, @"RowKey (ge|gt|le|lt) '([^']*)'");
        Assert.True(parts.Count > 0, $"no RowKey comparisons in clause: {clause}");
        foreach (Match m in parts)
        {
            var cmp = string.CompareOrdinal(rowKey, m.Groups[2].Value);
            var ok = m.Groups[1].Value switch
            {
                "ge" => cmp >= 0,
                "gt" => cmp > 0,
                "le" => cmp <= 0,
                "lt" => cmp < 0,
                _ => throw new InvalidOperationException(),
            };
            if (!ok) return false;
        }
        return true;
    }

    // ===== Audit boundaries =====

    [Fact]
    public void Audit_dateFrom_is_inclusive_to_the_tick()
    {
        var clause = BusinessTimestamp.AuditDateFromClause(T);
        Assert.True(Matches(clause, AuditRk(T)));                  // ts == from → included
        Assert.True(Matches(clause, AuditRk(T.AddTicks(1))));      // newer → included
        Assert.False(Matches(clause, AuditRk(T.AddTicks(-1))));    // 1 tick older → excluded
    }

    [Fact]
    public void Audit_dateTo_is_inclusive_to_the_tick()
    {
        var clause = BusinessTimestamp.AuditDateToClause(T);
        Assert.True(Matches(clause, AuditRk(T)));                  // ts == to → included
        Assert.True(Matches(clause, AuditRk(T.AddTicks(-1))));     // older → included
        Assert.False(Matches(clause, AuditRk(T.AddTicks(1))));     // 1 tick newer → excluded
    }

    [Fact]
    public void Audit_retention_is_strictly_older_than_cutoff()
    {
        var clause = BusinessTimestamp.AuditRetentionClause(T);
        Assert.False(Matches(clause, AuditRk(T)));                 // ts == cutoff → kept
        Assert.True(Matches(clause, AuditRk(T.AddTicks(-1))));     // 1 tick older → deleted
        Assert.False(Matches(clause, AuditRk(T.AddTicks(1))));     // newer → kept
    }

    // ===== Audit GUID safety (the single most dangerous invariant of this change) =====

    [Fact]
    public void Legacy_guid_rowkeys_sort_after_the_time_encoded_guard()
    {
        for (var i = 0; i < 20; i++)
        {
            var guidRk = Guid.NewGuid().ToString("N");
            Assert.True(string.CompareOrdinal(guidRk, BusinessTimestamp.AuditTimeEncodedUpperBound) > 0);
        }
    }

    [Fact]
    public void No_audit_date_or_retention_clause_matches_legacy_guid_rowkeys()
    {
        var clauses = new[]
        {
            BusinessTimestamp.AuditDateFromClause(T),
            BusinessTimestamp.AuditDateToClause(T),
            BusinessTimestamp.AuditDateToClause(DateTime.MaxValue.AddTicks(-1)), // widest possible window
            BusinessTimestamp.AuditRetentionClause(T),
            BusinessTimestamp.AuditRetentionClause(DateTime.MaxValue.AddTicks(-1)), // widest possible sweep
        };
        foreach (var clause in clauses)
        {
            for (var i = 0; i < 20; i++)
            {
                Assert.False(Matches(clause, Guid.NewGuid().ToString("N")),
                    $"clause matched a legacy GUID RowKey: {clause}");
            }
        }
    }

    [Fact]
    public void Audit_lower_bounded_clauses_carry_the_time_encoded_guard()
    {
        Assert.Contains($"RowKey lt '{BusinessTimestamp.AuditTimeEncodedUpperBound}'",
            BusinessTimestamp.AuditDateToClause(T));
        Assert.Contains($"RowKey lt '{BusinessTimestamp.AuditTimeEncodedUpperBound}'",
            BusinessTimestamp.AuditRetentionClause(T));
    }

    // ===== Ops boundaries =====

    [Fact]
    public void Ops_dateFrom_is_inclusive_to_the_tick()
    {
        var clause = BusinessTimestamp.OpsDateFromClause(T);
        Assert.True(Matches(clause, OpsRk(T)));
        Assert.True(Matches(clause, OpsRk(T.AddTicks(1))));
        Assert.False(Matches(clause, OpsRk(T.AddTicks(-1))));
    }

    [Fact]
    public void Ops_dateTo_is_inclusive_to_the_tick()
    {
        var clause = BusinessTimestamp.OpsDateToClause(T);
        Assert.True(Matches(clause, OpsRk(T)));
        Assert.True(Matches(clause, OpsRk(T.AddTicks(-1))));
        Assert.False(Matches(clause, OpsRk(T.AddTicks(1))));
    }

    [Fact]
    public void Ops_retention_is_strictly_older_than_cutoff()
    {
        var clause = BusinessTimestamp.OpsRetentionClause(T);
        Assert.False(Matches(clause, OpsRk(T)));
        Assert.True(Matches(clause, OpsRk(T.AddTicks(-1))));
        Assert.False(Matches(clause, OpsRk(T.AddTicks(1))));
    }

    // ===== Composition into the filter builders =====

    [Fact]
    public void Audit_filter_builders_emit_rowkey_windows_not_timestamp_clauses()
    {
        var from = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var plain = TableStorageService.BuildAuditLogFilter(Tenant, from, to);
        var fanOut = TableStorageService.BuildAuditLogFilterWithRowKeyBound(
            Tenant, from, to, lastRowKey: "!0123_x", excludeDeletions: false);

        foreach (var f in new[] { plain!, fanOut })
        {
            Assert.DoesNotContain("Timestamp ge", f);
            Assert.DoesNotContain("Timestamp le", f);
            Assert.Contains(BusinessTimestamp.AuditDateFromClause(from), f);
            Assert.Contains(BusinessTimestamp.AuditDateToClause(to), f);
        }
        Assert.Contains("RowKey gt '!0123_x'", fanOut); // fan-out cursor composes with the window
    }

    [Fact]
    public void Ops_filter_builder_emits_rowkey_window_not_timestamp_clauses()
    {
        var from = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var f = TableOpsEventRepository.BuildFilter("Security", from, to);

        Assert.NotNull(f);
        Assert.DoesNotContain("Timestamp ge", f);
        Assert.DoesNotContain("Timestamp le", f);
        Assert.Contains(BusinessTimestamp.OpsDateFromClause(from), f);
        Assert.Contains(BusinessTimestamp.OpsDateToClause(to), f);
    }
}

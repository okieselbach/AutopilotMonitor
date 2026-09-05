using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The raw-events date window used to compare the Azure SYSTEM Timestamp (row write time —
/// every storage migration resets it to the migration moment). These tests pin the business
/// resolution order for raw rows (OccurredUtc → RowKey prefix → system Timestamp), the
/// EventTypeIndex tenant resolution (column → PartitionKey suffix), the index write-time
/// pre-filter slack, and UTC-only parsing of the query dates.
/// </summary>
public class RawEventTimeTests
{
    private static readonly DateTime Occurred = new(2026, 8, 29, 20, 11, 0, DateTimeKind.Utc);
    private static readonly DateTime Migration = new(2026, 7, 18, 13, 58, 0, DateTimeKind.Utc);

    private static Dictionary<string, object?> RowWith(params (string Key, object? Value)[] cols)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in cols) d[k] = v;
        return d;
    }

    // ===== RawEventTime.Resolve =====

    [Fact]
    public void OccurredUtc_column_wins_over_RowKey_and_system_Timestamp()
    {
        var row = RowWith(
            ("OccurredUtc", new DateTimeOffset(Occurred)),
            ("RowKey", $"{Occurred.AddHours(-1):yyyyMMddHHmmssfff}_0000000042"),
            ("Timestamp", new DateTimeOffset(Migration)));

        Assert.Equal(Occurred, RawEventTime.Resolve(row));
    }

    [Fact]
    public void RowKey_prefix_is_used_when_OccurredUtc_is_absent()
    {
        // Pre-column rows: the sanitized agent time survives in the RowKey; the system
        // Timestamp says "migration day" and must be ignored.
        var row = RowWith(
            ("RowKey", $"{Occurred:yyyyMMddHHmmssfff}_0000000042"),
            ("Timestamp", new DateTimeOffset(Migration)));

        Assert.Equal(Occurred, RawEventTime.Resolve(row));
    }

    [Fact]
    public void System_Timestamp_is_the_last_resort_and_DateTime_kinds_normalize_to_utc()
    {
        var unspecified = DateTime.SpecifyKind(Migration, DateTimeKind.Unspecified);
        var row = RowWith(("RowKey", "not-a-time-key"), ("Timestamp", unspecified));

        var resolved = RawEventTime.Resolve(row);
        Assert.Equal(Migration, resolved);
        Assert.Equal(DateTimeKind.Utc, resolved.Kind);
    }

    [Fact]
    public void OccurredUtc_as_DateTime_value_is_honoured()
    {
        // Service reads materialize Edm.DateTime as DateTimeOffset OR DateTime depending on payload shape.
        var row = RowWith(("OccurredUtc", Occurred));
        Assert.Equal(Occurred, RawEventTime.Resolve(row));
    }

    [Fact]
    public void Row_without_any_time_resolves_to_MinValue()
    {
        Assert.Equal(DateTime.MinValue, RawEventTime.Resolve(RowWith(("EventType", "x"))));
    }

    // ===== IndexRowKeys.ResolveTenantId =====

    private const string TenantA = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

    [Fact]
    public void TenantId_column_is_preferred()
    {
        Assert.Equal(TenantA, IndexRowKeys.ResolveTenantId("whatever_app_install_failed", "app_install_failed", TenantA));
    }

    [Fact]
    public void PartitionKey_suffix_strip_works_for_the_CveIndex_shape()
    {
        Assert.Equal(TenantA, IndexRowKeys.ResolveTenantId($"{TenantA}_CVE-2024-21447", "CVE-2024-21447", null));
        Assert.Null(IndexRowKeys.ResolveTenantId($"{TenantA}_CVE-2024-21447", "CVE-2024-99999", null));
    }

    [Fact]
    public void PartitionKey_suffix_strip_handles_multi_underscore_event_types()
    {
        Assert.Equal(TenantA, IndexRowKeys.ResolveTenantId($"{TenantA}_app_install_failed", "app_install_failed", null));
        Assert.Equal(TenantA, IndexRowKeys.ResolveTenantId($"{TenantA}_whiteglove_complete", "whiteglove_complete", ""));
    }

    [Theory]
    [InlineData("app_install_failed")]                       // no tenant prefix at all
    [InlineData("a1b2c3d4_x_app_install_failed")]            // prefix contains '_' → not a tenant id
    [InlineData("a1b2c3d4-e5f6-7890-abcd-ef1234567890_other")] // different event type
    public void Foreign_PartitionKey_shapes_yield_null(string partitionKey)
    {
        Assert.Null(IndexRowKeys.ResolveTenantId(partitionKey, "app_install_failed", null));
    }

    // ===== Index write-time hint =====

    [Fact]
    public void IndexWrittenAfterHint_subtracts_the_future_clamp_plus_one_hour()
    {
        var expectedSlack = TimeSpan.FromHours(EventTimestampValidator.MaxFutureToleranceHours + 1);
        Assert.Equal(expectedSlack, QueryRawEventsPagination.IndexWriteTimeSlack);
        Assert.Equal(Occurred - expectedSlack, QueryRawEventsPagination.IndexWrittenAfterHint(Occurred));
        Assert.Null(QueryRawEventsPagination.IndexWrittenAfterHint(null));
    }
}

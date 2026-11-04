using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Ranking of <see cref="TableDistressReportRepository.KeepNewestAsync"/>: the table yields rows in
/// tenant order, so a cap must keep the newest rows across tenants, never the first rows read.
/// </summary>
public class TableDistressReportRepositoryNewestTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static DistressReportEntry Entry(string tenantId, int minute) => new()
    {
        TenantId = tenantId,
        ErrorType = "DeviceNotRegistered",
        SerialNumber = $"SN-{tenantId[0]}-{minute}",
        IngestedAt = T0.AddMinutes(minute),
    };

    private static async IAsyncEnumerable<DistressReportEntry> Source(params DistressReportEntry[] entries)
    {
        foreach (var entry in entries)
        {
            await Task.Yield();
            yield return entry;
        }
    }

    // Tenant order as the table returns it: all rows of tenant "a", then tenant "b", each newest first.
    private static DistressReportEntry[] TwoTenantsInterleavedInTime() =>
    [
        Entry("aaaaaaaa", 50), Entry("aaaaaaaa", 30), Entry("aaaaaaaa", 10),
        Entry("bbbbbbbb", 60), Entry("bbbbbbbb", 40), Entry("bbbbbbbb", 20),
    ];

    [Fact]
    public async Task OverTheCap_KeepsNewestAcrossTenants_NewestFirst()
    {
        var (reports, truncated) = await TableDistressReportRepository.KeepNewestAsync(
            Source(TwoTenantsInterleavedInTime()), maxResults: 4);

        Assert.True(truncated);
        Assert.Equal(new[] { 60, 50, 40, 30 }, reports.Select(r => (int)(r.IngestedAt - T0).TotalMinutes));
    }

    [Fact]
    public async Task AtTheCap_ReturnsAll_NotTruncated()
    {
        var (reports, truncated) = await TableDistressReportRepository.KeepNewestAsync(
            Source(TwoTenantsInterleavedInTime()), maxResults: 6);

        Assert.False(truncated);
        Assert.Equal(new[] { 60, 50, 40, 30, 20, 10 }, reports.Select(r => (int)(r.IngestedAt - T0).TotalMinutes));
    }

    [Fact]
    public async Task EmptyTable_ReturnsEmpty_NotTruncated()
    {
        var (reports, truncated) = await TableDistressReportRepository.KeepNewestAsync(Source(), maxResults: 4);

        Assert.Empty(reports);
        Assert.False(truncated);
    }
}

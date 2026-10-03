using System.Globalization;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The versioned platform-stats file is served immutable for a year, so every publish must write
/// a new name. Maintenance publishes every 2 hours; the per-day name it used to write was rewritten
/// up to 12 times a day, and caches kept the first copy of the day.
/// </summary>
public class PlatformStatsPublishTests
{
    private static readonly DateTime Publish = new(2026, 10, 3, 6, 0, 4, DateTimeKind.Utc);

    [Fact]
    public void Versioned_file_name_carries_the_publish_instant_to_the_second()
    {
        Assert.Equal("platform-stats.2026-10-03T060004Z.json", MaintenanceService.PlatformStatsVersionedFileName(Publish));
    }

    [Fact]
    public void Two_publishes_of_one_day_get_different_names()
    {
        var early = MaintenanceService.PlatformStatsVersionedFileName(new DateTime(2026, 10, 3, 2, 0, 3, DateTimeKind.Utc));
        var later = MaintenanceService.PlatformStatsVersionedFileName(new DateTime(2026, 10, 3, 4, 0, 3, DateTimeKind.Utc));

        Assert.NotEqual(early, later);
    }

    [Fact]
    public void Versioned_file_name_ignores_the_current_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // th-TH counts years in the Buddhist era (2569 for 2026).
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            Assert.Equal("platform-stats.2026-10-03T060004Z.json", MaintenanceService.PlatformStatsVersionedFileName(Publish));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}

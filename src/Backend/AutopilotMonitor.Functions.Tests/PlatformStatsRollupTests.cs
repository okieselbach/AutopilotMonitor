using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the "since release" semantics of the platform-stats rollup: a cumulative counter is
/// the persisted total plus the growth of its source since the previous run. A total derived
/// from an absolute figure stops moving for good once that figure shrinks below it — retention
/// prunes sessions, offboarding deletes a tenant's counters — which froze the public
/// landing-page figures while enrollments kept arriving.
/// </summary>
public class PlatformStatsRollupTests
{
    private static readonly DateTime Now = new(2026, 07, 11, 12, 0, 0, DateTimeKind.Utc);

    private static PlatformRollupSources Sources(long enrollments, long successful = 0, long events = 0, long tenants = 0, long models = 0)
        => new()
        {
            TotalEnrollments = enrollments,
            SuccessfulEnrollments = successful,
            TotalEventsProcessed = events,
            ActiveTenants = tenants,
            SeenDeviceModels = models
        };

    [Fact]
    public void Counters_grow_by_the_source_delta_although_the_source_is_far_below_the_total()
    {
        // The freeze scenario: the persisted total (19 312) is above anything the source can
        // show, yet 40 enrollments arrived since the last run. Max(source, total) never moves.
        var existing = new PlatformStats
        {
            TotalEnrollments = 19_312,
            SuccessfulEnrollments = 10_289,
            TotalEventsProcessed = 5_779_212,
            TotalTenants = 84,
            UniqueDeviceModels = 486
        };

        var stats = MaintenanceService.BuildPlatformStatsRollup(
            existing,
            baseline: Sources(14_540, successful: 10_240, events: 4_000_000, tenants: 80, models: 430),
            sources: Sources(14_580, successful: 10_275, events: 4_012_000, tenants: 81, models: 433),
            recomputedUsers: 0, signedUpTenants: 0, nowUtc: Now);

        Assert.Equal(19_352, stats.TotalEnrollments);
        Assert.Equal(10_324, stats.SuccessfulEnrollments);
        Assert.Equal(5_791_212, stats.TotalEventsProcessed);
        Assert.Equal(85, stats.TotalTenants);
        Assert.Equal(489, stats.UniqueDeviceModels);
    }

    [Fact]
    public void A_shrunken_source_never_lowers_the_total_and_the_next_run_counts_again()
    {
        // Offboarding deleted a tenant's counters (−3 000) while 20 enrollments arrived: the
        // delta of this interval is clamped, the baseline follows, the next interval is exact.
        var existing = new PlatformStats { TotalEnrollments = 50_000 };
        var afterOffboarding = Sources(37_020);

        var clamped = MaintenanceService.BuildPlatformStatsRollup(
            existing, baseline: Sources(40_000), sources: afterOffboarding,
            recomputedUsers: 0, signedUpTenants: 0, nowUtc: Now);
        Assert.Equal(50_000, clamped.TotalEnrollments);

        var next = MaintenanceService.BuildPlatformStatsRollup(
            clamped, baseline: afterOffboarding, sources: Sources(37_055),
            recomputedUsers: 0, signedUpTenants: 0, nowUtc: Now);
        Assert.Equal(50_035, next.TotalEnrollments);
    }

    [Fact]
    public void First_run_without_a_baseline_treats_the_source_as_a_lower_bound_only()
    {
        // No baseline yet: whatever the sources show is already contained in the persisted
        // totals (or exceeds them and heals them) — it must never be added on top.
        var existing = new PlatformStats { TotalEnrollments = 19_312, SuccessfulEnrollments = 10_289 };

        var stats = MaintenanceService.BuildPlatformStatsRollup(
            existing, baseline: null, sources: Sources(21_687, successful: 10_275),
            recomputedUsers: 0, signedUpTenants: 0, nowUtc: Now);

        Assert.Equal(21_687, stats.TotalEnrollments);
        Assert.Equal(10_289, stats.SuccessfulEnrollments);
    }

    [Fact]
    public void First_run_without_a_persisted_row_starts_from_the_sources()
    {
        var stats = MaintenanceService.BuildPlatformStatsRollup(
            existing: null, baseline: null,
            sources: Sources(10, successful: 8, events: 500, tenants: 2, models: 4),
            recomputedUsers: 3, signedUpTenants: 5, nowUtc: Now);

        Assert.Equal(10, stats.TotalEnrollments);
        Assert.Equal(8, stats.SuccessfulEnrollments);
        Assert.Equal(500, stats.TotalEventsProcessed);
        Assert.Equal(3, stats.TotalUsers);
        Assert.Equal(2, stats.TotalTenants);
        Assert.Equal(4, stats.UniqueDeviceModels);
        Assert.Equal(0, stats.IssuesDetected);
        Assert.Equal(5, stats.TotalSignedUpTenants);
        Assert.Equal(Now, stats.LastFullCompute);
        Assert.Equal(Now, stats.LastUpdated);
    }

    [Fact]
    public void Users_stay_a_high_water_mark_and_issues_are_carried_over()
    {
        var existing = new PlatformStats { TotalUsers = 800, IssuesDetected = 6_500 };

        var pruned = MaintenanceService.BuildPlatformStatsRollup(
            existing, baseline: Sources(0), sources: Sources(0),
            recomputedUsers: 90, signedUpTenants: 0, nowUtc: Now);
        Assert.Equal(800, pruned.TotalUsers);
        Assert.Equal(6_500, pruned.IssuesDetected);

        var grown = MaintenanceService.BuildPlatformStatsRollup(
            existing, baseline: Sources(0), sources: Sources(0),
            recomputedUsers: 950, signedUpTenants: 0, nowUtc: Now);
        Assert.Equal(950, grown.TotalUsers);
    }

    [Fact]
    public void SignedUpTenants_is_current_state_and_may_drop()
    {
        // Deliberate exception: TenantConfiguration is not retention-pruned, so its count is
        // authoritative — a drop reflects real offboarding, not data loss.
        var existing = new PlatformStats { TotalSignedUpTenants = 150 };

        var stats = MaintenanceService.BuildPlatformStatsRollup(
            existing, baseline: Sources(0), sources: Sources(0),
            recomputedUsers: 0, signedUpTenants: 140, nowUtc: Now);

        Assert.Equal(140, stats.TotalSignedUpTenants);
    }
}

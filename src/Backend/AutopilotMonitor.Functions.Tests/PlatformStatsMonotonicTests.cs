using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the "since release" semantics of the platform-stats recompute: every cumulative
/// counter is a monotonic high-water-mark. The tables the recompute scans are pruned by
/// retention, so a raw recompute only sees the retention window — persisting that verbatim
/// regressed the public landing-page figures after every cleanup.
/// </summary>
public class PlatformStatsMonotonicTests
{
    private static readonly DateTime Now = new(2026, 07, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Cumulative_counters_never_regress_below_persisted_values()
    {
        // Retention pruned old sessions: the recompute sees far less than the persisted
        // counters accumulated. Every cumulative figure must keep its high-water-mark.
        var existing = new PlatformStats
        {
            TotalEnrollments = 50_000,
            SuccessfulEnrollments = 40_000,
            TotalEventsProcessed = 9_000_000,
            TotalUsers = 800,
            TotalTenants = 120,
            UniqueDeviceModels = 450,
            IssuesDetected = 6_500,
            TotalSignedUpTenants = 150
        };

        var stats = MaintenanceService.BuildMonotonicPlatformStats(
            recomputedEnrollments: 7_000,
            recomputedSuccessful: 6_000,
            recomputedEvents: 1_200_000,
            recomputedUsers: 90,
            recomputedActiveTenants: 80,
            recomputedUniqueModels: 200,
            signedUpTenants: 155,
            existing: existing,
            nowUtc: Now);

        Assert.Equal(50_000, stats.TotalEnrollments);
        Assert.Equal(40_000, stats.SuccessfulEnrollments);
        Assert.Equal(9_000_000, stats.TotalEventsProcessed);
        Assert.Equal(800, stats.TotalUsers);
        Assert.Equal(120, stats.TotalTenants);
        Assert.Equal(450, stats.UniqueDeviceModels);
        Assert.Equal(6_500, stats.IssuesDetected);
    }

    [Fact]
    public void Recompute_raises_counters_when_live_data_exceeds_persisted_values()
    {
        // Self-heal: lost increments (fire-and-forget failures, pre-feature history) are
        // recovered as long as the sessions are still within retention.
        var existing = new PlatformStats
        {
            TotalEnrollments = 1_000,
            SuccessfulEnrollments = 700,
            TotalEventsProcessed = 100_000,
            TotalUsers = 50,
            TotalTenants = 10,
            UniqueDeviceModels = 30
        };

        var stats = MaintenanceService.BuildMonotonicPlatformStats(
            recomputedEnrollments: 1_500,
            recomputedSuccessful: 1_100,
            recomputedEvents: 250_000,
            recomputedUsers: 80,
            recomputedActiveTenants: 14,
            recomputedUniqueModels: 42,
            signedUpTenants: 20,
            existing: existing,
            nowUtc: Now);

        Assert.Equal(1_500, stats.TotalEnrollments);
        Assert.Equal(1_100, stats.SuccessfulEnrollments);
        Assert.Equal(250_000, stats.TotalEventsProcessed);
        Assert.Equal(80, stats.TotalUsers);
        Assert.Equal(14, stats.TotalTenants);
        Assert.Equal(42, stats.UniqueDeviceModels);
    }

    [Fact]
    public void SignedUpTenants_is_current_state_and_may_drop()
    {
        // Deliberate exception: TenantConfiguration is not retention-pruned, so its count is
        // authoritative — a drop reflects real offboarding, not data loss.
        var existing = new PlatformStats { TotalSignedUpTenants = 150 };

        var stats = MaintenanceService.BuildMonotonicPlatformStats(
            recomputedEnrollments: 0, recomputedSuccessful: 0, recomputedEvents: 0,
            recomputedUsers: 0, recomputedActiveTenants: 0, recomputedUniqueModels: 0,
            signedUpTenants: 140,
            existing: existing,
            nowUtc: Now);

        Assert.Equal(140, stats.TotalSignedUpTenants);
    }

    [Fact]
    public void First_run_without_persisted_row_uses_recomputed_values()
    {
        var stats = MaintenanceService.BuildMonotonicPlatformStats(
            recomputedEnrollments: 10,
            recomputedSuccessful: 8,
            recomputedEvents: 500,
            recomputedUsers: 3,
            recomputedActiveTenants: 2,
            recomputedUniqueModels: 4,
            signedUpTenants: 5,
            existing: null,
            nowUtc: Now);

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
}

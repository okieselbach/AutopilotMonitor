using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tier-boundary + percent-math tests for the SignalR quota watcher.
/// Mirrors the cert-expiry tests' approach: pure-function coverage of the
/// boundary risks (off-by-one, floor vs ceil, zero-division). Full-service
/// smoke tests would require constructing 16 dependencies; the percent +
/// classifier are the parts most likely to break the watcher silently.
/// </summary>
public class MaintenanceServiceSignalRQuotaTests
{
    // --- ClassifySignalRQuotaTier ---
    // Thresholds (per MaintenanceService.SignalRQuota.cs):
    //   >= 95% -> Critical
    //   >= 80% -> Warning
    //   else   -> None

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(79)]
    public void ClassifySignalRQuotaTier_BelowWarning_ReturnsNone(int percent)
    {
        Assert.Equal(
            MaintenanceService.SignalRQuotaTier.None,
            MaintenanceService.ClassifySignalRQuotaTier(percent));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(85)]
    [InlineData(94)]
    public void ClassifySignalRQuotaTier_InWarningBand_ReturnsWarning(int percent)
    {
        Assert.Equal(
            MaintenanceService.SignalRQuotaTier.Warning,
            MaintenanceService.ClassifySignalRQuotaTier(percent));
    }

    [Theory]
    [InlineData(95)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(120)]
    public void ClassifySignalRQuotaTier_AtOrAboveCritical_ReturnsCritical(int percent)
    {
        Assert.Equal(
            MaintenanceService.SignalRQuotaTier.Critical,
            MaintenanceService.ClassifySignalRQuotaTier(percent));
    }

    [Fact]
    public void ClassifySignalRQuotaTier_BoundaryAt79_IsBelowWarning()
    {
        Assert.Equal(
            MaintenanceService.SignalRQuotaTier.None,
            MaintenanceService.ClassifySignalRQuotaTier(79));
    }

    [Fact]
    public void ClassifySignalRQuotaTier_BoundaryAt80_IsWarningNotNone()
    {
        Assert.Equal(
            MaintenanceService.SignalRQuotaTier.Warning,
            MaintenanceService.ClassifySignalRQuotaTier(80));
    }

    [Fact]
    public void ClassifySignalRQuotaTier_BoundaryAt94_IsWarningNotCritical()
    {
        Assert.Equal(
            MaintenanceService.SignalRQuotaTier.Warning,
            MaintenanceService.ClassifySignalRQuotaTier(94));
    }

    [Fact]
    public void ClassifySignalRQuotaTier_BoundaryAt95_IsCriticalNotWarning()
    {
        Assert.Equal(
            MaintenanceService.SignalRQuotaTier.Critical,
            MaintenanceService.ClassifySignalRQuotaTier(95));
    }

    // --- CalculatePercent ---
    // Floors to integer to match what an operator sees on the dashboard.
    // Critical contract: 79.99% must NOT round up to 80% (warning trigger).

    [Theory]
    [InlineData(16, 20, 80)]   // exact 80%
    [InlineData(19, 20, 95)]   // exact 95%
    [InlineData(20, 20, 100)]
    [InlineData(0, 20, 0)]
    [InlineData(10, 20, 50)]
    public void CalculatePercent_ExactValues_RoundsCorrectly(double observed, double limit, int expected)
    {
        Assert.Equal(expected, MaintenanceService.CalculatePercent(observed, limit));
    }

    [Fact]
    public void CalculatePercent_JustUnderWarning_FloorsTo79()
    {
        // 15.99 / 20 = 79.95% -> floor -> 79 -> stays None tier
        Assert.Equal(79, MaintenanceService.CalculatePercent(15.99, 20));
    }

    [Fact]
    public void CalculatePercent_JustUnderCritical_FloorsTo94()
    {
        // 18.99 / 20 = 94.95% -> floor -> 94 -> stays Warning tier
        Assert.Equal(94, MaintenanceService.CalculatePercent(18.99, 20));
    }

    [Fact]
    public void CalculatePercent_ZeroLimit_ReturnsZero()
    {
        // Defensive: misconfigured limit must not divide by zero or NaN-poison
        // the tier classification.
        Assert.Equal(0, MaintenanceService.CalculatePercent(50, 0));
    }

    [Fact]
    public void CalculatePercent_NegativeLimit_ReturnsZero()
    {
        Assert.Equal(0, MaintenanceService.CalculatePercent(50, -1));
    }

    [Fact]
    public void CalculatePercent_OverLimit_ReturnsAbove100()
    {
        // Over-quota must surface as >100% so Critical fires, not silently cap.
        Assert.Equal(150, MaintenanceService.CalculatePercent(30, 20));
    }

    [Theory]
    [InlineData(16000, 20000, 80)]
    [InlineData(19000, 20000, 95)]
    [InlineData(15999, 20000, 79)]
    [InlineData(20000, 20000, 100)]
    public void CalculatePercent_MessageScale_RoundsCorrectly(long observed, long limit, int expected)
    {
        // Same math but at the message-quota scale (20k cap, not 20).
        // Verifies the double-precision path doesn't drift on larger numbers.
        Assert.Equal(expected, MaintenanceService.CalculatePercent(observed, limit));
    }

    // --- End-to-end tier mapping (the user-visible behavior) ---
    // Combines CalculatePercent + ClassifySignalRQuotaTier the way the watcher
    // actually uses them. Catches drift if either function changes
    // independently of the other.

    [Theory]
    [InlineData(15, 20, "None")]
    [InlineData(16, 20, "Warning")]
    [InlineData(18, 20, "Warning")]
    [InlineData(19, 20, "Critical")]
    [InlineData(20, 20, "Critical")]
    [InlineData(25, 20, "Critical")]
    public void ConnectionScenarios_MapToCorrectTier(int observed, int limit, string expectedTier)
    {
        var percent = MaintenanceService.CalculatePercent(observed, limit);
        var tier = MaintenanceService.ClassifySignalRQuotaTier(percent);
        Assert.Equal(expectedTier, tier.ToString());
    }

    [Theory]
    [InlineData(15999, 20000, "None")]
    [InlineData(16000, 20000, "Warning")]
    [InlineData(18999, 20000, "Warning")]
    [InlineData(19000, 20000, "Critical")]
    [InlineData(20000, 20000, "Critical")]
    [InlineData(25000, 20000, "Critical")]
    public void MessageScenarios_MapToCorrectTier(long observed, long limit, string expectedTier)
    {
        var percent = MaintenanceService.CalculatePercent(observed, limit);
        var tier = MaintenanceService.ClassifySignalRQuotaTier(percent);
        Assert.Equal(expectedTier, tier.ToString());
    }
}

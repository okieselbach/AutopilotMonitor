using System.Text.Json;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tier-boundary + JSON-extractor tests for the poison-queue watcher.
/// Mirrors <see cref="MaintenanceServiceSignalRQuotaTests"/>: pure-function coverage
/// of the parts that decide whether an operator gets paged, without standing up
/// the full MaintenanceService dependency graph. Full-service integration is
/// exercised by the live timer + ops event tables — what we guard here is the
/// classifier math, the dedup-key extractor, and the watch-list inventory.
/// </summary>
public class MaintenanceServicePoisonQueueTests
{
    // --- ClassifyPoisonQueueTier ---
    // Defaults: warn=1, critical=10
    //   count >= critical -> Critical
    //   count >= warning  -> Warning
    //   else              -> None

    [Theory]
    [InlineData(0L)]
    public void Classify_ZeroCount_ReturnsNone(long count)
    {
        Assert.Equal(
            MaintenanceService.PoisonQueueTier.None,
            MaintenanceService.ClassifyPoisonQueueTier(count,
                MaintenanceService.DefaultPoisonQueueWarningThreshold,
                MaintenanceService.DefaultPoisonQueueCriticalThreshold));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(5L)]
    [InlineData(9L)]
    public void Classify_InWarningBand_ReturnsWarning(long count)
    {
        Assert.Equal(
            MaintenanceService.PoisonQueueTier.Warning,
            MaintenanceService.ClassifyPoisonQueueTier(count,
                MaintenanceService.DefaultPoisonQueueWarningThreshold,
                MaintenanceService.DefaultPoisonQueueCriticalThreshold));
    }

    [Theory]
    [InlineData(10L)]
    [InlineData(100L)]
    [InlineData(10_000L)]
    public void Classify_AtOrAboveCritical_ReturnsCritical(long count)
    {
        Assert.Equal(
            MaintenanceService.PoisonQueueTier.Critical,
            MaintenanceService.ClassifyPoisonQueueTier(count,
                MaintenanceService.DefaultPoisonQueueWarningThreshold,
                MaintenanceService.DefaultPoisonQueueCriticalThreshold));
    }

    [Fact]
    public void Classify_CustomThresholds_RespectsConfig()
    {
        Assert.Equal(MaintenanceService.PoisonQueueTier.None,
            MaintenanceService.ClassifyPoisonQueueTier(4, 5, 50));
        Assert.Equal(MaintenanceService.PoisonQueueTier.Warning,
            MaintenanceService.ClassifyPoisonQueueTier(5, 5, 50));
        Assert.Equal(MaintenanceService.PoisonQueueTier.Warning,
            MaintenanceService.ClassifyPoisonQueueTier(49, 5, 50));
        Assert.Equal(MaintenanceService.PoisonQueueTier.Critical,
            MaintenanceService.ClassifyPoisonQueueTier(50, 5, 50));
    }

    // --- ExtractQueueName ---
    // Pulls queueName out of OpsEvent Details JSON so the seen-index can build
    // dedup keys like "PoisonQueueBacklogHigh|analyze-on-enrollment-end-poison".

    [Fact]
    public void ExtractQueueName_WellFormedDetails_ReturnsQueueName()
    {
        var detailsJson = JsonSerializer.Serialize(new
        {
            queueName = "analyze-on-enrollment-end-poison",
            count = 5L,
            threshold = 1,
        });

        Assert.Equal("analyze-on-enrollment-end-poison",
            MaintenanceService.ExtractQueueName(detailsJson));
    }

    [Fact]
    public void ExtractQueueName_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, MaintenanceService.ExtractQueueName(null));
        Assert.Equal(string.Empty, MaintenanceService.ExtractQueueName(string.Empty));
    }

    [Fact]
    public void ExtractQueueName_MalformedJson_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, MaintenanceService.ExtractQueueName("{not json"));
    }

    [Fact]
    public void ExtractQueueName_DetailsWithoutQueueName_ReturnsEmpty()
    {
        var detailsJson = JsonSerializer.Serialize(new { count = 5, threshold = 1 });
        Assert.Equal(string.Empty, MaintenanceService.ExtractQueueName(detailsJson));
    }

    [Fact]
    public void ExtractQueueName_QueueNameWrongType_ReturnsEmpty()
    {
        // Defensive: dedup key must never crash on non-string queueName.
        var detailsJson = JsonSerializer.Serialize(new { queueName = 42 });
        Assert.Equal(string.Empty, MaintenanceService.ExtractQueueName(detailsJson));
    }

    // --- Watch-list inventory ---

    [Fact]
    public void MonitoredPoisonQueues_CoversAllProducerQueues()
    {
        // Guard against silently adding a new producer queue without a poison entry.
        // If a fifth queue is introduced, MonitoredPoisonQueues + this assertion must move together.
        Assert.Equal(4, MaintenanceService.MonitoredPoisonQueues.Length);
        Assert.Contains(
            Constants.QueueNames.AnalyzeOnEnrollmentEnd + "-poison",
            MaintenanceService.MonitoredPoisonQueues);
        Assert.Contains(
            Constants.QueueNames.VulnerabilityCorrelate + "-poison",
            MaintenanceService.MonitoredPoisonQueues);
        Assert.Contains(
            Constants.QueueNames.TelemetryIndexReconcile + "-poison",
            MaintenanceService.MonitoredPoisonQueues);
        // PR1 critical-table backup queue (plan §Wave5 #2).
        Assert.Contains(
            Constants.QueueNames.CriticalTableBackupPoison,
            MaintenanceService.MonitoredPoisonQueues);
    }

    [Fact]
    public void Defaults_AreCalibratedForStrictMode()
    {
        // Production posture: every poison message counts. If we ever relax this,
        // a deliberate code change should be required, not a silent drift.
        Assert.Equal(1, MaintenanceService.DefaultPoisonQueueWarningThreshold);
        Assert.Equal(10, MaintenanceService.DefaultPoisonQueueCriticalThreshold);
    }
}

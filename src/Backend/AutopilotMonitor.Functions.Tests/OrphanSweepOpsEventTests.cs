using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Maintenance;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Shape of the ops events the orphan sweep and the maintenance run write: the dashboards and the
/// health pass read these details by name.
/// </summary>
public class OrphanSweepOpsEventTests
{
    private static (OpsEventService Service, List<OpsEventEntry> Saved) Rig()
    {
        var saved = new List<OpsEventEntry>();
        var opsRepo = new Mock<IOpsEventRepository>();
        opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
            .Callback<OpsEventEntry>(e => { lock (saved) saved.Add(e); })
            .Returns(Task.CompletedTask);
        var adminConfig = new Mock<AdminConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance,
            new MemoryCache(new MemoryCacheOptions())) { CallBase = false };
        var alertDispatch = TestNotifications.InertOpsAlertDispatch(adminConfig.Object);
        return (new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, alertDispatch), saved);
    }

    [Fact]
    public async Task MaintenanceCompleted_carries_the_step_timings()
    {
        var (service, saved) = Rig();
        var steps = new Dictionary<string, long> { ["stalledSessions"] = 1200, ["aggregateMetrics"] = 90000 };

        await service.RecordMaintenanceCompletedAsync(123456, "Timer", stepsMs: steps);

        var evt = Assert.Single(saved);
        Assert.Equal(OpsEventTypes.MaintenanceCompleted, evt.EventType);
        Assert.Contains("\"stepsMs\"", evt.Details);
        Assert.Contains("\"aggregateMetrics\":90000", evt.Details);
    }

    [Fact]
    public async Task OrphanEventsCleaned_is_a_Warning_with_per_table_totals_and_the_legacy_count()
    {
        var (service, saved) = Rig();
        var result = new OrphanSweepRunResult { Remaining = 7 };
        var legacy = new OrphanSweepCleanedSession { TenantId = "t", SessionId = "s-legacy", Legacy = true, InventoryKeysDecremented = 3, HadInventorySideRow = true };
        legacy.RowsByTable[Constants.TableNames.Events] = 18;
        legacy.RowsByTable[Constants.TableNames.EventTypeIndex] = 13;
        result.Add(legacy);
        var fresh = new OrphanSweepCleanedSession { TenantId = "t", SessionId = "s-fresh" };
        fresh.RowsByTable[Constants.TableNames.Events] = 1;
        result.Add(fresh);

        await service.RecordOrphanEventsCleanedAsync(result);

        var evt = Assert.Single(saved);
        Assert.Equal(OpsEventTypes.OrphanEventsCleaned, evt.EventType);
        Assert.Equal(OpsEventSeverity.Warning, evt.Severity);
        Assert.Contains("\"orphanSessions\":2", evt.Details);
        Assert.Contains("\"totalEventsDeleted\":19", evt.Details);
        Assert.Contains("\"legacySessions\":1", evt.Details);
        Assert.Contains("\"remaining\":7", evt.Details);
        Assert.Contains($"\"{Constants.TableNames.SoftwareInventory}\":3", evt.Details);
        Assert.Contains($"\"{Constants.TableNames.EventSessionIndex}\":2", evt.Details);
        Assert.Contains("\"legacy\":true", evt.Details);
    }

    [Fact]
    public async Task OrphanSweepCompleted_is_the_Info_heartbeat_with_the_counters()
    {
        var (service, saved) = Rig();
        var result = new OrphanSweepRunResult
        {
            HandlesScanned = 16784, SessionKeys = 16700, Candidates = 3, Remaining = 1, SkippedHandleGone = 2, DurationMs = 4200,
            Reconcile = new OrphanReconcileResult { IndexSessions = 16000, ResidueSessions = 0, DurationMs = 180000 },
        };

        await service.RecordOrphanSweepCompletedAsync(result, "Timer");

        var evt = Assert.Single(saved);
        Assert.Equal(OpsEventTypes.OrphanSweepCompleted, evt.EventType);
        Assert.Equal(OpsEventSeverity.Info, evt.Severity);
        Assert.Contains("\"handlesScanned\":16784", evt.Details);
        Assert.Contains("\"skippedHandleGone\":2", evt.Details);
        Assert.Contains("\"residueSessions\":0", evt.Details);
    }

    [Fact]
    public async Task OrphanReconcileCompleted_flips_to_Warning_with_a_sample_when_residue_exists()
    {
        var (service, saved) = Rig();
        var clean = new OrphanReconcileResult { IndexSessions = 10, ResidueSessions = 0 };
        var dirty = new OrphanReconcileResult { IndexSessions = 10, ResidueSessions = 2, Sample = { ("t1", "s1"), ("t1", "s2") } };

        await service.RecordOrphanReconcileCompletedAsync(clean);
        await service.RecordOrphanReconcileCompletedAsync(dirty);

        Assert.Equal(OpsEventSeverity.Info, saved[0].Severity);
        Assert.Equal(OpsEventSeverity.Warning, saved[1].Severity);
        Assert.Contains("\"sessionId\":\"s2\"", saved[1].Details);
        Assert.Contains("\"sampleTruncated\":false", saved[1].Details);
    }
}

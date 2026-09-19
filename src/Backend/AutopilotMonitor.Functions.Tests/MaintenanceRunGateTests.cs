using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Backup;
using AutopilotMonitor.Functions.Services.Maintenance;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Single-flight of the platform maintenance run: the 2h timer and the manual trigger enter
/// through one gate, so a second run is skipped instead of aggregating, cleaning up and
/// backfilling the same tables in parallel — and a skip never looks like an active run.
/// </summary>
public class MaintenanceRunGateTests
{
    private sealed class Harness
    {
        public Mock<MaintenanceRunLockStore> LockStore { get; }
        public Mock<BlobLeaseClient> Lease { get; } = new();
        public List<string> OpsEvents { get; } = new();
        public MaintenanceRunGate Sut { get; }

        public Harness()
        {
            LockStore = new Mock<MaintenanceRunLockStore>(new SessionDeletionMaintenanceFunctionTests.MaintenanceBlobStub());
            LockStore.Setup(l => l.AcquireLeaseAsync(It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Lease.Object);

            var opsRepo = new Mock<IOpsEventRepository>();
            opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
                .Returns<OpsEventEntry>(e => { lock (OpsEvents) OpsEvents.Add(e.EventType); return Task.CompletedTask; });
            var adminConfig = new Mock<AdminConfigurationService>(
                Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()));
            var opsService = new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance,
                TestNotifications.InertOpsAlertDispatch(adminConfig.Object));

            Sut = new MaintenanceRunGate(LockStore.Object, opsService, NullLogger<MaintenanceRunGate>.Instance);
        }

        public void HoldLease() =>
            LockStore.Setup(l => l.AcquireLeaseAsync(It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new LeaseHeldException("held", new Exception("409")));
    }

    [Fact]
    public async Task A_held_lease_skips_the_run_and_never_reports_it_as_started()
    {
        var h = new Harness();
        h.HoldLease();
        var ran = false;

        var result = await h.Sut.RunExclusiveAsync("Timer", () => { ran = true; return Task.CompletedTask; });

        Assert.False(result);
        Assert.False(ran);
        Assert.Equal(new[] { OpsEventTypes.MaintenanceSkippedLocked }, h.OpsEvents);
    }

    [Fact]
    public async Task Started_is_written_under_the_lease_before_the_body_and_the_lease_is_released_last()
    {
        var h = new Harness();
        var order = new List<string>();
        h.Lease.Setup(l => l.ReleaseAsync(It.IsAny<Azure.RequestConditions>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("release"))
            .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Blobs.Models.ReleasedObjectInfo>>());

        var result = await h.Sut.RunExclusiveAsync("admin@example.com", () =>
        {
            order.Add($"body after [{string.Join(",", h.OpsEvents)}]");
            return Task.CompletedTask;
        });

        Assert.True(result);
        Assert.Equal(new[] { $"body after [{OpsEventTypes.MaintenanceStarted}]", "release" }, order);
    }

    [Fact]
    public async Task The_lease_is_released_when_the_body_throws()
    {
        var h = new Harness();
        var released = false;
        h.Lease.Setup(l => l.ReleaseAsync(It.IsAny<Azure.RequestConditions>(), It.IsAny<CancellationToken>()))
            .Callback(() => released = true)
            .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Blobs.Models.ReleasedObjectInfo>>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Sut.RunExclusiveAsync("Timer", () => throw new InvalidOperationException("boom")));

        Assert.True(released);
    }

    [Fact]
    public async Task Probe_reports_an_active_run_while_the_lease_is_held()
    {
        var h = new Harness();
        h.HoldLease();

        Assert.True(await h.Sut.IsRunActiveAsync());
        Assert.Empty(h.OpsEvents);
    }

    [Fact]
    public async Task Probe_releases_its_lease_and_reports_no_active_run()
    {
        var h = new Harness();
        var released = false;
        h.Lease.Setup(l => l.ReleaseAsync(It.IsAny<Azure.RequestConditions>(), It.IsAny<CancellationToken>()))
            .Callback(() => released = true)
            .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Blobs.Models.ReleasedObjectInfo>>());

        Assert.False(await h.Sut.IsRunActiveAsync());
        Assert.True(released);
    }

    [Fact]
    public async Task Probe_lets_a_failed_release_surface_so_nothing_is_queued_behind_a_stuck_lease()
    {
        // A queued run picked up seconds later would find the probe's own lease still held,
        // skip as "locked" and delete the message — the operator's run would silently vanish.
        var h = new Harness();
        h.Lease.Setup(l => l.ReleaseAsync(It.IsAny<Azure.RequestConditions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Azure.RequestFailedException(500, "storage down"));

        await Assert.ThrowsAsync<Azure.RequestFailedException>(() => h.Sut.IsRunActiveAsync());
    }
}

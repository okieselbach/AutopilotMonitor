using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Locks in the negative-cache contract of <see cref="BlockedDeviceService"/>: a healthy
/// (never-blocked) device must NOT pay a BlockedDevices point-read on every ingest request —
/// the "not blocked" answer is cached and revalidated within the same window as positive
/// entries, so cross-instance blocks still propagate within seconds.
/// </summary>
public class BlockedDeviceServiceTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string Serial = "SN-12345";

    private static Mock<IDeviceSecurityRepository> NewRepo(bool blocked = false, DateTime? unblockAt = null)
    {
        var repo = new Mock<IDeviceSecurityRepository>();
        repo.Setup(r => r.GetBlockedDevicesAsync(TenantId))
            .ReturnsAsync(new List<BlockedDeviceEntry>());
        repo.Setup(r => r.IsDeviceBlockedAsync(TenantId, Serial))
            .ReturnsAsync((blocked, unblockAt, "Block", (string?)null));
        return repo;
    }

    [Fact]
    public async Task NotBlockedDevice_SecondCallWithinWindow_DoesNotHitStorage()
    {
        var repo = NewRepo(blocked: false);
        var service = new BlockedDeviceService(repo.Object, NullLogger<BlockedDeviceService>.Instance);

        var first = await service.IsBlockedAsync(TenantId, Serial);
        var second = await service.IsBlockedAsync(TenantId, Serial);
        var third = await service.IsBlockedAsync(TenantId, Serial);

        Assert.False(first.isBlocked);
        Assert.False(second.isBlocked);
        Assert.False(third.isBlocked);
        // Exactly ONE storage point-read (the first call's cache-miss refresh); the negative
        // answer is served from cache afterwards. This is the per-request cost fix.
        repo.Verify(r => r.IsDeviceBlockedAsync(TenantId, Serial), Times.Once);
    }

    [Fact]
    public async Task NegativeEntry_AfterRevalidationWindow_SeesBlockFromOtherInstance()
    {
        var repo = NewRepo(blocked: false);
        // Zero window → every subsequent call revalidates, simulating an expired entry.
        var service = new BlockedDeviceService(
            repo.Object, NullLogger<BlockedDeviceService>.Instance, TimeSpan.Zero);

        var first = await service.IsBlockedAsync(TenantId, Serial);
        Assert.False(first.isBlocked);

        // Another instance blocks the device → storage now answers "blocked".
        repo.Setup(r => r.IsDeviceBlockedAsync(TenantId, Serial))
            .ReturnsAsync((true, DateTime.UtcNow.AddHours(1), "Block", (string?)null));

        var second = await service.IsBlockedAsync(TenantId, Serial);

        Assert.True(second.isBlocked);
        repo.Verify(r => r.IsDeviceBlockedAsync(TenantId, Serial), Times.Exactly(2));
    }

    [Fact]
    public async Task BlockDevice_OverridesCachedNegativeEntry()
    {
        var repo = NewRepo(blocked: false);
        var service = new BlockedDeviceService(repo.Object, NullLogger<BlockedDeviceService>.Instance);

        // Prime the negative cache.
        var before = await service.IsBlockedAsync(TenantId, Serial);
        Assert.False(before.isBlocked);

        // Local block must flip the cached entry to blocked (AddOrUpdate sets IsBlocked = true) —
        // without that, the fresh negative entry would keep answering "not blocked" for 30s.
        await service.BlockDeviceAsync(TenantId, Serial, durationHours: 1, blockedByEmail: "alice@contoso.com");

        var after = await service.IsBlockedAsync(TenantId, Serial);
        Assert.True(after.isBlocked);
        Assert.Equal("Block", after.action);
    }

    [Fact]
    public async Task SessionAwareBlock_OverNegativeEntry_StaysSessionAware()
    {
        // Codex finding: a session-aware block overwriting a cached NEGATIVE entry must not
        // widen to a whole-device block (negative entries carry BlockedSessionIds = null,
        // which the merge previously kept — and null means "whole device" downstream).
        var repo = NewRepo(blocked: false);
        var service = new BlockedDeviceService(repo.Object, NullLogger<BlockedDeviceService>.Instance);

        // Prime the negative cache, then apply a session-aware auto-block locally.
        _ = await service.IsBlockedAsync(TenantId, Serial);
        await service.BlockDeviceAsync(TenantId, Serial, durationHours: 1,
            blockedByEmail: "alice@contoso.com", blockedSessionId: "33333333-3333-3333-3333-333333333333");

        // The blocked session itself is blocked...
        var sameSession = await service.IsBlockedAsync(TenantId, Serial, "33333333-3333-3333-3333-333333333333");
        Assert.True(sameSession.isBlocked);
        Assert.Equal("33333333-3333-3333-3333-333333333333", sameSession.blockedSessionIds);

        // ...but a NEW session on the same device auto-unblocks — with the widened
        // whole-device shape this returned blocked=true and never auto-unblocked.
        var newSession = await service.IsBlockedAsync(TenantId, Serial, "44444444-4444-4444-4444-444444444444");
        Assert.False(newSession.isBlocked);
    }

    [Fact]
    public async Task BlockedDevice_IsReportedBlocked_OnCacheMissRefresh()
    {
        var repo = NewRepo(blocked: true, unblockAt: DateTime.UtcNow.AddHours(2));
        var service = new BlockedDeviceService(repo.Object, NullLogger<BlockedDeviceService>.Instance);

        var result = await service.IsBlockedAsync(TenantId, Serial);

        Assert.True(result.isBlocked);
        Assert.NotNull(result.unblockAt);
    }
}

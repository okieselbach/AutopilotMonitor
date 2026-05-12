using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Regression tests for the multi-instance staleness bug in <see cref="BlockedDeviceService"/>.
///
/// CONTEXT: The service caches block state per Function App instance. Before the fix, once an
/// instance had loaded a tenant's block list, a cache miss would short-circuit to "not blocked"
/// without ever re-consulting storage. A block added on instance B (via DeviceBlockFunction)
/// therefore stayed invisible to instance A — kill signals never reached affected devices.
///
/// Fix: cache miss → storage point-read fallback + stale-positive revalidate window.
/// </summary>
public class BlockedDeviceServiceCrossInstanceTests
{
    private const string TenantA = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string SerialPF55 = "PF55PSKL";

    private static BlockedDeviceService CreateService(FakeDeviceSecurityRepository repo) =>
        new(repo, NullLogger<BlockedDeviceService>.Instance);

    [Theory]
    [InlineData("Block")] // Pauses uploads; session remains alive
    [InlineData("Kill")]  // Forces remote self-destruct
    public async Task IsBlockedAsync_CacheMissAfterTenantLoaded_FallsThroughToStorage(string action)
    {
        // Arrange — instance A loads the tenant's block list while the device is NOT yet blocked.
        // Action is action-agnostic: the staleness bug hit both Block and Kill the same way,
        // so the fallback must surface whichever action storage holds.
        var repo = new FakeDeviceSecurityRepository();
        var svc = CreateService(repo);

        var beforeBlock = await svc.IsBlockedAsync(TenantA, SerialPF55);
        Assert.False(beforeBlock.isBlocked); // baseline: load happens, cache empty for this device

        // Act — another Function App instance writes the block directly to storage (no local-cache
        // update on this instance, mirroring the actual multi-instance bug).
        repo.SetBlock(TenantA, SerialPF55, DateTime.UtcNow.AddHours(12), action);

        var afterBlock = await svc.IsBlockedAsync(TenantA, SerialPF55);

        // Assert — cache miss path must point-read storage, find the block, and return it.
        Assert.True(afterBlock.isBlocked);
        Assert.Equal(action, afterBlock.action);
    }

    [Fact]
    public async Task IsBlockedAsync_StalePositiveEntry_RevalidatesFromStorage()
    {
        // Arrange — instance has a cached positive entry from an earlier read.
        var repo = new FakeDeviceSecurityRepository();
        repo.SetBlock(TenantA, SerialPF55, DateTime.UtcNow.AddHours(12), action: "Block");

        var svc = CreateService(repo);
        var initial = await svc.IsBlockedAsync(TenantA, SerialPF55);
        Assert.True(initial.isBlocked);
        Assert.Equal("Block", initial.action);

        // Act — another instance upgrades Block → Kill and we backdate the cache entry's
        // LastCheckedUtc to force the revalidate path.
        repo.SetBlock(TenantA, SerialPF55, DateTime.UtcNow.AddHours(12), action: "Kill");
        BackdateCacheEntry(svc, TenantA, SerialPF55, TimeSpan.FromMinutes(5));

        var after = await svc.IsBlockedAsync(TenantA, SerialPF55);

        // Assert — Kill must surface even though Block was previously cached.
        Assert.True(after.isBlocked);
        Assert.Equal("Kill", after.action);
    }

    [Fact]
    public async Task IsBlockedAsync_StalePositiveEntry_StorageUnblock_PropagatesAsNotBlocked()
    {
        // Arrange — cached block, then another instance unblocks in storage.
        var repo = new FakeDeviceSecurityRepository();
        repo.SetBlock(TenantA, SerialPF55, DateTime.UtcNow.AddHours(12), action: "Block");

        var svc = CreateService(repo);
        var initial = await svc.IsBlockedAsync(TenantA, SerialPF55);
        Assert.True(initial.isBlocked);

        // Act — cross-instance unblock, then backdate the cache entry past the revalidate window.
        repo.ClearBlock(TenantA, SerialPF55);
        BackdateCacheEntry(svc, TenantA, SerialPF55, TimeSpan.FromMinutes(5));

        var after = await svc.IsBlockedAsync(TenantA, SerialPF55);

        // Assert — the revalidate path must observe the gone-from-storage state.
        Assert.False(after.isBlocked);
    }

    /// <summary>
    /// Reaches into the service's private cache to backdate a single entry's
    /// <c>LastCheckedUtc</c>, forcing the next <see cref="BlockedDeviceService.IsBlockedAsync"/>
    /// call to take the stale-revalidate branch. Reflection is acceptable here because the
    /// revalidate window is a private implementation detail we deliberately exercise.
    /// </summary>
    private static void BackdateCacheEntry(BlockedDeviceService svc, string tenantId, string serialNumber, TimeSpan age)
    {
        var cacheField = typeof(BlockedDeviceService).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(cacheField);

        var cache = (System.Collections.IDictionary)cacheField!.GetValue(svc)!;
        var key = $"{tenantId}|{serialNumber.ToUpperInvariant()}";
        var entry = cache[key];
        Assert.NotNull(entry);

        var entryType = entry!.GetType();
        var lastCheckedProp = entryType.GetProperty("LastCheckedUtc", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(lastCheckedProp);
        lastCheckedProp!.SetValue(entry, DateTime.UtcNow - age);
    }

    // =========================================================================
    // Minimal IDeviceSecurityRepository fake: only the calls the service actually exercises.
    // Backing dictionary doubles as "storage" so tests can mutate it to simulate writes from
    // another Function App instance.
    // =========================================================================
    private sealed class FakeDeviceSecurityRepository : IDeviceSecurityRepository
    {
        // Key: "tenantId|serialNumberUpper"
        private readonly ConcurrentDictionary<string, BlockedDeviceEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public void SetBlock(string tenantId, string serialNumber, DateTime unblockAt, string action)
        {
            _entries[Key(tenantId, serialNumber)] = new BlockedDeviceEntry
            {
                TenantId = tenantId,
                SerialNumber = serialNumber,
                BlockedAt = DateTime.UtcNow,
                UnblockAt = unblockAt,
                Action = action,
                BlockedSessionIds = null,
            };
        }

        public void ClearBlock(string tenantId, string serialNumber)
            => _entries.TryRemove(Key(tenantId, serialNumber), out _);

        public Task<(bool isBlocked, DateTime? unblockAt, string action, string? blockedSessionIds)> IsDeviceBlockedAsync(string tenantId, string serialNumber)
        {
            if (_entries.TryGetValue(Key(tenantId, serialNumber), out var entry) &&
                entry.UnblockAt is { } uat && DateTime.UtcNow < uat)
            {
                return Task.FromResult<(bool, DateTime?, string, string?)>(
                    (true, uat, entry.Action, entry.BlockedSessionIds));
            }
            return Task.FromResult<(bool, DateTime?, string, string?)>((false, null, "Block", null));
        }

        public Task<List<BlockedDeviceEntry>> GetBlockedDevicesAsync(string tenantId)
        {
            var now = DateTime.UtcNow;
            var matching = new List<BlockedDeviceEntry>();
            foreach (var kv in _entries)
            {
                if (string.Equals(kv.Value.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) &&
                    kv.Value.UnblockAt is { } uat && now < uat)
                {
                    matching.Add(kv.Value);
                }
            }
            return Task.FromResult(matching);
        }

        public Task<List<BlockedDeviceEntry>> GetAllBlockedDevicesAsync()
            => Task.FromResult(new List<BlockedDeviceEntry>(_entries.Values));

        public Task BlockDeviceAsync(string tenantId, string serialNumber, int durationHours,
            string blockedByEmail, string? reason = null, string action = "Block", string? blockedSessionId = null)
        {
            SetBlock(tenantId, serialNumber, DateTime.UtcNow.AddHours(durationHours), action);
            return Task.CompletedTask;
        }

        public Task UnblockDeviceAsync(string tenantId, string serialNumber)
        {
            ClearBlock(tenantId, serialNumber);
            return Task.CompletedTask;
        }

        // --- Version block surface is unused in these tests ---
        public Task<(bool isBlocked, string action, string? matchedPattern)> IsVersionBlockedAsync(string agentVersion)
            => Task.FromResult<(bool, string, string?)>((false, "Block", null));

        public Task<List<BlockedVersionEntry>> GetBlockedVersionsAsync()
            => Task.FromResult(new List<BlockedVersionEntry>());

        public Task BlockVersionAsync(string versionPattern, string action, string createdByEmail, string? reason = null)
            => Task.CompletedTask;

        public Task UnblockVersionAsync(string versionPattern)
            => Task.CompletedTask;

        private static string Key(string tenantId, string serialNumber)
            => $"{tenantId}|{serialNumber.ToUpperInvariant()}";
    }
}

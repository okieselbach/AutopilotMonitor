using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="KillSwitchEvaluator"/> — the shared device/version kill-switch check
/// behind both delivery channels (telemetry ingest + agent config). Pins three contracts:
/// (1) verdicts mirror the original ingest behaviour incl. check order (device first,
/// device-Block short-circuits before the version check), (2) every served KILL emits exactly
/// one <c>KillSignalDelivered</c> ops event per tenant+serial+pattern per 24h window (a
/// kill-blind old agent re-calls every few seconds and must not flood OpsEvents), and
/// (3) plain Blocks never emit ops events.
/// </summary>
public class KillSwitchEvaluatorTests
{
    private const string TenantA = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string Serial = "PF55PSKL";

    [Fact]
    public async Task Evaluate_NoMatch_NotBlocked_NoOpsEvent()
    {
        var (evaluator, savedOpsEvents) = CreateEvaluator(new FakeSecurityRepo());

        var verdict = await evaluator.EvaluateAsync(TenantA, Serial, "2.0.114", "telemetry");

        Assert.False(verdict.IsBlocked);
        Assert.False(verdict.IsKill);
        Assert.Empty(savedOpsEvents);
    }

    [Fact]
    public async Task Evaluate_DeviceBlock_BlockedNotKill_NoOpsEvent()
    {
        var repo = new FakeSecurityRepo();
        var unblockAt = DateTime.UtcNow.AddHours(12);
        repo.SetDeviceBlock(TenantA, Serial, unblockAt, "Block");
        var (evaluator, savedOpsEvents) = CreateEvaluator(repo);

        var verdict = await evaluator.EvaluateAsync(TenantA, Serial, "2.0.114", "telemetry");

        Assert.True(verdict.IsBlocked);
        Assert.False(verdict.IsKill);
        Assert.Equal(unblockAt, verdict.UnblockAt);
        Assert.Empty(savedOpsEvents); // Blocks are common — only Kill is alert-worthy.
    }

    [Fact]
    public async Task Evaluate_DeviceKill_Kill_EmitsKillSignalDelivered()
    {
        var repo = new FakeSecurityRepo();
        repo.SetDeviceBlock(TenantA, Serial, DateTime.UtcNow.AddHours(12), "Kill");
        var (evaluator, savedOpsEvents) = CreateEvaluator(repo);

        var verdict = await evaluator.EvaluateAsync(TenantA, Serial, "2.0.114", "config");

        Assert.True(verdict.IsBlocked);
        Assert.True(verdict.IsKill);
        var evt = Assert.Single(savedOpsEvents);
        Assert.Equal("KillSignalDelivered", evt.EventType);
        Assert.Equal(TenantA, evt.TenantId);
        Assert.Contains("config", evt.Details);
        Assert.Contains("device", evt.Details);
    }

    [Fact]
    public async Task Evaluate_VersionKill_Kill_MessageCarriesPattern_EmitsOpsEvent()
    {
        var repo = new FakeSecurityRepo();
        repo.AddVersionRule("1.*", "Kill");
        var (evaluator, savedOpsEvents) = CreateEvaluator(repo);

        var verdict = await evaluator.EvaluateAsync(TenantA, Serial, "1.0.30", "telemetry");

        Assert.True(verdict.IsBlocked);
        Assert.True(verdict.IsKill);
        Assert.Null(verdict.UnblockAt);
        Assert.Contains("1.*", verdict.Message);
        var evt = Assert.Single(savedOpsEvents);
        Assert.Equal("KillSignalDelivered", evt.EventType);
        Assert.Contains("version", evt.Details);
        Assert.Contains("1.*", evt.Details);
    }

    [Fact]
    public async Task Evaluate_VersionBlock_BlockedNotKill_NoOpsEvent()
    {
        var repo = new FakeSecurityRepo();
        repo.AddVersionRule("1.*", "Block");
        var (evaluator, savedOpsEvents) = CreateEvaluator(repo);

        var verdict = await evaluator.EvaluateAsync(TenantA, Serial, "1.0.30", "telemetry");

        Assert.True(verdict.IsBlocked);
        Assert.False(verdict.IsKill);
        Assert.Empty(savedOpsEvents);
    }

    [Fact]
    public async Task Evaluate_DeviceBlockShortCircuits_BeforeVersionKill()
    {
        // Ingest-order parity: the device check runs first and a match returns immediately —
        // a device Block wins over a version Kill (the agent pauses uploads; the kill then
        // reaches it on the config channel at next start).
        var repo = new FakeSecurityRepo();
        repo.SetDeviceBlock(TenantA, Serial, DateTime.UtcNow.AddHours(12), "Block");
        repo.AddVersionRule("1.*", "Kill");
        var (evaluator, savedOpsEvents) = CreateEvaluator(repo);

        var verdict = await evaluator.EvaluateAsync(TenantA, Serial, "1.0.30", "telemetry");

        Assert.True(verdict.IsBlocked);
        Assert.False(verdict.IsKill);
        Assert.Empty(savedOpsEvents);
    }

    [Fact]
    public async Task Evaluate_RepeatedKill_EmitsOpsEventOncePerWindow()
    {
        var repo = new FakeSecurityRepo();
        repo.AddVersionRule("1.*", "Kill");
        var (evaluator, savedOpsEvents) = CreateEvaluator(repo);

        // A kill-blind 1.x agent hammers the endpoint — 5 calls, both channels.
        await evaluator.EvaluateAsync(TenantA, Serial, "1.0.30", "telemetry");
        await evaluator.EvaluateAsync(TenantA, Serial, "1.0.30", "telemetry");
        await evaluator.EvaluateAsync(TenantA, Serial, "1.0.30", "config");
        await evaluator.EvaluateAsync(TenantA, Serial, "1.0.30", "telemetry");
        await evaluator.EvaluateAsync(TenantA, Serial, "1.0.30", "config");

        Assert.Single(savedOpsEvents);
    }

    [Fact]
    public async Task Evaluate_DistinctDevices_EmitSeparateOpsEvents()
    {
        var repo = new FakeSecurityRepo();
        repo.AddVersionRule("1.*", "Kill");
        var (evaluator, savedOpsEvents) = CreateEvaluator(repo);

        await evaluator.EvaluateAsync(TenantA, "SERIAL-A", "1.0.30", "telemetry");
        await evaluator.EvaluateAsync(TenantA, "SERIAL-B", "1.0.30", "telemetry");

        Assert.Equal(2, savedOpsEvents.Count);
    }

    [Fact]
    public void ShouldRecordOpsEvent_SameKeyCaseInsensitive_ClaimedOnce()
    {
        var (evaluator, _) = CreateEvaluator(new FakeSecurityRepo());

        Assert.True(evaluator.ShouldRecordOpsEvent(TenantA, Serial, "1.*"));
        Assert.False(evaluator.ShouldRecordOpsEvent(TenantA, Serial.ToLowerInvariant(), "1.*"));
        Assert.True(evaluator.ShouldRecordOpsEvent(TenantA, Serial, "2.*"));
    }

    // =========================================================================
    // Harness
    // =========================================================================

    private static (KillSwitchEvaluator evaluator, List<OpsEventEntry> savedOpsEvents) CreateEvaluator(
        FakeSecurityRepo repo)
    {
        var savedOpsEvents = new List<OpsEventEntry>();
        var opsRepo = new Mock<IOpsEventRepository>();
        opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
            .Callback<OpsEventEntry>(e => { lock (savedOpsEvents) savedOpsEvents.Add(e); })
            .Returns(Task.CompletedTask);

        var memCache = new MemoryCache(new MemoryCacheOptions());
        var adminConfig = new Mock<AdminConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, memCache);
        var alertDispatch = new OpsAlertDispatchService(
            adminConfig.Object,
            new TelegramNotificationService(new HttpClient(), Mock.Of<IConfigRepository>(),
                NullLogger<TelegramNotificationService>.Instance),
            new WebhookNotificationService(new HttpClient(),
                NullLogger<WebhookNotificationService>.Instance),
            NullLogger<OpsAlertDispatchService>.Instance);
        var opsService = new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, alertDispatch);

        var evaluator = new KillSwitchEvaluator(
            new BlockedDeviceService(repo, NullLogger<BlockedDeviceService>.Instance),
            new BlockedVersionService(repo, NullLogger<BlockedVersionService>.Instance),
            opsService,
            NullLogger<KillSwitchEvaluator>.Instance);

        return (evaluator, savedOpsEvents);
    }

    /// <summary>
    /// Minimal <see cref="IDeviceSecurityRepository"/> fake covering both the device-block and
    /// version-block surfaces the evaluator exercises.
    /// </summary>
    private sealed class FakeSecurityRepo : IDeviceSecurityRepository
    {
        private readonly ConcurrentDictionary<string, BlockedDeviceEntry> _devices = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<BlockedVersionEntry> _versionRules = new();

        public void SetDeviceBlock(string tenantId, string serialNumber, DateTime unblockAt, string action)
        {
            _devices[$"{tenantId}|{serialNumber.ToUpperInvariant()}"] = new BlockedDeviceEntry
            {
                TenantId = tenantId,
                SerialNumber = serialNumber,
                BlockedAt = DateTime.UtcNow,
                UnblockAt = unblockAt,
                Action = action,
            };
        }

        public void AddVersionRule(string pattern, string action)
        {
            lock (_versionRules)
                _versionRules.Add(new BlockedVersionEntry { VersionPattern = pattern, Action = action });
        }

        public Task<(bool isBlocked, DateTime? unblockAt, string action, string? blockedSessionIds)> IsDeviceBlockedAsync(
            string tenantId, string serialNumber)
        {
            if (_devices.TryGetValue($"{tenantId}|{serialNumber.ToUpperInvariant()}", out var entry) &&
                entry.UnblockAt is { } uat && DateTime.UtcNow < uat)
            {
                return Task.FromResult<(bool, DateTime?, string, string?)>((true, uat, entry.Action, null));
            }
            return Task.FromResult<(bool, DateTime?, string, string?)>((false, null, "Block", null));
        }

        public Task<List<BlockedDeviceEntry>> GetBlockedDevicesAsync(string tenantId)
            => Task.FromResult(_devices.Values.Where(d => string.Equals(d.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)).ToList());

        public Task<List<BlockedDeviceEntry>> GetAllBlockedDevicesAsync()
            => Task.FromResult(_devices.Values.ToList());

        public Task BlockDeviceAsync(string tenantId, string serialNumber, int durationHours,
            string blockedByEmail, string? reason = null, string action = "Block", string? blockedSessionId = null)
        {
            SetDeviceBlock(tenantId, serialNumber, DateTime.UtcNow.AddHours(durationHours), action);
            return Task.CompletedTask;
        }

        public Task UnblockDeviceAsync(string tenantId, string serialNumber)
        {
            _devices.TryRemove($"{tenantId}|{serialNumber.ToUpperInvariant()}", out _);
            return Task.CompletedTask;
        }

        public Task<(bool isBlocked, string action, string? matchedPattern)> IsVersionBlockedAsync(string agentVersion)
            => Task.FromResult<(bool, string, string?)>((false, "Block", null)); // evaluator uses BlockedVersionService's own matcher

        public Task<List<BlockedVersionEntry>> GetBlockedVersionsAsync()
        {
            lock (_versionRules)
                return Task.FromResult(new List<BlockedVersionEntry>(_versionRules));
        }

        public Task BlockVersionAsync(string versionPattern, string action, string createdByEmail, string? reason = null)
        {
            AddVersionRule(versionPattern, action);
            return Task.CompletedTask;
        }

        public Task UnblockVersionAsync(string versionPattern)
        {
            lock (_versionRules)
                _versionRules.RemoveAll(r => string.Equals(r.VersionPattern, versionPattern, StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }
    }
}

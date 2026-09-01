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

    // =========================================================================
    // Certificate-identity leg (CWE-807: block keyed by a caller-declared serial)
    // =========================================================================

    private const string DeviceId = "0f8fad5b-d9cb-469f-a165-70867728950e";

    [Fact]
    public async Task Evaluate_SerialHeaderMissing_IdentityAlias_Blocked()
    {
        // Bypass 1 of the finding: a blocked device that simply omits X-Device-SerialNumber
        // used to skip the device check entirely.
        var repo = new FakeSecurityRepo();
        var unblockAt = DateTime.UtcNow.AddHours(12);
        repo.SetDeviceBlock(TenantA, Serial, unblockAt, "Block");
        repo.SetIdentityAlias(TenantA, DeviceId, Serial);
        var (evaluator, savedOpsEvents) = CreateEvaluator(repo);

        var verdict = await evaluator.EvaluateAsync(TenantA, serialNumber: null, "2.0.114", "telemetry", intuneDeviceId: DeviceId);

        Assert.True(verdict.IsBlocked);
        Assert.False(verdict.IsKill);
        Assert.Equal(unblockAt, verdict.UnblockAt);
        Assert.Equal(Serial, verdict.BlockedSerial);
        Assert.Equal("IdentityBlocked", verdict.IdentityBinding);
        Assert.Empty(savedOpsEvents);
    }

    [Fact]
    public async Task Evaluate_ForeignSerialHeader_IdentityAlias_Blocked()
    {
        // Bypass 2: a foreign (unblocked) serial in the header misses the serial row.
        var repo = new FakeSecurityRepo();
        repo.SetDeviceBlock(TenantA, Serial, DateTime.UtcNow.AddHours(12), "Block");
        repo.SetIdentityAlias(TenantA, DeviceId, Serial);
        var (evaluator, _) = CreateEvaluator(repo);

        var verdict = await evaluator.EvaluateAsync(TenantA, "SOMEONE-ELSES-SERIAL", "2.0.114", "telemetry", intuneDeviceId: DeviceId);

        Assert.True(verdict.IsBlocked);
        Assert.Equal(Serial, verdict.BlockedSerial);
        Assert.Equal("IdentityBlocked", verdict.IdentityBinding);
    }

    [Fact]
    public async Task Evaluate_IdentityKill_EmitsOneOpsEvent_KeyedOnBlockedSerial()
    {
        var repo = new FakeSecurityRepo();
        repo.SetDeviceBlock(TenantA, Serial, DateTime.UtcNow.AddHours(12), "Kill");
        repo.SetIdentityAlias(TenantA, DeviceId, Serial);
        var (evaluator, savedOpsEvents) = CreateEvaluator(repo);

        var first = await evaluator.EvaluateAsync(TenantA, null, "2.0.114", "config", intuneDeviceId: DeviceId);
        var second = await evaluator.EvaluateAsync(TenantA, "FORGED", "2.0.114", "telemetry", intuneDeviceId: DeviceId);

        Assert.True(first.IsKill);
        Assert.True(second.IsKill);
        var evt = Assert.Single(savedOpsEvents);
        Assert.Equal("KillSignalDelivered", evt.EventType);
        Assert.Contains(Serial, evt.Details);
    }

    [Fact]
    public async Task Evaluate_SerialHit_ShortCircuits_IdentityNeverConsulted()
    {
        // Cost pin: the honest case (header == blocked serial) must not pay a second lookup.
        var repo = new FakeSecurityRepo();
        repo.SetDeviceBlock(TenantA, Serial, DateTime.UtcNow.AddHours(12), "Block");
        repo.SetIdentityAlias(TenantA, DeviceId, Serial);
        var (evaluator, _) = CreateEvaluator(repo);

        var verdict = await evaluator.EvaluateAsync(TenantA, Serial, "2.0.114", "telemetry", intuneDeviceId: DeviceId);

        Assert.True(verdict.IsBlocked);
        Assert.Equal("Match", verdict.IdentityBinding);
        Assert.Equal(0, repo.IdentityLookups);
    }

    [Fact]
    public async Task Evaluate_NoIdentity_SerialOnly_UnchangedBehaviour()
    {
        // Bootstrap-token callers / non-GUID CN: byte-identical to the pre-alias behaviour.
        var repo = new FakeSecurityRepo();
        repo.SetDeviceBlock(TenantA, Serial, DateTime.UtcNow.AddHours(12), "Block");
        repo.SetIdentityAlias(TenantA, DeviceId, Serial);
        var (evaluator, _) = CreateEvaluator(repo);

        var dodged = await evaluator.EvaluateAsync(TenantA, serialNumber: null, "2.0.114", "telemetry");
        var clean = await evaluator.EvaluateAsync(TenantA, "OTHER", "2.0.114", "telemetry");

        Assert.False(dodged.IsBlocked);
        Assert.False(clean.IsBlocked);
        Assert.Equal("NoIdentity", dodged.IdentityBinding);
        Assert.Equal(0, repo.IdentityLookups);
    }

    [Fact]
    public async Task Evaluate_IdentityPresent_NotBlocked_BindingMatch()
    {
        var (evaluator, _) = CreateEvaluator(new FakeSecurityRepo());

        var verdict = await evaluator.EvaluateAsync(TenantA, Serial, "2.0.114", "telemetry", intuneDeviceId: DeviceId);

        Assert.False(verdict.IsBlocked);
        Assert.Equal("Match", verdict.IdentityBinding);
    }

    // =========================================================================
    // Session-scoped blocks (watchdog auto-block): decided once the session is known
    // =========================================================================

    private const string BlockedSession = "33333333-3333-3333-3333-333333333333";
    private const string NewSession = "44444444-4444-4444-4444-444444444444";

    [Fact]
    public async Task Evaluate_SessionScoped_WithoutSession_BlockedAndCarriesSessionIds()
    {
        var repo = new FakeSecurityRepo();
        repo.SetDeviceBlock(TenantA, Serial, DateTime.UtcNow.AddHours(12), "Block", blockedSessionIds: BlockedSession);
        var (evaluator, _) = CreateEvaluator(repo);

        var verdict = await evaluator.EvaluateAsync(TenantA, Serial, "2.0.114", "telemetry");

        Assert.True(verdict.IsBlocked);
        Assert.True(verdict.IsSessionScoped);
        Assert.Equal(BlockedSession, verdict.BlockedSessionIds);
    }

    [Fact]
    public async Task Evaluate_SessionScoped_SameSession_StaysBlocked()
    {
        var repo = new FakeSecurityRepo();
        repo.SetDeviceBlock(TenantA, Serial, DateTime.UtcNow.AddHours(12), "Block", blockedSessionIds: BlockedSession);
        var (evaluator, _) = CreateEvaluator(repo);

        var verdict = await evaluator.EvaluateAsync(TenantA, Serial, "2.0.114", "telemetry", sessionId: BlockedSession);

        Assert.True(verdict.IsBlocked);
    }

    [Fact]
    public async Task Evaluate_SessionScoped_NewSession_AutoUnblocks_ThenVersionLegRuns()
    {
        // A new enrollment on the same device lifts the runaway-session block — and the version
        // leg the device hit had short-circuited must still get its turn.
        var repo = new FakeSecurityRepo();
        repo.SetDeviceBlock(TenantA, Serial, DateTime.UtcNow.AddHours(12), "Block", blockedSessionIds: BlockedSession);
        repo.AddVersionRule("1.*", "Block");
        var (evaluator, _) = CreateEvaluator(repo);

        var lifted = await evaluator.EvaluateAsync(TenantA, Serial, "2.0.114", "telemetry", sessionId: NewSession);
        var oldAgent = await evaluator.EvaluateAsync(TenantA, Serial, "1.0.30", "telemetry", sessionId: NewSession);

        Assert.False(lifted.IsBlocked);
        Assert.True(oldAgent.IsBlocked);
        Assert.Contains("1.*", oldAgent.Message);
    }

    [Fact]
    public async Task Evaluate_SessionScoped_ViaIdentity_NewSession_AutoUnblocksPrimary()
    {
        var repo = new FakeSecurityRepo();
        repo.SetDeviceBlock(TenantA, Serial, DateTime.UtcNow.AddHours(12), "Block", blockedSessionIds: BlockedSession);
        repo.SetIdentityAlias(TenantA, DeviceId, Serial);
        var (evaluator, _) = CreateEvaluator(repo);

        var pending = await evaluator.EvaluateAsync(TenantA, null, "2.0.114", "telemetry", intuneDeviceId: DeviceId);
        var lifted = await evaluator.EvaluateAsync(TenantA, null, "2.0.114", "telemetry", intuneDeviceId: DeviceId, sessionId: NewSession);

        Assert.True(pending.IsSessionScoped);
        Assert.False(lifted.IsBlocked);
        // The auto-unblock runs through the primary serial, so the serial row is gone too.
        var serialLeg = await repo.IsDeviceBlockedAsync(TenantA, Serial);
        Assert.False(serialLeg.isBlocked);
    }

    [Fact]
    public async Task Evaluate_Kill_IsNeverSessionScoped()
    {
        var repo = new FakeSecurityRepo();
        repo.SetDeviceBlock(TenantA, Serial, DateTime.UtcNow.AddHours(12), "Kill", blockedSessionIds: BlockedSession);
        var (evaluator, _) = CreateEvaluator(repo);

        var verdict = await evaluator.EvaluateAsync(TenantA, Serial, "2.0.114", "telemetry", sessionId: NewSession);

        Assert.True(verdict.IsBlocked);
        Assert.True(verdict.IsKill);
        Assert.False(verdict.IsSessionScoped);
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
        var alertDispatch = TestNotifications.InertOpsAlertDispatch(adminConfig.Object);
        var opsService = new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, alertDispatch);

        var evaluator = new KillSwitchEvaluator(
            new BlockedDeviceService(repo, Mock.Of<ISessionRepository>(), NullLogger<BlockedDeviceService>.Instance),
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

        public void SetDeviceBlock(string tenantId, string serialNumber, DateTime unblockAt, string action, string? blockedSessionIds = null)
        {
            _devices[$"{tenantId}|{serialNumber.ToUpperInvariant()}"] = new BlockedDeviceEntry
            {
                TenantId = tenantId,
                SerialNumber = serialNumber,
                BlockedAt = DateTime.UtcNow,
                UnblockAt = unblockAt,
                Action = action,
                BlockedSessionIds = blockedSessionIds,
            };
        }

        /// <summary>Storage reads of the identity leg — pins that a serial hit never pays a second lookup.</summary>
        public int IdentityLookups;

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
                return Task.FromResult<(bool, DateTime?, string, string?)>((true, uat, entry.Action, entry.BlockedSessionIds));
            }
            return Task.FromResult<(bool, DateTime?, string, string?)>((false, null, "Block", null));
        }

        /// <summary>Alias row: the identity leg answers with the primary's verdict + serial.</summary>
        public void SetIdentityAlias(string tenantId, string intuneDeviceId, string serialNumber)
        {
            _aliases[$"{tenantId}|id:{intuneDeviceId.ToLowerInvariant()}"] = serialNumber;
        }

        // alias key → primary serial (mirrors the id:{guid} rows)
        private readonly ConcurrentDictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

        public async Task<(bool isBlocked, DateTime? unblockAt, string action, string? blockedSessionIds, string? serialNumber)> IsDeviceIdentityBlockedAsync(
            string tenantId, string intuneDeviceId)
        {
            System.Threading.Interlocked.Increment(ref IdentityLookups);
            if (!_aliases.TryGetValue($"{tenantId}|id:{intuneDeviceId.ToLowerInvariant()}", out var serial))
                return (false, null, "Block", null, null);
            var (isBlocked, unblockAt, action, ids) = await IsDeviceBlockedAsync(tenantId, serial);
            return (isBlocked, unblockAt, action, ids, isBlocked ? serial.ToUpperInvariant() : null);
        }

        public Task<List<BlockedDeviceEntry>> GetBlockedDevicesAsync(string tenantId)
            => Task.FromResult(_devices.Values.Where(d => string.Equals(d.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)).ToList());

        public Task<List<BlockedDeviceEntry>> GetAllBlockedDevicesAsync()
            => Task.FromResult(_devices.Values.ToList());

        public Task BlockDeviceAsync(string tenantId, string serialNumber, int durationHours,
            string blockedByEmail, string? reason = null, string action = "Block", string? blockedSessionId = null,
            IReadOnlyCollection<string>? aliasDeviceIds = null)
        {
            SetDeviceBlock(tenantId, serialNumber, DateTime.UtcNow.AddHours(durationHours), action);
            foreach (var id in aliasDeviceIds ?? Array.Empty<string>())
                SetIdentityAlias(tenantId, id, serialNumber);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> UnblockDeviceAsync(string tenantId, string serialNumber)
        {
            _devices.TryRemove($"{tenantId}|{serialNumber.ToUpperInvariant()}", out _);
            var removed = new List<string>();
            foreach (var kv in _aliases)
            {
                if (!kv.Key.StartsWith($"{tenantId}|id:", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(kv.Value, serialNumber, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (_aliases.TryRemove(kv.Key, out _))
                    removed.Add(kv.Key.Substring(kv.Key.IndexOf("id:", StringComparison.Ordinal) + 3));
            }
            return Task.FromResult<IReadOnlyList<string>>(removed);
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

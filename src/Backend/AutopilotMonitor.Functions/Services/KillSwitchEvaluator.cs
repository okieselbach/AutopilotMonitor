using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Shared device- and version-kill-switch evaluation for every agent-facing endpoint that
    /// can deliver a Block/Kill control signal (telemetry ingest + agent config). Centralising
    /// the check keeps the two delivery channels behaviourally identical and gives kill
    /// delivery a single observability point: every served Kill emits a
    /// <c>KillSignalDelivered</c> ops event (throttled — an old agent binary that does not
    /// understand the kill field keeps calling every few seconds and must not flood OpsEvents).
    /// Check order mirrors the original ingest path: device block first (short-circuits),
    /// then version block.
    /// </summary>
    public class KillSwitchEvaluator
    {
        private readonly BlockedDeviceService _blockedDeviceService;
        private readonly BlockedVersionService _blockedVersionService;
        private readonly OpsEventService _opsEventService;
        private readonly ILogger<KillSwitchEvaluator> _logger;

        // One KillSignalDelivered ops event per tenant+serial+pattern per window. In-memory,
        // per-instance (state resets on Function App restart) — same accepted trade-off as
        // HardwareRejectionThrottleService: kills are rare, a duplicate event after a restart
        // or from a second instance is harmless, a per-request event from a kill-blind old
        // agent is not.
        private static readonly TimeSpan OpsEventThrottleWindow = TimeSpan.FromHours(24);
        private readonly ConcurrentDictionary<string, DateTime> _lastOpsEvent = new(StringComparer.OrdinalIgnoreCase);

        public KillSwitchEvaluator(
            BlockedDeviceService blockedDeviceService,
            BlockedVersionService blockedVersionService,
            OpsEventService opsEventService,
            ILogger<KillSwitchEvaluator> logger)
        {
            _blockedDeviceService = blockedDeviceService;
            _blockedVersionService = blockedVersionService;
            _opsEventService = opsEventService;
            _logger = logger;
        }

        /// <summary>
        /// Runs the device-serial check, then the agent-version check. Returns a non-blocked
        /// verdict when neither matches. <paramref name="channel"/> names the delivery channel
        /// for logging + the ops event ("telemetry" or "config").
        /// </summary>
        public async Task<KillSwitchVerdict> EvaluateAsync(
            string tenantId, string? serialNumber, string? agentVersion, string channel)
        {
            if (!string.IsNullOrEmpty(serialNumber))
            {
                // Session-aware block: without body-parse we can't discriminate on SessionId,
                // so we use the tenant/serial blanket check.
                var (isBlocked, unblockAt, blockAction, _) =
                    await _blockedDeviceService.IsBlockedAsync(tenantId, serialNumber);
                if (isBlocked)
                {
                    var isKill = string.Equals(blockAction, "Kill", StringComparison.OrdinalIgnoreCase);
                    _logger.LogWarning(
                        "KillSwitch[{Channel}]: {Action} device tenant={Tenant} serial={Serial} unblockAt={UnblockAt}",
                        channel, isKill ? "KILL" : "Block", tenantId, serialNumber, unblockAt);

                    if (isKill)
                        await TryRecordOpsEventAsync(tenantId, serialNumber, agentVersion, null, "device", channel);

                    return new KillSwitchVerdict(
                        isBlocked: true, isKill: isKill, unblockAt: unblockAt,
                        message: isKill
                            ? "Device has been issued a remote kill signal."
                            : "Device is temporarily blocked by an administrator.");
                }
            }

            if (!string.IsNullOrEmpty(agentVersion))
            {
                var (isVersionBlocked, versionAction, matchedPattern) =
                    await _blockedVersionService.IsVersionBlockedAsync(agentVersion);
                if (isVersionBlocked)
                {
                    var isKill = string.Equals(versionAction, "Kill", StringComparison.OrdinalIgnoreCase);
                    _logger.LogWarning(
                        "KillSwitch[{Channel}]: version {Action} tenant={Tenant} agentVersion={AgentVersion} pattern={Pattern}",
                        channel, isKill ? "KILL" : "block", tenantId, agentVersion, matchedPattern);

                    if (isKill)
                        await TryRecordOpsEventAsync(tenantId, serialNumber, agentVersion, matchedPattern, "version", channel);

                    return new KillSwitchVerdict(
                        isBlocked: true, isKill: isKill, unblockAt: null,
                        message: isKill
                            ? $"Agent version {agentVersion} has been issued a remote kill signal (pattern: {matchedPattern})."
                            : $"Agent version {agentVersion} is blocked by administrator (pattern: {matchedPattern}).");
                }
            }

            return KillSwitchVerdict.NotBlocked;
        }

        private async Task TryRecordOpsEventAsync(
            string tenantId, string? serialNumber, string? agentVersion, string? matchedPattern,
            string trigger, string channel)
        {
            if (!ShouldRecordOpsEvent(tenantId, serialNumber, matchedPattern)) return;

            // OpsEventService.WriteAsync is already fail-soft, but keep the kill response path
            // bulletproof regardless.
            try
            {
                await _opsEventService.RecordKillSignalDeliveredAsync(
                    tenantId, serialNumber, agentVersion, matchedPattern, trigger, channel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "KillSwitch: failed to record KillSignalDelivered ops event");
            }
        }

        // Thread-safe claim of the throttle slot (AddOrUpdate — at most one concurrent caller
        // per key wins), mirroring HardwareRejectionThrottleService.
        internal bool ShouldRecordOpsEvent(string tenantId, string? serialNumber, string? matchedPattern)
        {
            var key = $"{tenantId}|{serialNumber ?? ""}|{matchedPattern ?? ""}";
            var now = DateTime.UtcNow;

            var stored = _lastOpsEvent.AddOrUpdate(
                key,
                addValueFactory: _ => now,
                updateValueFactory: (_, existing) =>
                    (now - existing) >= OpsEventThrottleWindow ? now : existing);

            return stored == now;
        }
    }

    /// <summary>
    /// Outcome of a kill-switch evaluation. <see cref="NotBlocked"/> means the request may
    /// proceed; otherwise the caller relays Block/Kill to the agent on its channel's wire
    /// shape (ingest: DeviceBlocked response body; config: flags on AgentConfigResponse).
    /// </summary>
    public sealed class KillSwitchVerdict
    {
        public static readonly KillSwitchVerdict NotBlocked =
            new(isBlocked: false, isKill: false, unblockAt: null, message: string.Empty);

        public bool IsBlocked { get; }
        public bool IsKill { get; }
        public DateTime? UnblockAt { get; }
        public string Message { get; }

        public KillSwitchVerdict(bool isBlocked, bool isKill, DateTime? unblockAt, string message)
        {
            IsBlocked = isBlocked;
            IsKill = isKill;
            UnblockAt = unblockAt;
            Message = message;
        }
    }
}

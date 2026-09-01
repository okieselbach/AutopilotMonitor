using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
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
    /// Check order: device block by serial (short-circuits) → device block by certificate
    /// identity (the leg a serial-less or serial-forging agent cannot dodge, CWE-807) → version
    /// block.
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
        /// Runs the device-serial check, the device-identity check, then the agent-version check.
        /// Returns a non-blocked verdict when none matches. <paramref name="channel"/> names the
        /// delivery channel for logging + the ops event ("telemetry" or "config").
        /// <paramref name="intuneDeviceId"/> is the certificate identity (null = no identity on
        /// the request: bootstrap token, non-GUID CN — serial-only behaviour). <paramref name="sessionId"/>,
        /// when the caller already knows it, lets a session-scoped block auto-unblock on a new
        /// session; without it a session-scoped block answers blocked and carries its session list
        /// in <see cref="KillSwitchVerdict.BlockedSessionIds"/> so the caller can re-evaluate once
        /// the body is parsed.
        /// </summary>
        public async Task<KillSwitchVerdict> EvaluateAsync(
            string tenantId, string? serialNumber, string? agentVersion, string channel,
            string? intuneDeviceId = null, string? sessionId = null)
        {
            var identityBinding = string.IsNullOrWhiteSpace(intuneDeviceId)
                ? DeviceIdentityBinding.Outcome.NoIdentity
                : DeviceIdentityBinding.Outcome.Match;

            if (!string.IsNullOrEmpty(serialNumber))
            {
                var (isBlocked, unblockAt, blockAction, blockedSessionIds) =
                    await _blockedDeviceService.IsBlockedAsync(tenantId, serialNumber, sessionId);
                if (isBlocked)
                {
                    var isKill = string.Equals(blockAction, "Kill", StringComparison.OrdinalIgnoreCase);
                    _logger.LogWarning(
                        "KillSwitch[{Channel}]: {Action} device tenant={Tenant} serial={Serial} unblockAt={UnblockAt}",
                        channel, isKill ? "KILL" : "Block", tenantId, serialNumber, unblockAt);

                    if (isKill)
                        await TryRecordOpsEventAsync(tenantId, serialNumber, agentVersion, null, "device", channel);

                    return DeviceVerdict(isKill, unblockAt, blockedSessionIds, serialNumber, identityBinding);
                }
            }

            if (!string.IsNullOrWhiteSpace(intuneDeviceId))
            {
                var (isBlocked, unblockAt, blockAction, blockedSessionIds, rowSerial) =
                    await _blockedDeviceService.IsIdentityBlockedAsync(tenantId, intuneDeviceId, sessionId);
                if (isBlocked)
                {
                    var isKill = string.Equals(blockAction, "Kill", StringComparison.OrdinalIgnoreCase);
                    // The serial leg missed, so the header was absent or differs from the serial
                    // the block was placed under — both serials in the line, that IS the finding.
                    _logger.LogWarning(
                        "KillSwitch[{Channel}]: {Action} device by identity tenant={Tenant} headerSerial={HeaderSerial} blockedSerial={BlockedSerial} unblockAt={UnblockAt}",
                        channel, isKill ? "KILL" : "Block", tenantId, serialNumber ?? "n/a", rowSerial ?? "n/a", unblockAt);

                    if (isKill)
                        await TryRecordOpsEventAsync(tenantId, rowSerial ?? serialNumber, agentVersion, null, "device", channel);

                    return DeviceVerdict(isKill, unblockAt, blockedSessionIds, rowSerial ?? serialNumber,
                        DeviceIdentityBinding.Outcome.IdentityBlocked);
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
                            : $"Agent version {agentVersion} is blocked by administrator (pattern: {matchedPattern}).",
                        blockedSessionIds: null, blockedSerial: null, identityBinding: identityBinding);
                }
            }

            return KillSwitchVerdict.NotBlocked(identityBinding);
        }

        private static KillSwitchVerdict DeviceVerdict(
            bool isKill, DateTime? unblockAt, string? blockedSessionIds, string? blockedSerial, string identityBinding)
            => new(
                isBlocked: true, isKill: isKill, unblockAt: unblockAt,
                message: isKill
                    ? "Device has been issued a remote kill signal."
                    : "Device is temporarily blocked by an administrator.",
                blockedSessionIds: isKill ? null : blockedSessionIds,
                blockedSerial: blockedSerial,
                identityBinding: identityBinding);

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
    /// Outcome of a kill-switch evaluation. <see cref="IsBlocked"/> false means the request may
    /// proceed; otherwise the caller relays Block/Kill to the agent on its channel's wire
    /// shape (ingest: DeviceBlocked response body; config: flags on AgentConfigResponse).
    /// </summary>
    public sealed class KillSwitchVerdict
    {
        public static KillSwitchVerdict NotBlocked(string identityBinding = DeviceIdentityBinding.Outcome.NoIdentity) =>
            new(isBlocked: false, isKill: false, unblockAt: null, message: string.Empty,
                blockedSessionIds: null, blockedSerial: null, identityBinding: identityBinding);

        public bool IsBlocked { get; }
        public bool IsKill { get; }
        public DateTime? UnblockAt { get; }
        public string Message { get; }

        /// <summary>
        /// Session-scoped device block (watchdog auto-block): the comma-separated sessions it
        /// applies to. Null for whole-device blocks, kills and version blocks. A caller that
        /// evaluated without a session id should re-evaluate with it — a different session
        /// auto-unblocks.
        /// </summary>
        public string? BlockedSessionIds { get; }

        /// <summary>True when the block is session-scoped and the caller has not yet supplied a session id.</summary>
        public bool IsSessionScoped => IsBlocked && !IsKill && !string.IsNullOrEmpty(BlockedSessionIds);

        /// <summary>Serial the matching device block is keyed under (identity hits: the row's serial, not the header's).</summary>
        public string? BlockedSerial { get; }

        /// <summary>One of <see cref="DeviceIdentityBinding.Outcome"/> — the request-row dimension the caller stamps.</summary>
        public string IdentityBinding { get; }

        public KillSwitchVerdict(
            bool isBlocked, bool isKill, DateTime? unblockAt, string message,
            string? blockedSessionIds = null, string? blockedSerial = null,
            string identityBinding = DeviceIdentityBinding.Outcome.NoIdentity)
        {
            IsBlocked = isBlocked;
            IsKill = isKill;
            UnblockAt = unblockAt;
            Message = message;
            BlockedSessionIds = blockedSessionIds;
            BlockedSerial = blockedSerial;
            IdentityBinding = identityBinding;
        }
    }
}

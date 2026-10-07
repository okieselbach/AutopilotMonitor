using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Push;

/// <summary>Terminal-status hook for the ingest path: fires the pushes of everyone watching the session.</summary>
public interface IPushSessionWatchNotifier
{
    Task NotifyAsync(string tenantId, string sessionId, bool success, string? deviceName, string? serialNumber);
}

/// <summary>
/// "Notify my devices when this session ends" (plan use case 3): one watch per (session, person),
/// consumed at the terminal status. The watch row lives in the SESSION's tenant (tenant isolation
/// of the session), the devices it fires on are the watcher's OWN: their home-tenant scope and,
/// for a Global Admin, the platform scope — so an operator can watch a customer's session from
/// their own paired phone. Admin and Operator only (F14), only in a scope with an enabled Push
/// channel (K4), and the fan-out runs through the same role and freshness filter as any alert.
/// Watches expire after 48 hours (the agent's emergency break) and die with the session's tenant.
/// </summary>
public class PushSessionWatchService : IPushSessionWatchNotifier
{
    public static readonly TimeSpan WatchLifetime = TimeSpan.FromHours(48);
    /// <summary>The terminal push waits for an offline phone longer than a system message: plan "Watch bis Session-Ende + 1 h".</summary>
    public static readonly TimeSpan WatchPushTtl = TimeSpan.FromHours(1);
    private const char ScopeSeparator = ';';

    private readonly IPushDeviceRepository _repo;
    private readonly PushEligibility _eligibility;
    private readonly PushDeliveryService _delivery;
    private readonly ILogger<PushSessionWatchService> _logger;

    public PushSessionWatchService(
        IPushDeviceRepository repo,
        PushEligibility eligibility,
        PushDeliveryService delivery,
        ILogger<PushSessionWatchService> logger)
    {
        _repo = repo;
        _eligibility = eligibility;
        _delivery = delivery;
        _logger = logger;
    }

    /// <summary>The scopes a caller's own devices can live in: the platform partition for a Global Admin, the home tenant for everyone.</summary>
    internal static IReadOnlyList<NotificationScope> OwnScopes(PushCaller caller)
    {
        var scopes = new List<NotificationScope>(2);
        if (caller.IsGlobalAdmin)
            scopes.Add(NotificationScope.Platform);
        if (!string.IsNullOrWhiteSpace(caller.HomeTenantId))
            scopes.Add(NotificationScope.Tenant(caller.HomeTenantId));
        return scopes;
    }

    public async Task<PushOpResult<SessionWatchResponse>> WatchAsync(string tenantId, string sessionId, PushCaller caller)
    {
        var anyChannel = false;
        var anyEligible = false;
        var usable = new List<string>();

        foreach (var scope in OwnScopes(caller))
        {
            if (!await _eligibility.HasPushChannelAsync(scope).ConfigureAwait(false))
                continue;
            anyChannel = true;
            if (!await _eligibility.IsEligibleAsync(scope, caller.Upn, caller.ObjectId, caller.HomeTenantId).ConfigureAwait(false))
                continue;
            anyEligible = true;
            var hasDevice = (await _repo.GetDevicesAsync(scope.Key).ConfigureAwait(false))
                .Any(d => d.Status == Constants.Push.DeviceStatus.Active
                          && string.Equals(d.OwnerObjectId, caller.ObjectId, StringComparison.OrdinalIgnoreCase));
            if (hasDevice)
                usable.Add(scope.Key);
        }

        if (usable.Count == 0)
        {
            if (!anyChannel)
                return PushOpResult<SessionWatchResponse>.Fail(PushOpError.ChannelRequired, "Enable a Push notification channel first.");
            if (!anyEligible)
                return PushOpResult<SessionWatchResponse>.Fail(PushOpError.NotEligible);
            return PushOpResult<SessionWatchResponse>.Fail(PushOpError.Conflict, "Pair a device first — there is nothing to notify.");
        }

        var now = DateTime.UtcNow;
        var watch = new PushSessionWatch
        {
            TenantId = tenantId.ToLowerInvariant(),
            SessionId = sessionId,
            OwnerObjectId = caller.ObjectId,
            OwnerUpn = caller.Upn,
            OwnerScopes = string.Join(ScopeSeparator, usable),
            CreatedUtc = now,
            ExpiresUtc = now + WatchLifetime,
        };
        await _repo.UpsertWatchAsync(watch).ConfigureAwait(false);
        return PushOpResult<SessionWatchResponse>.Success(new SessionWatchResponse { Watching = true, ExpiresUtc = watch.ExpiresUtc });
    }

    public async Task<SessionWatchResponse> UnwatchAsync(string tenantId, string sessionId, PushCaller caller)
    {
        await _repo.DeleteWatchAsync(tenantId, sessionId, caller.ObjectId).ConfigureAwait(false);
        return new SessionWatchResponse { Watching = false };
    }

    public async Task<SessionWatchResponse> GetAsync(string tenantId, string sessionId, PushCaller caller)
    {
        var watch = await _repo.GetWatchAsync(tenantId, sessionId, caller.ObjectId).ConfigureAwait(false);
        var active = watch != null && watch.ExpiresUtc >= DateTime.UtcNow;
        return new SessionWatchResponse { Watching = active, ExpiresUtc = active ? watch!.ExpiresUtc : null };
    }

    /// <summary>Fail-soft: a watch push must never cost the ingest path anything.</summary>
    public async Task NotifyAsync(string tenantId, string sessionId, bool success, string? deviceName, string? serialNumber)
    {
        try
        {
            var watches = await _repo.GetWatchesForSessionAsync(tenantId, sessionId).ConfigureAwait(false);
            if (watches.Count == 0)
                return;

            var now = DateTime.UtcNow;
            var device = PushAlertProjector.Clean(deviceName, 48);
            var serial = PushAlertProjector.MaskSerial(serialNumber);
            var body = (device.Length > 0 ? device : "The device") + (serial.Length > 0 ? $" ({serial})" : string.Empty)
                       + (success ? " finished enrollment." : " failed enrollment.");

            foreach (var watch in watches)
            {
                if (watch.ExpiresUtc < now)
                {
                    await _repo.DeleteWatchAsync(tenantId, sessionId, watch.OwnerObjectId).ConfigureAwait(false);
                    continue;
                }

                foreach (var scope in ScopesOf(watch, tenantId))
                {
                    var payload = PushAlertProjector.SystemMessage("session_watch",
                        success ? "Enrollment finished" : "Enrollment failed",
                        body, success ? "success" : "error", scope, now, Constants.PortalSessionUrl(sessionId));
                    await _delivery.SendSystemToOwnerAsync(scope, watch.OwnerObjectId, payload, WatchPushTtl).ConfigureAwait(false);
                }

                await _repo.DeleteWatchAsync(tenantId, sessionId, watch.OwnerObjectId).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session watch notification failed for session {SessionId}", sessionId);
        }
    }

    private static IEnumerable<NotificationScope> ScopesOf(PushSessionWatch watch, string sessionTenantId)
    {
        var keys = (watch.OwnerScopes ?? string.Empty).Split(ScopeSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (keys.Length == 0)
            keys = new[] { sessionTenantId };
        foreach (var key in keys)
            yield return NotificationScope.FromKey(key);
    }
}

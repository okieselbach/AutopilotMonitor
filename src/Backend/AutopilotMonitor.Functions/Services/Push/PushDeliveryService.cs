using System.Collections.Concurrent;
using System.Text;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Push;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Push;

/// <summary>
/// Revokes every device of one owner in a scope — the role-loss hook (K6). Implementations never
/// throw: the admin services call it after the role row changed, and a push failure must not turn
/// a completed role change into an error.
/// </summary>
public interface IPushDeviceRevoker
{
    Task RevokeOwnerAsync(NotificationScope scope, string upn, string reason);
}

/// <summary>
/// The Push transport: resolves the Active devices of a scope, re-checks every owner's role and
/// freshness at send time, projects the alert onto the lock-screen shape, encrypts it per device
/// (RFC 8291) and sends it through the SSRF-gated push client. Outcomes land on the device row
/// (delivered / stale / failures) and in the <c>PushDelivery</c> custom event; endpoints never
/// land in a log. Fan-out per scope runs with bounded concurrency so the direct ops path stays
/// short. Never throws into the dispatcher.
/// </summary>
public class PushDeliveryService : IPushChannelSender, IPushDeviceRevoker
{
    public const string DeliveryEventName = "PushDelivery";
    public const int FanOutConcurrency = 8;
    public const int FloodWindowMinutes = 10;
    public const int FloodWindowCap = 10;
    /// <summary>Consecutive transport failures after which a device pauses (re-armed by the next successful re-subscribe).</summary>
    public const int PauseAfterConsecutiveFailures = 20;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(4);
    private static readonly TimeSpan SystemTtl = TimeSpan.FromMinutes(30);

    private readonly IPushDeviceRepository _repo;
    private readonly PushEligibility _eligibility;
    private readonly PushSettings _settings;
    private readonly WebPushSender _sender;
    private readonly Lazy<OpsEventService> _opsEvents;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PushDeliveryService> _logger;
    private readonly TelemetryClient? _telemetry;
    private readonly byte[]? _topicKey;

    public PushDeliveryService(
        IPushDeviceRepository repo,
        PushEligibility eligibility,
        PushSettings settings,
        WebPushSender sender,
        Lazy<OpsEventService> opsEvents,
        IMemoryCache cache,
        ILogger<PushDeliveryService> logger,
        TelemetryClient? telemetry = null)
    {
        _repo = repo;
        _eligibility = eligibility;
        _settings = settings;
        _sender = sender;
        _opsEvents = opsEvents;
        _cache = cache;
        _logger = logger;
        _telemetry = telemetry;
        // Topic HMAC key (K21): the platform's pagination signing key, any bytes do; absent ⇒ no Topic.
        var key = Environment.GetEnvironmentVariable("PaginationTokenSigningKey");
        _topicKey = string.IsNullOrEmpty(key) ? null : Encoding.UTF8.GetBytes(key);
    }

    // ── IPushChannelSender ───────────────────────────────────────────────────

    public async Task SendAsync(NotificationScope scope, NotificationAlert alert)
    {
        try
        {
            await SendCoreAsync(scope, alert).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Push fan-out failed for scope {Scope} alert {EventType}", scope, alert?.EventType);
        }
    }

    public async Task<WebhookTestResult> SendWithResultAsync(NotificationScope scope, NotificationAlert alert)
    {
        try
        {
            if (!_settings.IsConfigured)
                return new WebhookTestResult { Success = false, Message = "The push channel is not configured on the platform (VAPID key missing)." };

            var stats = await SendCoreAsync(scope, alert).ConfigureAwait(false);
            if (stats.Targets == 0)
                return new WebhookTestResult { Success = false, Message = "No active paired device in this scope — pair a device first." };

            return new WebhookTestResult
            {
                Success = stats.Delivered > 0,
                Message = $"Delivered to {stats.Delivered} of {stats.Targets} paired device(s)"
                          + (stats.Suppressed > 0 ? $", {stats.Suppressed} muted by flood control" : string.Empty) + ".",
            };
        }
        catch (Exception ex)
        {
            return new WebhookTestResult { Success = false, Message = $"Push send failed: {ex.Message}" };
        }
    }

    internal sealed class DeliveryStats
    {
        public int Targets;
        public int Delivered;
        public int Suppressed;
    }

    internal async Task<DeliveryStats> SendCoreAsync(NotificationScope scope, NotificationAlert alert)
    {
        var stats = new DeliveryStats();
        if (!scope.IsSet)
        {
            _logger.LogError("Push send refused: the caller passed no NotificationScope for alert {EventType}", alert.EventType);
            return stats;
        }
        if (!_settings.IsConfigured)
        {
            _logger.LogWarning("Push send skipped for scope {Scope}: {Reason}", scope, _settings.ConfigurationError);
            return stats;
        }

        var now = DateTime.UtcNow;
        var recipients = await ResolveRecipientsAsync(scope, now).ConfigureAwait(false);
        if (recipients.Count == 0)
            return stats;

        var payload = PushAlertProjector.Project(alert, scope, Guid.NewGuid().ToString("N"), now);
        var bytes = PushAlertProjector.ToJsonBytes(payload);
        var options = BuildOptions(alert, payload);
        stats.Targets = recipients.Count;

        var delivered = 0;
        var suppressed = 0;
        await Parallel.ForEachAsync(recipients, new ParallelOptions { MaxDegreeOfParallelism = FanOutConcurrency }, async (device, ct) =>
        {
            var slot = await ReserveSlotAsync(device, now).ConfigureAwait(false);
            if (slot != FloodDecision.Allowed)
            {
                Interlocked.Increment(ref suppressed);
                if (slot == FloodDecision.SuppressedFirst)
                {
                    var until = (device.WindowStartUtc ?? now).AddMinutes(FloodWindowMinutes);
                    var muted = PushAlertProjector.SystemMessage("push_muted", "Further alerts muted",
                        $"More than {FloodWindowCap} alerts in {FloodWindowMinutes} minutes — muted until {until:HH:mm} UTC.",
                        "warning", scope, now);
                    await SendToDeviceAsync(device, muted, PushAlertProjector.ToJsonBytes(muted), new PushSendOptions(SystemTtl, PushUrgency.Normal, null), alert.EventType).ConfigureAwait(false);
                }
                return;
            }

            var outcome = await SendToDeviceAsync(device, payload, bytes, options, alert.EventType).ConfigureAwait(false);
            if (outcome == PushOutcome.Delivered)
                Interlocked.Increment(ref delivered);
        }).ConfigureAwait(false);

        stats.Delivered = delivered;
        stats.Suppressed = suppressed;
        return stats;
    }

    /// <summary>
    /// The Active devices an alert in <paramref name="scope"/> reaches, role and freshness re-checked
    /// per owner (D-327). A tenant scope additionally reaches the Active platform devices whose
    /// owner's home tenant it is (D-334): a Global Admin's receiver holds one pairing, and in their
    /// own tenant they are an admin like any other — those rows pass the platform eligibility rule,
    /// and one endpoint is sent to once whichever scope its row lives in. No other tenant ever
    /// reaches a platform device.
    /// </summary>
    internal async Task<List<PushDevice>> ResolveRecipientsAsync(NotificationScope scope, DateTime now)
    {
        var own = ActiveWebPush(await _repo.GetDevicesAsync(scope.Key).ConfigureAwait(false));
        var recipients = own.Count == 0 ? new List<PushDevice>() : await FilterEligibleAsync(scope, own, now).ConfigureAwait(false);
        if (scope.IsPlatform)
            return recipients;

        var homeTenantRows = ActiveWebPush(await _repo.GetDevicesAsync(NotificationScope.Platform.Key).ConfigureAwait(false))
            .Where(d => string.Equals(d.OwnerHomeTenantId, scope.Key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (homeTenantRows.Count == 0)
            return recipients;

        var endpoints = new HashSet<string>(recipients.Select(d => d.Endpoint), StringComparer.Ordinal);
        foreach (var device in await FilterEligibleAsync(NotificationScope.Platform, homeTenantRows, now).ConfigureAwait(false))
        {
            if (endpoints.Add(device.Endpoint))
                recipients.Add(device);
        }
        return recipients;
    }

    private static List<PushDevice> ActiveWebPush(List<PushDevice> devices)
        => devices.Where(d => d.Status == Constants.Push.DeviceStatus.Active && d.Kind == "webpush").ToList();

    /// <summary>One push to one device, outside the flood window: test, paired, revoked, paused, session watch.</summary>
    public async Task<PushOutcome> SendSystemAsync(PushDevice device, PushPayload payload, TimeSpan? ttl = null)
    {
        if (!_settings.IsConfigured)
            return PushOutcome.ConfigError;
        return await SendToDeviceAsync(device, payload, PushAlertProjector.ToJsonBytes(payload),
            new PushSendOptions(ttl ?? SystemTtl, PushUrgency.High, null), payload.Type).ConfigureAwait(false);
    }

    /// <summary>
    /// A system push to every Active device ONE person owns in a scope, through the same role and
    /// freshness filter as an alert (D-327: resolved at every send) — the session-watch fan-out.
    /// Returns the number of deliveries.
    /// </summary>
    public async Task<int> SendSystemToOwnerAsync(NotificationScope scope, string ownerObjectId, PushPayload payload, TimeSpan? ttl = null)
    {
        if (!scope.IsSet || !_settings.IsConfigured || string.IsNullOrEmpty(ownerObjectId))
            return 0;

        var now = DateTime.UtcNow;
        var owned = (await _repo.GetDevicesAsync(scope.Key).ConfigureAwait(false))
            .Where(d => d.Status == Constants.Push.DeviceStatus.Active
                        && string.Equals(d.OwnerObjectId, ownerObjectId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (owned.Count == 0)
            return 0;

        var delivered = 0;
        foreach (var device in await FilterEligibleAsync(scope, owned, now).ConfigureAwait(false))
        {
            if (await SendSystemAsync(device, payload, ttl).ConfigureAwait(false) == PushOutcome.Delivered)
                delivered++;
        }
        return delivered;
    }

    // ── IPushDeviceRevoker ───────────────────────────────────────────────────

    public async Task RevokeOwnerAsync(NotificationScope scope, string upn, string reason)
    {
        try
        {
            if (!scope.IsSet || string.IsNullOrWhiteSpace(upn))
                return;
            var devices = (await _repo.GetDevicesAsync(scope.Key).ConfigureAwait(false))
                .Where(d => string.Equals(d.OwnerUpn, upn, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var device in devices)
                await RevokeDeviceAsync(device, reason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Revoking push devices after role loss failed for scope {Scope}", scope);
        }
    }

    /// <summary>Wipe push (best effort, only reaches an online device), then the row is gone.</summary>
    public async Task RevokeDeviceAsync(PushDevice device, string reason)
    {
        var scope = NotificationScope.FromKey(device.Scope);
        if (device.Status == Constants.Push.DeviceStatus.Active || device.Status == Constants.Push.DeviceStatus.Paused)
        {
            var wipe = PushAlertProjector.SystemMessage("push_revoked", "Device unpaired",
                "This device no longer receives Autopilot Monitor alerts.", "info", scope, DateTime.UtcNow);
            await SendSystemAsync(device, wipe).ConfigureAwait(false);
        }
        await _repo.DeleteDeviceAsync(device.Scope, device.DeviceId).ConfigureAwait(false);
        Track("PushPairing", new Dictionary<string, string>
        {
            ["action"] = "revoked", ["reason"] = reason, ["scope"] = scope.IsPlatform ? "platform" : "tenant", ["platform"] = device.Platform,
        });
    }

    // ── Recipients ──────────────────────────────────────────────────────────

    private async Task<List<PushDevice>> FilterEligibleAsync(NotificationScope scope, List<PushDevice> devices, DateTime now)
    {
        var eligibleByOwner = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var freshByOwner = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var result = new List<PushDevice>();

        foreach (var device in devices)
        {
            var ownerKey = $"{device.OwnerHomeTenantId}|{device.OwnerObjectId}|{device.OwnerUpn}";
            if (!eligibleByOwner.TryGetValue(ownerKey, out var eligible))
            {
                eligible = await _eligibility.IsEligibleAsync(scope, device.OwnerUpn, device.OwnerObjectId, device.OwnerHomeTenantId).ConfigureAwait(false);
                eligibleByOwner[ownerKey] = eligible;
            }
            if (!eligible)
            {
                // The role-loss hook normally gets here first; this is the belt to its braces.
                _logger.LogInformation("Push device {DeviceId} skipped: owner no longer eligible in scope {Scope}", device.DeviceId, scope);
                continue;
            }

            if (!freshByOwner.TryGetValue(ownerKey, out var fresh))
            {
                fresh = await _eligibility.IsOwnerFreshAsync(device.OwnerHomeTenantId, device.OwnerObjectId, now).ConfigureAwait(false);
                freshByOwner[ownerKey] = fresh;
            }
            if (!fresh)
            {
                await PauseAsync(device, "owner_inactive", scope, now).ConfigureAwait(false);
                continue;
            }

            result.Add(device);
        }

        return result;
    }

    /// <summary>
    /// The one pause path (send time and maintenance): a last "sign in to resume" push, then the
    /// row flips to Paused. Only Active rows pause — the pause push itself may have found the
    /// endpoint gone, and a Stale row stays Stale.
    /// </summary>
    public async Task PauseAsync(PushDevice device, string reason, NotificationScope scope, DateTime now)
    {
        var paused = PushAlertProjector.SystemMessage("push_paused", "Alerts paused",
            "Sign in to the portal to resume alerts on this device.", "warning", scope, now);
        await SendSystemAsync(device, paused).ConfigureAwait(false);
        await _repo.MutateDeviceAsync(device.Scope, device.DeviceId, current =>
            current.Status != Constants.Push.DeviceStatus.Active ? null : new Dictionary<string, object?>
            {
                ["Status"] = Constants.Push.DeviceStatus.Paused,
                ["StatusReason"] = reason,
                ["StatusChangedUtc"] = now,
            }).ConfigureAwait(false);
        Track("PushPairing", new Dictionary<string, string> { ["action"] = "paused", ["reason"] = reason, ["scope"] = scope.IsPlatform ? "platform" : "tenant", ["platform"] = device.Platform });
    }

    /// <summary>An owner-inactivity pause lifts itself once the owner signs in again (maintenance) — no re-pairing.</summary>
    public async Task ResumeAsync(PushDevice device, DateTime now)
    {
        var scope = NotificationScope.FromKey(device.Scope);
        var reason = device.StatusReason ?? string.Empty;
        var landed = await _repo.MutateDeviceAsync(device.Scope, device.DeviceId, current =>
            current.Status != Constants.Push.DeviceStatus.Paused ? null : new Dictionary<string, object?>
            {
                ["Status"] = Constants.Push.DeviceStatus.Active,
                ["StatusReason"] = null,
                ["StatusChangedUtc"] = now,
            }).ConfigureAwait(false);
        if (landed)
            Track("PushPairing", new Dictionary<string, string> { ["action"] = "resumed", ["reason"] = reason, ["scope"] = scope.IsPlatform ? "platform" : "tenant", ["platform"] = device.Platform });
    }

    // ── Flood control (K19): the window lives on the row, merged under CAS ────

    internal enum FloodDecision { Allowed, SuppressedFirst, Suppressed }

    private async Task<FloodDecision> ReserveSlotAsync(PushDevice device, DateTime now)
    {
        var decision = FloodDecision.Allowed;
        var landed = await _repo.MutateDeviceAsync(device.Scope, device.DeviceId, current =>
        {
            var windowStart = current.WindowStartUtc;
            if (windowStart == null || now - windowStart.Value > TimeSpan.FromMinutes(FloodWindowMinutes))
            {
                decision = FloodDecision.Allowed;
                return new Dictionary<string, object?> { ["WindowStartUtc"] = now, ["WindowCount"] = 1, ["SuppressedCount"] = 0 };
            }
            if (current.WindowCount < FloodWindowCap)
            {
                decision = FloodDecision.Allowed;
                return new Dictionary<string, object?> { ["WindowCount"] = current.WindowCount + 1 };
            }
            decision = current.SuppressedCount == 0 ? FloodDecision.SuppressedFirst : FloodDecision.Suppressed;
            device.WindowStartUtc = windowStart;
            return new Dictionary<string, object?> { ["SuppressedCount"] = current.SuppressedCount + 1 };
        }).ConfigureAwait(false);

        // A lost CAS race or a vanished row: do not send rather than send unbounded.
        return landed ? decision : FloodDecision.Suppressed;
    }

    // ── Transport ───────────────────────────────────────────────────────────

    private PushSendOptions BuildOptions(NotificationAlert alert, PushPayload payload)
    {
        var urgency = alert.Severity switch
        {
            NotificationSeverity.Error => PushUrgency.High,
            NotificationSeverity.Warning => PushUrgency.High,
            NotificationSeverity.Success => PushUrgency.Normal,
            _ => PushUrgency.Low,
        };
        var ttl = string.Equals(alert.EventType, "test", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromMinutes(5) : DefaultTtl;
        return new PushSendOptions(ttl, urgency, PushAlertProjector.BuildTopic(payload.PortalUrl, _topicKey));
    }

    private async Task<PushOutcome> SendToDeviceAsync(PushDevice device, PushPayload payload, byte[] plaintext, PushSendOptions options, string? alertEventType)
    {
        var keys = _settings.Keys!;
        var service = ClassifyService(device.Endpoint);
        var scopeKind = device.Scope == Constants.Push.PlatformScope ? "platform" : "tenant";

        var key = keys.Resolve(device.VapidKid);
        if (key == null)
        {
            await MarkStaleAsync(device, "vapid_key_mismatch", null).ConfigureAwait(false);
            await RaiseDeliveryFailedAsync(service, null, device.VapidKid, "vapid_key_mismatch", alertEventType).ConfigureAwait(false);
            Track(DeliveryEventName, new Dictionary<string, string> { ["service"] = service, ["outcome"] = "config_error", ["statusCode"] = "", ["scope"] = scopeKind, ["kid"] = device.VapidKid });
            return PushOutcome.ConfigError;
        }

        if (!WebPushSubscription.TryParse(device.Endpoint, device.P256dh, device.Auth, out var subscription, out _) || subscription == null)
        {
            await MarkStaleAsync(device, "invalid_subscription", null).ConfigureAwait(false);
            return PushOutcome.Failed;
        }

        PushSendResult result;
        try
        {
            var body = WebPushEncryptor.Encrypt(plaintext, subscription);
            result = await _sender.SendAsync(subscription, body, options, key, _settings.Subject, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Push send threw for device {DeviceId} ({Service})", device.DeviceId, service);
            result = new PushSendResult(PushOutcome.Failed, null, null, "exception");
        }

        await ApplyOutcomeAsync(device, result, service, alertEventType).ConfigureAwait(false);
        Track(DeliveryEventName, new Dictionary<string, string>
        {
            ["service"] = service,
            ["outcome"] = OutcomeName(result.Outcome),
            ["statusCode"] = result.StatusCode?.ToString() ?? string.Empty,
            ["scope"] = scopeKind,
            ["kid"] = device.VapidKid,
            ["type"] = payload.Type,
        });
        return result.Outcome;
    }

    private async Task ApplyOutcomeAsync(PushDevice device, PushSendResult result, string service, string? alertEventType)
    {
        var now = DateTime.UtcNow;
        switch (result.Outcome)
        {
            case PushOutcome.Delivered:
                await _repo.MutateDeviceAsync(device.Scope, device.DeviceId, _ => new Dictionary<string, object?>
                {
                    ["LastDeliveredUtc"] = now, ["ConsecutiveFailures"] = 0, ["LastStatusCode"] = result.StatusCode,
                }).ConfigureAwait(false);
                break;

            case PushOutcome.Gone:
                // K14: never delete — the receiver re-arms a Stale row on its next open (PUT push/device).
                await MarkStaleAsync(device, "gone", result.StatusCode).ConfigureAwait(false);
                break;

            case PushOutcome.PayloadTooLarge:
                _logger.LogError("Push payload exceeded the service limit for {Service} — projection budget bug", service);
                break;

            case PushOutcome.RetryLater:
                await _repo.MutateDeviceAsync(device.Scope, device.DeviceId, _ => new Dictionary<string, object?>
                {
                    ["LastStatusCode"] = result.StatusCode,
                }).ConfigureAwait(false);
                break;

            case PushOutcome.ConfigError:
                await RecordFailureAsync(device, result.StatusCode, now).ConfigureAwait(false);
                await RaiseDeliveryFailedAsync(service, result.StatusCode, device.VapidKid, result.Reason ?? "config_error", alertEventType).ConfigureAwait(false);
                break;

            default:
                await RecordFailureAsync(device, result.StatusCode, now).ConfigureAwait(false);
                break;
        }
    }

    private Task RecordFailureAsync(PushDevice device, int? statusCode, DateTime now)
        => _repo.MutateDeviceAsync(device.Scope, device.DeviceId, current =>
        {
            var failures = current.ConsecutiveFailures + 1;
            var patch = new Dictionary<string, object?> { ["ConsecutiveFailures"] = failures, ["LastStatusCode"] = statusCode };
            if (failures >= PauseAfterConsecutiveFailures)
            {
                patch["Status"] = Constants.Push.DeviceStatus.Paused;
                patch["StatusReason"] = "delivery_failures";
                patch["StatusChangedUtc"] = now;
            }
            return patch;
        });

    private Task MarkStaleAsync(PushDevice device, string reason, int? statusCode)
        => _repo.MutateDeviceAsync(device.Scope, device.DeviceId, _ => new Dictionary<string, object?>
        {
            ["Status"] = Constants.Push.DeviceStatus.Stale,
            ["StatusReason"] = reason,
            ["StatusChangedUtc"] = DateTime.UtcNow,
            ["LastStatusCode"] = statusCode,
        });

    /// <summary>
    /// At most one ops event per cause and hour per instance; never for a push that itself carried
    /// a PushDeliveryFailed alert (the alarm must not feed its own loop, D-185).
    /// </summary>
    private async Task RaiseDeliveryFailedAsync(string service, int? statusCode, string kid, string reason, string? alertEventType)
    {
        if (string.Equals(alertEventType, OpsEventTypes.PushDeliveryFailed, StringComparison.Ordinal))
            return;

        var cacheKey = $"push-delivery-failed:{service}:{statusCode}:{reason}";
        if (_cache.TryGetValue(cacheKey, out _))
            return;
        _cache.Set(cacheKey, true, TimeSpan.FromHours(1));

        try
        {
            await _opsEvents.Value.RecordPushDeliveryFailedAsync(service, statusCode, kid, reason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recording PushDeliveryFailed ops event failed");
        }
    }

    private static string OutcomeName(PushOutcome outcome) => outcome switch
    {
        PushOutcome.Delivered => "delivered",
        PushOutcome.Gone => "gone",
        PushOutcome.PayloadTooLarge => "bug",
        PushOutcome.RetryLater => "retry_later",
        PushOutcome.ConfigError => "config_error",
        _ => "failed",
    };

    /// <summary>The push service behind an endpoint, by host — the only endpoint-derived value that leaves the backend.</summary>
    public static string ClassifyService(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return "other";
        var host = uri.Host;
        if (host.EndsWith(".push.apple.com", StringComparison.OrdinalIgnoreCase)) return "apple";
        if (host.Equals("fcm.googleapis.com", StringComparison.OrdinalIgnoreCase)) return "fcm";
        if (host.EndsWith(".push.services.mozilla.com", StringComparison.OrdinalIgnoreCase)) return "mozilla";
        if (host.EndsWith(".notify.windows.com", StringComparison.OrdinalIgnoreCase)) return "wns";
        return "other";
    }

    private void Track(string name, Dictionary<string, string> properties)
    {
        try
        {
            _telemetry?.TrackEvent(name, properties);
        }
        catch
        {
            // Telemetry is never allowed to fail a send.
        }
    }
}

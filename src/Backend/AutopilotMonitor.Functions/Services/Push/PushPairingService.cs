using System.Security.Cryptography;
using System.Text;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Push;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Push;

/// <summary>
/// Who is pairing or managing devices: the authenticated caller as the middleware resolved it.
/// <paramref name="IsScopeAdmin"/> = sees and manages every device of the scope; <paramref name="IsGlobalAdmin"/>
/// = the caller's own devices may live in the platform partition (session watches fan out there).
/// </summary>
public sealed record PushCaller(string Upn, string ObjectId, string HomeTenantId, bool IsScopeAdmin, bool IsGlobalAdmin = false);

/// <summary>Outcome of a pairing/device operation; the function maps it to a status + error code.</summary>
public enum PushOpError
{
    None,
    ChannelRequired,
    NotEligible,
    NotFound,
    CodeInvalid,
    CodeUsed,
    InvalidSubscription,
    NotConfigured,
    Conflict,
}

public sealed record PushOpResult<T>(T? Value, PushOpError Error, string? Message = null)
{
    public bool Ok => Error == PushOpError.None;
    public static PushOpResult<T> Success(T value) => new(value, PushOpError.None);
    public static PushOpResult<T> Fail(PushOpError error, string? message = null) => new(default, error, message);
}

/// <summary>
/// Pairing (Korrekturen K7–K13) and device management for the Web Push channel. The PC side
/// creates a ten-minute one-shot code from an authenticated session; the receiver redeems it
/// anonymously (code-gated) and ends up Pending; the PC confirms. Codes are stored hashed, the
/// device secret is stored hashed, and no method here ever logs or returns an endpoint.
/// </summary>
public class PushPairingService
{
    public const int PollSeconds = 3;
    private const int DeviceSecretBytes = 32;

    private readonly IPushDeviceRepository _repo;
    private readonly PushEligibility _eligibility;
    private readonly PushSettings _settings;
    private readonly PushDeliveryService _delivery;
    private readonly OpsEventService _opsEvents;
    private readonly IMaintenanceRepository _maintenanceRepo;
    private readonly TenantConfigurationService _tenantConfig;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PushPairingService> _logger;
    private readonly TelemetryClient? _telemetry;

    public PushPairingService(
        IPushDeviceRepository repo,
        PushEligibility eligibility,
        PushSettings settings,
        PushDeliveryService delivery,
        OpsEventService opsEvents,
        IMaintenanceRepository maintenanceRepo,
        TenantConfigurationService tenantConfig,
        IMemoryCache cache,
        ILogger<PushPairingService> logger,
        TelemetryClient? telemetry = null)
    {
        _repo = repo;
        _eligibility = eligibility;
        _settings = settings;
        _delivery = delivery;
        _opsEvents = opsEvents;
        _maintenanceRepo = maintenanceRepo;
        _tenantConfig = tenantConfig;
        _cache = cache;
        _logger = logger;
        _telemetry = telemetry;
    }

    // ── PC side: create / status / confirm / reject ──────────────────────────

    public async Task<PushOpResult<CreatePairingResponse>> CreateAsync(NotificationScope scope, PushCaller caller)
    {
        if (!_settings.IsConfigured)
            return PushOpResult<CreatePairingResponse>.Fail(PushOpError.NotConfigured, "The push channel is not configured on the platform.");
        if (!await _eligibility.HasPushChannelAsync(scope).ConfigureAwait(false))
            return PushOpResult<CreatePairingResponse>.Fail(PushOpError.ChannelRequired, "Add and enable a Push notification channel first.");
        if (!await _eligibility.IsEligibleAsync(scope, caller.Upn, caller.ObjectId, caller.HomeTenantId).ConfigureAwait(false))
            return PushOpResult<CreatePairingResponse>.Fail(PushOpError.NotEligible, "Pairing needs an Admin or Operator role held in the member list of this tenant.");

        var now = DateTime.UtcNow;
        var code = GenerateCode();
        var grant = new PushPairingGrant
        {
            CodeHash = HashCode(code),
            TenantId = scope.Key,
            OwnerUpn = caller.Upn.ToLowerInvariant(),
            OwnerObjectId = caller.ObjectId.ToLowerInvariant(),
            OwnerHomeTenantId = caller.HomeTenantId.ToLowerInvariant(),
            Status = Constants.Push.PairingStatus.Pending,
            CreatedUtc = now,
            ExpiresUtc = now.AddMinutes(Constants.Push.PairingGrantMinutes),
        };
        await _repo.AddGrantAsync(grant).ConfigureAwait(false);

        // Pairing is a portal action from a signed-in session: it counts as a sign-in for freshness.
        await _repo.StampOwnerSignInAsync(grant.OwnerHomeTenantId, grant.OwnerObjectId, grant.OwnerUpn, now).ConfigureAwait(false);
        Track("created", scope);

        return PushOpResult<CreatePairingResponse>.Success(new CreatePairingResponse
        {
            PairingId = grant.CodeHash,
            Code = code,
            Url = Constants.PortalPushPairUrl(code),
            ExpiresUtc = grant.ExpiresUtc,
        });
    }

    public async Task<PushOpResult<PairingStatusResponse>> GetStatusAsync(NotificationScope scope, string pairingId, PushCaller caller)
    {
        var grant = await GetOwnedGrantAsync(scope, pairingId, caller).ConfigureAwait(false);
        if (grant == null)
            return PushOpResult<PairingStatusResponse>.Fail(PushOpError.NotFound);

        var now = DateTime.UtcNow;
        var status = grant.Status;
        if ((status == Constants.Push.PairingStatus.Pending || status == Constants.Push.PairingStatus.Redeemed) && grant.ExpiresUtc < now)
            status = Constants.Push.PairingStatus.Expired;

        PairingDeviceDto? dto = null;
        if (grant.DeviceId != null && status != Constants.Push.PairingStatus.Expired)
        {
            var device = await _repo.GetDeviceAsync(scope.Key, grant.DeviceId).ConfigureAwait(false);
            if (device != null)
                dto = new PairingDeviceDto { DeviceId = device.DeviceId, Label = device.Label, Platform = device.Platform, RedeemedUtc = grant.RedeemedUtc ?? device.PairedUtc };
        }

        return PushOpResult<PairingStatusResponse>.Success(new PairingStatusResponse { Status = status, Device = dto });
    }

    public async Task<PushOpResult<ConfirmPairingResponse>> ConfirmAsync(NotificationScope scope, string pairingId, PushCaller caller)
    {
        var grant = await GetOwnedGrantAsync(scope, pairingId, caller).ConfigureAwait(false);
        if (grant == null || grant.DeviceId == null)
            return PushOpResult<ConfirmPairingResponse>.Fail(PushOpError.NotFound);

        var now = DateTime.UtcNow;
        if (grant.Status != Constants.Push.PairingStatus.Redeemed || grant.ExpiresUtc < now)
            return PushOpResult<ConfirmPairingResponse>.Fail(PushOpError.Conflict, "This pairing is not waiting for confirmation (expired, rejected or already confirmed).");

        var device = await _repo.GetDeviceAsync(scope.Key, grant.DeviceId).ConfigureAwait(false);
        if (device == null || device.Status != Constants.Push.DeviceStatus.Pending)
            return PushOpResult<ConfirmPairingResponse>.Fail(PushOpError.NotFound);

        grant.Status = Constants.Push.PairingStatus.Confirmed;
        if (!await _repo.TryUpdateGrantAsync(grant).ConfigureAwait(false))
            return PushOpResult<ConfirmPairingResponse>.Fail(PushOpError.Conflict, "The pairing changed concurrently — reload and try again.");

        await _repo.MutateDeviceAsync(scope.Key, device.DeviceId, _ => new Dictionary<string, object?>
        {
            ["Status"] = Constants.Push.DeviceStatus.Active,
            ["StatusReason"] = null,
            ["ConfirmedUtc"] = now,
            ["StatusChangedUtc"] = now,
        }).ConfigureAwait(false);
        device.Status = Constants.Push.DeviceStatus.Active;

        var paired = PushAlertProjector.SystemMessage("push_paired", "Device paired",
            $"This device now receives Autopilot Monitor alerts for {await ScopeNameAsync(scope).ConfigureAwait(false)}.",
            "success", scope, now);
        await _delivery.SendSystemAsync(device, paired).ConfigureAwait(false);

        await AuditAsync(scope, "CREATE", device, caller).ConfigureAwait(false);
        Track("confirmed", scope, device.Platform);
        return PushOpResult<ConfirmPairingResponse>.Success(new ConfirmPairingResponse { DeviceId = device.DeviceId });
    }

    public async Task<PushOpError> RejectAsync(NotificationScope scope, string pairingId, PushCaller caller)
    {
        var grant = await GetOwnedGrantAsync(scope, pairingId, caller).ConfigureAwait(false);
        if (grant == null)
            return PushOpError.NotFound;

        if (grant.Status == Constants.Push.PairingStatus.Pending || grant.Status == Constants.Push.PairingStatus.Redeemed)
        {
            grant.Status = Constants.Push.PairingStatus.Rejected;
            await _repo.TryUpdateGrantAsync(grant).ConfigureAwait(false);
        }
        if (grant.DeviceId != null)
        {
            var device = await _repo.GetDeviceAsync(scope.Key, grant.DeviceId).ConfigureAwait(false);
            if (device != null && device.Status == Constants.Push.DeviceStatus.Pending)
                await _repo.DeleteDeviceAsync(scope.Key, device.DeviceId).ConfigureAwait(false);
        }
        Track("rejected", scope);
        return PushOpError.None;
    }

    // ── Receiver side: begin / redeem / device token routes ─────────────────

    public async Task<PushOpResult<BeginPairResponse>> BeginAsync(string? rawCode)
    {
        if (!_settings.IsConfigured)
            return PushOpResult<BeginPairResponse>.Fail(PushOpError.NotConfigured);

        var grant = await LookupGrantForRedeemAsync(rawCode).ConfigureAwait(false);
        if (grant == null)
            return PushOpResult<BeginPairResponse>.Fail(PushOpError.CodeInvalid);

        var key = _settings.Keys!.Active;
        return PushOpResult<BeginPairResponse>.Success(new BeginPairResponse { Kid = key.Kid, VapidPublicKey = key.PublicKeyBase64Url });
    }

    public async Task<PushOpResult<RedeemPairResponse>> RedeemAsync(RedeemPairRequest request)
    {
        if (!_settings.IsConfigured)
            return PushOpResult<RedeemPairResponse>.Fail(PushOpError.NotConfigured);

        var grant = await LookupGrantForRedeemAsync(request.Code).ConfigureAwait(false);
        if (grant == null)
            return PushOpResult<RedeemPairResponse>.Fail(PushOpError.CodeInvalid);

        var key = _settings.Keys!.Active;
        if (!string.Equals(request.Kid, key.Kid, StringComparison.Ordinal))
            return PushOpResult<RedeemPairResponse>.Fail(PushOpError.InvalidSubscription, "The server key changed — start the pairing again.");

        var scope = grant.TenantId == Constants.Push.PlatformScope ? NotificationScope.Platform : NotificationScope.Tenant(grant.TenantId);
        var subscriptionError = await ValidateSubscriptionAsync(request.Endpoint, request.P256dh, request.Auth, scope).ConfigureAwait(false);
        if (subscriptionError != null)
            return PushOpResult<RedeemPairResponse>.Fail(PushOpError.InvalidSubscription, subscriptionError);

        var now = DateTime.UtcNow;
        var secret = RandomNumberGenerator.GetBytes(DeviceSecretBytes);
        var secretText = System.Buffers.Text.Base64Url.EncodeToString(secret);
        var device = new PushDevice
        {
            Scope = scope.Key,
            DeviceId = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(12)),
            OwnerUpn = grant.OwnerUpn,
            OwnerObjectId = grant.OwnerObjectId,
            OwnerHomeTenantId = grant.OwnerHomeTenantId,
            Kind = "webpush",
            Endpoint = request.Endpoint.Trim(),
            P256dh = request.P256dh.Trim(),
            Auth = request.Auth.Trim(),
            DeviceSecretHash = HashSecret(secret),
            VapidKid = key.Kid,
            Label = CleanLabel(request.Label, request.Platform),
            Platform = NormalizePlatform(request.Platform),
            AppVersion = PushAlertProjector.Clean(request.AppVersion, Constants.Push.MaxAppVersionLength),
            Status = Constants.Push.DeviceStatus.Pending,
            PairedUtc = now,
        };

        // Device first, grant second (one-shot CAS): an orphaned Pending device is harmless and
        // swept, a Redeemed grant without a device would strand the owner's confirm.
        await _repo.AddDeviceAsync(device).ConfigureAwait(false);
        grant.Status = Constants.Push.PairingStatus.Redeemed;
        grant.RedeemedUtc = now;
        grant.DeviceId = device.DeviceId;
        if (!await _repo.TryUpdateGrantAsync(grant).ConfigureAwait(false))
        {
            await _repo.DeleteDeviceAsync(device.Scope, device.DeviceId).ConfigureAwait(false);
            return PushOpResult<RedeemPairResponse>.Fail(PushOpError.CodeUsed);
        }

        Track("redeemed", scope, device.Platform);
        return PushOpResult<RedeemPairResponse>.Success(new RedeemPairResponse
        {
            DeviceToken = $"{device.Scope}.{device.DeviceId}.{secretText}",
            DeviceId = device.DeviceId,
            Status = Constants.Push.DeviceStatus.Pending,
            PollSeconds = PollSeconds,
        });
    }

    /// <summary>The device a token names, or null — the token's secret must hash to the row's hash (constant time).</summary>
    public async Task<PushDevice?> ResolveDeviceAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256)
            return null;
        var parts = token.Split('.', 3);
        if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty))
            return null;
        if (parts[0] != Constants.Push.PlatformScope && !Guid.TryParse(parts[0], out _))
            return null;

        var device = await _repo.GetDeviceAsync(parts[0].ToLowerInvariant(), parts[1]).ConfigureAwait(false);
        if (device == null)
            return null;

        byte[] secret;
        try
        {
            secret = System.Buffers.Text.Base64Url.DecodeFromChars(parts[2]);
        }
        catch (FormatException)
        {
            return null;
        }

        var expected = Encoding.ASCII.GetBytes(device.DeviceSecretHash);
        var actual = Encoding.ASCII.GetBytes(HashSecret(secret));
        return CryptographicOperations.FixedTimeEquals(expected, actual) ? device : null;
    }

    public async Task<PushDeviceStatusResponse> GetDeviceStatusAsync(PushDevice device)
    {
        // A GET that stamps: "opened at" is the health signal the device list shows.
        try
        {
            await _repo.MutateDeviceAsync(device.Scope, device.DeviceId, _ => new Dictionary<string, object?> { ["LastOpenedUtc"] = DateTime.UtcNow }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LastOpenedUtc stamp failed for push device {DeviceId}", device.DeviceId);
        }

        var scope = device.Scope == Constants.Push.PlatformScope ? NotificationScope.Platform : NotificationScope.Tenant(device.Scope);
        return new PushDeviceStatusResponse
        {
            DeviceId = device.DeviceId,
            Status = device.Status,
            Label = device.Label,
            Platform = device.Platform,
            Kid = device.VapidKid,
            PairedUtc = device.PairedUtc,
            ConfirmedUtc = device.ConfirmedUtc,
            LastDeliveredUtc = device.LastDeliveredUtc,
            OwnerUpn = device.OwnerUpn,
            ScopeName = await ScopeNameAsync(scope).ConfigureAwait(false),
        };
    }

    public async Task<PushOpResult<PushDeviceStatusResponse>> ResubscribeAsync(PushDevice device, ResubscribeRequest request)
    {
        if (!_settings.IsConfigured)
            return PushOpResult<PushDeviceStatusResponse>.Fail(PushOpError.NotConfigured);

        var key = _settings.Keys!.Resolve(request.Kid);
        if (key == null)
            return PushOpResult<PushDeviceStatusResponse>.Fail(PushOpError.InvalidSubscription, "Unknown server key — re-pair this device.");

        var scope = device.Scope == Constants.Push.PlatformScope ? NotificationScope.Platform : NotificationScope.Tenant(device.Scope);
        var error = await ValidateSubscriptionAsync(request.Endpoint, request.P256dh, request.Auth, scope).ConfigureAwait(false);
        if (error != null)
            return PushOpResult<PushDeviceStatusResponse>.Fail(PushOpError.InvalidSubscription, error);

        var now = DateTime.UtcNow;
        await _repo.MutateDeviceAsync(device.Scope, device.DeviceId, current =>
        {
            var patch = new Dictionary<string, object?>
            {
                ["Endpoint"] = request.Endpoint.Trim(),
                ["P256dh"] = request.P256dh.Trim(),
                ["Auth"] = request.Auth.Trim(),
                ["VapidKid"] = key.Kid,
                ["ConsecutiveFailures"] = 0,
            };
            // A Stale row (endpoint gone, key mismatch) and a row paused for transport failures are
            // re-armed by a fresh subscription; Pending and an owner-inactivity pause keep their state
            // (the owner's sign-in lifts that one).
            if (current.Status == Constants.Push.DeviceStatus.Stale
                || (current.Status == Constants.Push.DeviceStatus.Paused && current.StatusReason == "delivery_failures"))
            {
                patch["Status"] = Constants.Push.DeviceStatus.Active;
                patch["StatusReason"] = null;
                patch["StatusChangedUtc"] = now;
            }
            return patch;
        }).ConfigureAwait(false);

        var refreshed = await _repo.GetDeviceAsync(device.Scope, device.DeviceId).ConfigureAwait(false) ?? device;
        return PushOpResult<PushDeviceStatusResponse>.Success(await GetDeviceStatusAsync(refreshed).ConfigureAwait(false));
    }

    public async Task UnpairAsync(PushDevice device)
    {
        await _repo.DeleteDeviceAsync(device.Scope, device.DeviceId).ConfigureAwait(false);
        Track("revoked", device.Scope == Constants.Push.PlatformScope ? NotificationScope.Platform : NotificationScope.Tenant(device.Scope), device.Platform, "unpaired");
    }

    // ── Device management (PC side) ─────────────────────────────────────────

    public async Task<PushDeviceListResponse> ListAsync(NotificationScope scope, PushCaller caller)
    {
        var devices = await _repo.GetDevicesAsync(scope.Key).ConfigureAwait(false);
        var items = devices
            .Where(d => d.Status != Constants.Push.DeviceStatus.Pending)
            .Where(d => caller.IsScopeAdmin || IsOwn(d, caller))
            .OrderByDescending(d => d.PairedUtc)
            .Select(d => new PushDeviceDto
            {
                DeviceId = d.DeviceId,
                Label = d.Label,
                Platform = d.Platform,
                Status = d.Status,
                Kid = d.VapidKid,
                PairedUtc = d.PairedUtc,
                ConfirmedUtc = d.ConfirmedUtc,
                LastDeliveredUtc = d.LastDeliveredUtc,
                LastOpenedUtc = d.LastOpenedUtc,
                OwnerUpn = d.OwnerUpn,
                IsOwn = IsOwn(d, caller),
            })
            .ToList();
        return new PushDeviceListResponse { Devices = items };
    }

    public async Task<PushOpError> DeleteAsync(NotificationScope scope, string deviceId, PushCaller caller)
    {
        var device = await _repo.GetDeviceAsync(scope.Key, deviceId).ConfigureAwait(false);
        if (device == null || !(caller.IsScopeAdmin || IsOwn(device, caller)))
            return PushOpError.NotFound;

        await _delivery.RevokeDeviceAsync(device, IsOwn(device, caller) ? "unpaired" : "removed_by_admin").ConfigureAwait(false);
        await AuditAsync(scope, "DELETE", device, caller).ConfigureAwait(false);
        return PushOpError.None;
    }

    public async Task<PushOpResult<TestWebhookNotificationResponse>> TestAsync(NotificationScope scope, string deviceId, PushCaller caller)
    {
        var device = await _repo.GetDeviceAsync(scope.Key, deviceId).ConfigureAwait(false);
        if (device == null || !(caller.IsScopeAdmin || IsOwn(device, caller)))
            return PushOpResult<TestWebhookNotificationResponse>.Fail(PushOpError.NotFound);
        if (device.Status != Constants.Push.DeviceStatus.Active)
            return PushOpResult<TestWebhookNotificationResponse>.Success(new TestWebhookNotificationResponse { Success = false, Message = $"The device is {device.Status.ToLowerInvariant()} — only active devices receive pushes." });

        var payload = PushAlertProjector.SystemMessage("push_test", "Test notification",
            "This is a test notification from Autopilot Monitor.", "info", scope, DateTime.UtcNow, Constants.PortalBaseUrl);
        var outcome = await _delivery.SendSystemAsync(device, payload).ConfigureAwait(false);
        return PushOpResult<TestWebhookNotificationResponse>.Success(new TestWebhookNotificationResponse
        {
            Success = outcome == PushOutcome.Delivered,
            Message = outcome switch
            {
                PushOutcome.Delivered => "Test notification sent.",
                PushOutcome.Gone => "The push service no longer knows this device — open the receiver app on it to re-subscribe.",
                PushOutcome.RetryLater => "The push service is throttling — try again in a minute.",
                PushOutcome.ConfigError => "The push service refused the platform's key — check the VAPID configuration.",
                _ => "The push service did not accept the message.",
            },
        });
    }

    public async Task<string> ScopeNameAsync(NotificationScope scope)
    {
        if (scope.IsPlatform)
            return "Platform operator";
        var (config, exists) = await _tenantConfig.TryGetConfigurationAsync(scope.Key).ConfigureAwait(false);
        if (!exists)
            return scope.Key;
        if (!string.IsNullOrWhiteSpace(config.CompanyName)) return config.CompanyName;
        if (!string.IsNullOrWhiteSpace(config.DomainName)) return config.DomainName;
        return scope.Key;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static bool IsOwn(PushDevice device, PushCaller caller)
        => string.Equals(device.OwnerObjectId, caller.ObjectId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(device.OwnerHomeTenantId, caller.HomeTenantId, StringComparison.OrdinalIgnoreCase);

    private async Task<PushPairingGrant?> GetOwnedGrantAsync(NotificationScope scope, string pairingId, PushCaller caller)
    {
        if (string.IsNullOrWhiteSpace(pairingId) || pairingId.Length > 64)
            return null;
        // Same ownership rule as IsOwn for devices: the person (oid) in their home tenant, within the scope.
        var grant = await _repo.GetGrantAsync(pairingId).ConfigureAwait(false);
        if (grant == null || grant.TenantId != scope.Key
            || !string.Equals(grant.OwnerObjectId, caller.ObjectId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(grant.OwnerHomeTenantId, caller.HomeTenantId, StringComparison.OrdinalIgnoreCase))
            return null;
        return grant;
    }

    /// <summary>
    /// Unknown, expired, used and burnt codes all come back as null — one answer for the anonymous
    /// caller (K9). A wrong-state hit counts as a failed attempt on the row; five burn the code.
    /// </summary>
    private async Task<PushPairingGrant?> LookupGrantForRedeemAsync(string? rawCode)
    {
        var code = NormalizeCode(rawCode);
        if (code == null)
            return null;

        var grant = await _repo.GetGrantAsync(HashCode(code)).ConfigureAwait(false);
        if (grant == null)
            return null;

        var usable = grant.Status == Constants.Push.PairingStatus.Pending && grant.ExpiresUtc >= DateTime.UtcNow;
        if (usable)
            return grant;

        grant.FailedAttempts++;
        if (grant.FailedAttempts >= Constants.Push.MaxRedeemFailures)
            await _repo.DeleteGrantAsync(grant.CodeHash).ConfigureAwait(false);
        else
            await _repo.TryUpdateGrantAsync(grant).ConfigureAwait(false);
        return null;
    }

    private async Task<string?> ValidateSubscriptionAsync(string? endpoint, string? p256dh, string? auth, NotificationScope scope)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri))
            return "The subscription endpoint is not a valid URL.";

        var policyError = PushEndpointPolicy.Validate(uri);
        if (policyError != null)
        {
            // Hostname only — the endpoint is a credential; a new browser push service shows up here.
            // Once per host, scope and hour per instance: the caller chooses the host, and every ops
            // event fans out to the operator channels (D-185).
            var dedupeKey = $"push-endpoint-refused:{scope.Key}:{uri.Host.ToLowerInvariant()}";
            if (!_cache.TryGetValue(dedupeKey, out _))
            {
                _cache.Set(dedupeKey, true, TimeSpan.FromHours(1));
                try
                {
                    await _opsEvents.RecordPushEndpointRefusedAsync(uri.Host, scope.IsPlatform ? "platform" : "tenant").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Recording PushEndpointRefused ops event failed");
                }
            }
            return policyError;
        }

        return WebPushSubscription.TryParse(uri.ToString(), p256dh ?? string.Empty, auth ?? string.Empty, out _, out var parseError)
            ? null
            : parseError ?? "The subscription keys have the wrong shape.";
    }

    private Task AuditAsync(NotificationScope scope, string action, PushDevice device, PushCaller caller)
    {
        if (scope.IsPlatform)
            return Task.CompletedTask;
        return _maintenanceRepo.LogAuditEntryAsync(scope.Key, action, "PushDevice", device.DeviceId, caller.Upn,
            new Dictionary<string, string> { ["platform"] = device.Platform, ["label"] = device.Label, ["owner"] = device.OwnerUpn });
    }

    private void Track(string action, NotificationScope scope, string? platform = null, string? reason = null)
    {
        try
        {
            _telemetry?.TrackEvent("PushPairing", new Dictionary<string, string>
            {
                ["action"] = action,
                ["scope"] = scope.IsPlatform ? "platform" : "tenant",
                ["platform"] = platform ?? string.Empty,
                ["reason"] = reason ?? string.Empty,
            });
        }
        catch
        {
            // Telemetry never fails a pairing.
        }
    }

    // ── Codes ───────────────────────────────────────────────────────────────

    /// <summary>11 characters drawn uniformly from the Crockford alphabet (55 bits).</summary>
    public static string GenerateCode()
        => RandomNumberGenerator.GetString(Constants.Push.CodeAlphabet, Constants.Push.CodeLength);

    /// <summary>
    /// What a typed code becomes before hashing: upper-case, spaces and hyphens dropped, the
    /// Crockford look-alikes folded (O→0, I/L→1). Null when the result is not exactly a code.
    /// </summary>
    public static string? NormalizeCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 64)
            return null;

        var sb = new StringBuilder(Constants.Push.CodeLength);
        foreach (var ch in raw)
        {
            if (ch == ' ' || ch == '-')
                continue;
            var c = char.ToUpperInvariant(ch);
            c = c switch { 'O' => '0', 'I' => '1', 'L' => '1', _ => c };
            if (Constants.Push.CodeAlphabet.IndexOf(c) < 0)
                return null;
            sb.Append(c);
            if (sb.Length > Constants.Push.CodeLength)
                return null;
        }
        return sb.Length == Constants.Push.CodeLength ? sb.ToString() : null;
    }

    public static string HashCode(string code)
        => System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(code)));

    private static string HashSecret(byte[] secret)
        => System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(secret));

    private static string NormalizePlatform(string? platform)
    {
        var p = (platform ?? string.Empty).Trim().ToLowerInvariant();
        return Constants.Push.Platforms.Contains(p) ? p : "other";
    }

    private static string CleanLabel(string? label, string? platform)
    {
        var clean = PushAlertProjector.Clean(label, Constants.Push.MaxLabelLength);
        if (clean.Length > 0)
            return clean;
        return NormalizePlatform(platform) switch
        {
            "ios-homescreen" => "iPhone",
            "android-chrome" => "Android phone",
            "windows-chromium" => "Windows browser",
            "macos-safari" => "Mac",
            "firefox" => "Firefox",
            _ => "Device",
        };
    }
}

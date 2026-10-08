using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    // Web Push pairing + devices (Functions/Push). Declaration order == wire order.

    /// <summary>POST push/pairings · POST global/push/pairings — a fresh pairing code for the caller's own person.</summary>
    public class CreatePairingResponse : IApiResponse
    {
        /// <summary>Handle for the PC-side status poll and the confirm/reject calls.</summary>
        public string PairingId { get; set; } = default!;
        /// <summary>The 11-character code to type into the receiver app.</summary>
        public string Code { get; set; } = default!;
        /// <summary>The same code as the pairing link (QR payload): …/push/pair/#p=&lt;code&gt;.</summary>
        public string Url { get; set; } = default!;
        public DateTime ExpiresUtc { get; set; }
    }

    /// <summary>GET push/pairings/{pairingId} — what the receiver has done with the code so far.</summary>
    public class PairingStatusResponse : IApiResponse
    {
        /// <summary>Pending · Redeemed (a device waits for confirmation) · Confirmed · Rejected · Expired.</summary>
        public string Status { get; set; } = default!;
        /// <summary>The redeeming device, present from Redeemed on.</summary>
        public PairingDeviceDto? Device { get; set; }
    }

    public class PairingDeviceDto
    {
        public string DeviceId { get; set; } = default!;
        public string Label { get; set; } = default!;
        public string Platform { get; set; } = default!;
        public DateTime RedeemedUtc { get; set; }
    }

    /// <summary>POST push/pairings/{pairingId}/confirm — the device is Active from now on.</summary>
    public class ConfirmPairingResponse : IApiResponse
    {
        public string DeviceId { get; set; } = default!;
    }

    /// <summary>POST push/pair/begin — the receiver validates its code before asking for notification permission.</summary>
    public class BeginPairRequest : IApiRequest
    {
        public string Code { get; set; } = default!;
    }

    /// <summary>The VAPID application server key the receiver must subscribe with, and its identity.</summary>
    public class BeginPairResponse : IApiResponse
    {
        public string Kid { get; set; } = default!;
        /// <summary>Uncompressed P-256 point, base64url — pushManager.subscribe({applicationServerKey}).</summary>
        public string VapidPublicKey { get; set; } = default!;
    }

    /// <summary>POST push/pair — the receiver hands over its subscription; the device is created Pending.</summary>
    public class RedeemPairRequest : IApiRequest
    {
        public string Code { get; set; } = default!;
        public string Kid { get; set; } = default!;
        public string Endpoint { get; set; } = default!;
        public string P256dh { get; set; } = default!;
        public string Auth { get; set; } = default!;
        public string? Label { get; set; }
        public string? Platform { get; set; }
        public string? AppVersion { get; set; }
    }

    public class RedeemPairResponse : IApiResponse
    {
        /// <summary>"{scope}.{deviceId}.{secret}" — the receiver's only credential; header X-Push-Device-Token.</summary>
        public string DeviceToken { get; set; } = default!;
        public string DeviceId { get; set; } = default!;
        /// <summary>Always Pending here; the PC confirms.</summary>
        public string Status { get; set; } = default!;
        /// <summary>Suggested interval for the receiver's status poll while Pending.</summary>
        public int PollSeconds { get; set; }
    }

    /// <summary>GET/PUT push/device — the receiver's own view of its registration.</summary>
    public class PushDeviceStatusResponse : IApiResponse
    {
        public string DeviceId { get; set; } = default!;
        /// <summary>Pending · Active · Paused · Stale (a revoked device is gone: the token answers 401).</summary>
        public string Status { get; set; } = default!;
        public string Label { get; set; } = default!;
        public string Platform { get; set; } = default!;
        public string Kid { get; set; } = default!;
        public DateTime PairedUtc { get; set; }
        public DateTime? ConfirmedUtc { get; set; }
        public DateTime? LastDeliveredUtc { get; set; }
        /// <summary>The owner's UPN — this is the owner's own device.</summary>
        public string OwnerUpn { get; set; } = default!;
        /// <summary>Tenant display name, or "Platform operator" for the platform scope.</summary>
        public string ScopeName { get; set; } = default!;
    }

    /// <summary>PUT push/device — a changed or re-created subscription (same device, same owner).</summary>
    public class ResubscribeRequest : IApiRequest
    {
        public string Endpoint { get; set; } = default!;
        public string P256dh { get; set; } = default!;
        public string Auth { get; set; } = default!;
        public string Kid { get; set; } = default!;
    }

    /// <summary>GET push/devices · GET global/push/devices — own devices (everyone) plus every device of the scope (admins).</summary>
    public class PushDeviceListResponse : IApiResponse
    {
        public IReadOnlyList<PushDeviceDto> Devices { get; set; } = default!;
    }

    public class PushDeviceDto
    {
        public string DeviceId { get; set; } = default!;
        public string Label { get; set; } = default!;
        public string Platform { get; set; } = default!;
        public string Status { get; set; } = default!;
        public string Kid { get; set; } = default!;
        public DateTime PairedUtc { get; set; }
        public DateTime? ConfirmedUtc { get; set; }
        public DateTime? LastDeliveredUtc { get; set; }
        public DateTime? LastOpenedUtc { get; set; }
        public string OwnerUpn { get; set; } = default!;
        /// <summary>True for the caller's own devices.</summary>
        public bool IsOwn { get; set; }
    }

    /// <summary>PUT sessions/{sessionId}/watch — "notify my devices when this session ends".</summary>
    public class SessionWatchResponse : IApiResponse
    {
        public bool Watching { get; set; }
        public DateTime? ExpiresUtc { get; set; }
    }
}

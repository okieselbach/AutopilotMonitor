using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for the Web Push channel: paired devices, pairing grants, owner sign-in stamps
    /// and session watches (tables <c>PushDevices</c>, <c>PushPairingGrants</c>, <c>PushOwners</c>,
    /// <c>PushSessionWatches</c>). Every row is a credential or binds one; the implementation
    /// propagates storage exceptions — callers decide what is fail-soft.
    /// </summary>
    public interface IPushDeviceRepository
    {
        // --- Devices (PK = scope, RK = deviceId) ---
        Task<PushDevice?> GetDeviceAsync(string scope, string deviceId);
        Task<List<PushDevice>> GetDevicesAsync(string scope);
        /// <summary>Insert only (409 on an existing key surfaces as an exception).</summary>
        Task AddDeviceAsync(PushDevice device);
        /// <summary>Compare-and-swap merge: <paramref name="patch"/> sees the current row and returns the changed properties, or null for "nothing to do". Returns true when a write landed.</summary>
        Task<bool> MutateDeviceAsync(string scope, string deviceId, Func<PushDevice, IReadOnlyDictionary<string, object?>?> patch);
        Task DeleteDeviceAsync(string scope, string deviceId);

        // --- Pairing grants (PK = "grant", RK = SHA-256(code), TenantId property = scope) ---
        Task<PushPairingGrant?> GetGrantAsync(string codeHash);
        Task AddGrantAsync(PushPairingGrant grant);
        /// <summary>
        /// Conditional update on the ETag read with the grant: the one-shot redeem and the
        /// confirm/reject transitions. Returns false when the row changed underneath (the loser).
        /// </summary>
        Task<bool> TryUpdateGrantAsync(PushPairingGrant grant);
        Task DeleteGrantAsync(string codeHash);
        /// <summary>Grants whose ExpiresUtc lies before <paramref name="cutoffUtc"/> (maintenance sweep).</summary>
        Task<List<PushPairingGrant>> GetExpiredGrantsAsync(DateTime cutoffUtc);

        // --- Owners (PK = home tenant, RK = oid) ---
        Task<PushOwner?> GetOwnerAsync(string homeTenantId, string objectId);
        /// <summary>Upsert of the sign-in stamp — one write per portal sign-in.</summary>
        Task StampOwnerSignInAsync(string homeTenantId, string objectId, string upn, DateTime signInUtc);

        // --- Session watches (PK = tenantId, RK = "{sessionId}_{oid}") ---
        Task UpsertWatchAsync(PushSessionWatch watch);
        Task DeleteWatchAsync(string tenantId, string sessionId, string objectId);
        Task<PushSessionWatch?> GetWatchAsync(string tenantId, string sessionId, string objectId);
        /// <summary>Every watch on one session (the terminal-status fan-out), newest first.</summary>
        Task<List<PushSessionWatch>> GetWatchesForSessionAsync(string tenantId, string sessionId);

        // --- Maintenance (cross-partition scans; never on a hot path) ---
        /// <summary>Every device row of every scope — the pause/stale/expiry sweep.</summary>
        Task<List<PushDevice>> GetAllDevicesAsync();
        Task<List<PushSessionWatch>> GetExpiredWatchesAsync(DateTime cutoffUtc);
    }

    /// <summary>One paired receiver. Endpoint, P256dh, Auth and DeviceSecretHash are credentials.</summary>
    public class PushDevice
    {
        /// <summary>"platform" or the tenant id (lowercase).</summary>
        public string Scope { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string OwnerUpn { get; set; } = string.Empty;
        public string OwnerObjectId { get; set; } = string.Empty;
        /// <summary>The owner's HOME tenant (JWT tid at pairing) — where the PushOwners stamp lives.</summary>
        public string OwnerHomeTenantId { get; set; } = string.Empty;
        /// <summary>"webpush" today; "apns" once the native app exists.</summary>
        public string Kind { get; set; } = "webpush";
        public string Endpoint { get; set; } = string.Empty;
        public string P256dh { get; set; } = string.Empty;
        public string Auth { get; set; } = string.Empty;
        /// <summary>Base64url(SHA-256(device secret)); the secret itself is never stored.</summary>
        public string DeviceSecretHash { get; set; } = string.Empty;
        public string VapidKid { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Platform { get; set; } = "other";
        public string AppVersion { get; set; } = string.Empty;
        /// <summary>Constants.Push.DeviceStatus (Pending · Active · Paused · Stale). Revoked devices are deleted, never kept.</summary>
        public string Status { get; set; } = string.Empty;
        /// <summary>Why the device is Stale (gone · vapid_key_mismatch · invalid_subscription) or Paused (owner_inactive · delivery_failures).</summary>
        public string? StatusReason { get; set; }
        public DateTime PairedUtc { get; set; }
        public DateTime? ConfirmedUtc { get; set; }
        public DateTime? LastDeliveredUtc { get; set; }
        public DateTime? LastOpenedUtc { get; set; }
        public DateTime? StatusChangedUtc { get; set; }
        public int? LastStatusCode { get; set; }
        public int ConsecutiveFailures { get; set; }
        /// <summary>Flood window (Korrektur K19): count of pushes since WindowStartUtc; SuppressedCount = dropped in that window.</summary>
        public DateTime? WindowStartUtc { get; set; }
        public int WindowCount { get; set; }
        public int SuppressedCount { get; set; }
    }

    /// <summary>A ten-minute one-shot pairing code, stored only as its hash.</summary>
    public class PushPairingGrant
    {
        /// <summary>Base64url(SHA-256(code)) — the RowKey and the pairing id the PC polls with.</summary>
        public string CodeHash { get; set; } = string.Empty;
        /// <summary>Scope the device will belong to ("platform" or tenant id); the offboarding property wipe keys on it.</summary>
        public string TenantId { get; set; } = string.Empty;
        public string OwnerUpn { get; set; } = string.Empty;
        public string OwnerObjectId { get; set; } = string.Empty;
        public string OwnerHomeTenantId { get; set; } = string.Empty;
        /// <summary>Constants.Push.PairingStatus.</summary>
        public string Status { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public DateTime? RedeemedUtc { get; set; }
        /// <summary>The Pending device created on redeem.</summary>
        public string? DeviceId { get; set; }
        public int FailedAttempts { get; set; }
        /// <summary>Storage concurrency token of the row as read; TryUpdateGrantAsync writes with it.</summary>
        public string? ETag { get; set; }
    }

    /// <summary>Last portal sign-in of one person — the push freshness source.</summary>
    public class PushOwner
    {
        public string HomeTenantId { get; set; } = string.Empty;
        public string ObjectId { get; set; } = string.Empty;
        public string Upn { get; set; } = string.Empty;
        public DateTime LastSignInUtc { get; set; }
    }

    /// <summary>"Notify my devices when this session ends" — one row per (session, person), stored in the SESSION's tenant.</summary>
    public class PushSessionWatch
    {
        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string OwnerObjectId { get; set; } = string.Empty;
        public string OwnerUpn { get; set; } = string.Empty;
        /// <summary>
        /// Scope keys (';'-separated) whose devices of the watcher receive the push — the watcher's home
        /// tenant and, for a Global Admin, "platform"; set at watch time from the scopes that held an
        /// eligible, active device. Empty (legacy rows) means the session tenant.
        /// </summary>
        public string OwnerScopes { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// One user delegation key per process instead of one per SAS. Under Managed Identity every
    /// blob SAS needs a delegation key from the service; minting it per download or upload URL
    /// costs a storage round-trip and an AAD token exchange on the hot path (audit 2026-09-05
    /// F11). A key is valid for up to seven days; this cache mints one for
    /// <see cref="KeyLifetime"/>, hands it out while at least <see cref="RefreshMargin"/> of
    /// validity remains and the requested SAS expiry fits inside it, and re-mints under a lock
    /// otherwise. The SAS itself keeps its own short lifetime and blob scope.
    /// </summary>
    public sealed class UserDelegationKeyCache
    {
        public static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(24);
        public static readonly TimeSpan RefreshMargin = TimeSpan.FromHours(2);

        /// <summary>Clock-skew tolerance on the key start, mirrors the SAS <c>StartsOn</c>.</summary>
        private static readonly TimeSpan StartSkew = TimeSpan.FromMinutes(5);

        private readonly BlobServiceClient _client;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private UserDelegationKey? _key;

        public UserDelegationKeyCache(BlobServiceClient client, Func<DateTimeOffset>? utcNow = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Returns a key that covers a SAS expiring at <paramref name="sasExpiresOn"/>, minting a
        /// fresh one when the cached key is missing, close to expiry or too short for the SAS.
        /// </summary>
        public async Task<UserDelegationKey> GetAsync(DateTimeOffset sasExpiresOn, CancellationToken cancellationToken = default)
        {
            var cached = _key;
            if (Covers(cached, sasExpiresOn)) return cached!;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cached = _key;
                if (Covers(cached, sasExpiresOn)) return cached!;

                var now = _utcNow();
                var fresh = await _client.GetUserDelegationKeyAsync(now - StartSkew, now + KeyLifetime, cancellationToken)
                    .ConfigureAwait(false);
                _key = fresh.Value;
                return fresh.Value;
            }
            finally
            {
                _gate.Release();
            }
        }

        private bool Covers(UserDelegationKey? key, DateTimeOffset sasExpiresOn)
        {
            if (key == null) return false;
            var now = _utcNow();
            return key.SignedExpiresOn - now >= RefreshMargin && sasExpiresOn <= key.SignedExpiresOn;
        }
    }
}

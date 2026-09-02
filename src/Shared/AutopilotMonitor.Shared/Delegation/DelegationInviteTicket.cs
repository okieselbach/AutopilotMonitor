using System;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutopilotMonitor.Shared.Delegation
{
    /// <summary>
    /// Stateless codec for the self-service delegation <b>invitation link</b>: a Pro (MSP) tenant admin mints
    /// one, hands the link to a customer, and the customer's tenant admin accepts it in the portal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ticket binds the inviting HOME tenant and the invitation row id into an HMAC-SHA256 signature.
    /// The accept endpoint trusts ONLY the values inside the ticket to locate the invitation row; the row
    /// (status Pending, not expired, one-shot ETag flip to Accepted) is the authority on whether it is still
    /// redeemable, so the ticket alone cannot be replayed, and the ACCEPTING tenant is always the caller's
    /// validated JWT tenant — the ticket never names a target.
    /// </para>
    /// <para>
    /// Mirrors <see cref="Diagnostics.DiagnosticsDownloadTicket"/>: tamper ⇒ HMAC fails; a fixed purpose tag
    /// gives domain separation from every other token signed with the shared key; the TTL
    /// (<see cref="DefaultTtl"/>, 7 days — the row's ExpiresDate is the authoritative expiry) bounds a
    /// leaked link. Reuses the provisioned <c>PaginationTokenSigningKey</c>.
    /// </para>
    /// </remarks>
    public static class DelegationInviteTicket
    {
        /// <summary>Invitation lifetime — long enough to forward a link to a customer, short enough to bound a leak.</summary>
        public static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(7);

        private const string Purpose = "msp-invite-v1";
        private const string SigningKeyEnvVar = "PaginationTokenSigningKey";

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
        };

        private static byte[]? _testOverrideKey;

        /// <summary>Test-only hook to inject a fixed signing key (production reads the env var).</summary>
        internal static void SetSigningKeyForTesting(byte[]? key) => _testOverrideKey = key;

        private static byte[] GetSigningKey()
        {
            var test = _testOverrideKey;
            if (test != null) return test;

            var raw = Environment.GetEnvironmentVariable(SigningKeyEnvVar);
            if (string.IsNullOrEmpty(raw))
                throw new InvalidOperationException(
                    $"{SigningKeyEnvVar} environment variable is required for DelegationInviteTicket HMAC signing. " +
                    "Set it to a base64-encoded random 32-byte key.");
            try
            {
                return Convert.FromBase64String(raw);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException($"{SigningKeyEnvVar} is not valid base64.", ex);
            }
        }

        /// <summary>Encodes a signed ticket binding the inviting home tenant and the invitation row id.</summary>
        public static string Encode(string homeTenantId, string invitationId, DateTimeOffset? issuedAt = null)
        {
            if (string.IsNullOrWhiteSpace(homeTenantId)) throw new ArgumentException("homeTenantId required", nameof(homeTenantId));
            if (string.IsNullOrWhiteSpace(invitationId)) throw new ArgumentException("invitationId required", nameof(invitationId));

            var payload = new TicketPayload
            {
                P = Purpose,
                Hid = homeTenantId.ToLowerInvariant(),
                Iid = invitationId,
                Iat = (issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds(),
            };

            var canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
            payload.Sig = ComputeSignature(canonicalBytes);
            return Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
        }

        /// <summary>
        /// Validates and decodes a ticket. True only when the HMAC verifies, the purpose matches and the ticket
        /// is within its TTL. <paramref name="rejectReason"/> is an observability handle, never shown verbatim.
        /// </summary>
        public static bool TryDecode(
            string raw,
            out string homeTenantId,
            out string invitationId,
            out string? rejectReason,
            DateTimeOffset? now = null,
            TimeSpan? ttl = null)
        {
            homeTenantId = string.Empty;
            invitationId = string.Empty;
            rejectReason = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                rejectReason = "empty";
                return false;
            }

            TicketPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<TicketPayload>(Base64UrlDecode(raw), JsonOpts);
            }
            catch (FormatException)
            {
                rejectReason = "malformed_base64";
                return false;
            }
            catch (JsonException)
            {
                rejectReason = "malformed_json";
                return false;
            }

            if (payload == null) { rejectReason = "null_payload"; return false; }
            if (string.IsNullOrEmpty(payload.Hid)) { rejectReason = "missing_home"; return false; }
            if (string.IsNullOrEmpty(payload.Iid)) { rejectReason = "missing_invitation"; return false; }
            if (string.IsNullOrEmpty(payload.Sig)) { rejectReason = "missing_signature"; return false; }

            var providedSig = payload.Sig!;
            payload.Sig = null;
            byte[] canonicalBytes;
            try
            {
                canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
            }
            catch (JsonException)
            {
                rejectReason = "malformed_json";
                return false;
            }
            if (!FixedTimeEquals(providedSig, ComputeSignature(canonicalBytes)))
            {
                rejectReason = "bad_signature";
                return false;
            }

            if (!FixedTimeEquals(payload.P ?? string.Empty, Purpose))
            {
                rejectReason = "wrong_purpose";
                return false;
            }

            var nowTs = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
            var maxAge = (long)(ttl ?? DefaultTtl).TotalSeconds;
            if (payload.Iat <= 0 || nowTs - payload.Iat > maxAge)
            {
                rejectReason = "expired";
                return false;
            }

            homeTenantId = payload.Hid;
            invitationId = payload.Iid;
            return true;
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            var diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static string ComputeSignature(byte[] payloadBytes)
        {
            using var hmac = new HMACSHA256(GetSigningKey());
            return Convert.ToBase64String(hmac.ComputeHash(payloadBytes));
        }

        private static string Base64UrlEncode(byte[] bytes)
            => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        private static byte[] Base64UrlDecode(string s)
        {
            var padded = s.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
                case 0: break;
                default: throw new FormatException("invalid base64url length");
            }
            return Convert.FromBase64String(padded);
        }

        private sealed class TicketPayload
        {
            [JsonPropertyName("p")] public string P { get; set; } = string.Empty;
            [JsonPropertyName("hid")] public string Hid { get; set; } = string.Empty;
            [JsonPropertyName("iid")] public string Iid { get; set; } = string.Empty;
            [JsonPropertyName("iat")] public long Iat { get; set; }
            [JsonPropertyName("sig")] public string? Sig { get; set; }
        }
    }
}

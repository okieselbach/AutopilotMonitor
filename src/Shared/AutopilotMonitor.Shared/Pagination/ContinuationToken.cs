using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutopilotMonitor.Shared.Pagination
{
    /// <summary>
    /// Stateless codec for paginated-endpoint continuation tokens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each token wraps the underlying Azure-Tables continuation string together
    /// with a SHA-256 fingerprint of the canonical filter args, the tenantId of
    /// the originating request, a UTC issued-at timestamp, and an HMAC-SHA256
    /// signature over the payload. Validation is stateless: the function
    /// endpoint recomputes the expected fingerprint from the current request
    /// and asks <see cref="TryDecode"/> to compare. The HMAC is verified against
    /// the server's signing key.
    /// </para>
    /// <para>
    /// Security properties:
    /// <list type="bullet">
    ///   <item><description>Cross-tenant replay → caller's resolved tenantId
    ///   must match the token's; mismatch is rejected.</description></item>
    ///   <item><description>Tampered filter args → recomputed fingerprint will
    ///   not match the token's, request rejected.</description></item>
    ///   <item><description>Tampered payload (incl. <c>iat</c> bypass) → HMAC
    ///   verification fails, request rejected.</description></item>
    ///   <item><description>24h expiry — tokens older than
    ///   <see cref="DefaultTtl"/> are rejected (HMAC prevents bypass by
    ///   client-side iat tampering).</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Signing key sourced from the <c>PaginationTokenSigningKey</c>
    /// environment variable (base64-encoded random bytes, ≥32 bytes
    /// recommended). Tests may inject a key via
    /// <see cref="SetSigningKeyForTesting"/>. Key rotation invalidates
    /// in-flight tokens; callers receive a clean rejection and restart
    /// pagination from page 1 — acceptable given the 24h TTL.
    /// </para>
    /// </remarks>
    public static class ContinuationToken
    {
        /// <summary>Default 24h token expiry — see plan §"Open questions" 3.</summary>
        public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

        private const string SigningKeyEnvVar = "PaginationTokenSigningKey";

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
        };

        private static byte[]? _testOverrideKey;

        // Execution-context-local, so a test that swaps keys mid-test (key
        // rotation) cannot race concurrently running test classes that sign
        // and verify under the process-wide default.
        private static readonly AsyncLocal<byte[]?> _scopedTestOverrideKey = new AsyncLocal<byte[]?>();

        /// <summary>
        /// Test-only hook to inject a fixed signing key process-wide. Production
        /// code reads the key from the <c>PaginationTokenSigningKey</c> env var.
        /// </summary>
        internal static void SetSigningKeyForTesting(byte[]? key) => _testOverrideKey = key;

        /// <summary>
        /// Test-only hook to inject a signing key for the current async context
        /// only. Use this instead of <see cref="SetSigningKeyForTesting"/> when a
        /// test needs a key that differs from the process-wide test default —
        /// tests run in parallel and a mid-test swap of the static key bleeds
        /// into every other test's Encode/TryDecode.
        /// </summary>
        internal static void SetScopedSigningKeyForTesting(byte[]? key) => _scopedTestOverrideKey.Value = key;

        private static byte[] GetSigningKey()
        {
            var scoped = _scopedTestOverrideKey.Value;
            if (scoped != null) return scoped;

            var test = _testOverrideKey;
            if (test != null) return test;

            var raw = Environment.GetEnvironmentVariable(SigningKeyEnvVar);
            if (string.IsNullOrEmpty(raw))
                throw new InvalidOperationException(
                    $"{SigningKeyEnvVar} environment variable is required for ContinuationToken HMAC signing. " +
                    "Set it to a base64-encoded random 32-byte key.");
            try
            {
                return Convert.FromBase64String(raw);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"{SigningKeyEnvVar} is not valid base64.", ex);
            }
        }

        /// <summary>
        /// Encodes a continuation token. <paramref name="azureToken"/> is the
        /// raw <c>Azure.Data.Tables</c> page continuation; the remaining
        /// parameters bind the token to the originating request. The token is
        /// HMAC-SHA256 signed with the server's signing key — any modification
        /// (including <c>iat</c> tampering) will fail decode-side verification.
        /// </summary>
        public static string Encode(
            string azureToken,
            string tenantId,
            string filterFingerprint,
            DateTimeOffset? issuedAt = null)
        {
            if (azureToken == null) throw new ArgumentNullException(nameof(azureToken));
            if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("tenantId required", nameof(tenantId));
            if (string.IsNullOrWhiteSpace(filterFingerprint)) throw new ArgumentException("fingerprint required", nameof(filterFingerprint));

            var payload = new TokenPayload
            {
                T = azureToken,
                Tid = tenantId,
                Fp = filterFingerprint,
                Iat = (issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds(),
                // Sig deliberately left null here so the canonical-bytes
                // computation below excludes it (JsonIgnoreCondition.WhenWritingDefault).
            };

            var canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
            payload.Sig = ComputeSignature(canonicalBytes);

            var signedJson = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
            return Base64UrlEncode(signedJson);
        }

        /// <summary>
        /// Validates and decodes a token. Returns <c>true</c> only if the
        /// caller's tenantId, the recomputed fingerprint, and the token age
        /// all match the policy. On failure <paramref name="rejectReason"/>
        /// reports a stable, log-safe code.
        /// </summary>
        public static bool TryDecode(
            string raw,
            string expectedTenantId,
            string expectedFingerprint,
            out string azureToken,
            out string? rejectReason,
            DateTimeOffset? now = null,
            TimeSpan? ttl = null)
        {
            azureToken = string.Empty;
            rejectReason = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                rejectReason = "empty";
                return false;
            }

            TokenPayload? payload;
            try
            {
                var bytes = Base64UrlDecode(raw);
                payload = JsonSerializer.Deserialize<TokenPayload>(bytes, JsonOpts);
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

            if (payload == null)
            {
                rejectReason = "null_payload";
                return false;
            }
            if (string.IsNullOrEmpty(payload.Tid))
            {
                rejectReason = "missing_tenant";
                return false;
            }
            if (string.IsNullOrEmpty(payload.Fp))
            {
                rejectReason = "missing_fingerprint";
                return false;
            }

            if (string.IsNullOrEmpty(payload.Sig))
            {
                rejectReason = "missing_signature";
                return false;
            }

            // Verify HMAC before any other field check: a tampered payload (e.g.
            // iat reset to bypass expiry, fp swapped to match the new request)
            // is rejected here regardless of how plausible the other fields look.
            // Cheap to fail fast with a fixed-time compare.
            var providedSig = payload.Sig!; // null-checked above; suppress nullable warning
            payload.Sig = null; // exclude from canonical bytes (matches Encode)
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
            var expectedSig = ComputeSignature(canonicalBytes);
            if (!FixedTimeEquals(providedSig, expectedSig))
            {
                rejectReason = "bad_signature";
                return false;
            }

            if (!FixedTimeEquals(payload.Tid, expectedTenantId ?? string.Empty))
            {
                rejectReason = "cross_tenant";
                return false;
            }
            if (!FixedTimeEquals(payload.Fp, expectedFingerprint ?? string.Empty))
            {
                rejectReason = "filter_mismatch";
                return false;
            }

            var nowTs = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
            var maxAge = (long)(ttl ?? DefaultTtl).TotalSeconds;
            if (payload.Iat <= 0 || nowTs - payload.Iat > maxAge)
            {
                rejectReason = "expired";
                return false;
            }

            azureToken = payload.T ?? string.Empty;
            return true;
        }

        /// <summary>
        /// Builds the SHA-256 fingerprint over canonicalized filter arguments.
        /// Keys are case-insensitive and ordered; null/empty values are dropped
        /// so callers can pass through optional filter params unconditionally.
        /// </summary>
        public static string ComputeFingerprint(IEnumerable<KeyValuePair<string, string?>> filterArgs)
        {
            if (filterArgs == null) throw new ArgumentNullException(nameof(filterArgs));

            var canonical = new StringBuilder();
            var first = true;
            foreach (var kv in filterArgs
                .Where(kv => !string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                .Select(kv => new KeyValuePair<string, string>(kv.Key.ToLowerInvariant(), kv.Value!))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (!first) canonical.Append('&');
                canonical.Append(kv.Key);
                canonical.Append('=');
                canonical.Append(kv.Value);
                first = false;
            }

            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
            var hex = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
            {
                hex.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return hex.ToString();
        }

        // Constant-time comparison — protects against timing-side-channel snooping
        // on the tenantId / fingerprint / signature checks. Critical for the HMAC
        // signature: a leaky compare would let an attacker forge tokens via
        // byte-by-byte timing oracle.
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
            var hash = hmac.ComputeHash(payloadBytes);
            return Convert.ToBase64String(hash);
        }

        private static string Base64UrlEncode(byte[] bytes)
        {
            var s = Convert.ToBase64String(bytes);
            return s.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

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

        private sealed class TokenPayload
        {
            [JsonPropertyName("t")] public string T { get; set; } = string.Empty;
            [JsonPropertyName("tid")] public string Tid { get; set; } = string.Empty;
            [JsonPropertyName("fp")] public string Fp { get; set; } = string.Empty;
            [JsonPropertyName("iat")] public long Iat { get; set; }

            // HMAC-SHA256(canonical-payload-bytes, key), base64. Excluded from the
            // canonical bytes via JsonIgnoreCondition.WhenWritingDefault when null.
            [JsonPropertyName("sig")] public string? Sig { get; set; }
        }
    }
}

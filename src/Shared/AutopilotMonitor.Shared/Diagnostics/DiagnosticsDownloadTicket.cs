using System;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutopilotMonitor.Shared.Diagnostics
{
    /// <summary>
    /// Stateless codec for short-lived, self-authenticating diagnostics-download tickets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A ticket lets an unauthenticated caller (e.g. an MCP/AI client that holds no JWT in
    /// its shell) download a single diagnostics blob through the backend proxy. The minting
    /// endpoint (<c>POST diagnostics/download-ticket</c>) performs the real authorization
    /// (<c>MemberRead</c> + cross-tenant scoping); the ticket then binds the authorized
    /// <c>tenantId</c> + <c>blobName</c> into an HMAC-SHA256 signature. The download endpoint
    /// (<c>GET diagnostics/download?t=...</c>) trusts ONLY the values inside the ticket — it
    /// never reads tenantId/blobName from the query — so a ticket cannot be retargeted to a
    /// different tenant or blob.
    /// </para>
    /// <para>
    /// Security properties mirror <see cref="Pagination.ContinuationToken"/>:
    /// <list type="bullet">
    ///   <item><description>Tampered tenantId/blobName/iat → recomputed HMAC fails.</description></item>
    ///   <item><description>Short TTL (<see cref="DefaultTtl"/>, 10 min) bounds replay; iat is
    ///   inside the signed payload so it cannot be extended client-side.</description></item>
    ///   <item><description>Domain separation: a fixed purpose tag (<see cref="Purpose"/>) is part
    ///   of the signed payload, so a continuation token signed with the same key can never be
    ///   replayed as a download ticket (different JSON shape + purpose).</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Reuses the already-provisioned <c>PaginationTokenSigningKey</c> env var (no new secret to
    /// deploy). Tests inject a key via <see cref="SetSigningKeyForTesting"/>.
    /// </para>
    /// </remarks>
    public static class DiagnosticsDownloadTicket
    {
        /// <summary>Default 10-minute ticket expiry — long enough to download, short enough to bound replay.</summary>
        public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Domain-separation tag baked into the signed payload — a ticket minted for one download
        /// surface never opens another. Diagnostics packages (default) and session-report ZIPs
        /// share the codec, the signing key and the TTL, but not the purpose.
        /// </summary>
        public const string DiagnosticsPurpose = "diag-dl-v1";
        /// <summary>Purpose of a ticket for the operator <c>session-reports</c> container (report ZIPs, preserved diag copies).</summary>
        public const string SessionReportPurpose = "report-dl-v1";

        private const string SigningKeyEnvVar = "PaginationTokenSigningKey";

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
        };

        private static byte[]? _testOverrideKey;

        /// <summary>
        /// Test-only hook to inject a fixed signing key. Production reads the key from the
        /// <c>PaginationTokenSigningKey</c> env var.
        /// </summary>
        internal static void SetSigningKeyForTesting(byte[]? key) => _testOverrideKey = key;

        private static byte[] GetSigningKey()
        {
            var test = _testOverrideKey;
            if (test != null) return test;

            var raw = Environment.GetEnvironmentVariable(SigningKeyEnvVar);
            if (string.IsNullOrEmpty(raw))
                throw new InvalidOperationException(
                    $"{SigningKeyEnvVar} environment variable is required for DiagnosticsDownloadTicket HMAC signing. " +
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

        /// <summary>
        /// Encodes a signed ticket binding <paramref name="tenantId"/>, <paramref name="blobName"/>,
        /// and <paramref name="destination"/>. Any modification (incl. iat tampering) fails decode.
        /// </summary>
        public static string Encode(
            string tenantId,
            string blobName,
            string destination,
            DateTimeOffset? issuedAt = null,
            string? purpose = null)
        {
            if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("tenantId required", nameof(tenantId));
            if (string.IsNullOrWhiteSpace(blobName)) throw new ArgumentException("blobName required", nameof(blobName));

            var payload = new TicketPayload
            {
                P = purpose ?? DiagnosticsPurpose,
                Tid = tenantId,
                Blob = blobName,
                Dst = destination ?? string.Empty,
                Iat = (issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds(),
                // Sig left null so the canonical-bytes computation excludes it.
            };

            var canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
            payload.Sig = ComputeSignature(canonicalBytes);

            var signedJson = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
            return Base64UrlEncode(signedJson);
        }

        /// <summary>
        /// Validates and decodes a ticket. Returns <c>true</c> only when the HMAC verifies, the
        /// purpose tag matches, and the ticket is not expired. The bound tenantId/blobName/destination
        /// are returned to the caller as the sole authority for the download.
        /// </summary>
        public static bool TryDecode(
            string raw,
            out string tenantId,
            out string blobName,
            out string destination,
            out string? rejectReason,
            DateTimeOffset? now = null,
            TimeSpan? ttl = null,
            string? purpose = null)
        {
            tenantId = string.Empty;
            blobName = string.Empty;
            destination = string.Empty;
            rejectReason = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                rejectReason = "empty";
                return false;
            }

            TicketPayload? payload;
            try
            {
                var bytes = Base64UrlDecode(raw);
                payload = JsonSerializer.Deserialize<TicketPayload>(bytes, JsonOpts);
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
            if (string.IsNullOrEmpty(payload.Tid)) { rejectReason = "missing_tenant"; return false; }
            if (string.IsNullOrEmpty(payload.Blob)) { rejectReason = "missing_blob"; return false; }
            if (string.IsNullOrEmpty(payload.Sig)) { rejectReason = "missing_signature"; return false; }

            // Verify HMAC before trusting any field.
            var providedSig = payload.Sig!;
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

            // Domain separation: reject anything not minted for THIS download surface.
            if (!FixedTimeEquals(payload.P ?? string.Empty, purpose ?? DiagnosticsPurpose))
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

            tenantId = payload.Tid;
            blobName = payload.Blob;
            destination = payload.Dst ?? string.Empty;
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

        private sealed class TicketPayload
        {
            [JsonPropertyName("p")] public string P { get; set; } = string.Empty;
            [JsonPropertyName("tid")] public string Tid { get; set; } = string.Empty;
            [JsonPropertyName("blob")] public string Blob { get; set; } = string.Empty;
            [JsonPropertyName("dst")] public string Dst { get; set; } = string.Empty;
            [JsonPropertyName("iat")] public long Iat { get; set; }
            [JsonPropertyName("sig")] public string? Sig { get; set; }
        }
    }
}

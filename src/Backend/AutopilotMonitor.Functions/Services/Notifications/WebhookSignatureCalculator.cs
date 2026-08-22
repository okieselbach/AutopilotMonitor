using System;
using System.Security.Cryptography;
using System.Text;

namespace AutopilotMonitor.Functions.Services.Notifications
{
    /// <summary>
    /// Computes the HMAC request signature for generic JSON webhooks (Stripe-style scheme).
    /// The receiver recomputes HMACSHA256(secret, "{timestamp}.{rawBody}") and compares it
    /// (constant-time) against the signature header; the timestamp binds replay protection.
    /// </summary>
    public static class WebhookSignatureCalculator
    {
        /// <summary>Header carrying the unix-seconds timestamp the signature was computed with.</summary>
        public const string TimestampHeader = "X-AutopilotMonitor-Timestamp";

        /// <summary>Header carrying the signature: "sha256=&lt;lowercase hex HMAC&gt;".</summary>
        public const string SignatureHeader = "X-AutopilotMonitor-Signature";

        /// <summary>
        /// Computes "sha256=&lt;hex&gt;" over "{timestamp}.{body}" with the given secret.
        /// Secret and message are UTF-8; hex is lowercase.
        /// </summary>
        public static string ComputeSignature(string secret, string timestamp, string body)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
            return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}

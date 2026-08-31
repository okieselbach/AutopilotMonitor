using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Response of POST diagnostics/download-ticket: a short-lived, self-authenticating
    /// download URL for one diagnostics blob (HMAC ticket in the query string).
    /// </summary>
    // Declaration order == wire order.
    public class DiagnosticsDownloadTicketResponse : IApiResponse
    {
        public bool Success { get; set; }

        /// <summary>Relative download URL ("/api/diagnostics/download?t=...").</summary>
        public string Url { get; set; } = default!;

        public DateTime ExpiresAt { get; set; }
        public string BlobName { get; set; } = default!;

        /// <summary>"Hosted" or "CustomerSas".</summary>
        public string Destination { get; set; } = default!;

        /// <summary>Best-effort blob size, or null when the size probe timed out/failed — the key is omitted when null.</summary>
        public long? SizeBytes { get; set; }
    }
}

using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Queue-message envelope for the <c>ime-msi-archive</c> queue. Enqueued by
    /// <c>EventIngestProcessor</c> when an <c>ime_agent_version</c> event carries a version
    /// the platform has never seen before (RecordImeVersionAsync insert succeeded). The
    /// worker downloads the IME installer and archives it into the <c>ime-archive</c> blob
    /// container so every fleet-observed IME build stays available for later decompilation
    /// and build-to-build diffing, even after Microsoft's versionless CDN URL has moved on.
    /// </summary>
    public sealed class ImeMsiArchiveEnvelope
    {
        /// <summary>Schema version — bump on breaking envelope changes so consumers can reject or migrate.</summary>
        public string EnvelopeVersion { get; set; } = "1";

        /// <summary>The newly sighted IME version, e.g. <c>1.104.102.0</c> (blob folder name).</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>
        /// The EnterpriseDesktopAppManagement-CSP <c>CurrentDownloadUrl</c> from the event's
        /// <c>msiDownloadUrl</c> enrichment. Null when the reporting agent predates the
        /// enrichment — the worker then falls back to the canonical versionless CDN URL
        /// (safe: a brand-new version is by definition what that URL currently serves).
        /// </summary>
        public string? MsiDownloadUrl { get; set; }

        /// <summary>
        /// The event's <c>msiMatchedBy</c> companion (<c>productVersion</c> = URL is
        /// version-authoritative, <c>fileName</c> = registry drift). Provenance only.
        /// </summary>
        public string? MsiMatchedBy { get; set; }

        /// <summary>First-seen tenant — provenance only, never used for auth or paths.</summary>
        public string TenantId { get; set; } = string.Empty;

        /// <summary>First-seen session — provenance only.</summary>
        public string SessionId { get; set; } = string.Empty;

        /// <summary>UTC time the producer enqueued the message; useful for measuring queue lag.</summary>
        public DateTime EnqueuedAt { get; set; }
    }
}

using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Classification of pre-auth distress signals sent when the authenticated error channel
    /// is unreachable (cert missing, hardware blocked, device not registered, etc.).
    /// </summary>
    public enum DistressErrorType
    {
        /// <summary>No MDM certificate found in the local certificate store.</summary>
        AuthCertificateMissing = 0,

        /// <summary>Certificate found but failed local validation (expired, wrong issuer, etc.).</summary>
        AuthCertificateInvalid = 1,

        /// <summary>Backend returned 401 — certificate rejected by server.</summary>
        AuthCertificateRejected = 2,

        /// <summary>Backend returned 403 — device manufacturer/model not in hardware whitelist.</summary>
        HardwareNotAllowed = 3,

        /// <summary>Backend returned 403 — device not found in Autopilot/Corporate Identifier registry.</summary>
        DeviceNotRegistered = 4,

        /// <summary>Backend returned 403 — tenant not found or suspended.</summary>
        TenantRejected = 5,

        /// <summary>GET /api/agent/config returned an auth error (401/403).</summary>
        ConfigFetchDenied = 6,

        /// <summary>POST /api/agent/register-session returned an auth error (401/403).</summary>
        SessionRegistrationDenied = 7,

        /// <summary>
        /// The device's TPM-backed client certificate cannot perform RSA-PSS signing — Schannel
        /// silently filters the cert out of TLS client-auth on Windows 11 25H2+ (which prefers
        /// PSS), so every mTLS handshake completes with no client cert and the backend rejects
        /// it. Surfaced after a terminal <c>SecureChannelFailure</c> when the PSS capability
        /// probe (<c>TpmPssCapabilityProbe</c>) confirms PKCS#1 works but PSS fails. Common on
        /// 2015-era Infineon TPM 2.0 firmware (e.g. Surface Book 1 SLB 9665); the fix is a TPM
        /// firmware update or device replacement, not an agent change.
        /// </summary>
        TpmPssUnsupported = 8,
    }

    /// <summary>
    /// Agent-reported state of the client certificate that triggered the distress signal.
    /// All values are UNVERIFIED; the distress channel has no authentication. Validated at the
    /// endpoint with <c>Enum.IsDefined</c> — adding a new value requires a coordinated rollout.
    /// V2 agents only; V1 agents leave the field <c>null</c> / <c>Unknown</c>.
    /// </summary>
    public enum DistressCertSourceState
    {
        /// <summary>Agent did not classify the cert state (default / V1 agent).</summary>
        Unknown = 0,

        /// <summary>No MDM client certificate found in the local store.</summary>
        NoCertInStore = 1,

        /// <summary>Cert located and looked valid client-side (NotBefore/After OK, EKU present).</summary>
        Found = 2,

        /// <summary>Cert located but already past NotAfter at the time of the failing call.</summary>
        FoundExpired = 3,

        /// <summary>Cert located but the private key is unavailable (TPM access denied, key deleted).</summary>
        FoundMissingPrivateKey = 4,

        /// <summary>Reading the cert store itself failed (access denied, store corrupt).</summary>
        StoreAccessError = 5,
    }

    /// <summary>
    /// Lightweight payload sent by the agent to the pre-auth distress channel endpoint
    /// when authentication-related failures prevent use of the normal error channel.
    ///
    /// All fields are treated as UNVERIFIED claims — the distress endpoint has no auth.
    /// Never stored on disk; fire-and-forget from the agent.
    /// </summary>
    public class DistressReport
    {
        /// <summary>
        /// Tenant ID from the device's MDM enrollment registry key. Unverified.
        /// </summary>
        public string TenantId { get; set; } = default!;

        /// <summary>
        /// Classification of the distress signal.
        /// </summary>
        public DistressErrorType ErrorType { get; set; }

        /// <summary>
        /// Device manufacturer (from WMI). Unverified. Max 64 chars.
        /// Useful for whitelist diagnostics ("Acer devices are being blocked").
        /// </summary>
        public string? Manufacturer { get; set; }

        /// <summary>
        /// Device model (from WMI). Unverified. Max 64 chars.
        /// </summary>
        public string? Model { get; set; }

        /// <summary>
        /// Device serial number (from WMI). Unverified. Max 64 chars.
        /// </summary>
        public string? SerialNumber { get; set; }

        /// <summary>
        /// Agent version string. Unverified. Max 32 chars.
        /// </summary>
        public string? AgentVersion { get; set; }

        /// <summary>
        /// HTTP status code returned by the backend, if applicable.
        /// Null for local failures (cert not found, etc.).
        /// </summary>
        public int? HttpStatusCode { get; set; }

        /// <summary>
        /// Short error message. Max 256 chars, sanitized on ingestion.
        /// No stack traces, no sensitive data.
        /// </summary>
        public string? Message { get; set; }

        /// <summary>
        /// UTC timestamp of when the distress occurred on the agent.
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// True when the agent's local W365 marker check (<c>CloudPcDetector</c>: Windows365
        /// registry key AND CloudManagedDesktopExtension service) identified the device as a
        /// Windows 365 Cloud PC. Unverified like all distress fields — pure triage context:
        /// a rejected Cloud PC points the admin at the Cloud PC validation toggle instead of
        /// a futile Autopilot-registration hunt. False for older agents that predate the field.
        /// </summary>
        public bool IsCloudPc { get; set; }

        /// <summary>
        /// Agent's classification of the cert state at the time of the failing call. V2 agents only.
        /// Optional; left null/Unknown by V1 agents. Validated server-side with <c>Enum.IsDefined</c>.
        /// </summary>
        public DistressCertSourceState? CertSourceState { get; set; }

        /// <summary>
        /// SHA-1 thumbprint of the client cert the agent attempted to use, uppercase hex (40 chars),
        /// or null if no cert was selected. Format-validated server-side (<c>^[0-9A-Fa-f]{40}$</c>).
        /// </summary>
        public string? CertThumbprint { get; set; }

        /// <summary>
        /// X.500 distinguished name of the cert subject (e.g. <c>CN=&lt;DeviceId&gt;</c>).
        /// Hard-capped to 96 chars server-side, sanitized for control chars. Optional.
        /// </summary>
        public string? CertSubject { get; set; }

        /// <summary>
        /// X.500 distinguished name of the cert issuer (e.g. <c>CN=Microsoft Intune MDM Devices CA</c>).
        /// Hard-capped to 96 chars server-side, sanitized for control chars. Optional.
        /// </summary>
        public string? CertIssuer { get; set; }

        /// <summary>
        /// Cert <c>NotBefore</c> in UTC, or null if no cert was inspected. Bounded by the same
        /// past/future window as <see cref="Timestamp"/>; out-of-range values are dropped.
        /// </summary>
        public DateTime? CertNotBefore { get; set; }

        /// <summary>
        /// Cert <c>NotAfter</c> in UTC, or null if no cert was inspected. Bounded the same way as
        /// <see cref="CertNotBefore"/>; out-of-range values are dropped.
        /// </summary>
        public DateTime? CertNotAfter { get; set; }
    }
}

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
        /// Session registration ended in a TLS <c>SecureChannelFailure</c> and the device's
        /// TPM-backed client key signs PKCS#1 but not RSA-PSS (<c>TpmPssCapabilityProbe</c>), so
        /// Schannel can withhold the certificate from the handshake. It classifies that start, not
        /// the device: some devices fail during setup (OOBE) and connect after it.
        /// <see cref="DistressReport.OobeState"/>, <see cref="DistressReport.OsBuild"/> and
        /// <see cref="DistressReport.TpmVersion"/> say when and where. Common on older TPM
        /// firmware; the remedy is a TPM firmware update or device replacement.
        /// </summary>
        TpmPssUnsupported = 8,

        /// <summary>
        /// The agent got a 401/403 this backend did not author: the response does not echo the
        /// request's <c>X-Correlation-ID</c>. It is the platform's "Client Certificate Required"
        /// page or a proxy's own page, so the device certificate never reached the backend —
        /// typically TLS inspection on the network path. <see cref="DistressReport.Message"/>
        /// carries the operation, the status, the page title and the issuer of the server
        /// certificate the agent saw.
        /// </summary>
        ClientCertificateNotReceived = 9,
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
    /// Never stored on disk by the agent.
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

        /// <summary>
        /// OS build with update revision when the report was sent (e.g. <c>26200.6899</c>).
        /// Kept server-side only as digits and dots, at most 32 chars. Null from older agents.
        /// </summary>
        public string? OsBuild { get; set; }

        /// <summary>
        /// Windows' own OOBE flag (<c>SystemSetupInfo.OutOfBoxExperienceState</c>: <c>not_started</c>,
        /// <c>in_progress</c>, <c>completed</c>, <c>unknown_&lt;n&gt;</c>, <c>unavailable</c>) — it
        /// flips to <c>completed</c> at the desktop and says nothing about ESP progress (see
        /// <see cref="EspPhase"/>). Sampled with <see cref="EspPhase"/>. Kept server-side only as
        /// <c>[a-z0-9_]</c>, at most 16 chars. Null from older agents.
        /// </summary>
        public string? OobeState { get; set; }

        /// <summary>
        /// Autopilot ESP phase from the ESP registry: <c>device_preparation</c>,
        /// <c>device_setup</c>, <c>account_setup</c> (running), <c>&lt;phase&gt;_failed</c>,
        /// <c>device_setup_done</c>, <c>esp_done</c>, <c>none</c> or <c>unavailable</c>. A failed
        /// registration samples it before its first attempt, other reports when sent. Kept
        /// server-side only as <c>[a-z_]</c>, at most 32 chars. Null from older agents.
        /// </summary>
        public string? EspPhase { get; set; }

        /// <summary>
        /// When the device last booted (UTC), sampled with <see cref="EspPhase"/>. Kept server-side
        /// only within 30 days before and 5 minutes after <see cref="Timestamp"/>. Null from older agents.
        /// </summary>
        public DateTime? DeviceBootUtc { get; set; }

        /// <summary>
        /// TPM manufacturer id and firmware version (e.g. <c>IFX 7.63.3353.0</c>), sent with
        /// <see cref="DistressErrorType.TpmPssUnsupported"/> only. Sanitized, at most 48 chars.
        /// </summary>
        public string? TpmVersion { get; set; }
    }
}

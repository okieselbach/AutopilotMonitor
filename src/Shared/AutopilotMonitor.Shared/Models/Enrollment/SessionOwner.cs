using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// The device identity a session is bound to — stamped by the backend from the validated
    /// caller identity at registration and carried on the Sessions row. Never populated from
    /// agent input.
    /// <para>
    /// SESSION-OWNER-BINDING: a session-scoped write (telemetry ingest, re-registration, error
    /// report) is compared against this owner. Cert-authenticated callers are identified by the
    /// TLS-proven certificate (thumbprint, plus the Intune device id from the certificate CN so a
    /// re-issued certificate of the same device still matches); bootstrap-token callers by the
    /// short code that admitted them plus the serial they announced.
    /// </para>
    /// </summary>
    public sealed class SessionOwner
    {
        /// <summary>Sessions-row column names. Kept here so writer and reader share one spelling.</summary>
        public static class Columns
        {
            public const string Kind = "OwnerKind";
            public const string Thumbprint = "OwnerThumbprint";
            public const string DeviceId = "OwnerDeviceId";
            public const string BootstrapCode = "OwnerBootstrapCode";
            public const string Serial = "OwnerSerial";
            public const string BoundAt = "OwnerBoundAt";

            public static readonly string[] All = { Kind, Thumbprint, DeviceId, BootstrapCode, Serial, BoundAt };
        }

        /// <summary>Values of <see cref="Kind"/>. Stable strings — queried by exact match.</summary>
        public static class Kinds
        {
            public const string Cert = "Cert";
            public const string Bootstrap = "Bootstrap";
        }

        /// <summary><see cref="Kinds.Cert"/> or <see cref="Kinds.Bootstrap"/>.</summary>
        public string Kind { get; set; } = default!;

        /// <summary>Cert-owned only: thumbprint of the TLS client certificate that registered the session.</summary>
        public string? Thumbprint { get; set; }

        /// <summary>Cert-owned only: Intune device id from the certificate CN (lower-case GUID), when the CN had that shape.</summary>
        public string? DeviceId { get; set; }

        /// <summary>Bootstrap-owned only: the 6-character short code whose token admitted the caller.</summary>
        public string? BootstrapCode { get; set; }

        /// <summary>Serial number the caller announced in <c>X-Device-SerialNumber</c> when the binding was made. Trimmed, original casing.</summary>
        public string? Serial { get; set; }

        /// <summary>When this binding was stamped (UTC).</summary>
        public DateTime BoundAt { get; set; }

        public bool IsCert => Kind == Kinds.Cert;
        public bool IsBootstrap => Kind == Kinds.Bootstrap;
    }
}

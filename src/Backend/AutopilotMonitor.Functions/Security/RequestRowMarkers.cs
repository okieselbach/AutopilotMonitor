using Microsoft.Azure.Functions.Worker.Http;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Request-row dimensions the agent security gate stamps via <c>FunctionContext.Items</c>;
    /// <c>RequestTelemetryMiddleware</c> copies them onto the <c>requests</c> row it emits per call.
    /// Same carrier as <see cref="CertTenantBinding.RequestItemKey"/>: worker-side
    /// <c>LogInformation</c> never reaches App Insights, so the accepting path would otherwise be
    /// invisible — only its failures would leave a trace.
    /// </summary>
    public static class RequestRowMarkers
    {
        /// <summary>
        /// The tenant the request was proven to belong to (certificate tenant binding matched, or
        /// the bootstrap token belongs to it). Agent routes that take the tenant from the body or
        /// the query string carry no trusted header, so this is the only safe source for the
        /// <c>TenantId</c> dimension on register / config / upload-url rows.
        /// </summary>
        public const string ValidatedTenantKey = "ValidatedTenantId";

        /// <summary>
        /// Outcome of the device-validation chain: the admitting <see cref="ValidatorType"/> name,
        /// or one of <see cref="DeviceValidation"/>. Absent when an earlier stage (certificate,
        /// certificate tenant, hardware, rate limit) ended the request — the chain never ran.
        /// </summary>
        public const string DeviceValidationKey = "DeviceValidation";

        /// <summary>Closed set of non-validator values, so <c>summarize by</c> stays stable.</summary>
        public static class DeviceValidation
        {
            /// <summary>Tenant runs without a device validator (<c>AllowInsecureAgentRequests</c>).</summary>
            public const string None = "None";
            /// <summary>Every enabled validator failed transiently — the agent got 503 Retry-After.</summary>
            public const string Transient = "Transient";
            /// <summary>Every enabled validator missed definitively — the agent got 403.</summary>
            public const string Rejected = "Rejected";
        }

        public static string DeviceValidationValue(ValidatorType validatedBy)
            => validatedBy == ValidatorType.Unknown ? DeviceValidation.None : validatedBy.ToString();

        /// <summary>
        /// Outcome of the Intune device lookup behind the client certificate
        /// (<see cref="IntuneDeviceBindingOutcome"/> name). Absent when no lookup result exists for
        /// the request: the tenant granted no permission, or an observation is still running.
        /// </summary>
        public const string CertDeviceBindingKey = "CertDeviceBinding";

        /// <summary>
        /// <see cref="IntuneDeviceBindingRole"/> of that lookup: <c>Admitting</c> when it decided
        /// admission, <c>Observing</c> when another validator had already admitted the device.
        /// </summary>
        public const string CertDeviceBindingRoleKey = "CertDeviceBindingRole";

        /// <summary>
        /// <c>true</c> / <c>false</c>: whether the agent's serial header equals the serial Intune
        /// recorded for the certificate's device. Absent when either side is unknown. Observation
        /// only: nothing rejects on it.
        /// </summary>
        public const string CertDeviceSerialMatchKey = "CertDeviceSerialMatch";

        /// <summary>Intune managedDeviceOwnerType of the certificate's device (company / personal / unknown).</summary>
        public const string CertDeviceOwnerTypeKey = "CertDeviceOwnerType";

        /// <summary>Intune deviceEnrollmentType of the certificate's device.</summary>
        public const string CertDeviceEnrollmentTypeKey = "CertDeviceEnrollmentType";

        /// <summary>Every cert-device key; <c>RequestTelemetryMiddleware</c> copies them onto the request row.</summary>
        public static readonly string[] CertDeviceBindingKeys =
        {
            CertDeviceBindingKey, CertDeviceBindingRoleKey, CertDeviceSerialMatchKey,
            CertDeviceOwnerTypeKey, CertDeviceEnrollmentTypeKey,
        };

        /// <summary>
        /// Stamps one Intune device lookup onto the request row. <paramref name="headerSerial"/> is
        /// the agent-supplied serial, compared case-insensitively against the one Intune recorded.
        /// </summary>
        public static void StampCertDeviceBinding(
            HttpRequestData req, IntuneDeviceBindingResult result, IntuneDeviceBindingRole role, string? headerSerial)
        {
            Stamp(req, CertDeviceBindingKey, result.Outcome.ToString());
            Stamp(req, CertDeviceBindingRoleKey, role.ToString());

            var serialMatch = SerialMatch(headerSerial, result.SerialNumber);
            if (serialMatch.HasValue)
                Stamp(req, CertDeviceSerialMatchKey, serialMatch.Value ? "true" : "false");
            if (!string.IsNullOrEmpty(result.OwnerType))
                Stamp(req, CertDeviceOwnerTypeKey, result.OwnerType!);
            if (!string.IsNullOrEmpty(result.EnrollmentType))
                Stamp(req, CertDeviceEnrollmentTypeKey, result.EnrollmentType!);
        }

        /// <summary>Null when either serial is unknown; otherwise a trimmed, case-insensitive comparison.</summary>
        internal static bool? SerialMatch(string? headerSerial, string? intuneSerial)
        {
            if (string.IsNullOrWhiteSpace(headerSerial) || string.IsNullOrWhiteSpace(intuneSerial))
                return null;
            return string.Equals(headerSerial!.Trim(), intuneSerial!.Trim(), System.StringComparison.OrdinalIgnoreCase);
        }

        public static void Stamp(HttpRequestData req, string key, string value)
        {
            var items = req.FunctionContext?.Items;
            if (items != null)
                items[key] = value;
        }
    }
}

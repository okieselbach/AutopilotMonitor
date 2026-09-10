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

        public static void Stamp(HttpRequestData req, string key, string value)
        {
            var items = req.FunctionContext?.Items;
            if (items != null)
                items[key] = value;
        }
    }
}

using Microsoft.Azure.Functions.Worker.Http;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// DEVICE-IDENTITY-BLOCK-BINDING — request-row dimension for the identity leg of the kill
    /// switch. Carried on the (unsampled) request row via <c>FunctionContext.Items</c>, same
    /// carrier and reasoning as <see cref="CertTenantBinding"/>: worker-side LogInformation never
    /// reaches App Insights, so the bulk outcome needs a per-request property to be a denominator.
    /// </summary>
    public static class DeviceIdentityBinding
    {
        public const string RequestItemKey = "DeviceIdentityBinding";

        public static class Outcome
        {
            /// <summary>No certificate identity on the request (bootstrap token, non-GUID CN) — serial-only enforcement.</summary>
            public const string NoIdentity = "NoIdentity";
            /// <summary>Identity present; no block keyed by it.</summary>
            public const string Match = "Match";
            /// <summary>Identity present and blocked via its alias row (the serial leg did not match — omitted/forged header).</summary>
            public const string IdentityBlocked = "IdentityBlocked";
        }

        public static void Stamp(HttpRequestData req, string outcome)
        {
            var items = req.FunctionContext?.Items;
            if (items != null)
                items[RequestItemKey] = outcome;
        }
    }
}

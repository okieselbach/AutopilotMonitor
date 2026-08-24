using System;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Compares the Entra TenantId stamped into the agent's mTLS client certificate against the
    /// tenant the request claims to belong to.
    /// <para>
    /// Without this, a validated certificate only proves "issued by the Intune MDM Device CA to
    /// some tenant" — the chain is pinned to Microsoft's roots, which every Intune tenant on the
    /// planet shares. The tenant scoping comes solely from the downstream device validators, so an
    /// attacker with their own Intune tenant and a known serial number of the victim tenant would
    /// pass certificate validation. Binding the cert's tenant to the requested tenant closes that.
    /// </para>
    /// </summary>
    /// <remarks>
    /// SHADOW MODE (stage 1): the outcome is observed and logged only — it never changes an
    /// authorization decision. Enforcement is stage 2, once telemetry shows how many field
    /// certificates actually carry the extension. Grep marker for the stage-2 change:
    /// <c>CERT-TENANT-BINDING-SHADOW</c>.
    /// </remarks>
    public static class CertTenantBinding
    {
        /// <summary>
        /// <see cref="Microsoft.Azure.Functions.Worker.FunctionContext"/> item key under which
        /// <c>SecurityValidator</c> hands the outcome to <c>RequestTelemetryMiddleware</c>, which
        /// stamps it onto the request row as the <c>CertTenantBinding</c> dimension.
        /// </summary>
        /// <remarks>
        /// The request row is the carrier because worker-side <c>LogInformation</c> never reaches
        /// App Insights (the provider's default rule is Warning+), so the bulk "Match" outcome
        /// cannot be observed as a trace and the shadow telemetry would have no denominator.
        /// </remarks>
        public const string RequestItemKey = "CertTenantBinding";

        /// <summary>
        /// Stable outcome codes emitted in the <c>AgentCertTenantBinding</c> structured log.
        /// Keep these strings stable — they are queried by exact match in KQL
        /// (<c>customDimensions.Outcome == "Mismatch"</c>).
        /// </summary>
        public static class Outcome
        {
            /// <summary>Certificate tenant equals the requested tenant — the expected case.</summary>
            public const string Match = "Match";

            /// <summary>Certificate belongs to a different tenant than the request claims.</summary>
            public const string Mismatch = "Mismatch";

            /// <summary>Certificate carries no tenant extension (older enrollment / unexpected CA).</summary>
            public const string ExtensionMissing = "ExtensionMissing";

            /// <summary>Tenant extension present but undecodable.</summary>
            public const string Unparseable = "Unparseable";

            /// <summary>Requested tenant id was not a GUID, so no meaningful comparison is possible.</summary>
            public const string RequestTenantNotAGuid = "RequestTenantNotAGuid";
        }

        /// <summary>
        /// Evaluates the binding. Pure — no logging, no I/O, no side effects.
        /// </summary>
        /// <param name="certTenantId">Tenant GUID decoded from the client certificate, if any.</param>
        /// <param name="status">Whether the certificate carried a decodable tenant extension.</param>
        /// <param name="requestedTenantId">The tenant id the request is scoped to.</param>
        /// <returns>One of the <see cref="Outcome"/> codes.</returns>
        public static string Evaluate(Guid? certTenantId, CertTenantIdStatus status, string? requestedTenantId)
        {
            switch (status)
            {
                case CertTenantIdStatus.ExtensionMissing:
                    return Outcome.ExtensionMissing;
                case CertTenantIdStatus.Unparseable:
                    return Outcome.Unparseable;
            }

            if (certTenantId == null)
                return Outcome.ExtensionMissing;

            if (!Guid.TryParse(requestedTenantId, out var requested))
                return Outcome.RequestTenantNotAGuid;

            return certTenantId.Value == requested ? Outcome.Match : Outcome.Mismatch;
        }

        /// <summary>
        /// Whether an outcome would block the request once stage-2 enforcement is switched on.
        /// Nothing consumes this for authorization yet — it exists so the shadow telemetry can be
        /// read as "would this have been rejected?" without re-deriving the rule at query time.
        /// </summary>
        public static bool WouldRejectUnderEnforcement(string outcome) =>
            outcome == Outcome.Mismatch;
    }
}

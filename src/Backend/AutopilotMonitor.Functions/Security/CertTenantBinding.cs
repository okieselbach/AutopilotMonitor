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
    /// ENFORCED since 2026-08-25 (stage 2): a <see cref="Outcome.Mismatch"/> is rejected with 403.
    /// It shipped as shadow-only first and was switched on against measured data — 35,684 requests
    /// across 32 tenants, every one of them a Match, with the Warning channel verified to be
    /// carrying other traffic so the zero was not a telemetry artefact.
    /// <para>
    /// <see cref="Rejects"/> is the whole rule and the only thing that blocks; the outcome is still
    /// recorded on every request. Grep marker for the sites involved:
    /// <c>CERT-TENANT-BINDING</c>.
    /// </para>
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
        /// Whether an outcome blocks the request. This is the enforcement rule, in one place.
        /// </summary>
        /// <remarks>
        /// Only <see cref="Outcome.Mismatch"/> rejects, and only Mismatch is actual evidence of the
        /// attack: a certificate the Intune CA issued to a different tenant than the one the request
        /// claims. Everything else means "cannot tell" and must not cost a legitimate device its
        /// enrollment:
        /// <list type="bullet">
        /// <item><description><see cref="Outcome.ExtensionMissing"/> — the certificate carries no
        /// tenant stamp. Measured at 0 of 35,684 requests across 32 tenants before enforcement was
        /// switched on, but that sample only covers devices that were active in the window; older
        /// certificates or a sovereign-cloud CA could still lack it. Rejecting on absence would lock
        /// those devices out for something they cannot control.
        /// <para>
        /// FUTURE TIGHTENING: once the fleet is provably all-5.14 (watch the ExtensionMissing rate
        /// in the telemetry below), this can become a rejection too. That is a deliberate follow-up
        /// decision, not an oversight.
        /// </para></description></item>
        /// <item><description><see cref="Outcome.Unparseable"/> — extension present but undecodable.
        /// Same reasoning: a decoder or encoding surprise is our problem, not the device's.</description></item>
        /// <item><description><see cref="Outcome.RequestTenantNotAGuid"/> — nothing to compare
        /// against. A non-GUID tenant fails the tenant lookup in §0 long before this point anyway.</description></item>
        /// </list>
        /// </remarks>
        public static bool Rejects(string outcome) => outcome == Outcome.Mismatch;

        /// <summary>
        /// Retained name for the telemetry field that records what the rule decided. Identical to
        /// <see cref="Rejects"/> — kept so KQL written during the shadow phase keeps working.
        /// </summary>
        public static bool WouldRejectUnderEnforcement(string outcome) => Rejects(outcome);
    }
}

using System;
using AutopilotMonitor.Functions.Security;

namespace AutopilotMonitor.Functions.Services.Offboarding
{
    /// <summary>
    /// OData filter builders for tenant-offboarding queries. All inputs are validated and
    /// escaped before interpolation. Centralizing the patterns here keeps SafeWipe-callers
    /// honest and gives the test suite a single surface to attack with injection payloads.
    /// </summary>
    /// <remarks>
    /// Rules (memory: feedback_storage_helpers_fail_soft, plan §5.5):
    /// <list type="number">
    /// <item>Every tenant id is run through <see cref="SecurityValidator.EnsureValidGuid"/> before use.</item>
    /// <item>Every interpolated value passes through <see cref="ODataSanitizer.EscapeValue"/>.</item>
    /// <item>Composite-PK range uses <c>'{tenantId}_~'</c> upper bound — the underscore is the
    ///       anchor; without it the range bleeds into the next ASCII bucket.</item>
    /// </list>
    /// </remarks>
    public static class OffboardingFilters
    {
        /// <summary>
        /// <c>PartitionKey eq '{tenantId}'</c> — for Variant A SafeWipe of tenant-PK tables.
        /// </summary>
        public static string ExactPartition(string normalizedTenantId)
        {
            SecurityValidator.EnsureValidGuid(normalizedTenantId, nameof(normalizedTenantId));
            return $"PartitionKey eq '{ODataSanitizer.EscapeValue(normalizedTenantId)}'";
        }

        /// <summary>
        /// <c>PartitionKey ge '{tenantId}_' and PartitionKey lt '{tenantId}_~'</c> — for
        /// Variant A SafeWipe of composite-PK tables (events, signals, decision transitions,
        /// discriminator-PK index tables, …). The underscore-tilde upper bound traps the
        /// range strictly to <c>{tenantId}_*</c>; switching to <c>'{tenantId}~'</c> would
        /// match keys outside the tenant prefix.
        /// </summary>
        public static string CompositePartitionRange(string normalizedTenantId)
        {
            SecurityValidator.EnsureValidGuid(normalizedTenantId, nameof(normalizedTenantId));
            var safe = ODataSanitizer.EscapeValue(normalizedTenantId);
            return $"PartitionKey ge '{safe}_' and PartitionKey lt '{safe}_~'";
        }

        /// <summary>
        /// <c>PartitionKey eq '{discriminator}' and TenantId eq '{tenantId}'</c> — for
        /// Variant B SafeWipe of discriminator-PK tables with a TenantId property as the
        /// only tenant anchor. Verify-step MUST re-check the property server-side because
        /// the PK is non-anchored.
        /// </summary>
        public static string DiscriminatorWithTenantProp(string discriminator, string normalizedTenantId)
        {
            if (string.IsNullOrEmpty(discriminator)) throw new ArgumentException("Discriminator required", nameof(discriminator));
            SecurityValidator.EnsureValidGuid(normalizedTenantId, nameof(normalizedTenantId));
            return $"PartitionKey eq '{ODataSanitizer.EscapeValue(discriminator)}' " +
                   $"and TenantId eq '{ODataSanitizer.EscapeValue(normalizedTenantId)}'";
        }

        /// <summary>
        /// <c>TenantId eq '{tenantId}'</c> — Variant C SafeWipe (Property-only full-table
        /// filter). Reserve for low-volume tables; Group-by-PartitionKey before batching
        /// because rows can span many PKs.
        /// </summary>
        public static string TenantIdProperty(string normalizedTenantId)
        {
            SecurityValidator.EnsureValidGuid(normalizedTenantId, nameof(normalizedTenantId));
            return $"TenantId eq '{ODataSanitizer.EscapeValue(normalizedTenantId)}'";
        }
    }
}

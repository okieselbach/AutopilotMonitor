using System;

namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>
    /// Key helpers for the session index tables whose PartitionKey is
    /// <c>{tenantId}_{key}</c> — <c>EventTypeIndex</c> (<c>{tenantId}_{eventType}</c>) and
    /// <c>CveIndex</c> (<c>{tenantId}_{cveId}</c>). Rows carry <c>TenantId</c>/<c>SessionId</c>
    /// as columns; the PartitionKey is the fallback for rows written before those columns
    /// existed. The key value is always known to the caller, so stripping its suffix is
    /// unambiguous even though event types contain underscores — tenant IDs never do.
    /// A consumer that has the row therefore never needs a SessionsIndex scan to learn the tenant.
    /// </summary>
    internal static class IndexRowKeys
    {
        /// <summary>
        /// Resolves the tenant of an index row: the <c>TenantId</c> column when present, else
        /// the PartitionKey with the known <c>_{keySuffix}</c> removed. Returns null when
        /// neither yields a plausible tenant (foreign PartitionKey shape).
        /// </summary>
        internal static string? ResolveTenantId(string? partitionKey, string keySuffix, string? tenantIdColumn)
        {
            if (!string.IsNullOrEmpty(tenantIdColumn))
                return tenantIdColumn;
            if (string.IsNullOrEmpty(partitionKey) || string.IsNullOrEmpty(keySuffix))
                return null;

            var suffix = "_" + keySuffix;
            if (partitionKey!.Length <= suffix.Length)
                return null;
            if (!partitionKey.EndsWith(suffix, StringComparison.Ordinal))
                return null;

            var tenant = partitionKey.Substring(0, partitionKey.Length - suffix.Length);
            return tenant.IndexOf('_') >= 0 ? null : tenant;
        }
    }
}

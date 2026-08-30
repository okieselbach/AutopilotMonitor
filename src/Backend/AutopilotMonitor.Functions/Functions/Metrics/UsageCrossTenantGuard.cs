using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Functions.Functions.Metrics;

/// <summary>
/// Cross-tenant access guard for <c>GET /api/metrics/mcp-usage/user/{userId}</c>.
///
/// The route accepts an Azure AD object id (oid), which has no inherent tenant scoping —
/// middleware can't validate it the way it does for <c>{tenantId}</c> routes. The function
/// therefore projects the repository result down to the caller's own tenant before it is
/// returned. Two properties matter:
///
/// <list type="bullet">
///   <item>Records that cannot be attributed to the caller's tenant — foreign <c>TenantId</c>
///   OR an empty one (legacy rows / tokens without a resolvable tid) — are dropped for
///   non-global callers. Unattributed data is not the caller's data.</item>
///   <item>The filter is silent: a foreign oid and an unknown oid both yield an empty set, so
///   the response cannot be used as an existence oracle for users of other tenants.</item>
/// </list>
/// </summary>
public static class UsageCrossTenantGuard
{
    /// <summary>
    /// Returns the subset of <paramref name="records"/> the caller may see. Global-scope
    /// callers (GA / Global Reader) see everything; everyone else sees only records whose
    /// <see cref="UserUsageRecord.TenantId"/> equals <paramref name="callerTenantId"/>
    /// (case-insensitive). A caller without a tenant id sees nothing.
    /// </summary>
    public static IReadOnlyList<UserUsageRecord> FilterForCaller(
        IEnumerable<UserUsageRecord>? records,
        string? callerTenantId,
        bool hasGlobalScope)
    {
        if (records == null) return Array.Empty<UserUsageRecord>();
        if (hasGlobalScope) return records.ToList();
        if (string.IsNullOrEmpty(callerTenantId)) return Array.Empty<UserUsageRecord>();

        return records
            .Where(r => !string.IsNullOrEmpty(r.TenantId)
                        && string.Equals(r.TenantId, callerTenantId, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}

using System;

namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>
    /// Lifetimes of the rows derived from sessions and of the audit trail. Tenant rows follow the
    /// tenant's effective retention and are pruned per tenant by <c>SessionRetentionFanoutService</c>;
    /// the fixed values here bound that from outside: the audit floor, the ceiling no tenant row
    /// outlives (retention 0, a tenant the fanout did not reach), and the lifetimes of platform rows
    /// that belong to no tenant. Route caps over these tables refer to the same constants.
    /// </summary>
    public static class DataLifetimes
    {
        /// <summary>Ceiling for every tenant row: the Pro retention cap, the longest window any route reads.</summary>
        public const int TenantRowCeilingDays = 365;

        /// <summary>
        /// A tenant's audit trail is kept at least this long, whatever its retention; the platform partition
        /// (<c>Constants.AuditGlobalTenantId</c>) and legacy rows without an event time keep exactly this.
        /// </summary>
        public const int AuditLogFloorDays = 180;

        /// <summary>Platform ("global") first-time-right rows: as far back as the Global Admin route reads.</summary>
        public const int GlobalDeviceJourneyDays = TenantRowCeilingDays;

        /// <summary>Platform ("global") rule stats and the legacy layout until its cutover.</summary>
        public const int GlobalRuleStatsDays = 90;

        /// <summary>Time-attribution rows: the route reads only the newest 30-day window.</summary>
        public const int TimeAttributionDays = 180;

        /// <summary>Verdict-calibration rows (Global Admin only).</summary>
        public const int VerdictCalibrationDays = 180;

        /// <summary>Daily usage-metrics snapshots.</summary>
        public const int UsageMetricsDays = 180;

        /// <summary>
        /// A tenant's audit trail: its effective retention, never below the floor nor above the ceiling;
        /// 0 (never delete) keeps it to the ceiling.
        /// </summary>
        public static int TenantAuditDays(int effectiveRetentionDays)
            => effectiveRetentionDays <= 0
                ? TenantRowCeilingDays
                : Math.Clamp(effectiveRetentionDays, AuditLogFloorDays, TenantRowCeilingDays);
    }
}

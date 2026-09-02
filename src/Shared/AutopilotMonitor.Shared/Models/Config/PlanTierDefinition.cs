namespace AutopilotMonitor.Shared.Models.Config
{
    /// <summary>
    /// Defines a usage plan tier with request limits: the per-USER windows every account on the plan gets,
    /// and the organization-wide TENANT windows all members of a tenant on this plan share.
    /// Stored as JSON array in AdminConfiguration.PlanTierDefinitionsJson. 0 = unlimited for that window.
    /// </summary>
    public class PlanTierDefinition
    {
        public string Name { get; set; } = string.Empty;
        public int DailyRequestLimit { get; set; } = 100;
        public int MonthlyRequestLimit { get; set; } = 3000;
        public string Description { get; set; } = string.Empty;

        /// <summary>Tenant-wide daily limit; null (not set) = the edition's catalog tenant limit, 0 = unlimited.</summary>
        public int? TenantDailyRequestLimit { get; set; }

        /// <summary>Tenant-wide monthly limit; null (not set) = the edition's catalog tenant limit, 0 = unlimited.</summary>
        public int? TenantMonthlyRequestLimit { get; set; }
    }
}

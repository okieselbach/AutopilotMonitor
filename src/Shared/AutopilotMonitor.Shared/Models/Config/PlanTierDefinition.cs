namespace AutopilotMonitor.Shared.Models.Config
{
    /// <summary>
    /// Defines a usage plan tier with request limits: the per-USER windows every account on the plan gets,
    /// the organization-wide TENANT windows all members of a tenant on this plan share, and the per-SLOT
    /// growth of both — what every purchased delegation slot beyond the edition's included ones adds.
    /// Stored as JSON array in AdminConfiguration.PlanTierDefinitionsJson. 0 = unlimited for a window,
    /// 0 = no growth for a slot value; null (not set) = the edition's catalog value.
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

        /// <summary>Per-user daily requests added per purchased delegation slot; null = catalog value, 0 = none.</summary>
        public int? SlotDailyRequestLimit { get; set; }

        /// <summary>Per-user monthly requests added per purchased delegation slot; null = catalog value, 0 = none.</summary>
        public int? SlotMonthlyRequestLimit { get; set; }

        /// <summary>Tenant-wide daily requests added per purchased delegation slot; null = catalog value, 0 = none.</summary>
        public int? SlotTenantDailyRequestLimit { get; set; }

        /// <summary>Tenant-wide monthly requests added per purchased delegation slot; null = catalog value, 0 = none.</summary>
        public int? SlotTenantMonthlyRequestLimit { get; set; }
    }
}

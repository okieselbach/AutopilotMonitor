namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// The source figures the platform-wide counters are rolled up from, as one maintenance run
    /// saw them. Persisted next to the platform row (PartitionKey "global", RowKey
    /// "rollup-baseline") so the next run can add the growth since then: the platform counters
    /// only ever grow by the positive delta between two runs and are never derived from an
    /// absolute figure again — a source that shrinks (offboarding deletes a tenant's counters)
    /// costs the growth of one interval instead of freezing the counter.
    /// </summary>
    public class PlatformRollupSources
    {
        /// <summary>Sum of the per-tenant TotalEnrollments counters</summary>
        public long TotalEnrollments { get; set; }

        /// <summary>Sum of the per-tenant SuccessfulEnrollments counters</summary>
        public long SuccessfulEnrollments { get; set; }

        /// <summary>Sum of the per-tenant TotalEventsProcessed counters</summary>
        public long TotalEventsProcessed { get; set; }

        /// <summary>Tenants whose counters show at least one enrollment</summary>
        public long ActiveTenants { get; set; }

        /// <summary>Size of the persisted set of device models ever seen</summary>
        public long SeenDeviceModels { get; set; }
    }
}

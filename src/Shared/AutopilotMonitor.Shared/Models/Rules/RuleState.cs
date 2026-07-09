namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Per-tenant override of a rule's default behavior.
    /// Stored in the RuleStates table (PartitionKey=TenantId, RowKey=RuleId).
    /// Absent entries mean "use rule defaults from the definition".
    /// </summary>
    public class RuleState
    {
        /// <summary>
        /// Whether the rule is enabled for this tenant. Overrides the rule's default.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Tenant-scoped override for <see cref="AnalyzeRule.MarkSessionAsFailedDefault"/>.
        /// Null means the tenant has not opted in or out — the rule's default applies.
        /// Only meaningful for analyze rules; ignored for gather rules.
        /// </summary>
        public bool? MarkSessionAsFailed { get; set; }

        /// <summary>
        /// Tenant-scoped override for <see cref="AnalyzeRule.NotifyDefault"/>: send an outbound
        /// notification when this rule newly fires. Null = inherit the rule default.
        /// Only meaningful for analyze rules; ignored for gather rules.
        /// </summary>
        public bool? Notify { get; set; }

        /// <summary>
        /// JSON array of notification-channel ids (tenant's NotificationChannels) targeted when
        /// this rule fires. Null/empty = no targets → no notification even if Notify is true.
        /// </summary>
        public string? NotifyChannelIdsJson { get; set; }
    }
}

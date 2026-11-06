namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// A tenant's effective edition as the portal's plan facet names it. The display precedence is Pro conferred
    /// by a managing (MSP) tenant, then the stored Pro tier, then a running trial, else Community — the order of
    /// the backend's edition resolution and of the portal's <c>effectiveEdition</c>. The web derives its
    /// <c>PlanEdition</c> type from this list (shared manifest), so the two cannot name different values.
    /// </summary>
    public static class TenantEditionNames
    {
        public const string Community = "community";
        public const string Pro = "pro";
        public const string ProTrial = "pro-trial";
        public const string ProMsp = "pro-msp";

        public static readonly string[] All = { Community, Pro, ProTrial, ProMsp };
    }
}

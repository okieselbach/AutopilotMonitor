namespace AutopilotMonitor.Functions.Services.Offboarding
{
    /// <summary>
    /// What one <see cref="TenantOffboardingHandler.HandleAsync"/> pickup left behind. A thrown
    /// exception is the fourth outcome: the worker retries the envelope.
    /// </summary>
    public enum TenantOffboardingOutcome
    {
        /// <summary>Drain not settled yet — a follow-up envelope was enqueued.</summary>
        Pending,

        /// <summary>History is Completed, by this pickup or an earlier one.</summary>
        Completed,

        /// <summary>History is already Failed — nothing was done, operator action required.</summary>
        Failed,
    }
}

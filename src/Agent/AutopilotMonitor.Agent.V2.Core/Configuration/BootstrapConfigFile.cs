namespace AutopilotMonitor.Agent.V2.Core.Configuration
{
    /// <summary>
    /// Persisted bootstrap configuration for OOBE-bootstrapped agents. Plan §4.x M4.6.α.
    /// <para>
    /// Saved to <c>%ProgramData%\AutopilotMonitor\bootstrap-config.json</c> by <c>--install</c> mode
    /// so the Scheduled Task (whose command line has no args) can pick up the bootstrap token and
    /// tenant id on the first post-install run.
    /// </para>
    /// </summary>
    public sealed class BootstrapConfigFile
    {
        public string BootstrapToken { get; set; }
        public string TenantId { get; set; }

        /// <summary>
        /// TenantId-wait timeout in seconds, persisted from the <c>--tenant-id-wait</c>
        /// install-time arg. Read by AgentBootstrap when the registry probe finds no
        /// TenantId on first try. 0 = explicit opt-out (legacy fast-fail).
        /// Null / missing (files written before this property existed) = not configured —
        /// the agent-side default (600 s) applies. Nullable so a legacy file without the
        /// property cannot masquerade as an explicit 0 and silently disable the wait.
        /// </summary>
        public int? TenantIdWaitSeconds { get; set; }
    }
}

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Controls who can access the remote MCP server.
    /// Stored as string in AdminConfiguration.McpAccessPolicy.
    /// </summary>
    public enum McpAccessPolicy
    {
        /// <summary>MCP access completely disabled.</summary>
        Disabled = 0,

        /// <summary>Platform roles, delegated (MSP) admins and explicitly whitelisted MCP users.</summary>
        WhitelistOnly = 1,

        /// <summary>
        /// The WhitelistOnly grants plus every effective tenant member (Admin / Operator / Viewer) of the
        /// tenant the token was issued for. Not "any authenticated user": a sign-in without a role (e.g. a
        /// Progress Portal end-user) is denied. The McpUsers table then acts as a per-user override list
        /// (individual usage plan, block).
        /// </summary>
        AllMembers = 2
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Self-hosted MCP client registrations: a Tenant Admin registers the exact OAuth callback of a client
    /// the organization runs itself (a self-hosted chat front end) and hands it client id
    /// <c>amc_&lt;RegistrationId&gt;</c>. The MCP server's OAuth proxy treats the registration as that
    /// client's redirect allowlist entry and binds the flow to <see cref="McpClientRegistration.TenantId"/>.
    /// One partition for every tenant (a registration is a rare, deliberate act), RK = registration id, so
    /// the MCP server's anonymous lookup is a point read.
    /// </summary>
    public interface IMcpClientRegistrationRepository
    {
        /// <summary>Stores a new registration; false when storage refused it.</summary>
        Task<bool> CreateAsync(McpClientRegistration registration);

        /// <summary>One registration by id, or null.</summary>
        Task<McpClientRegistration?> GetAsync(string registrationId);

        /// <summary>Every registration of one tenant.</summary>
        Task<List<McpClientRegistration>> GetForTenantAsync(string tenantId);

        /// <summary>Deletes a registration; false when it did not exist.</summary>
        Task<bool> DeleteAsync(string registrationId);
    }

    /// <summary>One self-hosted MCP client registration.</summary>
    public class McpClientRegistration
    {
        /// <summary>32 lowercase hex characters; the client id is <c>amc_</c> + this value.</summary>
        public string RegistrationId { get; set; } = string.Empty;

        /// <summary>The registering tenant (lowercase GUID) — the only tenant the client can sign in to.</summary>
        public string TenantId { get; set; } = string.Empty;

        /// <summary>Display name the Tenant Admin chose.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>The exact callback URL (https, or http on loopback).</summary>
        public string RedirectUri { get; set; } = string.Empty;

        public string CreatedBy { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }
}

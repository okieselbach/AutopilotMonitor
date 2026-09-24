using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>One self-hosted MCP client registration as the portal lists it.</summary>
    // Declaration order == wire order.
    public class McpClientRegistrationItem
    {
        public string RegistrationId { get; set; } = default!;

        /// <summary>The OAuth client id the self-hosted client is configured with: <c>amc_</c> + registration id.</summary>
        public string ClientId { get; set; } = default!;

        public string Name { get; set; } = default!;
        public string RedirectUri { get; set; } = default!;
        public string CreatedBy { get; set; } = default!;
        public DateTime CreatedUtc { get; set; }
    }

    /// <summary>Response of GET tenants/{tenantId}/mcp-client-registrations.</summary>
    // Declaration order == wire order.
    public class McpClientRegistrationListResponse : IApiResponse
    {
        /// <summary>Whether the platform currently accepts self-hosted client registrations (operator switch).</summary>
        public bool Enabled { get; set; }

        /// <summary>How many registrations a tenant may hold.</summary>
        public int MaxRegistrations { get; set; }

        /// <summary>The MCP server URL the self-hosted client connects to.</summary>
        public string ServerUrl { get; set; } = default!;

        public IReadOnlyList<McpClientRegistrationItem> Registrations { get; set; } = default!;
    }

    /// <summary>Body of POST tenants/{tenantId}/mcp-client-registrations.</summary>
    public class CreateMcpClientRegistrationRequest : IApiRequest
    {
        /// <summary>Display name, 1-64 characters.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>The client's exact OAuth callback URL: https, or http on localhost / 127.0.0.1; no query, fragment or wildcard.</summary>
        public string RedirectUri { get; set; } = string.Empty;
    }

    /// <summary>Response of POST tenants/{tenantId}/mcp-client-registrations: the stored registration.</summary>
    // Declaration order == wire order.
    public class CreateMcpClientRegistrationResponse : IApiResponse
    {
        public McpClientRegistrationItem Registration { get; set; } = default!;
    }

    /// <summary>
    /// Response of the anonymous GET mcp/client-registrations/{registrationId} — the MCP server's OAuth proxy
    /// resolves a client id <c>amc_&lt;registrationId&gt;</c> through it. Carries nothing secret: the tenant id
    /// and the callback the registration binds the flow to.
    /// </summary>
    // Declaration order == wire order.
    public class McpClientRegistrationLookupResponse : IApiResponse
    {
        public string RegistrationId { get; set; } = default!;
        public string TenantId { get; set; } = default!;
        public string RedirectUri { get; set; } = default!;
        public string Name { get; set; } = default!;
    }
}

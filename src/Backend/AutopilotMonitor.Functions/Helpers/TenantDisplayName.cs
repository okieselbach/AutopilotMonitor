using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// How a message names a tenant to people: the company name, else the domain, else the tenant id.
/// One chain for the push surfaces and the SLA alerts, so a tenant reads the same on every channel.
/// </summary>
public static class TenantDisplayName
{
    public static string Of(TenantConfiguration? config, string tenantId)
    {
        if (config != null)
        {
            if (!string.IsNullOrWhiteSpace(config.CompanyName)) return config.CompanyName;
            if (!string.IsNullOrWhiteSpace(config.DomainName)) return config.DomainName;
        }
        return tenantId;
    }
}

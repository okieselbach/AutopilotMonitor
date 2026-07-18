using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Services;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the endpoint-migration serving contract: the shared allowlist rules
/// (<see cref="AgentEndpointMigrationRules"/>, used verbatim by the agent too) and the
/// backend-side per-tenant resolution (<see cref="GetAgentConfigFunction.ResolveMigrateTarget"/> —
/// tenant override wins, empty override pins, invalid values are never served).
/// </summary>
public class AgentEndpointMigrationTests
{
    private const string TenantA = "11111111-2222-3333-4444-555555555555";
    private const string TenantB = "66666666-7777-8888-9999-000000000000";
    private const string UsUrl = "https://autopilotmonitor-api-us.azurewebsites.net";
    private const string EuUrl = "https://autopilotmonitor-api-eu.azurewebsites.net";

    // ── Shared validation rules ─────────────────────────────────────────────

    [Theory]
    [InlineData("https://autopilotmonitor-api-us.azurewebsites.net", "https://autopilotmonitor-api-us.azurewebsites.net")]
    [InlineData("https://AutopilotMonitor-API-US.AzureWebsites.NET/", "https://autopilotmonitor-api-us.azurewebsites.net")]
    [InlineData("  https://x.azurewebsites.net  ", "https://x.azurewebsites.net")]
    public void TryNormalizeTarget_accepts_and_normalizes(string candidate, string expected)
    {
        Assert.True(AgentEndpointMigrationRules.TryNormalizeTarget(candidate, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://x.azurewebsites.net")]          // https only
    [InlineData("https://attacker.example.com")]        // not allowlisted
    [InlineData("https://evil-azurewebsites.net")]      // label-boundary bypass attempt
    [InlineData("https://.azurewebsites.net")]          // bare suffix
    [InlineData("https://x.azurewebsites.net:8443")]    // non-default port
    [InlineData("https://u:p@x.azurewebsites.net")]     // userinfo
    [InlineData("https://x.azurewebsites.net/api")]     // path
    [InlineData("https://x.azurewebsites.net?a=1")]     // query
    [InlineData("https://x.azurewebsites.net#frag")]    // fragment
    [InlineData("ftp://x.azurewebsites.net")]
    [InlineData("not a url")]
    public void TryNormalizeTarget_rejects(string? candidate)
    {
        Assert.False(AgentEndpointMigrationRules.TryNormalizeTarget(candidate!, out var normalized));
        Assert.Null(normalized);
    }

    [Fact]
    public void TryNormalizeTarget_rejects_overlong_candidate()
    {
        var candidate = "https://" + new string('a', AgentEndpointMigrationRules.MaxTargetLength) + ".azurewebsites.net";
        Assert.False(AgentEndpointMigrationRules.TryNormalizeTarget(candidate, out _));
    }

    [Fact]
    public void IsEffectiveMigration_rejects_target_equal_to_current()
    {
        Assert.False(AgentEndpointMigrationRules.IsEffectiveMigration(EuUrl, EuUrl + "/", out var target));
        Assert.Null(target);

        Assert.True(AgentEndpointMigrationRules.IsEffectiveMigration(UsUrl, EuUrl, out target));
        Assert.Equal(UsUrl, target);
    }

    // ── Backend resolution: global vs per-tenant override ──────────────────

    [Fact]
    public void No_configuration_resolves_to_null()
    {
        var config = new AdminConfiguration();

        Assert.Null(GetAgentConfigFunction.ResolveMigrateTarget(config, TenantA, out var rejected));
        Assert.Null(rejected);
    }

    [Fact]
    public void Global_target_applies_to_every_tenant()
    {
        var config = new AdminConfiguration { AgentMigrateApiBaseUrl = UsUrl };

        Assert.Equal(UsUrl, GetAgentConfigFunction.ResolveMigrateTarget(config, TenantA, out _));
        Assert.Equal(UsUrl, GetAgentConfigFunction.ResolveMigrateTarget(config, TenantB, out _));
    }

    [Fact]
    public void Tenant_override_wins_over_global()
    {
        var config = new AdminConfiguration
        {
            AgentMigrateApiBaseUrl = UsUrl,
            AgentMigrateTenantOverridesJson = $"{{\"{TenantA}\": \"{EuUrl}\"}}"
        };

        Assert.Equal(EuUrl, GetAgentConfigFunction.ResolveMigrateTarget(config, TenantA, out _));
        Assert.Equal(UsUrl, GetAgentConfigFunction.ResolveMigrateTarget(config, TenantB, out _));
    }

    [Fact]
    public void Empty_tenant_override_pins_tenant_against_global_target()
    {
        var config = new AdminConfiguration
        {
            AgentMigrateApiBaseUrl = UsUrl,
            AgentMigrateTenantOverridesJson = $"{{\"{TenantA}\": \"\"}}"
        };

        Assert.Null(GetAgentConfigFunction.ResolveMigrateTarget(config, TenantA, out var rejected));
        Assert.Null(rejected); // pinned, not rejected
        Assert.Equal(UsUrl, GetAgentConfigFunction.ResolveMigrateTarget(config, TenantB, out _));
    }

    [Fact]
    public void Tenant_override_key_is_case_insensitive()
    {
        var config = new AdminConfiguration
        {
            AgentMigrateTenantOverridesJson = $"{{\"{TenantA.ToUpperInvariant()}\": \"{UsUrl}\"}}"
        };

        Assert.Equal(UsUrl, GetAgentConfigFunction.ResolveMigrateTarget(config, TenantA, out _));
    }

    [Fact]
    public void Invalid_configured_target_is_never_served_and_reported_as_rejected()
    {
        var config = new AdminConfiguration { AgentMigrateApiBaseUrl = "https://attacker.example.com" };

        Assert.Null(GetAgentConfigFunction.ResolveMigrateTarget(config, TenantA, out var rejected));
        Assert.Equal("https://attacker.example.com", rejected);
    }

    [Fact]
    public void Malformed_overrides_json_degrades_to_global_target()
    {
        var config = new AdminConfiguration
        {
            AgentMigrateApiBaseUrl = UsUrl,
            AgentMigrateTenantOverridesJson = "{not json"
        };

        Assert.Equal(UsUrl, GetAgentConfigFunction.ResolveMigrateTarget(config, TenantA, out _));
    }

    [Fact]
    public void Served_target_is_normalized()
    {
        var config = new AdminConfiguration
        {
            AgentMigrateApiBaseUrl = "https://AutopilotMonitor-API-US.azurewebsites.net/"
        };

        Assert.Equal(UsUrl, GetAgentConfigFunction.ResolveMigrateTarget(config, TenantA, out _));
    }
}

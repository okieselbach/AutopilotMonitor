using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Default-value + round-trip tests for <see cref="AdminConfiguration.AutoApproveNewTenants"/>.
/// The flag gates the tenant auto-approve queue worker — defaulting to false keeps new tenant
/// signups on manual Global Admin approval until the operator deliberately opts in.
/// </summary>
public class AdminConfigurationAutoApproveNewTenantsTests
{
    [Fact]
    public void AutoApproveNewTenants_defaults_to_false_on_new_config()
    {
        var cfg = new AdminConfiguration();
        Assert.False(cfg.AutoApproveNewTenants);
    }

    [Fact]
    public void AutoApproveNewTenants_defaults_to_false_on_CreateDefault()
    {
        // CreateDefault() is called when no admin-config row exists yet — if it somehow
        // enabled auto-approve, tenants would activate without any operator opt-in.
        var cfg = AdminConfiguration.CreateDefault();
        Assert.False(cfg.AutoApproveNewTenants);
    }

    [Fact]
    public void AutoApproveNewTenants_true_persists_on_the_config()
    {
        var cfg = new AdminConfiguration { AutoApproveNewTenants = true };
        Assert.True(cfg.AutoApproveNewTenants);
    }
}

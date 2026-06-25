using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Covers the delegated ("MSP") config/all transformation seams in
/// <see cref="GetAllTenantConfigurationsFunction"/>. The HTTP entry point is intentionally not exercised
/// (mocking HttpRequestData + the middleware chain is more setup than the test is worth — same rationale as
/// <see cref="GetAllBlockedDevicesFunctionTests"/>); the security-relevant logic is the pure subset/redaction
/// transform, which is tested directly.
///
/// Guards two invariants for a delegated caller: (1) only managed tenants that actually have a config row
/// are returned (no phantom/empty entries), and (2) per-tenant secrets are ALWAYS redacted (a delegated
/// admin is never a Global Admin, so it must never receive SAS/webhook/header secrets for a managed tenant).
/// </summary>
public class GetAllTenantConfigurationsFunctionTests
{
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private const string TenantC = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public void ExistingManagedConfigs_DropsTenantsWithoutConfigRow()
    {
        var reads = new (TenantConfiguration config, bool exists)[]
        {
            (new TenantConfiguration { TenantId = TenantB }, true),
            (new TenantConfiguration { TenantId = TenantC }, false), // no config row → must be dropped
        };

        var result = GetAllTenantConfigurationsFunction.ExistingManagedConfigs(reads);

        Assert.Single(result);
        Assert.Equal(TenantB, result[0].TenantId);
    }

    [Fact]
    public void ExistingManagedConfigs_AllExist_KeepsAll()
    {
        var reads = new (TenantConfiguration config, bool exists)[]
        {
            (new TenantConfiguration { TenantId = TenantB }, true),
            (new TenantConfiguration { TenantId = TenantC }, true),
        };

        var result = GetAllTenantConfigurationsFunction.ExistingManagedConfigs(reads);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ExistingManagedConfigs_NoneExist_Empty()
    {
        var reads = new (TenantConfiguration config, bool exists)[]
        {
            (new TenantConfiguration { TenantId = TenantB }, false),
        };

        Assert.Empty(GetAllTenantConfigurationsFunction.ExistingManagedConfigs(reads));
    }

    [Fact]
    public void DelegatedBareArrayView_RedactsSecretsButKeepsNonSecrets()
    {
        var managed = new[]
        {
            new TenantConfiguration
            {
                TenantId = TenantB,
                DiagnosticsBlobSasUrl = "https://acct.blob.core.windows.net/c?sig=secret",
                WebhookUrl = "https://hooks.example/abc",
                DomainName = "contoso.com",
            },
        };

        var view = GetAllTenantConfigurationsFunction.DelegatedBareArrayView(managed);

        var v = Assert.Single(view);
        Assert.Equal(Constants.RedactedSecretPlaceholder, v.DiagnosticsBlobSasUrl);
        Assert.Equal(Constants.RedactedSecretPlaceholder, v.WebhookUrl);
        Assert.Equal("contoso.com", v.DomainName);                 // non-secret preserved
        // original instance is never mutated (redaction returns a copy)
        Assert.Equal("https://hooks.example/abc", managed[0].WebhookUrl);
    }

    [Fact]
    public void DelegatedBareArrayView_Empty_Empty()
        => Assert.Empty(GetAllTenantConfigurationsFunction.DelegatedBareArrayView(new TenantConfiguration[0]));
}

using System.Collections.Generic;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Resolution matrix for the dual app-registration window (see EntraAppRegistry):
/// legacy unconfigured ⇒ always primary (code deploy is a no-op before the config swap);
/// null homing ⇒ legacy; known ids ⇒ that app; unknown ⇒ primary fallback.
/// </summary>
public class EntraAppRegistryTests
{
    private const string PrimaryId = "886ab5e2-6144-442c-80cc-9b28e0667731";
    private const string LegacyId = "1a400946-62c1-4ab4-aa37-f730ac89704d";

    private static EntraAppRegistry Build(Dictionary<string, string?> settings) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<EntraAppRegistry>.Instance);

    private static EntraAppRegistry DualApp() => Build(new()
    {
        ["EntraId:ClientId"] = PrimaryId,
        ["EntraId:ClientSecret"] = "primary-secret",
        ["EntraId:LegacyClientId"] = LegacyId,
        ["EntraId:LegacyClientSecret"] = "legacy-secret",
    });

    private static TenantConfiguration Tenant(string? homedAppClientId) => new()
    {
        TenantId = "11111111-1111-1111-1111-111111111111",
        HomedAppClientId = homedAppClientId,
    };

    [Fact]
    public void LegacyUnconfigured_AlwaysResolvesPrimary_EvenForNullHoming()
    {
        var registry = Build(new()
        {
            ["EntraId:ClientId"] = PrimaryId,
            ["EntraId:ClientSecret"] = "primary-secret",
        });

        Assert.False(registry.LegacyConfigured);
        Assert.Equal(PrimaryId, registry.ResolveForTenant(Tenant(null)).ClientId);
        Assert.Equal(PrimaryId, registry.ResolveForTenant(null).ClientId);
        Assert.False(registry.ResolveForTenant(Tenant(null)).IsLegacy);
    }

    [Fact]
    public void NullHoming_ResolvesLegacy()
    {
        var resolved = DualApp().ResolveForTenant(Tenant(null));
        Assert.True(resolved.IsLegacy);
        Assert.Equal(LegacyId, resolved.ClientId);
        Assert.Equal("legacy-secret", resolved.ClientSecret);
    }

    [Fact]
    public void MissingConfigRow_ResolvesLegacy_SameAsNullHoming()
    {
        Assert.True(DualApp().ResolveForTenant(null).IsLegacy);
    }

    [Fact]
    public void PrimaryHoming_ResolvesPrimary_CaseAndBraceInsensitive()
    {
        var registry = DualApp();
        Assert.False(registry.ResolveForTenant(Tenant(PrimaryId)).IsLegacy);
        Assert.False(registry.ResolveForTenant(Tenant(PrimaryId.ToUpperInvariant())).IsLegacy);
        Assert.False(registry.ResolveForTenant(Tenant("{" + PrimaryId + "}")).IsLegacy);
    }

    [Fact]
    public void LegacyHoming_ResolvesLegacy()
    {
        var resolved = DualApp().ResolveForTenant(Tenant(LegacyId));
        Assert.True(resolved.IsLegacy);
        Assert.Equal("legacy-secret", resolved.ClientSecret);
    }

    [Fact]
    public void UnknownHoming_FallsBackToPrimary()
    {
        var resolved = DualApp().ResolveForTenant(Tenant("99999999-9999-9999-9999-999999999999"));
        Assert.False(resolved.IsLegacy);
        Assert.Equal(PrimaryId, resolved.ClientId);
    }

    [Fact]
    public void MalformedLegacyClientId_TreatedAsUnconfigured()
    {
        var registry = Build(new()
        {
            ["EntraId:ClientId"] = PrimaryId,
            ["EntraId:LegacyClientId"] = "not-a-guid",
            ["EntraId:LegacyClientSecret"] = "legacy-secret",
        });

        Assert.False(registry.LegacyConfigured);
        Assert.False(registry.ResolveForTenant(Tenant(null)).IsLegacy);
    }

    [Theory]
    [InlineData(PrimaryId, true)]
    [InlineData("api://" + PrimaryId, true)]
    [InlineData("API://" + PrimaryId, true)]
    [InlineData(LegacyId, false)]
    [InlineData("api://" + LegacyId, false)]
    [InlineData("https://graph.microsoft.com", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsPrimary_MatchesBareAndApiPrefixedAudiences(string? audience, bool expected)
    {
        Assert.Equal(expected, DualApp().IsPrimary(audience));
    }

    [Theory]
    [InlineData("api://" + LegacyId, LegacyId)]
    [InlineData(LegacyId, LegacyId)]
    [InlineData("API://886AB5E2-6144-442C-80CC-9B28E0667731", PrimaryId)]
    [InlineData("https://graph.microsoft.com", null)] // non-GUID audience must never persist as provenance
    [InlineData("api://not-a-guid", null)]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    public void NormalizeAudience_StripsApiPrefixAndRequiresGuid(string? audience, string? expected)
    {
        Assert.Equal(expected, EntraAppRegistry.NormalizeAudience(audience));
    }
}

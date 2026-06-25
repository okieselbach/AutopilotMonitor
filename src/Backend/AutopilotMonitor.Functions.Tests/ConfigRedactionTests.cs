using System.Reflection;
using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Guards the GlobalReader secret-redaction contract on the config models. The redactors are
/// deny-lists (RedactedCopyForReader masks an explicit set of secret fields); these tests fail
/// if a NEW secret-looking string field is added to a model but not to its redactor — closing the
/// drift gap that would otherwise leak a fresh SAS/webhook/API-key field to a read-only reader.
/// </summary>
public class ConfigRedactionTests
{
    // A string property whose NAME matches one of these fragments is considered secret-bearing.
    private static readonly string[] SecretNameFragments =
        { "SasUrl", "WebhookUrl", "ApiKey", "CustomHeadersJson", "Secret", "Password", "BotToken" };

    private static bool IsSecretName(string name)
        => SecretNameFragments.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<PropertyInfo> WritableStringProps(Type t)
        => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanRead && p.CanWrite);

    [Fact]
    public void AdminConfiguration_RedactsEverySecretLookingStringField()
    {
        var cfg = new AdminConfiguration();
        foreach (var p in WritableStringProps(typeof(AdminConfiguration)))
            p.SetValue(cfg, "POPULATED_SECRET_VALUE");

        var redacted = cfg.RedactedCopyForReader();

        foreach (var p in WritableStringProps(typeof(AdminConfiguration)).Where(p => IsSecretName(p.Name)))
        {
            Assert.Equal(Constants.RedactedSecretPlaceholder, (string?)p.GetValue(redacted));
        }
    }

    [Fact]
    public void TenantConfiguration_RedactsEverySecretLookingStringField()
    {
        var cfg = new TenantConfiguration();
        foreach (var p in WritableStringProps(typeof(TenantConfiguration)))
            p.SetValue(cfg, "POPULATED_SECRET_VALUE");

        var redacted = cfg.RedactedCopyForReader();

        foreach (var p in WritableStringProps(typeof(TenantConfiguration)).Where(p => IsSecretName(p.Name)))
        {
            Assert.Equal(Constants.RedactedSecretPlaceholder, (string?)p.GetValue(redacted));
        }
    }

    [Fact]
    public void AdminConfiguration_RedactionLeavesNonSecretsAndOriginalIntact()
    {
        var cfg = new AdminConfiguration { NvdApiKey = "real-key", UpdatedBy = "admin@contoso.com" };

        var redacted = cfg.RedactedCopyForReader();

        Assert.Equal(Constants.RedactedSecretPlaceholder, redacted.NvdApiKey);
        Assert.Equal("admin@contoso.com", redacted.UpdatedBy);           // non-secret preserved
        Assert.Equal("real-key", cfg.NvdApiKey);                          // original NOT mutated
    }

    [Fact]
    public void TenantConfiguration_RedactionLeavesNonSecretsAndOriginalIntact()
    {
        var cfg = new TenantConfiguration
        {
            DiagnosticsBlobSasUrl = "https://acct.blob.core.windows.net/c?sig=secret",
            WebhookUrl = "https://hooks.example/abc",
            DomainName = "contoso.com",
        };

        var redacted = cfg.RedactedCopyForReader();

        Assert.Equal(Constants.RedactedSecretPlaceholder, redacted.DiagnosticsBlobSasUrl);
        Assert.Equal(Constants.RedactedSecretPlaceholder, redacted.WebhookUrl);
        Assert.Equal("contoso.com", redacted.DomainName);                 // non-secret preserved
        Assert.Equal("https://acct.blob.core.windows.net/c?sig=secret", cfg.DiagnosticsBlobSasUrl); // original intact
    }

    // ── Save-guard: a redacted view round-tripped on save must NOT overwrite real secrets ──

    [Fact]
    public void TenantConfiguration_RestoreRedactedSecrets_RecoversRealValuesFromExisting()
    {
        var existing = new TenantConfiguration
        {
            DiagnosticsBlobSasUrl = "https://acct.blob.core.windows.net/c?sig=real",
            TeamsWebhookUrl = "https://teams.example/real",
            WebhookUrl = "https://hooks.example/real",
            WebhookCustomHeadersJson = "{\"X-Api-Key\":\"real\"}",
            DomainName = "contoso.com",
        };

        // Simulate the read-only reader view being saved back unchanged.
        var incoming = existing.RedactedCopyForReader();
        Assert.Equal(Constants.RedactedSecretPlaceholder, incoming.WebhookUrl); // sanity: it really is redacted

        incoming.RestoreRedactedSecretsFrom(existing);

        Assert.Equal(existing.DiagnosticsBlobSasUrl, incoming.DiagnosticsBlobSasUrl);
        Assert.Equal(existing.TeamsWebhookUrl, incoming.TeamsWebhookUrl);
        Assert.Equal(existing.WebhookUrl, incoming.WebhookUrl);
        Assert.Equal(existing.WebhookCustomHeadersJson, incoming.WebhookCustomHeadersJson);
    }

    [Fact]
    public void TenantConfiguration_RestoreRedactedSecrets_KeepsGenuineNewValues()
    {
        // A real edit (non-placeholder) must be preserved — restore only swaps the sentinel.
        var existing = new TenantConfiguration { WebhookUrl = "https://hooks.example/old" };
        var incoming = new TenantConfiguration
        {
            WebhookUrl = "https://hooks.example/NEW",      // genuine change
            DiagnosticsBlobSasUrl = Constants.RedactedSecretPlaceholder, // untouched in UI
        };
        existing.DiagnosticsBlobSasUrl = "https://acct.blob.core.windows.net/c?sig=old";

        incoming.RestoreRedactedSecretsFrom(existing);

        Assert.Equal("https://hooks.example/NEW", incoming.WebhookUrl);                 // genuine edit kept
        Assert.Equal("https://acct.blob.core.windows.net/c?sig=old", incoming.DiagnosticsBlobSasUrl); // placeholder restored
    }

    [Fact]
    public void Redaction_LeavesEmptySecretsEmpty()
    {
        // An unset secret stays empty (UI shows "not configured", not a misleading "***REDACTED***").
        var admin = new AdminConfiguration { NvdApiKey = "" }.RedactedCopyForReader();
        Assert.Equal("", admin.NvdApiKey);

        var tenant = new TenantConfiguration { WebhookUrl = "" }.RedactedCopyForReader();
        Assert.Equal("", tenant.WebhookUrl);
    }

    // ── Config GET secret-clearance decision (GetTenantConfigurationFunction.CanViewSecrets) ──
    // Redact-by-default: only GA (any tenant) or the tenant's OWN admin sees unredacted secrets.

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void CanViewSecrets_GlobalAdmin_True()
        => Assert.True(GetTenantConfigurationFunction.CanViewSecrets(
            new RequestContext { IsGlobalAdmin = true, TenantId = TenantA, TargetTenantId = TenantB }));

    [Fact]
    public void CanViewSecrets_OwnTenantAdmin_True()
        => Assert.True(GetTenantConfigurationFunction.CanViewSecrets(
            new RequestContext { IsTenantAdmin = true, TenantId = TenantA, TargetTenantId = TenantA }));

    [Fact]
    public void CanViewSecrets_GlobalReaderCrossTenant_False()
        => Assert.False(GetTenantConfigurationFunction.CanViewSecrets(
            new RequestContext { IsGlobalReader = true, TenantId = TenantA, TargetTenantId = TenantB }));

    [Fact]
    public void CanViewSecrets_TenantAdminViewingForeignTenant_False()
        // Admin of A, but target is B (e.g. reached via delegation or global scope) ⇒ redacted.
        => Assert.False(GetTenantConfigurationFunction.CanViewSecrets(
            new RequestContext { IsTenantAdmin = true, TenantId = TenantA, TargetTenantId = TenantB }));

    [Fact]
    public void CanViewSecrets_DelegatedReaderCrossTenant_False()
        // Delegated reader of B who is ALSO admin of their own home tenant A, viewing B ⇒ redacted.
        => Assert.False(GetTenantConfigurationFunction.CanViewSecrets(
            new RequestContext
            {
                IsDelegatedReader = true,
                IsTenantAdmin = true,        // admin of their OWN tenant A…
                TenantId = TenantA,
                TargetTenantId = TenantB,    // …but viewing B
            }));

    [Fact]
    public void CanViewSecrets_PureDelegatedReader_False()
        => Assert.False(GetTenantConfigurationFunction.CanViewSecrets(
            new RequestContext { IsDelegatedReader = true, TenantId = TenantA, TargetTenantId = TenantB }));
}

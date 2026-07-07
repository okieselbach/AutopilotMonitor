using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Server-side keep-list projection for the paginated config/all surface. This is
/// the backend security boundary that keeps tenant secrets off the wire even when a
/// caller supplies fields= — requested keys are intersected with the keep-list, never
/// widened. tenantId is always present.
/// </summary>
public class TenantConfigProjectionTests
{
    private static TenantConfiguration FullConfig() => new()
    {
        TenantId = "contoso-tenant-id",
        DomainName = "contoso.example.com",
        PlanTier = "pro",
        Disabled = true,
        DisabledReason = "suspended",
        OnboardedBy = "alice@contoso.example.com",
        DataRetentionDays = 180,
        // secret-bearing fields that must never be projected:
        TeamsWebhookUrl = "https://contoso.webhook.office.com/secret",
        DiagnosticsBlobSasUrl = "https://blob.core.windows.net/c?sig=SECRETSAS",
    };

    private static readonly string[] AllSafeKeys =
    {
        "tenantId", "domainName", "planTier", "trialExpiresUtc", "trialConsumed",
        "disabled", "disabledReason",
        "onboardedAt", "onboardedBy", "lastUpdated", "dataRetentionDays",
    };

    [Fact]
    public void Project_with_null_fields_emits_every_safe_field_and_no_secret()
    {
        var p = TenantConfigProjection.Project(FullConfig(), requested: null);

        foreach (var key in AllSafeKeys) Assert.True(p.ContainsKey(key), $"missing {key}");
        Assert.Equal(AllSafeKeys.Length, p.Count);

        // Belt-and-suspenders: no projected value carries a secret marker.
        var serialized = System.Text.Json.JsonSerializer.Serialize(p);
        Assert.DoesNotContain("sig=", serialized);
        Assert.DoesNotContain("webhook", serialized);
        Assert.DoesNotContain("SECRET", serialized);
    }

    [Fact]
    public void Project_honours_a_requested_subset()
    {
        var requested = TenantConfigProjection.ParseFields("domainName,planTier");
        var p = TenantConfigProjection.Project(FullConfig(), requested);

        Assert.Equal(new[] { "domainName", "planTier", "tenantId" }, p.Keys.OrderBy(k => k));
    }

    [Fact]
    public void Project_always_includes_tenantId_even_when_not_requested()
    {
        var requested = TenantConfigProjection.ParseFields("domainName");
        var p = TenantConfigProjection.Project(FullConfig(), requested);

        Assert.True(p.ContainsKey("tenantId"));
        Assert.Equal("contoso-tenant-id", p["tenantId"]);
    }

    [Fact]
    public void Project_silently_drops_unknown_or_secret_field_requests()
    {
        // A caller cannot widen the projection to a secret (or any non-safe key).
        var requested = TenantConfigProjection.ParseFields("tenantId,teamsWebhookUrl,bogus");
        var p = TenantConfigProjection.Project(FullConfig(), requested);

        Assert.Equal(new[] { "tenantId" }, p.Keys);
        Assert.DoesNotContain("teamsWebhookUrl", p.Keys);
    }

    [Fact]
    public void ParseFields_is_case_insensitive_and_tolerates_whitespace_and_blanks()
    {
        var requested = TenantConfigProjection.ParseFields(" TENANTID , , DomainName ");
        var p = TenantConfigProjection.Project(FullConfig(), requested);

        Assert.Equal(new[] { "domainName", "tenantId" }, p.Keys.OrderBy(k => k));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",, ,")]
    public void ParseFields_returns_null_for_empty_input(string? fields)
    {
        Assert.Null(TenantConfigProjection.ParseFields(fields));
    }
}

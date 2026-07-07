using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="GetTenantFeatureFlagsFunction.BuildPayload"/> projection.
///
/// CORRECTNESS GUARD: <c>/api/config/{tenantId}/feature-flags</c> is gated MemberRead, so every
/// field in this payload is implicitly readable by Operators and Viewers. A regression that
/// adds an admin-sensitive field here (webhook URLs, SAS tokens, admin lists, …) would expose
/// it to every tenant member. These tests lock the field set + nullable-default semantics.
/// </summary>
public class GetTenantFeatureFlagsPayloadTests
{
    private static readonly DateTime Now = new(2026, 7, 7, 12, 0, 0, DateTimeKind.Utc);

    private static JsonElement Serialize(TenantConfiguration config)
    {
        var payload = GetTenantFeatureFlagsFunction.BuildPayload(config, Now);
        var json = JsonSerializer.Serialize(payload);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void Payload_HasExpectedFieldSet_AndNothingElse()
    {
        var element = Serialize(new TenantConfiguration());

        var fieldNames = element.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();

        // CORRECTNESS GUARD: adding a new field to BuildPayload requires updating this test
        // — and that forces a deliberate code review of whether the new field is non-sensitive.
        Assert.Equal(new[]
        {
            "bootstrapTokenEnabled",
            "edition",
            "enableIntegrityBypassAnalyzer",
            "enableSoftwareInventoryAnalyzer",
            "entitlements",
            "isTrial",
            "showScriptOutput",
            "trialAvailable",
            "trialExpiresUtc",
            "unrestrictedMode",
            "validateAutopilotDevice",
        }, fieldNames);
    }

    [Fact]
    public void Payload_PassesThroughExplicitFieldValues()
    {
        var config = new TenantConfiguration
        {
            BootstrapTokenEnabled = true,
            ValidateAutopilotDevice = true,
            ShowScriptOutput = false,
            EnableSoftwareInventoryAnalyzer = true,
            EnableIntegrityBypassAnalyzer = false,
            UnrestrictedMode = true,
        };

        var element = Serialize(config);

        Assert.True(element.GetProperty("bootstrapTokenEnabled").GetBoolean());
        Assert.True(element.GetProperty("validateAutopilotDevice").GetBoolean());
        Assert.False(element.GetProperty("showScriptOutput").GetBoolean());
        Assert.True(element.GetProperty("enableSoftwareInventoryAnalyzer").GetBoolean());
        Assert.False(element.GetProperty("enableIntegrityBypassAnalyzer").GetBoolean());
        Assert.True(element.GetProperty("unrestrictedMode").GetBoolean());
    }

    [Fact]
    public void Payload_NullableFlags_FallBackToAgentSideDefaults()
    {
        // ShowScriptOutput / EnableSoftwareInventoryAnalyzer / EnableIntegrityBypassAnalyzer are
        // nullable on the model — null means "use the agent-side default". The endpoint flattens
        // those nulls so the UI does not have to re-derive defaults.
        var config = new TenantConfiguration
        {
            ShowScriptOutput = null,
            EnableSoftwareInventoryAnalyzer = null,
            EnableIntegrityBypassAnalyzer = null,
        };

        var element = Serialize(config);

        Assert.True(element.GetProperty("showScriptOutput").GetBoolean(), "ShowScriptOutput default is true");
        Assert.False(element.GetProperty("enableSoftwareInventoryAnalyzer").GetBoolean(), "SoftwareInventory default is false");
        Assert.True(element.GetProperty("enableIntegrityBypassAnalyzer").GetBoolean(), "IntegrityBypass default is true");
    }

    [Fact]
    public void Payload_DoesNotExposeAdminSensitiveFields()
    {
        // Sentinel: load up the model with values for known-admin-only fields and assert none
        // of those fields leak into the payload. If a future field name lands in the model that
        // happens to collide with an admin-only one here, this test will fail and force review.
        var config = new TenantConfiguration
        {
            DiagnosticsBlobSasUrl = "https://example.blob.core.windows.net/diagnostics?sv=secret-sas-token",
            TeamsWebhookUrl = "https://outlook.office.com/webhook/secret-team-hook",
            WebhookUrl = "https://hooks.example.com/services/secret-generic-hook",
        };

        var json = JsonSerializer.Serialize(GetTenantFeatureFlagsFunction.BuildPayload(config, Now));

        Assert.DoesNotContain("secret-sas-token", json);
        Assert.DoesNotContain("secret-team-hook", json);
        Assert.DoesNotContain("secret-generic-hook", json);
        Assert.DoesNotContain("diagnosticsBlobSasUrl", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("teamsWebhookUrl", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("webhookUrl", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── Edition / trial surface ─────────────────────────────────────────────

    [Fact]
    public void Payload_CommunityDefault_EditionAndTrialAvailability()
    {
        var element = Serialize(new TenantConfiguration()); // PlanTier default "free" → Community

        Assert.Equal("community", element.GetProperty("edition").GetString());
        Assert.False(element.GetProperty("isTrial").GetBoolean());
        Assert.True(element.GetProperty("trialAvailable").GetBoolean());
        Assert.Equal(90, element.GetProperty("entitlements").GetProperty("retentionCapDays").GetInt32());
        Assert.False(element.GetProperty("entitlements").GetProperty("delegatedAdminAllowed").GetBoolean());
        Assert.Equal("community", element.GetProperty("entitlements").GetProperty("mcpUsagePlan").GetString());
    }

    [Fact]
    public void Payload_EnterpriseTier_NotTrial()
    {
        var element = Serialize(new TenantConfiguration { PlanTier = "enterprise" });

        Assert.Equal("enterprise", element.GetProperty("edition").GetString());
        Assert.False(element.GetProperty("isTrial").GetBoolean());
        Assert.False(element.GetProperty("trialAvailable").GetBoolean());
        Assert.Equal(365, element.GetProperty("entitlements").GetProperty("retentionCapDays").GetInt32());
        Assert.Equal(150, element.GetProperty("entitlements").GetProperty("userRateLimitPerMinute").GetInt32());
        Assert.True(element.GetProperty("entitlements").GetProperty("delegatedAdminAllowed").GetBoolean());
    }

    [Fact]
    public void Payload_ActiveTrial_IsTrialWithExpiry()
    {
        var expiry = Now.AddDays(10);
        var element = Serialize(new TenantConfiguration { TrialExpiresUtc = expiry, TrialConsumed = true });

        Assert.Equal("enterprise", element.GetProperty("edition").GetString());
        Assert.True(element.GetProperty("isTrial").GetBoolean());
        Assert.False(element.GetProperty("trialAvailable").GetBoolean());
        Assert.Equal(expiry, element.GetProperty("trialExpiresUtc").GetDateTime());
    }

    [Fact]
    public void Payload_ExpiredTrial_DegradesToCommunity_NoTrialAvailable()
    {
        var element = Serialize(new TenantConfiguration { TrialExpiresUtc = Now.AddDays(-1), TrialConsumed = true });

        Assert.Equal("community", element.GetProperty("edition").GetString());
        Assert.False(element.GetProperty("isTrial").GetBoolean());
        Assert.False(element.GetProperty("trialAvailable").GetBoolean(), "consumed trial must not be offered again");
    }
}

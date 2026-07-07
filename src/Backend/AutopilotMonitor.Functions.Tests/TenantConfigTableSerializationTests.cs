using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Shared.Models;
using Azure.Data.Tables;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Store↔Map roundtrip for the tenant-config table serialization, focused on the plan/trial
/// fields (project rule "table-serialization": every model field must appear in BOTH
/// ConvertToTenantTableEntity and ConvertFromTenantTableEntity). Includes legacy-row shapes
/// (rows written before the trial columns existed) which must map to safe defaults.
/// </summary>
public class TenantConfigTableSerializationTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void Roundtrip_PlanAndTrialFields_SurviveStoreAndMap()
    {
        var expires = new DateTime(2026, 8, 6, 10, 30, 0, DateTimeKind.Utc);
        var started = new DateTime(2026, 7, 7, 10, 30, 0, DateTimeKind.Utc);
        var config = new TenantConfiguration
        {
            TenantId = TenantId,
            DomainName = "contoso.com",
            UpdatedBy = "admin@contoso.com",
            PlanTier = "enterprise",
            TrialExpiresUtc = expires,
            TrialStartedUtc = started,
            TrialConsumed = true,
            TrialGrantedBy = "alice@contoso.com"
        };

        var entity = TableConfigRepository.ConvertToTenantTableEntity(config);
        var mapped = TableConfigRepository.ConvertFromTenantTableEntity(entity);

        Assert.Equal("enterprise", mapped.PlanTier);
        Assert.Equal(expires, mapped.TrialExpiresUtc);
        Assert.Equal(started, mapped.TrialStartedUtc);
        Assert.True(mapped.TrialConsumed);
        Assert.Equal("alice@contoso.com", mapped.TrialGrantedBy);
    }

    [Fact]
    public void Roundtrip_NoTrial_NullsStayNull()
    {
        var config = new TenantConfiguration
        {
            TenantId = TenantId,
            DomainName = "contoso.com",
            UpdatedBy = "admin@contoso.com"
        };

        var mapped = TableConfigRepository.ConvertFromTenantTableEntity(
            TableConfigRepository.ConvertToTenantTableEntity(config));

        Assert.Null(mapped.TrialExpiresUtc);
        Assert.Null(mapped.TrialStartedUtc);
        Assert.False(mapped.TrialConsumed);
        Assert.Null(mapped.TrialGrantedBy);
    }

    [Fact]
    public void Map_LegacyRow_WithoutTrialColumns_DefaultsSafely()
    {
        // A row written before the trial fields existed: no Trial* columns at all, legacy tier.
        var entity = new TableEntity(TenantId, "config")
        {
            { "DomainName", "fabrikam.com" },
            { "PlanTier", "pro" }
        };

        var mapped = TableConfigRepository.ConvertFromTenantTableEntity(entity);

        Assert.Equal("pro", mapped.PlanTier); // legacy value preserved (resolves to Community at read time)
        Assert.Null(mapped.TrialExpiresUtc);
        Assert.False(mapped.TrialConsumed);
        Assert.Null(mapped.TrialGrantedBy);
    }

    [Fact]
    public void Map_LegacyRow_WithoutPlanTier_DefaultsToFree()
    {
        var entity = new TableEntity(TenantId, "config") { { "DomainName", "fabrikam.com" } };
        Assert.Equal("free", TableConfigRepository.ConvertFromTenantTableEntity(entity).PlanTier);
    }
}

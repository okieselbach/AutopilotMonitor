using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Regression tests for the OData filter builders behind the metrics/usage endpoints.
/// These call sites previously interpolated query-string / route input straight into the
/// filter, allowing an injected OR clause to escape the tenant scope and read every tenant's
/// rows (e.g. ruleType="x' or RowKey ge '"). The builders must OData-escape every value.
/// </summary>
public class MetricsFilterInjectionTests
{
    // ---- TableStorageService.BuildRuleStatsFilter (metrics/rule-stats, MemberRead) ----

    [Fact]
    public void BuildRuleStatsFilter_NormalInput_ProducesScopedFilter()
    {
        var filter = TableStorageService.BuildRuleStatsFilter("tenant1", "2024-01-01", "2024-02-01", "analyze");

        Assert.Equal(
            "PartitionKey ge '2024-01-01' and PartitionKey le '2024-02-01' and " +
            "RowKey ge 'tenant1_' and RowKey lt 'tenant1_~' and RuleType eq 'analyze'",
            filter);
    }

    [Fact]
    public void BuildRuleStatsFilter_RuleTypeInjection_StaysInsideLiteral()
    {
        // The reported HIGH-severity payload.
        var filter = TableStorageService.BuildRuleStatsFilter("tenant1", null, null, "x' or RowKey ge '");

        // The tenant scope must survive...
        Assert.Contains("RowKey ge 'tenant1_'", filter);
        Assert.Contains("RowKey lt 'tenant1_~'", filter);
        // ...and the injected quote must be doubled, so the OR can't escape the string literal.
        Assert.Equal(
            "RowKey ge 'tenant1_' and RowKey lt 'tenant1_~' and RuleType eq 'x'' or RowKey ge '''",
            filter);
    }

    [Theory]
    [InlineData("' or RowKey ge '")]   // startDate injection
    public void BuildRuleStatsFilter_StartDateInjection_IsEscaped(string payload)
    {
        var filter = TableStorageService.BuildRuleStatsFilter("tenant1", payload, null, null);

        Assert.Contains("PartitionKey ge ''' or RowKey ge '''", filter);
        Assert.Contains("RowKey ge 'tenant1_'", filter);
    }

    [Fact]
    public void BuildRuleStatsFilter_AllNull_ReturnsNull()
    {
        Assert.Null(TableStorageService.BuildRuleStatsFilter(null, null, null, null));
    }

    // ---- TableUserUsageRepository.BuildUserUsageFilter (metrics/mcp-usage/user/{userId}) ----

    [Fact]
    public void BuildUserUsageFilter_UserIdInjection_StaysInsideLiteral()
    {
        var filter = TableUserUsageRepository.BuildUserUsageFilter("' or PartitionKey ne '", null, null);

        Assert.Equal("PartitionKey eq ''' or PartitionKey ne '''", filter);
    }

    [Fact]
    public void BuildUserUsageFilter_DateInjection_IsEscaped()
    {
        // Date param normalizes hyphens, but a quote must still be doubled.
        var filter = TableUserUsageRepository.BuildUserUsageFilter("user1", "2024' or Date ge '", null);

        Assert.Contains("PartitionKey eq 'user1'", filter);
        Assert.Contains("Date ge '2024'' or Date ge '''", filter);
    }

    [Fact]
    public void BuildTenantUsageFilter_TenantInjection_StaysInsideLiteral()
    {
        var filter = TableUserUsageRepository.BuildTenantUsageFilter("' or TenantId ne '", null, null);

        Assert.Equal("TenantId eq ''' or TenantId ne '''", filter);
    }

    [Fact]
    public void BuildTenantUsageFilter_NoScope_ReturnsNull()
    {
        Assert.Null(TableUserUsageRepository.BuildTenantUsageFilter(null, null, null));
    }
}

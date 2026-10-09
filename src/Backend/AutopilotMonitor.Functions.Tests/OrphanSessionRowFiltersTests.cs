using System;
using System.Linq;
using AutopilotMonitor.Functions.Services.Maintenance;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the per-table filter of the orphan-session sweep to the writer's key shape. A filter
/// that names the wrong side (tenant where the session belongs, or vice versa) would either
/// miss the session's rows or touch another session's — both silently.
/// </summary>
public class OrphanSessionRowFiltersTests
{
    private const string TenantId  = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void Every_filter_table_has_a_filter_naming_both_ids()
    {
        foreach (var table in OrphanSessionRowFilters.Tables)
        {
            var filter = OrphanSessionRowFilters.For(table, TenantId, SessionId);
            Assert.Contains(TenantId, filter);
            Assert.Contains(SessionId, filter);
        }
    }

    [Theory]
    [InlineData(Constants.TableNames.Events)]
    [InlineData(Constants.TableNames.RuleResults)]
    [InlineData(Constants.TableNames.Signals)]
    [InlineData(Constants.TableNames.DecisionTransitions)]
    [InlineData(Constants.TableNames.VulnerabilityReports)]
    public void Session_partition_tables_filter_on_the_composite_partition_key(string table)
    {
        Assert.Equal($"PartitionKey eq '{TenantId}_{SessionId}'", OrphanSessionRowFilters.For(table, TenantId, SessionId));
    }

    [Theory]
    [InlineData(Constants.TableNames.DeviceSnapshot)]
    [InlineData(Constants.TableNames.SessionTimeBreakdowns)]
    public void One_row_per_session_tables_filter_on_tenant_partition_and_session_row_key(string table)
    {
        Assert.Equal($"PartitionKey eq '{TenantId}' and RowKey eq '{SessionId}'", OrphanSessionRowFilters.For(table, TenantId, SessionId));
    }

    [Fact]
    public void AppInstallSummaries_filter_on_the_SessionId_column()
    {
        Assert.Equal($"PartitionKey eq '{TenantId}' and SessionId eq '{SessionId}'",
            OrphanSessionRowFilters.For(Constants.TableNames.AppInstallSummaries, TenantId, SessionId));
    }

    [Fact]
    public void EventTypeIndex_filter_scans_the_tenant_range_and_matches_the_SessionId_column()
    {
        // RowKey is {invertedTicks}_{sessionId}; without a Sessions row the ticks are not reconstructible.
        Assert.Equal($"PartitionKey ge '{TenantId}_' and PartitionKey lt '{TenantId}_~' and SessionId eq '{SessionId}'",
            OrphanSessionRowFilters.For(Constants.TableNames.EventTypeIndex, TenantId, SessionId));
    }

    [Fact]
    public void CveIndex_filter_scans_the_tenant_range_and_matches_the_session_row_key()
    {
        Assert.Equal($"PartitionKey ge '{TenantId}_' and PartitionKey lt '{TenantId}_~' and RowKey eq '{SessionId}'",
            OrphanSessionRowFilters.For(Constants.TableNames.CveIndex, TenantId, SessionId));
    }

    [Fact]
    public void SessionAnnotations_filter_names_every_lane()
    {
        var filter = OrphanSessionRowFilters.For(Constants.TableNames.SessionAnnotations, TenantId, SessionId);
        Assert.StartsWith($"PartitionKey eq '{TenantId}' and (", filter);
        foreach (var lane in AnnotationLanes.All)
            Assert.Contains($"RowKey eq '{SessionId}_{lane}'", filter);
        Assert.Equal(AnnotationLanes.All.Length - 1, filter.Split(" or ").Length - 1);
    }

    [Fact]
    public void SessionTenantLookup_filter_pins_the_row_to_this_tenant()
    {
        Assert.Equal($"PartitionKey eq '{SessionId}' and RowKey eq 'tenant' and TenantId eq '{TenantId}'",
            OrphanSessionRowFilters.For(Constants.TableNames.SessionTenantLookup, TenantId, SessionId));
    }

    [Fact]
    public void Unknown_table_throws_instead_of_guessing()
    {
        Assert.Throws<ArgumentException>(() => OrphanSessionRowFilters.For(Constants.TableNames.Sessions, TenantId, SessionId));
    }

    [Theory]
    [InlineData("not-a-guid", SessionId)]
    [InlineData(TenantId, "'; drop")]
    public void Non_guid_ids_throw(string tenantId, string sessionId)
    {
        Assert.ThrowsAny<ArgumentException>(() => OrphanSessionRowFilters.For(Constants.TableNames.Events, tenantId, sessionId));
    }

    [Fact]
    public void Swept_tables_are_the_filter_tables_plus_inventory_pair_plus_handle()
    {
        var expected = OrphanSessionRowFilters.Tables
            .Concat(new[]
            {
                Constants.TableNames.SessionInventoryContributions,
                Constants.TableNames.SoftwareInventory,
                Constants.TableNames.EventSessionIndex,
            })
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected, OrphanSessionSweeper.SweptTables.ToHashSet(StringComparer.Ordinal));
    }
}

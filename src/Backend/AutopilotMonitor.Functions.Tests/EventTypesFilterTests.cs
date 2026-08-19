using AutopilotMonitor.Functions.Services;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the OData filter composition behind <c>GetSessionEventsByTypesAsync</c>:
/// partition scope AND-ed with a parenthesized or-chain, values escaped. The grouping
/// parentheses are load-bearing — without them the or-branches escape the partition
/// scope and the query degrades to a cross-partition table scan.
/// </summary>
public class EventTypesFilterTests
{
    [Fact]
    public void Single_type_builds_scoped_equality()
    {
        var filter = TableStorageService.BuildEventTypesFilter("tenant_session", new[] { "agent_metrics_snapshot" });
        Assert.Equal("PartitionKey eq 'tenant_session' and (EventType eq 'agent_metrics_snapshot')", filter);
    }

    [Fact]
    public void Multiple_types_or_chain_stays_inside_the_partition_scope()
    {
        var filter = TableStorageService.BuildEventTypesFilter(
            "tenant_session", new[] { "agent_metrics_snapshot", "agent_started", "spool_pressure_detected" });
        Assert.Equal(
            "PartitionKey eq 'tenant_session' and (EventType eq 'agent_metrics_snapshot' or EventType eq 'agent_started' or EventType eq 'spool_pressure_detected')",
            filter);
    }

    [Fact]
    public void Values_with_single_quotes_are_escaped()
    {
        var filter = TableStorageService.BuildEventTypesFilter("t'x", new[] { "a'b" });
        Assert.Equal("PartitionKey eq 't''x' and (EventType eq 'a''b')", filter);
    }
}

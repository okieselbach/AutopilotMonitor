using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Cover for the ConnectionType predicate used on every search path that does NOT push the
/// filter to OData — the deviceProperties batch-get (<c>ApplyBasicFilters</c>) and the legacy
/// unpaged scan. Sessions that predate the projection (null ConnectionType) must never match
/// a set filter; an unset filter matches everything.
/// </summary>
public class ConnectionTypeFilterTests
{
    private static SessionSummary Session(string? connectionType) =>
        new() { SessionId = "s", TenantId = "t", ConnectionType = connectionType };

    [Theory]
    [InlineData(null)]
    [InlineData("WiFi")]
    [InlineData("Ethernet")]
    public void NoFilter_AlwaysMatches(string? connectionType)
    {
        var filter = new SessionSearchFilter();
        Assert.True(TableStorageService.MatchesConnectionType(Session(connectionType), filter));
    }

    [Theory]
    [InlineData("Ethernet", true)]
    [InlineData("ethernet", true)]  // case-insensitive, matching the other string filters
    [InlineData("WiFi", false)]
    [InlineData(null, false)]       // legacy session without the projected column
    public void EthernetFilter_MatchesOnlyEthernet(string? connectionType, bool expected)
    {
        var filter = new SessionSearchFilter { ConnectionType = "Ethernet" };
        Assert.Equal(expected, TableStorageService.MatchesConnectionType(Session(connectionType), filter));
    }

    [Theory]
    [InlineData("WiFi", true)]
    [InlineData("Ethernet", false)]
    [InlineData(null, false)]
    public void WiFiFilter_MatchesOnlyWiFi(string? connectionType, bool expected)
    {
        var filter = new SessionSearchFilter { ConnectionType = "WiFi" };
        Assert.Equal(expected, TableStorageService.MatchesConnectionType(Session(connectionType), filter));
    }
}

using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Cover for the RebootCount range predicate used on every search path that does NOT push the
/// bound to OData — the deviceProperties batch-get (<c>ApplyBasicFilters</c>) and the legacy
/// unpaged scan. Without it a <c>deviceProperties + rebootCountMin=5</c> query leaks sessions
/// with RebootCount &lt; 5 (the medium finding). Min is inclusive (">= many reboots").
/// </summary>
public class RebootCountFilterBoundsTests
{
    private static SessionSummary Session(int rebootCount) =>
        new() { SessionId = "s", TenantId = "t", RebootCount = rebootCount };

    [Theory]
    [InlineData(0, true)]   // no bounds → always matches
    [InlineData(5, true)]
    public void NoBounds_AlwaysMatches(int reboots, bool expected)
    {
        var filter = new SessionSearchFilter();
        Assert.Equal(expected, TableStorageService.MatchesRebootCountBounds(Session(reboots), filter));
    }

    [Theory]
    [InlineData(4, false)] // below min → excluded
    [InlineData(5, true)]  // min is inclusive
    [InlineData(9, true)]
    public void Min_IsInclusiveLowerBound(int reboots, bool expected)
    {
        var filter = new SessionSearchFilter { RebootCountMin = 5 };
        Assert.Equal(expected, TableStorageService.MatchesRebootCountBounds(Session(reboots), filter));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, true)]  // max is inclusive
    [InlineData(4, false)] // above max → excluded
    public void Max_IsInclusiveUpperBound(int reboots, bool expected)
    {
        var filter = new SessionSearchFilter { RebootCountMax = 3 };
        Assert.Equal(expected, TableStorageService.MatchesRebootCountBounds(Session(reboots), filter));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public void MinAndMax_FormAClosedRange(int reboots, bool expected)
    {
        var filter = new SessionSearchFilter { RebootCountMin = 2, RebootCountMax = 4 };
        Assert.Equal(expected, TableStorageService.MatchesRebootCountBounds(Session(reboots), filter));
    }
}

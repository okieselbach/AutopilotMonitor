using AutopilotMonitor.Functions.Services;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the device-property filter matching behind the MCP <c>prop.*</c> /
/// <c>deviceProperties</c> filter (TableStorageService.MatchesDevicePropertyFilter).
///
/// The trailing-<c>*</c> prefix wildcard is what lets "give me all ARM sessions"
/// resolve to a single filter (<c>hardware_spec.cpuArchitecture = "ARM*"</c>)
/// matching both "ARM" and "ARM64". Exact-match, numeric and array behavior must
/// remain unchanged.
/// </summary>
public class DevicePropertyFilterWildcardTests
{
    [Theory]
    [InlineData("ARM", "ARM*", true)]    // family parent
    [InlineData("ARM64", "ARM*", true)]  // the case the feature exists for
    [InlineData("arm64", "ARM*", true)]  // case-insensitive
    [InlineData("x64", "ARM*", false)]   // unrelated arch must not leak in
    [InlineData("x86", "x*", true)]
    [InlineData("x64", "x*", true)]
    [InlineData("ARM64", "x*", false)]
    public void Trailing_star_is_a_prefix_wildcard(string actual, string filter, bool expected)
    {
        Assert.Equal(expected, TableStorageService.MatchesDevicePropertyFilter(actual, filter));
    }

    [Theory]
    [InlineData("ARM64", "ARM64", true)]
    [InlineData("x64", "x86", false)]
    [InlineData("X64", "x64", true)]   // exact match stays case-insensitive
    public void Exact_match_is_unchanged(string actual, string filter, bool expected)
    {
        Assert.Equal(expected, TableStorageService.MatchesDevicePropertyFilter(actual, filter));
    }

    [Theory]
    [InlineData("16", ">=8", true)]
    [InlineData("4", ">=8", false)]
    [InlineData("8", "<=8", true)]
    public void Numeric_operators_are_unchanged(string actual, string filter, bool expected)
    {
        Assert.Equal(expected, TableStorageService.MatchesDevicePropertyFilter(actual, filter));
    }

    [Fact]
    public void Lone_star_is_treated_literally_not_as_match_all()
    {
        // "*" (length 1) is below the wildcard threshold, so it falls through to
        // exact equality rather than becoming a match-everything sentinel.
        Assert.True(TableStorageService.MatchesDevicePropertyFilter("*", "*"));
        Assert.False(TableStorageService.MatchesDevicePropertyFilter("anything", "*"));
    }

    [Fact]
    public void Embedded_star_is_literal_only_trailing_star_is_a_wildcard()
    {
        Assert.True(TableStorageService.MatchesDevicePropertyFilter("A*B", "A*B"));
        Assert.False(TableStorageService.MatchesDevicePropertyFilter("AXB", "A*B"));
    }
}

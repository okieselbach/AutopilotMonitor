using AutopilotMonitor.Functions.Services.Vulnerability;

namespace AutopilotMonitor.Functions.Tests;

public class VersionComparerTests
{
    // -----------------------------------------------------------------------
    // Compare
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_EqualVersions_ReturnsZero()
    {
        Assert.Equal(0, VersionComparer.Compare("1.2.3", "1.2.3"));
    }

    [Fact]
    public void Compare_HigherVersion_ReturnsPositive()
    {
        Assert.True(VersionComparer.Compare("2.0", "1.9") > 0);
    }

    [Fact]
    public void Compare_LowerVersion_ReturnsNegative()
    {
        Assert.True(VersionComparer.Compare("1.0", "2.0") < 0);
    }

    [Fact]
    public void Compare_DifferentLengths_PadsWithZeros()
    {
        Assert.Equal(0, VersionComparer.Compare("1.2", "1.2.0"));
        Assert.Equal(0, VersionComparer.Compare("1.2.0.0", "1.2"));
    }

    [Theory]
    [InlineData("89beta", "89", 0)]
    [InlineData("5rc1", "5", 0)]
    [InlineData("10alpha", "9", 1)]
    public void Compare_NonNumericSuffix_ParsesNumericPrefix(string v1, string v2, int expected)
    {
        var result = VersionComparer.Compare(v1, v2);
        if (expected == 0) Assert.Equal(0, result);
        else if (expected > 0) Assert.True(result > 0);
        else Assert.True(result < 0);
    }

    [Fact]
    public void Compare_BothNull_ReturnsZero()
    {
        Assert.Equal(0, VersionComparer.Compare(null!, null!));
    }

    [Fact]
    public void Compare_FirstNull_ReturnsNegative()
    {
        Assert.True(VersionComparer.Compare(null!, "1.0") < 0);
    }

    [Fact]
    public void Compare_SecondNull_ReturnsPositive()
    {
        Assert.True(VersionComparer.Compare("1.0", null!) > 0);
    }

    [Fact]
    public void Compare_RealWorldCiscoVersions_OrdersCorrectly()
    {
        // Cisco AnyConnect 4.10 vs Cisco Secure Client 5.1.5.65
        Assert.True(VersionComparer.Compare("5.1.5.65", "4.10.06079") > 0);
        Assert.True(VersionComparer.Compare("4.10.06079", "5.1.5.65") < 0);
    }

    // -----------------------------------------------------------------------
    // IsVersionAffected
    // -----------------------------------------------------------------------

    // (installed, startInclusive, startExclusive, endInclusive, endExclusive) → affected?
    [Theory]
    [InlineData("5.1.5", "5.0", null, null, "6.0", true)]   // within [5.0, 6.0)
    [InlineData("4.9", "5.0", null, null, "6.0", false)]    // below the inclusive start
    [InlineData("6.1", "5.0", null, null, "6.0", false)]    // above the exclusive end
    [InlineData("6.0", "5.0", null, "6.0", null, true)]     // inclusive end includes the boundary
    [InlineData("6.0", "5.0", null, null, "6.0", false)]    // exclusive end excludes the boundary
    [InlineData("5.0", null, "5.0", null, "6.0", false)]    // exclusive start excludes the boundary
    public void IsVersionAffected_respects_boundary_inclusivity(
        string installed, string? startIncl, string? startExcl, string? endIncl, string? endExcl, bool expected)
    {
        Assert.Equal(expected, VersionComparer.IsVersionAffected(installed, startIncl, startExcl, endIncl, endExcl));
    }

    [Fact]
    public void IsVersionAffected_OnlyEndBoundary_MatchesBelow()
    {
        // CVE-2024-0213: affects versions before 5.8.1
        Assert.True(VersionComparer.IsVersionAffected("5.8.0.161", null, null, null, "5.8.1"));
        Assert.False(VersionComparer.IsVersionAffected("5.8.1", null, null, null, "5.8.1"));
    }

    [Fact]
    public void IsVersionAffected_NullInstalled_ReturnsFalse()
    {
        Assert.False(VersionComparer.IsVersionAffected(null!, "1.0", null, null, "2.0"));
        Assert.False(VersionComparer.IsVersionAffected("", "1.0", null, null, "2.0"));
    }

    [Fact]
    public void IsVersionAffected_AllNullBoundaries_ReturnsTrue()
    {
        // No constraints = all versions affected
        Assert.True(VersionComparer.IsVersionAffected("99.99.99", null, null, null, null));
    }
}

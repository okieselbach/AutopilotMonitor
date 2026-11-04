using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Cover for the group-tag search filter and for the device-snapshot path's in-memory twin of the
/// OData push-down: a filter must answer the same on both search paths.
/// </summary>
public class GroupTagSearchFilterTests
{
    private static SessionSummary Session(string? groupTag = null) => new()
    {
        SessionId = "s", TenantId = "t", AutopilotGroupTag = groupTag,
        Manufacturer = "Contoso", Model = "EliteBook 840", EnrollmentType = "v1",
        DeviceName = "LAB-01", OsBuild = "26100.1000", Status = SessionStatus.Failed,
    };

    [Theory]
    [InlineData("TEST", true)]
    [InlineData("test", true)]      // Intune does not fix the casing of a tag
    [InlineData(" Test ", true)]
    [InlineData("TEST-LAB", false)] // exact, not prefix
    [InlineData(null, false)]       // session without a recorded tag
    public void GroupTagFilter_IsCaseInsensitiveAndExact(string? sessionTag, bool expected)
    {
        var filter = new SessionSearchFilter { GroupTag = "Test" };
        Assert.Equal(expected, TableStorageService.MatchesGroupTag(Session(sessionTag), filter));
    }

    [Fact]
    public void NoGroupTagFilter_MatchesEverySession()
    {
        Assert.True(TableStorageService.MatchesGroupTag(Session(), new SessionSearchFilter()));
        Assert.True(TableStorageService.MatchesGroupTag(Session(), new SessionSearchFilter { GroupTag = "  " }));
    }

    [Theory]
    [InlineData("Manufacturer", "Contoso", true)]
    [InlineData("Manufacturer", "contoso", false)] // OData eq is ordinal: the twin is too
    [InlineData("Model", "EliteBook 840", true)]
    [InlineData("Model", "EliteBook", false)]
    [InlineData("EnrollmentType", "v2", false)]
    [InlineData("DeviceName", "LAB", true)]        // prefix, like the ge/lt range
    [InlineData("DeviceName", "PC", false)]
    [InlineData("OsBuild", "26100", true)]
    [InlineData("OsBuild", "22631", false)]
    [InlineData("Status", "Failed", true)]
    [InlineData("Status", "Succeeded", false)]
    public void DeviceSnapshotPath_AppliesThePushedDownColumns(string column, string value, bool expected)
    {
        var filter = column switch
        {
            "Manufacturer" => new SessionSearchFilter { Manufacturer = value },
            "Model" => new SessionSearchFilter { Model = value },
            "EnrollmentType" => new SessionSearchFilter { EnrollmentType = value },
            "DeviceName" => new SessionSearchFilter { DeviceName = value },
            "OsBuild" => new SessionSearchFilter { OsBuild = value },
            _ => new SessionSearchFilter { Status = value },
        };
        Assert.Equal(expected, TableStorageService.MatchesPushedDownColumns(Session(), filter));
    }

    [Fact]
    public void Fingerprint_BindsTheGroupTag()
    {
        // A continuation token minted for one group tag must not be replayed against another.
        var a = SearchSessionsPagination.Fingerprint("tenant", "t", null, new SessionSearchFilter { GroupTag = "TEST" });
        var b = SearchSessionsPagination.Fingerprint("tenant", "t", null, new SessionSearchFilter { GroupTag = "PROD" });
        Assert.NotEqual(a, b);
    }
}

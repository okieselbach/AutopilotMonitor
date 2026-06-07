using System;
using System.Collections.Generic;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Apps;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Covers the opt-in pagination surface on <see cref="AppsAnalyticsHelper.BuildAppsListResponse"/>:
/// no pageSize → legacy full array; pageSize → offset-paginated envelope with a stable cursor.
/// </summary>
public class AppsAnalyticsHelperPagingTests
{
    private static List<AppInstallSummary> FiveApps()
    {
        var started = DateTime.UtcNow.AddDays(-1);
        var list = new List<AppInstallSummary>();
        // Names intentionally out of order so the deterministic appName tiebreaker is exercised.
        foreach (var name in new[] { "Echo", "Alpha", "Delta", "Bravo", "Charlie" })
            list.Add(new AppInstallSummary { AppName = name, Status = "Succeeded", StartedAt = started });
        return list;
    }

    private static JsonElement Build(int? pageSize, int skip = 0) =>
        JsonSerializer.SerializeToElement(AppsAnalyticsHelper.BuildAppsListResponse(
            FiveApps(), days: 30, pageSize: pageSize, skip: skip,
            nextLinkForOffset: o => $"/api/apps/list?pageSize={pageSize}&skip={o}"));

    [Fact]
    public void NoPageSize_ReturnsLegacyFullArray_WithoutPagingFields()
    {
        var root = Build(pageSize: null);

        Assert.Equal(5, root.GetProperty("totalApps").GetInt32());
        Assert.Equal(5, root.GetProperty("apps").GetArrayLength());
        Assert.False(root.TryGetProperty("nextLink", out _));
        Assert.False(root.TryGetProperty("offset", out _));
    }

    [Fact]
    public void PageSize_FirstPage_SlicesDeterministicallyAndEmitsNextLink()
    {
        var root = Build(pageSize: 2, skip: 0);

        Assert.Equal(5, root.GetProperty("totalApps").GetInt32());
        Assert.Equal(2, root.GetProperty("count").GetInt32());
        Assert.Equal(0, root.GetProperty("offset").GetInt32());

        var apps = root.GetProperty("apps");
        Assert.Equal("Alpha", apps[0].GetProperty("appName").GetString());
        Assert.Equal("Bravo", apps[1].GetProperty("appName").GetString());
        Assert.Equal("/api/apps/list?pageSize=2&skip=2", root.GetProperty("nextLink").GetString());
    }

    [Fact]
    public void PageSize_LastPage_HasNullNextLink()
    {
        var root = Build(pageSize: 2, skip: 4);

        Assert.Equal(1, root.GetProperty("count").GetInt32());
        Assert.Equal("Echo", root.GetProperty("apps")[0].GetProperty("appName").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("nextLink").ValueKind);
    }
}

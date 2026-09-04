using System.Collections.Generic;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The operator KQL proxy resolves its <c>source</c> against app settings once: every configured
/// store maps to its own Kusto endpoint and token scope, a known-but-unconfigured store names the
/// missing setting (503 material), an unknown name lists the vocabulary (400 material), and the
/// default is the backend store.
/// </summary>
public class LogQuerySourceCatalogTests
{
    private static LogQuerySourceCatalog Build(params (string Key, string? Value)[] settings)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (k, v) in settings) dict[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        return new LogQuerySourceCatalog(config);
    }

    [Fact]
    public void Every_configured_source_resolves_to_its_own_endpoint_and_scope()
    {
        var catalog = Build(
            (LogQuerySourceCatalog.BackendAppIdSetting, "aaaa-backend"),
            (LogQuerySourceCatalog.WebAppIdSetting, "bbbb-web"),
            (LogQuerySourceCatalog.McpWorkspaceIdSetting, "cccc-workspace"));

        Assert.True(catalog.TryResolve("backend", out var backend, out _));
        Assert.Equal("https://api.applicationinsights.io/v1/apps/aaaa-backend/query", backend!.QueryUri.ToString());
        Assert.Equal("https://api.applicationinsights.io/.default", backend.TokenScope);

        Assert.True(catalog.TryResolve("web", out var web, out _));
        Assert.Equal("https://api.applicationinsights.io/v1/apps/bbbb-web/query", web!.QueryUri.ToString());

        Assert.True(catalog.TryResolve("mcp", out var mcp, out _));
        Assert.Equal("https://api.loganalytics.io/v1/workspaces/cccc-workspace/query", mcp!.QueryUri.ToString());
        Assert.Equal("https://api.loganalytics.io/.default", mcp.TokenScope);

        Assert.Equal(3, catalog.Available.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Missing_source_defaults_to_backend(string? name)
    {
        var catalog = Build((LogQuerySourceCatalog.BackendAppIdSetting, "aaaa-backend"));

        Assert.True(catalog.TryResolve(name, out var source, out _));
        Assert.Equal(LogQuerySources.Backend, source!.Name);
    }

    [Fact]
    public void Known_but_unconfigured_source_names_the_missing_setting_and_leaves_others_intact()
    {
        var catalog = Build((LogQuerySourceCatalog.BackendAppIdSetting, "aaaa-backend"));

        Assert.False(catalog.TryResolve("web", out var web, out var error));
        Assert.Null(web);
        Assert.Contains(LogQuerySourceCatalog.WebAppIdSetting, error);
        Assert.Contains("not configured", error);

        Assert.True(catalog.TryResolve("backend", out _, out _));
    }

    [Fact]
    public void Unknown_source_lists_the_vocabulary()
    {
        var catalog = Build((LogQuerySourceCatalog.BackendAppIdSetting, "aaaa-backend"));

        Assert.False(catalog.TryResolve("portal", out _, out var error));
        Assert.Contains("Unknown log source 'portal'", error);
        foreach (var s in LogQuerySources.All)
            Assert.Contains(s, error);
    }

    [Fact]
    public void Vocabulary_is_the_three_stores_in_declaration_order()
    {
        Assert.Equal(new[] { "backend", "web", "mcp" }, LogQuerySources.All);
        Assert.True(LogQuerySources.IsKnown("mcp"));
        Assert.False(LogQuerySources.IsKnown("MCP"));
        Assert.False(LogQuerySources.IsKnown(null));
    }
}

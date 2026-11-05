using System.Text.Json;
using System.Text.RegularExpressions;
using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Xunit;
using static AutopilotMonitor.Functions.Services.McpUsageTelemetryQueries;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The fixed KQL of the MCP usage overview: which rows it reads (the worker's request row, MCP only, no access
/// probe), that it counts calls exactly, leaves platform admins out unless asked, matches log lines by their
/// start, reads only fields the MCP server writes (tests/fixtures/mcp-log-lines, proven on the MCP side by
/// usage-log-fields.test.ts) — and that the parsers read the result tables by column name.
/// </summary>
public class McpUsageTelemetryQueriesTests
{
    private static readonly Window Customers = new(new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc), IncludePlatformAdmins: false);
    private static readonly Window Everyone = Customers with { IncludePlatformAdmins = true };

    private static IEnumerable<string> RequestQueries(Window w) => new[] { Calls(w), Routes(w), Refusals(w), Peaks(w) };
    private static IEnumerable<string> McpLogQueries(Window w) => new[] { ToolQuality(w), McpRefusals(w), McpServerFacts(w) };

    [Fact]
    public void Window_CoversWholeUtcDays()
    {
        Assert.Equal("datetime(2026-10-07T00:00:00Z)", Customers.From);
        Assert.Equal("datetime(2026-11-06T00:00:00Z)", Customers.To);
        Assert.Equal("P31D", Customers.Timespan); // the where clauses bound the window; the timespan only has to cover it
        foreach (var query in RequestQueries(Customers).Concat(McpLogQueries(Customers)))
        {
            Assert.Contains(">= datetime(2026-10-07T00:00:00Z)", query);
            Assert.Contains("< datetime(2026-11-06T00:00:00Z)", query);
        }
    }

    [Fact]
    public void RequestQueries_ReadTheWorkerRow_McpOnly_WithoutTheAccessProbe()
    {
        foreach (var query in RequestQueries(Customers))
        {
            Assert.Contains("customDimensions.Source) == 'WorkerMiddleware'", query);
            Assert.Contains("customDimensions.ClientSource) =~ 'mcp'", query);
            Assert.Contains("customDimensions.FunctionName) != 'GlobalAccessProbe'", query);
            Assert.Contains("customDimensions.McpToolName", query);
            Assert.Contains("customDimensions.CorrelationId", query);
            Assert.Contains("tolower(tostring(customDimensions.TenantId))", query);
            Assert.Contains("tolower(tostring(customDimensions.UserId))", query);
        }
    }

    [Fact]
    public void Calls_AreCountedOncePerCorrelationId_NeverEstimated()
    {
        var query = Calls(Customers);

        Assert.Contains("by corr, tool, tenant, principal", query);
        Assert.Contains("calls = count()", query);
        foreach (var q in RequestQueries(Customers).Concat(McpLogQueries(Customers)))
            Assert.DoesNotContain("dcount(", q);
    }

    [Fact]
    public void PlatformAdmins_AreLeftOut_UnlessIncluded()
    {
        foreach (var query in RequestQueries(Customers))
            Assert.Contains("role !in ('GlobalAdmin', 'GlobalReader')", query);
        Assert.Contains("tostring(d.scope) != 'ga'", ToolQuality(Customers));

        foreach (var query in RequestQueries(Everyone).Concat(McpLogQueries(Everyone)))
        {
            Assert.DoesNotContain("'GlobalAdmin'", query);
            Assert.DoesNotContain("!= 'ga'", query);
        }
    }

    [Fact]
    public void Refusals_AreClassifiedByTheLinesTheMiddlewaresWrite()
    {
        var query = Refusals(Customers);

        Assert.Contains($"startswith '{McpQuotaEnforcementMiddleware.BlockedLogPrefix}'", query);
        Assert.Contains($"startswith '{UserRateLimitMiddleware.ThrottledLogPrefix}'", query);
        Assert.Equal("[McpQuota] BLOCKED", McpQuotaEnforcementMiddleware.BlockedLogPrefix);
        Assert.Equal("[UserRateLimit] THROTTLED", UserRateLimitMiddleware.ThrottledLogPrefix);
        Assert.Contains("join kind=inner lines on operation_Id", query);
    }

    [Fact]
    public void Peaks_SlideASixtySecondWindowInTenSecondSteps()
    {
        var query = Peaks(Customers);

        Assert.Contains("bin(timestamp, 10s)", query);
        Assert.Contains("bin(at, 10s)", query);
        Assert.Equal(2, Regex.Matches(query, Regex.Escape("mv-expand k = range(0, 5, 1)")).Count);
    }

    [Fact]
    public void McpLogLines_AreMatchedByTheirStart_NeverByHas()
    {
        Assert.Contains($"Log_s startswith '{ToolCallLinePrefix}'", ToolQuality(Customers));
        Assert.Contains($"Log_s startswith '{McpRefusalLinePrefix}'", McpRefusals(Customers));
        Assert.Contains($"Log_s startswith '{BootLinePrefix}'", McpServerFacts(Customers));
        foreach (var query in McpLogQueries(Customers))
            Assert.DoesNotContain("Log_s has", query);
    }

    [Fact]
    public void McpLogQueries_ReadOnlyWhatTheServerWrites()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepoRoot(), "tests", "fixtures", "mcp-log-lines", "fields.json")));
        var prefixes = contract.RootElement.GetProperty("prefixes");
        var fields = contract.RootElement.GetProperty("fields");

        Assert.Equal(ToolCallLinePrefix, prefixes.GetProperty("tool_call").GetString());
        Assert.Equal(BootLinePrefix, prefixes.GetProperty("boot").GetString());
        Assert.Equal(McpRefusalLinePrefix, prefixes.GetProperty("mcp_refusal").GetString());

        var toolCallFields = fields.GetProperty("tool_call").EnumerateArray().Select(f => f.GetString()!).ToHashSet();
        var readFromToolCall = Regex.Matches(ToolQuality(Everyone), @"\bd\.(\w+)").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.NotEmpty(readFromToolCall);
        Assert.Subset(toolCallFields, readFromToolCall);

        var bootFields = fields.GetProperty("boot").EnumerateArray().Select(f => f.GetString()!).ToHashSet();
        var readFromBoot = Regex.Matches(McpServerFacts(Everyone), @"parse_json\(Log_s\)\.(\w+)").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Equal(new[] { "rateLimitPerMinute" }, readFromBoot);
        Assert.Subset(bootFields, readFromBoot);
    }

    // ── Parsers ──

    private static KqlTable Table(string[] columns, params object?[][] rows)
        => new()
        {
            Name = "PrimaryResult",
            Columns = columns.Select(c => new KqlColumn { Name = c, Type = "dynamic" }).ToList(),
            Rows = rows.Select(r => (IReadOnlyList<JsonElement>)r.Select(cell => JsonSerializer.SerializeToElement(cell)).ToList()).ToList(),
        };

    [Fact]
    public void ParseCalls_ReadsByColumnName_WhateverTheOrder()
    {
        var table = Table(
            new[] { "role", "charged", "day", "principal", "tool", "calls", "tenant", "servedCalls", "firstAt" },
            new object?[] { "Admin", 59L, "20261028", "user@contoso.com", "get_time_attribution", 64L, "t1", 59L, "2026-10-28T16:12:30.4442709Z" });

        var row = Assert.Single(ParseCalls(table));

        Assert.Equal(new CallRow("20261028", "get_time_attribution", "t1", "user@contoso.com", 64, 59, 59,
            new DateTime(2026, 10, 28, 16, 12, 30, 444, DateTimeKind.Utc).AddTicks(2709), "Admin"), row);
        Assert.Equal(DateTimeKind.Utc, row.LastAt!.Value.Kind);
    }

    [Fact]
    public void ParseRefusals_KeepsMissingCountsMissing()
    {
        var table = Table(
            new[] { "gate", "day", "tenant", "principal", "level", "scope", "plan", "refused", "firstAt", "lastAt", "usedCount", "limitValue" },
            new object?[] { "minute", "20261026", "t1", "user@contoso.com", "", "", "", 2L, "2026-10-26T07:55:42Z", "2026-10-26T07:55:43Z", null, null });

        var row = Assert.Single(ParseRefusals(table));

        Assert.Equal("minute", row.Gate);
        Assert.Equal(2, row.Refused);
        Assert.Null(row.Used);
        Assert.Null(row.Limit);
    }

    [Fact]
    public void ParseToolQuality_RoundsAFractionalCount_AndKeepsTheMean()
    {
        var table = Table(
            new[] { "tool", "calls", "errors", "cancelled", "overCap", "p95Ms", "avgChars" },
            new object?[] { "search_events", 26L, 1L, 0L, 0L, 1029.6, 10602.5 });

        var row = Assert.Single(ParseToolQuality(table));

        Assert.Equal(26, row.Calls);
        Assert.Equal(1030, row.P95Ms);
        Assert.Equal(10602.5, row.AvgChars);
    }

    [Fact]
    public void ParseServerFacts_WithoutABootLine_HasNoLimit()
    {
        var facts = ParseServerFacts(Table(new[] { "oldest", "rateLimitPerMinute" }, new object?[] { "2026-10-07T01:52:43.167992Z", null }));

        Assert.Equal(new DateTime(2026, 10, 7, 1, 52, 43, DateTimeKind.Utc).AddTicks(1_679_920), facts.Oldest);
        Assert.Null(facts.RateLimitPerMinute);
        Assert.Equal(new ServerFacts(null, null), ParseServerFacts(Table(new[] { "oldest", "rateLimitPerMinute" })));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

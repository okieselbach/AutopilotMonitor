using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The assume-breach alarm for the Global-Admin-only surface: a refused GlobalAdminOnly request must
/// land as a <c>PrivilegedRouteDenied</c> Security ops event that carries the caller's identity and
/// the MCP tool name, Critical for an outsider and Warning for a known Global Reader, throttled per
/// caller+path so a scripted probe cannot flood the push channel.
/// </summary>
public class PrivilegedDenialReporterTests
{
    private static (PrivilegedDenialReporter Reporter, List<OpsEventEntry> Saved) Rig(bool repoThrows = false)
    {
        var saved = new List<OpsEventEntry>();
        var opsRepo = new Mock<IOpsEventRepository>();
        var setup = opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()));
        if (repoThrows)
            setup.ThrowsAsync(new System.InvalidOperationException("table down"));
        else
            setup.Callback<OpsEventEntry>(e => { lock (saved) saved.Add(e); }).Returns(Task.CompletedTask);
        var adminConfig = new Mock<AdminConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance,
            new MemoryCache(new MemoryCacheOptions())) { CallBase = false };
        var opsEvents = new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance,
            TestNotifications.InertOpsAlertDispatch(adminConfig.Object));
        var reporter = new PrivilegedDenialReporter(opsEvents, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<PrivilegedDenialReporter>.Instance);
        return (reporter, saved);
    }

    private static PrivilegedDenial Denial(
        string path = "/api/global/raw/logs", string callerRole = "None", string callerId = "oid-1",
        string? clientSource = "mcp", string? mcpTool = "query_backend_logs")
        => new(
            Method: "POST", Path: path, StatusCode: 403, Reason: "NotGlobalAdmin", Policy: "GlobalAdminOnly",
            CallerId: callerId, Upn: "user@contoso.com", ObjectId: "oid-1",
            TenantId: "11111111-1111-1111-1111-111111111111", CallerRole: callerRole,
            ClientSource: clientSource, McpToolName: mcpTool, CorrelationId: "corr-1");

    /// <summary>The write is fire-and-forget; give the completed-task continuation a moment.</summary>
    private static List<OpsEventEntry> Settle(List<OpsEventEntry> saved, int expected)
    {
        var spin = new SpinWait();
        var deadline = System.DateTime.UtcNow.AddSeconds(2);
        while (System.DateTime.UtcNow < deadline)
        {
            lock (saved) if (saved.Count >= expected) break;
            spin.SpinOnce();
        }
        lock (saved) return saved.ToList();
    }

    [Fact]
    public void Outsider_lands_as_Critical_security_event_with_identity_and_tool()
    {
        var (reporter, saved) = Rig();

        reporter.Report(Denial());

        var evt = Assert.Single(Settle(saved, 1));
        Assert.Equal(OpsEventCategory.Security, evt.Category);
        Assert.Equal(OpsEventTypes.PrivilegedRouteDenied, evt.EventType);
        Assert.Equal(OpsEventSeverity.Critical, evt.Severity);
        Assert.Equal("11111111-1111-1111-1111-111111111111", evt.TenantId);
        Assert.Equal("user@contoso.com", evt.UserId);
        Assert.Contains("/api/global/raw/logs", evt.Message);
        Assert.Contains("query_backend_logs", evt.Message);

        using var details = JsonDocument.Parse(evt.Details!);
        var d = details.RootElement;
        Assert.Equal("oid-1", d.GetProperty("oid").GetString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", d.GetProperty("tid").GetString());
        Assert.Equal("mcp", d.GetProperty("clientSource").GetString());
        Assert.Equal("query_backend_logs", d.GetProperty("mcpToolName").GetString());
        Assert.Equal("corr-1", d.GetProperty("correlationId").GetString());
        Assert.Equal("GlobalAdminOnly", d.GetProperty("policy").GetString());
        Assert.Equal("NotGlobalAdmin", d.GetProperty("reason").GetString());
    }

    [Fact]
    public void Global_reader_lands_as_Warning_not_Critical()
    {
        var (reporter, saved) = Rig();

        reporter.Report(Denial(callerRole: Constants.GlobalRoles.GlobalReader));

        var evt = Assert.Single(Settle(saved, 1));
        Assert.Equal(OpsEventSeverity.Warning, evt.Severity);
        Assert.Equal(OpsEventTypes.PrivilegedRouteDenied, evt.EventType);
    }

    [Fact]
    public void Direct_http_probe_without_mcp_headers_still_lands()
    {
        var (reporter, saved) = Rig();

        reporter.Report(Denial(clientSource: null, mcpTool: null));

        var evt = Assert.Single(Settle(saved, 1));
        Assert.Contains("via direct", evt.Message);
        using var details = JsonDocument.Parse(evt.Details!);
        // Absent-when-null is the wire policy for API responses, but ops-event details use the default
        // serializer: the keys are present with null so a reader can tell "no header" from "not recorded".
        Assert.Equal(JsonValueKind.Null, details.RootElement.GetProperty("mcpToolName").ValueKind);
    }

    [Fact]
    public void One_event_per_caller_per_window_whatever_path_another_caller_is_reported()
    {
        var (reporter, saved) = Rig();

        reporter.Report(Denial());
        reporter.Report(Denial(mcpTool: "query_table"));                      // same caller → throttled
        reporter.Report(Denial(path: "/api/global/raw/tables"));              // same caller, other route → throttled (a sweep is ONE event)
        reporter.Report(Denial(callerId: "oid-2"));                            // new caller → reported

        var events = Settle(saved, 2);
        Assert.Equal(2, events.Count);
        Assert.DoesNotContain(events, e => e.Message.Contains("/api/global/raw/tables"));
        Assert.DoesNotContain(events, e => e.Message.Contains("query_table"));
        Assert.All(events, e => Assert.Contains("/api/global/raw/logs", e.Message));
    }

    [Fact]
    public void Instance_budget_ends_in_one_storm_marker_then_silence()
    {
        var (reporter, saved) = Rig();

        // Six distinct callers: five normal events, the sixth trips the storm marker.
        for (var i = 1; i <= 6; i++)
            reporter.Report(Denial(callerId: $"oid-{i}", path: $"/api/global/raw/route-{i}"));
        // Everything after that stays trace-only for the window — new callers included.
        for (var i = 7; i <= 40; i++)
            reporter.Report(Denial(callerId: $"oid-{i}"));

        var events = Settle(saved, PrivilegedDenialReporter.MaxEventsPerWindow + 1);
        Assert.Equal(PrivilegedDenialReporter.MaxEventsPerWindow + 1, events.Count);
        Assert.All(events, e => Assert.Equal(OpsEventTypes.PrivilegedRouteDenied, e.EventType));

        var normal = events.Take(PrivilegedDenialReporter.MaxEventsPerWindow).ToList();
        Assert.All(normal, e => Assert.DoesNotContain("storm", e.Message));

        var storm = events.Last();
        Assert.Equal(OpsEventSeverity.Critical, storm.Severity);
        Assert.Contains("denial storm", storm.Message);
        Assert.Contains("/api/global/raw/route-6", storm.Message);
        using var details = JsonDocument.Parse(storm.Details!);
        Assert.True(details.RootElement.GetProperty("storm").GetBoolean());
        Assert.Equal(PrivilegedDenialReporter.MaxEventsPerWindow, details.RootElement.GetProperty("cap").GetInt32());
        Assert.Equal("oid-1", details.RootElement.GetProperty("lastOid").GetString());
    }

    [Fact]
    public void Reader_denials_count_against_the_budget_too()
    {
        var (reporter, saved) = Rig();

        for (var i = 1; i <= PrivilegedDenialReporter.MaxEventsPerWindow + 3; i++)
            reporter.Report(Denial(callerId: $"reader-{i}", callerRole: Constants.GlobalRoles.GlobalReader));

        var events = Settle(saved, PrivilegedDenialReporter.MaxEventsPerWindow + 1);
        Assert.Equal(PrivilegedDenialReporter.MaxEventsPerWindow + 1, events.Count);
        // The five Reader slips are Warning (no push under a Critical rule); the storm marker is Critical.
        Assert.Equal(PrivilegedDenialReporter.MaxEventsPerWindow, events.Count(e => e.Severity == OpsEventSeverity.Warning));
        Assert.Equal(OpsEventSeverity.Critical, events.Last().Severity);
    }

    [Fact]
    public void Repository_failure_never_escapes_the_deny_path()
    {
        var (reporter, _) = Rig(repoThrows: true);

        var ex = Record.Exception(() => reporter.Report(Denial()));

        Assert.Null(ex);
    }
}

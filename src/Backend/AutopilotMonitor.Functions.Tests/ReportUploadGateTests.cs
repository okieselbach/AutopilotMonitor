using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Functions.Reports;
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
/// The per-instance cap on concurrent report uploads: a caller past the limit gets 429 with
/// Retry-After, the first refusal of a window raises ReportUploadLimitReached while later ones in
/// that window stay in the trace, and a released slot is free again.
/// </summary>
public class ReportUploadGateTests
{
    private const string Upn = "admin@example.com";

    private readonly List<OpsEventEntry> _opsEvents = new();

    [Fact]
    public async Task Caller_past_the_limit_gets_429_and_one_ops_event_per_window()
    {
        var gate = CreateGate(limit: 1);
        using var upload = gate.TryEnter();
        Assert.NotNull(upload);
        Assert.Null(gate.TryEnter());

        var (req, _) = EndpointHarness.Request("tenant-a", Upn);
        var refused = await gate.RefuseAsync(req, "sessions/{sessionId}/report", "tenant-a", Upn);
        await gate.RefuseAsync(req, "diagnostics/files", "tenant-a", Upn);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.RateLimited, EndpointHarness.ErrorCode(refused));
        Assert.True(refused.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.Equal("5", retryAfter!.Single());

        var alert = Assert.Single(_opsEvents);
        Assert.Equal(OpsEventTypes.ReportUploadLimitReached, alert.EventType);
        Assert.Equal(OpsEventSeverity.Warning, alert.Severity);
        Assert.Equal("tenant-a", alert.TenantId);
        Assert.Contains("sessions/{sessionId}/report", alert.Details);
    }

    [Fact]
    public void Alert_is_taken_once_per_window()
    {
        var gate = CreateGate(limit: 1);
        var start = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(gate.TakeAlert(start));
        Assert.False(gate.TakeAlert(start.AddMinutes(59)));
        Assert.True(gate.TakeAlert(start + ReportUploadGate.AlertWindow));
        Assert.False(gate.TakeAlert(start + ReportUploadGate.AlertWindow));
    }

    [Fact]
    public void Released_slot_is_free_again_and_an_instance_takes_three()
    {
        var gate = CreateGate(limit: 1);
        gate.TryEnter()!.Dispose();
        Assert.NotNull(gate.TryEnter());

        var production = new ReportUploadGate(CreateOps(), NullLogger<ReportUploadGate>.Instance);
        Assert.Equal(3, production.Limit);
        var slots = Enumerable.Range(0, 3).Select(_ => production.TryEnter()).ToList();
        Assert.All(slots, Assert.NotNull);
        Assert.Null(production.TryEnter());
    }

    private ReportUploadGate CreateGate(int limit) => new(limit, CreateOps(), NullLogger<ReportUploadGate>.Instance);

    private OpsEventService CreateOps()
    {
        var repo = new Mock<IOpsEventRepository>();
        repo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
            .Returns<OpsEventEntry>(e => { lock (_opsEvents) _opsEvents.Add(e); return Task.CompletedTask; });
        var adminConfig = new Mock<AdminConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()));
        return new OpsEventService(repo.Object, NullLogger<OpsEventService>.Instance,
            TestNotifications.InertOpsAlertDispatch(adminConfig.Object));
    }
}

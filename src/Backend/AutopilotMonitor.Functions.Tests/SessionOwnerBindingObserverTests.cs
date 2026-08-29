using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// SESSION-OWNER-BINDING-SHADOW carriers: the outcome lands on the request row for every
/// request (denominator), non-Match outcomes produce a Warning, would-reject outcomes raise one
/// throttled ops event, stamping is fail-soft, and nothing is ever refused.
/// </summary>
public class SessionOwnerBindingObserverTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Session = "22222222-2222-2222-2222-222222222222";
    private const string Thumb1 = "AA11BB22CC33DD44EE55FF6677889900AABBCCDD";
    private const string Thumb2 = "0011223344556677889900AABBCCDDEEFF001122";

    private sealed class Rig
    {
        public List<OpsEventEntry> Ops { get; } = new();
        public Mock<ISessionRepository> Repo { get; } = new();
        public CapturingLogger Logger { get; } = new();
        public SessionOwnerBindingObserver Sut { get; }

        public Rig()
        {
            var opsRepo = new Mock<IOpsEventRepository>();
            opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
                .Callback<OpsEventEntry>(e => { lock (Ops) Ops.Add(e); })
                .Returns(Task.CompletedTask);
            var adminConfig = new Mock<AdminConfigurationService>(
                Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance,
                new MemoryCache(new MemoryCacheOptions())) { CallBase = false };
            var alertDispatch = new OpsAlertDispatchService(
                adminConfig.Object,
                new TelegramNotificationService(new HttpClient(), Mock.Of<IConfigRepository>(),
                    NullLogger<TelegramNotificationService>.Instance),
                new WebhookNotificationService(new HttpClient(), NullLogger<WebhookNotificationService>.Instance),
                NullLogger<OpsAlertDispatchService>.Instance);
            var opsService = new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, alertDispatch);

            Sut = new SessionOwnerBindingObserver(
                Logger, opsService, new MemoryCache(new MemoryCacheOptions()), Repo.Object);
        }
    }

    private static (HttpRequestData Req, Dictionary<object, object> Items) Request(string agentVersion = "2.0.9999")
    {
        var items = new Dictionary<object, object>();
        var contextMock = new Mock<Microsoft.Azure.Functions.Worker.FunctionContext>();
        contextMock.SetupGet(c => c.Items).Returns(items);
        var reqMock = new Mock<HttpRequestData>(contextMock.Object);
        reqMock.SetupGet(r => r.Headers).Returns(new HttpHeadersCollection { { "X-Agent-Version", agentVersion } });
        return (reqMock.Object, items);
    }

    private static SecurityValidationResult Cert(string thumb, string serial = "SN-1") => new()
    {
        IsValid = true, CertificateThumbprint = thumb, IntuneDeviceId = Guid.NewGuid().ToString(), SerialNumber = serial,
    };

    private static TableEntity OwnedRow(string thumb, string serial = "SN-1")
    {
        var row = new TableEntity(Tenant, Session) { ["SerialNumber"] = serial };
        SessionOwnershipPolicy.ApplyTo(row, new SessionOwner
        {
            Kind = SessionOwner.Kinds.Cert, Thumbprint = thumb, DeviceId = Guid.NewGuid().ToString(), Serial = serial, BoundAt = DateTime.UtcNow,
        });
        return row;
    }

    private static async Task<List<OpsEventEntry>> SettleAsync(Rig rig, int expected)
    {
        // The ops event is fire-and-forget; give the continuation a moment.
        for (var i = 0; i < 50; i++)
        {
            lock (rig.Ops) if (rig.Ops.Count >= expected) break;
            await Task.Delay(20);
        }
        lock (rig.Ops) return rig.Ops.ToList();
    }

    [Fact]
    public void Match_lands_on_the_request_row_and_stays_silent()
    {
        var rig = new Rig();
        var (req, items) = Request();

        var d = rig.Sut.Observe(req, Tenant, Session, OwnedRow(Thumb1), Cert(Thumb1), "agent/telemetry");

        Assert.Equal(SessionOwnershipPolicy.Outcome.Match, d.Outcome);
        Assert.Equal(SessionOwnershipPolicy.Outcome.Match, items[SessionOwnershipPolicy.RequestItemKey]);
        Assert.DoesNotContain(rig.Logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Empty(rig.Ops);
    }

    [Fact]
    public async Task Would_reject_outcome_warns_and_raises_one_ops_event_per_session_and_outcome()
    {
        var rig = new Rig();
        var (req, items) = Request("2.0.1500");
        var row = OwnedRow(Thumb1);

        var first = rig.Sut.Observe(req, Tenant, Session, row, Cert(Thumb2, "SN-ATTACKER"), "agent/telemetry");
        var second = rig.Sut.Observe(req, Tenant, Session, row, Cert(Thumb2, "SN-ATTACKER"), "agent/telemetry");

        Assert.Equal(SessionOwnershipPolicy.Outcome.MismatchCert, first.Outcome);
        Assert.True(first.WouldReject);
        Assert.Equal(SessionOwnershipPolicy.Outcome.MismatchCert, items[SessionOwnershipPolicy.RequestItemKey]);

        var warnings = rig.Logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Contains("AgentSessionOwnerBinding outcome=MismatchCert enforced=False wouldReject=True", w.Message));
        Assert.Contains("serialMatch=False", warnings[0].Message);
        Assert.Contains("ver=2.0.1500", warnings[0].Message);

        var ops = await SettleAsync(rig, 1);
        var evt = Assert.Single(ops);
        Assert.Equal("SessionOwnerMismatch", evt.EventType);
        Assert.Equal(OpsEventCategory.Security, evt.Category);
        Assert.Equal(Tenant, evt.TenantId);
        Assert.Contains("MismatchCert", evt.Message);
        Assert.Contains("shadow", evt.Message);
        Assert.DoesNotContain(Thumb1, evt.Message);
        Assert.DoesNotContain(Thumb2, evt.Message);
        Assert.Contains("\"serialMatch\":false", evt.Details);
        Assert.Contains("\"enforced\":false", evt.Details);
        _ = second;
    }

    [Fact]
    public void Rebind_warns_but_raises_no_ops_event_and_hands_back_the_owner()
    {
        var rig = new Rig();
        var (req, _) = Request();
        var row = new TableEntity(Tenant, Session) { ["SerialNumber"] = "SN-1" };
        SessionOwnershipPolicy.ApplyTo(row, new SessionOwner { Kind = SessionOwner.Kinds.Bootstrap, BootstrapCode = "ABC123", Serial = "SN-1", BoundAt = DateTime.UtcNow });

        var d = rig.Sut.Observe(req, Tenant, Session, row, Cert(Thumb1), "agent/register-session");

        Assert.Equal(SessionOwnershipPolicy.Outcome.RebindBootstrapHandoff, d.Outcome);
        Assert.NotNull(d.OwnerToStamp);
        Assert.Contains(rig.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("wouldReject=False"));
        Assert.Empty(rig.Ops);
    }

    [Fact]
    public async Task Stamp_writes_the_owner_and_swallows_storage_failures()
    {
        var rig = new Rig();
        rig.Repo.Setup(r => r.UpdateSessionOwnerAsync(Tenant, Session, It.IsAny<SessionOwner>()))
            .ThrowsAsync(new InvalidOperationException("storage down"));
        var (req, _) = Request();
        var legacyRow = new TableEntity(Tenant, Session) { ["SerialNumber"] = "SN-1" };

        var d = rig.Sut.Observe(req, Tenant, Session, legacyRow, Cert(Thumb1), "agent/telemetry");
        Assert.Equal(SessionOwnershipPolicy.Outcome.ClaimLegacy, d.Outcome);

        await rig.Sut.StampAsync(Tenant, Session, d);

        rig.Repo.Verify(r => r.UpdateSessionOwnerAsync(Tenant, Session, It.Is<SessionOwner>(o => o.Thumbprint == Thumb1)), Times.Once);
        Assert.Contains(rig.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("stamp failed"));
    }

    [Fact]
    public async Task Stamp_is_a_no_op_without_an_owner()
    {
        var rig = new Rig();
        await rig.Sut.StampAsync(Tenant, Session, new SessionOwnershipPolicy.Decision(SessionOwnershipPolicy.Outcome.Match, null, true));
        rig.Repo.Verify(r => r.UpdateSessionOwnerAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SessionOwner>()), Times.Never);
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger<SessionOwnerBindingObserver>
    {
        public List<LogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}

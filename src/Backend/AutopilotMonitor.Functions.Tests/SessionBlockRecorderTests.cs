using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Analyze;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The session's record of a device block — the tenant's only view of it: the event the
/// timeline shows, the writer that must never undo or fail the block, the status reason of the
/// session it silenced, and which sessions an operator block lands on.
/// </summary>
public class SessionBlockRecorderTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";
    private const string Serial = "SN-0001";

    private static readonly DateTime BlockedAt = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    private static SessionSummary Session(string serial = Serial) => new()
    {
        TenantId = TenantId,
        SessionId = SessionId,
        SerialNumber = serial,
        StartedAt = BlockedAt.AddHours(-14),
    };

    private static SessionBlockRecord EventLimit(string action) =>
        MaintenanceService.BuildEventLimitBlockRecord(action, BlockedAt, durationHours: 24, eventCount: 2553, threshold: 2500);

    private static SessionBlockRecord Operator(string action, int hours = 12) => new()
    {
        BlockedAt = BlockedAt,
        Action = action,
        BlockedUntil = BlockedAt.AddHours(hours),
        Cause = SessionBlockCauses.Operator,
    };

    private static List<EnrollmentEvent> Events(params long[] sequences) =>
        sequences.Select(s => new EnrollmentEvent { SessionId = SessionId, TenantId = TenantId, Sequence = s }).ToList();

    // ── The timeline event ───────────────────────────────────────────────────

    [Fact]
    public void EventLimitBlock_ExplainsVolumeAndWindow_AndSortsLast()
    {
        var evt = SessionBlockRecorder.BuildDeviceBlockedEvent(Session(), EventLimit(DeviceBlockActions.Block), Events(3, 2553, 17));

        Assert.Equal(Constants.EventTypes.DeviceBlocked, evt.EventType);
        Assert.Equal("Monitoring paused: this session sent 2,553 events (limit 2,500), so the device's uploads are blocked for 24 h.", evt.Message);
        Assert.Equal(EventSeverity.Warning, evt.Severity);
        Assert.Equal("System.Maintenance", evt.Source);
        Assert.Equal(2554, evt.Sequence);
        Assert.Equal(BlockedAt, evt.Timestamp);
        Assert.Equal(DeviceBlockActions.Block, evt.Data["action"]);
        Assert.Equal(SessionBlockCauses.EventLimit, evt.Data["cause"]);
        Assert.Equal(BlockedAt.AddHours(24).ToString("o"), evt.Data["blockedUntil"]);
        Assert.Equal(2553, evt.Data["eventCount"]);
        Assert.Equal(2500, evt.Data["threshold"]);
    }

    [Fact]
    public void EventLimitKill_SaysTheAgentLeaves_AndTheDeviceStaysUnmonitored()
    {
        // A Kill is device-wide until it expires: a new enrollment's agent gets the kill signal too,
        // so the record must carry the end like a Block does.
        var record = EventLimit(DeviceBlockActions.Kill);
        var evt = SessionBlockRecorder.BuildDeviceBlockedEvent(Session(), record, Events(1));

        Assert.Equal(BlockedAt.AddHours(24), record.BlockedUntil);
        Assert.Equal("Monitoring ended: this session sent 2,553 events (limit 2,500); the agent removes itself and the device stays unmonitored for 24 h.", evt.Message);
        Assert.Equal(BlockedAt.AddHours(24).ToString("o"), evt.Data["blockedUntil"]);
    }

    [Fact]
    public void OperatorBlock_NamesNoVolume_EvenWhenCountsAreSet()
    {
        var record = Operator(DeviceBlockActions.Block);
        record.EventCount = 9999;
        record.EventThreshold = 2500;

        var evt = SessionBlockRecorder.BuildDeviceBlockedEvent(Session(), record, Events());

        Assert.Equal("Monitoring paused by the platform operator for 12 h.", evt.Message);
        Assert.Equal("System.Operator", evt.Source);
        Assert.Equal(1, evt.Sequence);
        Assert.False(evt.Data.ContainsKey("eventCount"));
        Assert.False(evt.Data.ContainsKey("threshold"));
    }

    [Fact]
    public void OperatorKill_SaysTheAgentLeaves()
    {
        var evt = SessionBlockRecorder.BuildDeviceBlockedEvent(Session(), Operator(DeviceBlockActions.Kill), Events(5));

        Assert.Equal("Monitoring ended by the platform operator; the agent removes itself and the device stays unmonitored for 12 h.", evt.Message);
    }

    // ── The writer ───────────────────────────────────────────────────────────

    private static Mock<ISessionRepository> Repo(SessionSummary? session, bool recordWrites = true)
    {
        var repo = new Mock<ISessionRepository>();
        repo.Setup(r => r.GetSessionAsync(TenantId, SessionId)).ReturnsAsync(session);
        repo.Setup(r => r.RecordSessionBlockAsync(TenantId, SessionId, It.IsAny<SessionBlockRecord>())).ReturnsAsync(recordWrites);
        repo.Setup(r => r.GetSessionEventsAsync(TenantId, SessionId, It.IsAny<int>())).ReturnsAsync(Events(1, 2));
        repo.Setup(r => r.StoreEventsBatchAsync(It.IsAny<List<EnrollmentEvent>>()))
            .ReturnsAsync((List<EnrollmentEvent> e) => e);
        return repo;
    }

    private static SessionBlockRecorder Recorder(Mock<ISessionRepository> repo, Mock<IAnalyzeOnEnrollmentEndProducer>? producer = null) =>
        new(repo.Object, (producer ?? new Mock<IAnalyzeOnEnrollmentEndProducer>()).Object, NullLogger<SessionBlockRecorder>.Instance);

    [Fact]
    public async Task Record_WritesFieldsThenTimelineEvent_ThenAnInterimAnalyzeRun()
    {
        var repo = Repo(Session());
        var producer = new Mock<IAnalyzeOnEnrollmentEndProducer>();

        await Recorder(repo, producer).RecordAsync(TenantId, SessionId, Serial, EventLimit(DeviceBlockActions.Block));

        repo.Verify(r => r.RecordSessionBlockAsync(TenantId, SessionId,
            It.Is<SessionBlockRecord>(b => b.Cause == SessionBlockCauses.EventLimit && b.EventCount == 2553)), Times.Once);
        repo.Verify(r => r.StoreEventsBatchAsync(It.Is<List<EnrollmentEvent>>(e =>
            e.Count == 1 && e[0].EventType == Constants.EventTypes.DeviceBlocked && e[0].Sequence == 3)), Times.Once);
        repo.Verify(r => r.UpsertEventTypeIndexBatchAsync(TenantId, SessionId, It.IsAny<IEnumerable<EnrollmentEvent>>()), Times.Once);
        repo.Verify(r => r.ReconcileSessionCountersAsync(TenantId, SessionId), Times.Once);
        // The event bypasses ingest, so nothing else would start the on_event:device_blocked rules.
        producer.Verify(p => p.EnqueueAsync(It.Is<AnalyzeOnEnrollmentEndEnvelope>(e =>
            e.SessionId == SessionId && e.Reason == AnalyzeOnEnrollmentEndHandler.ReasonInterimTrigger
            && e.TriggerEventTypes != null && e.TriggerEventTypes.Single() == Constants.EventTypes.DeviceBlocked),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Record_SerialComparisonIgnoresCaseAndPadding()
    {
        var repo = Repo(Session(" sn-0001 "));

        await Recorder(repo).RecordAsync(TenantId, SessionId, Serial, EventLimit(DeviceBlockActions.Block));

        repo.Verify(r => r.RecordSessionBlockAsync(TenantId, SessionId, It.IsAny<SessionBlockRecord>()), Times.Once);
    }

    [Fact]
    public async Task Record_SessionOfAnotherDevice_WritesNothing()
    {
        // The session id comes from the caller (an operator's form): it must not stamp a block
        // onto a session the blocked device never ran.
        var repo = Repo(Session("SN-OTHER"));

        await Recorder(repo).RecordAsync(TenantId, SessionId, Serial, Operator(DeviceBlockActions.Block));

        repo.Verify(r => r.RecordSessionBlockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SessionBlockRecord>()), Times.Never);
        repo.Verify(r => r.StoreEventsBatchAsync(It.IsAny<List<EnrollmentEvent>>()), Times.Never);
    }

    [Fact]
    public async Task Record_UnknownSession_WritesNothing()
    {
        var repo = Repo(session: null);

        await Recorder(repo).RecordAsync(TenantId, SessionId, Serial, Operator(DeviceBlockActions.Block));

        repo.Verify(r => r.RecordSessionBlockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SessionBlockRecord>()), Times.Never);
        repo.Verify(r => r.StoreEventsBatchAsync(It.IsAny<List<EnrollmentEvent>>()), Times.Never);
    }

    [Fact]
    public async Task Record_FieldWriteFailed_StoresNoEvent()
    {
        // The card and the timeline must tell the same story: no event without the record.
        var repo = Repo(Session(), recordWrites: false);

        await Recorder(repo).RecordAsync(TenantId, SessionId, Serial, EventLimit(DeviceBlockActions.Block));

        repo.Verify(r => r.StoreEventsBatchAsync(It.IsAny<List<EnrollmentEvent>>()), Times.Never);
    }

    [Fact]
    public async Task Record_StorageFailure_NeverThrows()
    {
        // Called after the block is in force — a throw here would abort the sweep's tenant loop
        // or fail the operator's block request although the block already landed.
        var repo = Repo(Session());
        repo.Setup(r => r.StoreEventsBatchAsync(It.IsAny<List<EnrollmentEvent>>())).ThrowsAsync(new InvalidOperationException("storage down"));

        await Recorder(repo).RecordAsync(TenantId, SessionId, Serial, EventLimit(DeviceBlockActions.Block));
    }

    // ── The status reason of the silenced session ────────────────────────────

    private static SessionSummary Blocked(string action, string cause, DateTime? lastIngestAt)
    {
        var s = Session();
        s.BlockedAt = BlockedAt;
        s.BlockAction = action;
        s.BlockCause = cause;
        s.BlockedUntil = BlockedAt.AddHours(24);
        s.LastIngestAt = lastIngestAt;
        return s;
    }

    [Fact]
    public void Reason_NeverBlocked_IsNull()
    {
        Assert.Null(MaintenanceService.DescribeBlockedMonitoring(Session()));
    }

    [Fact]
    public void Reason_EventLimitBlock()
    {
        Assert.Equal("Monitoring paused by the platform's event limit",
            MaintenanceService.DescribeBlockedMonitoring(Blocked(DeviceBlockActions.Block, SessionBlockCauses.EventLimit, BlockedAt.AddMinutes(-65))));
    }

    [Fact]
    public void Reason_OperatorKill()
    {
        Assert.Equal("Monitoring ended by the platform operator",
            MaintenanceService.DescribeBlockedMonitoring(Blocked(DeviceBlockActions.Kill, SessionBlockCauses.Operator, BlockedAt.AddMinutes(-5))));
    }

    [Fact]
    public void Reason_AgentCameBackAfterTheBlock_IsNotTheBlock()
    {
        // A still-running agent resumes uploading once the block ends; if it goes silent later,
        // that silence has its own cause.
        Assert.Null(MaintenanceService.DescribeBlockedMonitoring(
            Blocked(DeviceBlockActions.Block, SessionBlockCauses.EventLimit, BlockedAt.AddHours(25))));
    }

    // ── Which sessions an operator block lands on ────────────────────────────

    [Fact]
    public void Operator_NamedSession_IsRecorded()
    {
        Assert.Equal(new[] { SessionId },
            DeviceBlockFunction.SessionsToRecord(SessionId, DeviceBlockActions.Block, alreadyBlocked: false, existingBlockedSessionIds: null));
    }

    [Fact]
    public void Operator_WholeDeviceBlock_IsRecordedNowhere()
    {
        Assert.Empty(DeviceBlockFunction.SessionsToRecord(null, DeviceBlockActions.Block, alreadyBlocked: true, existingBlockedSessionIds: "a,b"));
    }

    [Fact]
    public void Operator_KillUpgrade_UpdatesTheSessionsTheBlockNamed()
    {
        Assert.Equal(new[] { "a", "b" },
            DeviceBlockFunction.SessionsToRecord(null, DeviceBlockActions.Kill, alreadyBlocked: true, existingBlockedSessionIds: "a, b,,A"));
    }

    [Theory]
    [InlineData(false, "a")]  // nothing to upgrade
    [InlineData(true, null)]  // the existing block already covers the whole device
    public void Operator_KillWithoutSessionScope_IsRecordedNowhere(bool alreadyBlocked, string? existing)
    {
        Assert.Empty(DeviceBlockFunction.SessionsToRecord(null, DeviceBlockActions.Kill, alreadyBlocked, existing));
    }
}

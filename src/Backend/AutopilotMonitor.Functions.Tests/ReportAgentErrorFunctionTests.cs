using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins <see cref="ReportAgentErrorFunction.BuildAgentEmergencyBreakEvent"/> — the backend-materialized
/// timeline event synthesized from the agent's best-effort 48h emergency-break report
/// (tasks/enrollment-status-reclassification.md). The load-bearing bits are the Sequence assignment
/// (must sort AFTER the session's last event) and that it is a Warning, non-terminal marker — the timeout
/// classifier, not this event, decides the real verdict.
/// </summary>
public class ReportAgentErrorFunctionTests
{
    private const string TenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    private static AgentErrorReport Report(DateTime ts, string? message = null) => new()
    {
        SessionId = SessionId,
        TenantId = TenantId,
        ErrorType = AgentErrorType.SessionAgeEmergencyBreak,
        Message = message!,
        AgentVersion = "2.0.1236",
        Timestamp = ts,
    };

    private static readonly DateTime Now = new(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Sequence_is_one_past_the_session_max()
    {
        var existing = new List<EnrollmentEvent>
        {
            new() { Sequence = 5 }, new() { Sequence = 88 }, new() { Sequence = 12 },
        };
        var evt = ReportAgentErrorFunction.BuildAgentEmergencyBreakEvent(
            Report(Now.AddMinutes(-1)), TenantId, existing, Now);
        Assert.Equal(89, evt.Sequence);
    }

    [Fact]
    public void Sequence_is_one_when_no_prior_events()
    {
        Assert.Equal(1, ReportAgentErrorFunction.BuildAgentEmergencyBreakEvent(
            Report(Now), TenantId, new List<EnrollmentEvent>(), Now).Sequence);
    }

    [Fact]
    public void Shape_is_warning_non_terminal_agent_break()
    {
        var evt = ReportAgentErrorFunction.BuildAgentEmergencyBreakEvent(Report(Now), TenantId, null!, Now);

        Assert.Equal("agent_emergency_break", evt.EventType);
        Assert.Equal(AutopilotMonitor.Shared.Constants.EventTypes.AgentEmergencyBreak, evt.EventType);
        Assert.Equal(EventSeverity.Warning, evt.Severity);
        Assert.Equal(EnrollmentPhase.Unknown, evt.Phase);
        Assert.Equal(TenantId, evt.TenantId);
        Assert.Equal(SessionId, evt.SessionId);
        Assert.Equal("emergency_channel", evt.Data["source"]);
    }

    [Fact]
    public void Uses_agent_break_timestamp_when_valid_else_receipt_time()
    {
        var breakTime = Now.AddHours(-2);
        Assert.Equal(breakTime, ReportAgentErrorFunction.BuildAgentEmergencyBreakEvent(
            Report(breakTime), TenantId, null!, Now).Timestamp);

        // Bogus/default timestamp → fall back to receipt time (now).
        Assert.Equal(Now, ReportAgentErrorFunction.BuildAgentEmergencyBreakEvent(
            Report(default), TenantId, null!, Now).Timestamp);
    }

    [Fact]
    public void Falls_back_to_default_message_when_report_message_blank()
    {
        var evt = ReportAgentErrorFunction.BuildAgentEmergencyBreakEvent(Report(Now, message: ""), TenantId, null!, Now);
        Assert.Contains("emergency break", evt.Message, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // MaterializeEmergencyBreakArtifactsAsync — the artifact set per break report
    // (2026-07-23 hardening): timeline event + cross-session EventType index row +
    // AgentEmergencyBreak ops event, once per session. The index upsert is load-bearing:
    // StoreEventsBatchAsync alone leaves the event invisible to every search-by-eventType
    // surface (found in the 2026-07-22 incident analysis).
    // =========================================================================

    private sealed class Harness
    {
        public readonly Mock<ISessionRepository> SessionRepo = new();
        public readonly List<OpsEventEntry> OpsEvents = new();
        public readonly OpsEventService OpsService;

        public Harness(List<EnrollmentEvent>? existingEvents = null)
        {
            SessionRepo.Setup(r => r.GetSessionEventsAsync(TenantId, SessionId, It.IsAny<int>()))
                .ReturnsAsync(existingEvents ?? new List<EnrollmentEvent>());
            SessionRepo.Setup(r => r.StoreEventsBatchAsync(It.IsAny<List<EnrollmentEvent>>()))
                .ReturnsAsync((List<EnrollmentEvent> e) => e);
            SessionRepo.Setup(r => r.UpsertEventTypeIndexBatchAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<EnrollmentEvent>>()))
                .Returns(Task.CompletedTask);

            var opsRepo = new Mock<IOpsEventRepository>();
            opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
                .Callback<OpsEventEntry>(e => { lock (OpsEvents) OpsEvents.Add(e); })
                .Returns(Task.CompletedTask);

            var adminConfig = new Mock<AdminConfigurationService>(
                Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance,
                new MemoryCache(new MemoryCacheOptions()));
            var alertDispatch = new OpsAlertDispatchService(
                adminConfig.Object,
                new TelegramNotificationService(new HttpClient(), Mock.Of<IConfigRepository>(),
                    NullLogger<TelegramNotificationService>.Instance),
                new WebhookNotificationService(new HttpClient(),
                    NullLogger<WebhookNotificationService>.Instance),
                NullLogger<OpsAlertDispatchService>.Instance);
            OpsService = new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, alertDispatch);
        }

        public Task RunAsync(AgentErrorReport report)
            => ReportAgentErrorFunction.MaterializeEmergencyBreakArtifactsAsync(
                report, TenantId, SessionRepo.Object, OpsService, NullLogger<ReportAgentErrorFunction>.Instance);
    }

    [Fact]
    public async Task First_break_report_materializes_event_index_row_and_ops_event()
    {
        var h = new Harness();

        await h.RunAsync(Report(Now));

        h.SessionRepo.Verify(r => r.StoreEventsBatchAsync(
            It.Is<List<EnrollmentEvent>>(e => e.Count == 1
                && e[0].EventType == AutopilotMonitor.Shared.Constants.EventTypes.AgentEmergencyBreak)), Times.Once);
        h.SessionRepo.Verify(r => r.UpsertEventTypeIndexBatchAsync(
            TenantId, SessionId, It.IsAny<IEnumerable<EnrollmentEvent>>()), Times.Once);

        var ops = Assert.Single(h.OpsEvents);
        Assert.Equal("AgentEmergencyBreak", ops.EventType);
        Assert.Equal(TenantId, ops.TenantId);
    }

    [Fact]
    public async Task Already_materialized_session_produces_no_second_artifact_set()
    {
        var h = new Harness(new List<EnrollmentEvent>
        {
            new() { EventType = AutopilotMonitor.Shared.Constants.EventTypes.AgentEmergencyBreak, Sequence = 45 },
        });

        await h.RunAsync(Report(Now));

        h.SessionRepo.Verify(r => r.StoreEventsBatchAsync(It.IsAny<List<EnrollmentEvent>>()), Times.Never);
        h.SessionRepo.Verify(r => r.UpsertEventTypeIndexBatchAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<EnrollmentEvent>>()), Times.Never);
        Assert.Empty(h.OpsEvents);
    }

    [Fact]
    public async Task Non_break_error_types_and_missing_session_id_are_ignored()
    {
        var h = new Harness();

        var other = Report(Now);
        other.ErrorType = AgentErrorType.ConfigFetchFailed;
        await h.RunAsync(other);

        var noSession = Report(Now);
        noSession.SessionId = null!;
        await h.RunAsync(noSession);

        h.SessionRepo.Verify(r => r.GetSessionEventsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        Assert.Empty(h.OpsEvents);
    }

    [Fact]
    public async Task Storage_failure_is_swallowed_and_never_throws()
    {
        var h = new Harness();
        h.SessionRepo.Setup(r => r.StoreEventsBatchAsync(It.IsAny<List<EnrollmentEvent>>()))
            .ThrowsAsync(new InvalidOperationException("storage down"));

        // Must not throw — the always-200 emergency channel must never turn into a retry loop.
        await h.RunAsync(Report(Now));

        Assert.Empty(h.OpsEvents);
    }

    // =========================================================================
    // MaterializeIntegrityMismatchAsync — an IntegrityCheckFailed report becomes an
    // AgentBinaryIntegrityMismatch ops event, UNLESS it is the known release race
    // (agents comparing against hash snapshots that don't belong to their own binary;
    // AdminConfig cache TTL 5 min). e9753578 proved the race: a genuine 1410 exe was
    // compared against the cached 1409 hash and reported a "mismatch" that cost a day
    // of mis-attributed root-causing while sitting invisible in App Insights.
    // =========================================================================

    private const string RunningExeSha = "5fca975e43fa5fca975e43fa5fca975e43fa5fca975e43fa5fca975e43fa0000";
    private const string PublishedExeSha = "8ebaa060ad348ebaa060ad348ebaa060ad348ebaa060ad348ebaa060ad340000";

    private static AgentErrorReport IntegrityReport(
        string? sessionId = SessionId, string agentVersion = "2.0.1410", string? actualSha = null) => new()
    {
        SessionId = sessionId!,
        TenantId = TenantId,
        ErrorType = AgentErrorType.IntegrityCheckFailed,
        Message = $"Running exe SHA-256 differs from backend-advertised hash. actual={actualSha ?? RunningExeSha}, expected={PublishedExeSha}",
        AgentVersion = agentVersion,
        Timestamp = Now,
    };

    private static Task RunIntegrityAsync(
        Harness h, AgentErrorReport report,
        string? latestVersion = "2.0.1410", string? latestExeSha = PublishedExeSha)
        => ReportAgentErrorFunction.MaterializeIntegrityMismatchAsync(
            report, TenantId, h.OpsService, latestVersion, latestExeSha,
            NullLogger<ReportAgentErrorFunction>.Instance);

    [Fact]
    public async Task SameVersion_with_unknown_binary_records_ops_event()
    {
        // The real signal: an agent claiming the LATEST version whose running exe is NOT
        // the published one — tamper, stale blob, or a build outside the release pipeline.
        var h = new Harness();

        await RunIntegrityAsync(h, IntegrityReport());

        var ops = Assert.Single(h.OpsEvents);
        Assert.Equal("AgentBinaryIntegrityMismatch", ops.EventType);
        Assert.Equal(TenantId, ops.TenantId);
        Assert.Contains("actual=5fca", ops.Message);
        Assert.Contains("2.0.1410", ops.Message);
    }

    [Fact]
    public async Task RunningHash_equal_to_published_exe_is_suppressed_as_stale_expectation()
    {
        // e9753578's exact shape: the device RUNS the published binary; only its cached
        // expectation was the previous release's hash. Self-heals — must not alert.
        var h = new Harness();

        await RunIntegrityAsync(h, IntegrityReport(actualSha: PublishedExeSha));

        Assert.Empty(h.OpsEvents);
    }

    [Fact]
    public async Task Older_agent_version_than_latest_is_suppressed_as_update_race()
    {
        // The long-known direction: version N agent sees version N+1's hash right after a
        // release; the forced self-update heals it.
        var h = new Harness();

        await RunIntegrityAsync(h, IntegrityReport(agentVersion: "2.0.1409+d43cfe2b"), latestVersion: "2.0.1410");

        Assert.Empty(h.OpsEvents);
    }

    [Fact]
    public async Task Version_with_commit_suffix_matches_latest_without_suffix()
    {
        // Agent versions carry "+<commit>", AdminConfiguration does not — the comparison
        // must normalize, otherwise every genuine mismatch would look like a version race.
        var h = new Harness();

        await RunIntegrityAsync(h, IntegrityReport(agentVersion: "2.0.1410+3b59dab2df6c"));

        Assert.Single(h.OpsEvents);
    }

    [Fact]
    public async Task Missing_latest_config_fails_open_to_recording()
    {
        // No published-version info available → visibility wins over suppression.
        var h = new Harness();

        await RunIntegrityAsync(h, IntegrityReport(), latestVersion: null, latestExeSha: null);

        Assert.Single(h.OpsEvents);
    }

    [Fact]
    public async Task Integrity_mismatch_without_session_id_still_records_ops_event()
    {
        // The report can arrive before session registration — the binary evidence must
        // never be dropped for lack of a session.
        var h = new Harness();

        await RunIntegrityAsync(h, IntegrityReport(sessionId: null));

        var ops = Assert.Single(h.OpsEvents);
        Assert.Equal("AgentBinaryIntegrityMismatch", ops.EventType);
    }

    [Fact]
    public async Task Other_error_types_do_not_record_integrity_ops_event()
    {
        var h = new Harness();

        await RunIntegrityAsync(h, Report(Now));

        Assert.Empty(h.OpsEvents);
    }
}

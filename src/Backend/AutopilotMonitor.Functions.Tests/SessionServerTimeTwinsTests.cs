using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The server-clock twins (StartedAtServer / ResumedAtServer / CompletedAtServer) on the session row:
/// ingest feeds each upload to the session's <see cref="ServerTime.Tracker"/> in the counter merge
/// while the session runs, for sessions it follows from their first upload; once CompletedAt is set,
/// the counter reconcile sets them from the full event stream. The tracker state stays on the
/// primary row, the twins reach the index.
/// </summary>
public class SessionServerTimeTwinsTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    // Device clock 9 h ahead of the server (time zone in the hardware clock), constant all session.
    private static readonly TimeSpan Ahead = TimeSpan.FromHours(9);
    private static readonly DateTime ServerStart = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DeviceStart = ServerStart + Ahead;

    // ==================================================================== ingest ====

    [Fact]
    public async Task Ingest_sets_the_start_on_the_server_clock_and_keeps_the_tracker_on_the_primary_row()
    {
        var harness = new Harness(RunningRow());

        await harness.Sut.IncrementSessionEventCountAsync(TenantId, SessionId, increment: 2, upload: FirstUpload());

        Assert.NotNull(harness.Written);
        Assert.Equal(new DateTimeOffset(ServerStart), harness.Written!.GetDateTimeOffset("StartedAtServer"));
        Assert.False(string.IsNullOrEmpty(harness.Written.GetString(TableStorageService.ServerTimeStateColumn)));
        // The twin is mirrored, the working state is not.
        Assert.NotNull(harness.IndexWritten);
        Assert.Equal(new DateTimeOffset(ServerStart), harness.IndexWritten!.GetDateTimeOffset("StartedAtServer"));
        Assert.False(harness.IndexWritten.ContainsKey(TableStorageService.ServerTimeStateColumn));
    }

    [Fact]
    public async Task Ingest_continues_from_the_stored_tracker()
    {
        var first = new Harness(RunningRow());
        await first.Sut.IncrementSessionEventCountAsync(TenantId, SessionId, increment: 2, upload: FirstUpload());
        var row = RunningRow(eventCount: 2);
        row[TableStorageService.ServerTimeStateColumn] = first.Written!.GetString(TableStorageService.ServerTimeStateColumn);
        row["StartedAtServer"] = first.Written.GetDateTimeOffset("StartedAtServer");

        // A later upload with an earlier activity event (IME log replay, 10 min before the agent start).
        var second = new Harness(row);
        await second.Sut.IncrementSessionEventCountAsync(TenantId, SessionId, increment: 1, upload: new List<EnrollmentEvent>
        {
            Event(3, "ime_agent_version", "ImeLogTracker", DeviceStart.AddMinutes(-10), sent: DeviceStart.AddMinutes(1), received: ServerStart.AddMinutes(1)),
        });

        Assert.Equal(new DateTimeOffset(ServerStart.AddMinutes(-10)), second.Written!.GetDateTimeOffset("StartedAtServer"));
    }

    [Fact]
    public async Task Ingest_does_not_follow_a_session_already_under_way()
    {
        // Every session in flight when this build arrives: its first uploads were never absorbed, so
        // a tracker starting now would take a later upload for the start (hours off).
        var harness = new Harness(RunningRow(eventCount: 10));

        await harness.Sut.IncrementSessionEventCountAsync(TenantId, SessionId, increment: 2, upload: FirstUpload());

        Assert.Equal(12, harness.Written!.GetInt32("EventCount"));
        Assert.False(harness.Written.ContainsKey("StartedAtServer"));
        Assert.False(harness.Written.ContainsKey(TableStorageService.ServerTimeStateColumn));
    }

    [Fact]
    public async Task Ingest_does_not_follow_a_session_whose_first_upload_has_no_agent_start()
    {
        var harness = new Harness(RunningRow());

        await harness.Sut.IncrementSessionEventCountAsync(TenantId, SessionId, increment: 1, upload: new List<EnrollmentEvent>
        {
            Event(1, "ime_agent_version", "ImeLogTracker", DeviceStart.AddMinutes(-5), sent: DeviceStart, received: ServerStart),
        });

        Assert.Equal(1, harness.Written!.GetInt32("EventCount"));
        Assert.False(harness.Written.ContainsKey("StartedAtServer"));
        Assert.False(harness.Written.ContainsKey(TableStorageService.ServerTimeStateColumn));
    }

    [Fact]
    public async Task Ingest_leaves_a_tracker_of_another_format_alone()
    {
        var row = RunningRow(eventCount: 2);
        row[TableStorageService.ServerTimeStateColumn] = "{\"version\":2,\"runs\":1}";
        var harness = new Harness(row);

        await harness.Sut.IncrementSessionEventCountAsync(TenantId, SessionId, increment: 1, upload: FirstUpload());

        Assert.Equal(3, harness.Written!.GetInt32("EventCount"));
        Assert.False(harness.Written.ContainsKey(TableStorageService.ServerTimeStateColumn));
        Assert.False(harness.Written.ContainsKey("StartedAtServer"));
    }

    [Fact]
    public async Task Ingest_leaves_the_twins_to_the_reconcile_once_the_session_completed()
    {
        var row = RunningRow();
        row["CompletedAt"] = new DateTimeOffset(DeviceStart.AddMinutes(40));
        var harness = new Harness(row);

        await harness.Sut.IncrementSessionEventCountAsync(TenantId, SessionId, increment: 2, upload: FirstUpload());

        Assert.Equal(2, harness.Written!.GetInt32("EventCount"));
        Assert.False(harness.Written.ContainsKey("StartedAtServer"));
        Assert.False(harness.Written.ContainsKey(TableStorageService.ServerTimeStateColumn));
    }

    [Fact]
    public async Task Ingest_without_send_time_writes_no_tracker()
    {
        var harness = new Harness(RunningRow());
        var oldAgentUpload = FirstUpload().Select(e => { e.SentAt = null; return e; }).ToList();

        await harness.Sut.IncrementSessionEventCountAsync(TenantId, SessionId, increment: 2, upload: oldAgentUpload);

        Assert.Equal(2, harness.Written!.GetInt32("EventCount"));
        Assert.False(harness.Written.ContainsKey("StartedAtServer"));
        Assert.False(harness.Written.ContainsKey(TableStorageService.ServerTimeStateColumn));
    }

    [Fact]
    public async Task Ingest_keeps_an_unreadable_tracker_and_still_counts()
    {
        var row = RunningRow(eventCount: 10);
        row[TableStorageService.ServerTimeStateColumn] = "{";
        var harness = new Harness(row);

        await harness.Sut.IncrementSessionEventCountAsync(TenantId, SessionId, increment: 2, upload: FirstUpload());

        Assert.Equal(12, harness.Written!.GetInt32("EventCount"));
        Assert.False(harness.Written.ContainsKey(TableStorageService.ServerTimeStateColumn));
        Assert.False(harness.Written.ContainsKey("StartedAtServer"));
    }

    // ================================================================= reconcile ====

    [Fact]
    public async Task Reconcile_sets_start_and_end_from_the_full_event_stream_once_completed()
    {
        var row = RunningRow();
        row["Status"] = "Succeeded";
        row["CompletedAt"] = new DateTimeOffset(DeviceStart.AddMinutes(40));
        row["EventCount"] = 3;
        var harness = new Harness(row, CompletedSessionRows());

        await harness.Sut.ReconcileSessionCountersAsync(TenantId, SessionId);

        Assert.NotNull(harness.Written);
        Assert.Equal(new DateTimeOffset(ServerStart), harness.Written!.GetDateTimeOffset("StartedAtServer"));
        Assert.Equal(new DateTimeOffset(ServerStart.AddMinutes(40)), harness.Written.GetDateTimeOffset("CompletedAtServer"));
        Assert.False(harness.Written.ContainsKey("EventCount")); // counters were already right
        Assert.Equal(new DateTimeOffset(ServerStart.AddMinutes(40)), harness.IndexWritten!.GetDateTimeOffset("CompletedAtServer"));
    }

    [Fact]
    public async Task Reconcile_leaves_the_twins_to_ingest_while_the_session_runs()
    {
        var row = RunningRow();
        row["EventCount"] = 3;
        var harness = new Harness(row, CompletedSessionRows());

        await harness.Sut.ReconcileSessionCountersAsync(TenantId, SessionId);

        Assert.Null(harness.Written); // counters right, twins not its business yet → no write
    }

    [Fact]
    public async Task Reconcile_replay_with_twins_already_right_writes_nothing()
    {
        var row = RunningRow();
        row["Status"] = "Succeeded";
        row["CompletedAt"] = new DateTimeOffset(DeviceStart.AddMinutes(40));
        row["EventCount"] = 3;
        row["StartedAtServer"] = new DateTimeOffset(ServerStart);
        row["CompletedAtServer"] = new DateTimeOffset(ServerStart.AddMinutes(40));
        var harness = new Harness(row, CompletedSessionRows());

        await harness.Sut.ReconcileSessionCountersAsync(TenantId, SessionId);

        Assert.Null(harness.Written);
    }

    [Fact]
    public async Task Reconcile_sets_the_part_2_start_even_when_part_2_does_not_fit_the_duration()
    {
        // WhiteGlove Part 2 on a clock 4 days back, closed by the sweep at LastEventAt (a Part 1 device
        // time): the end stays in Part 1, the Part 2 start is still a fact — the same one ingest's
        // tracker wrote live, so the twins never contradict each other after completion.
        var resumedServer = ServerStart.AddDays(2);
        var resumedDevice = resumedServer.AddDays(-4);
        var row = RunningRow();
        row["Status"] = "Failed";
        row["IsPreProvisioned"] = true;
        row["CompletedAt"] = new DateTimeOffset(DeviceStart.AddMinutes(40));
        row["EventCount"] = 5;
        var harness = new Harness(row, new[]
        {
            EventRow(1, "agent_started", "Agent", DeviceStart, DeviceStart.AddSeconds(10), ServerStart.AddSeconds(10)),
            EventRow(2, "performance_snapshot", "PerformanceCollector", DeviceStart.AddSeconds(5), DeviceStart.AddSeconds(10), ServerStart.AddSeconds(10)),
            EventRow(3, "whiteglove_complete", "EspAndHelloTracker", DeviceStart.AddMinutes(40), DeviceStart.AddMinutes(40).AddSeconds(1), ServerStart.AddMinutes(40).AddSeconds(1)),
            EventRow(4, "agent_started", "Agent", resumedDevice, resumedDevice.AddSeconds(10), resumedServer.AddSeconds(10)),
            EventRow(5, "performance_snapshot", "PerformanceCollector", resumedDevice.AddMinutes(5), resumedDevice.AddMinutes(5).AddSeconds(1), resumedServer.AddMinutes(5).AddSeconds(1)),
        });

        await harness.Sut.ReconcileSessionCountersAsync(TenantId, SessionId);

        Assert.Equal(new DateTimeOffset(ServerStart), harness.Written!.GetDateTimeOffset("StartedAtServer"));
        Assert.Equal(new DateTimeOffset(ServerStart.AddMinutes(40)), harness.Written.GetDateTimeOffset("CompletedAtServer"));
        Assert.Equal(new DateTimeOffset(resumedServer), harness.Written.GetDateTimeOffset("ResumedAtServer"));
    }

    [Fact]
    public void Event_rows_are_read_with_their_original_device_time_when_clamped()
    {
        var row = EventRow(4, "app_install_completed", "ImeLogTracker", DeviceStart, DeviceStart.AddSeconds(5), ServerStart.AddSeconds(5));
        row["OccurredUtc"] = new DateTimeOffset(ServerStart.AddSeconds(5)); // the clamped value
        row["TimestampClamped"] = true;
        row["OriginalTimestamp"] = new DateTimeOffset(DeviceStart);

        var e = TableStorageService.ToServerTimeEvent(row);

        Assert.NotNull(e);
        Assert.Equal(DeviceStart, e!.Time);
        Assert.Equal(4, e.Sequence);
        Assert.Null(TableStorageService.ToServerTimeEvent(new TableEntity("p", "r") { ["OccurredUtc"] = new DateTimeOffset(DeviceStart) }));
    }

    // =================================================================== helpers ====

    /// <summary>A running session row; <paramref name="eventCount"/> 0 = its first upload is about to arrive.</summary>
    private static TableEntity RunningRow(int eventCount = 0)
    {
        var row = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"] = new DateTimeOffset(DeviceStart),
            ["Status"] = "InProgress",
            ["EventCount"] = eventCount,
            ["IndexRowKey"] = "2516000000000000000_" + SessionId,
        };
        row.ETag = new ETag("0xEXISTING");
        return row;
    }

    private static List<EnrollmentEvent> FirstUpload() => new()
    {
        Event(1, "agent_started", "Agent", DeviceStart, sent: DeviceStart.AddSeconds(10), received: ServerStart.AddSeconds(10)),
        Event(2, "performance_snapshot", "PerformanceCollector", DeviceStart.AddSeconds(5), sent: DeviceStart.AddSeconds(10), received: ServerStart.AddSeconds(10)),
    };

    private static EnrollmentEvent Event(long sequence, string type, string source, DateTime time, DateTime? sent, DateTime received) => new()
    {
        EventId = $"e{sequence}",
        TenantId = TenantId,
        SessionId = SessionId,
        Sequence = sequence,
        EventType = type,
        Source = source,
        Timestamp = time,
        SentAt = sent,
        ReceivedAt = received,
        Message = string.Empty,
        Data = new Dictionary<string, object>(),
    };

    /// <summary>Two uploads at a constant 9-h offset: the agent start, and the completion 40 min later.</summary>
    private static TableEntity[] CompletedSessionRows() => new[]
    {
        EventRow(1, "agent_started", "Agent", DeviceStart, DeviceStart.AddSeconds(10), ServerStart.AddSeconds(10)),
        EventRow(2, "performance_snapshot", "PerformanceCollector", DeviceStart.AddSeconds(5), DeviceStart.AddSeconds(10), ServerStart.AddSeconds(10)),
        EventRow(3, "enrollment_complete", "DecisionEngine", DeviceStart.AddMinutes(40), DeviceStart.AddMinutes(40).AddSeconds(1), ServerStart.AddMinutes(40).AddSeconds(1)),
    };

    private static TableEntity EventRow(long sequence, string type, string source, DateTime time, DateTime sent, DateTime received) =>
        new($"{TenantId}_{SessionId}", $"{time:yyyyMMddHHmmssfff}_{sequence:D10}")
        {
            ["Sequence"] = sequence,
            ["EventType"] = type,
            ["Source"] = source,
            ["OccurredUtc"] = new DateTimeOffset(time),
            ["SentAt"] = new DateTimeOffset(sent),
            ["ReceivedAt"] = new DateTimeOffset(received),
            ["TimestampClamped"] = false,
        };

    /// <summary>
    /// SDK-mock harness: the Sessions row is read and every merge captured; the Events table
    /// serves <paramref name="eventRows"/> to any query; SessionsIndex merges are captured.
    /// </summary>
    private sealed class Harness
    {
        public TableStorageService Sut { get; }
        public TableEntity? Written { get; private set; }
        public TableEntity? IndexWritten { get; private set; }

        public Harness(TableEntity existing, TableEntity[]? eventRows = null)
        {
            var sessions = new Mock<TableClient>();
            sessions.Setup(t => t.GetEntityAsync<TableEntity>(
                    TenantId, SessionId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(existing, new Mock<Response>().Object));
            var ifExists = new Mock<NullableResponse<TableEntity>>();
            ifExists.SetupGet(r => r.HasValue).Returns(true);
            ifExists.SetupGet(r => r.Value).Returns(existing);
            sessions.Setup(t => t.GetEntityIfExistsAsync<TableEntity>(
                    TenantId, SessionId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ifExists.Object);
            sessions.Setup(t => t.UpdateEntityAsync(
                    It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, ETag, TableUpdateMode, CancellationToken>((e, _, _, _) =>
                {
                    Written = e;
                    return Task.FromResult(new Mock<Response>().Object);
                });

            var events = new Mock<TableClient>();
            events.Setup(t => t.QueryAsync<TableEntity>(
                    It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .Returns(AsyncPageable<TableEntity>.FromPages(new[]
                {
                    Page<TableEntity>.FromValues(eventRows ?? Array.Empty<TableEntity>(), null, new Mock<Response>().Object),
                }));

            var index = new Mock<TableClient>();
            index.Setup(t => t.UpdateEntityAsync(
                    It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, ETag, TableUpdateMode, CancellationToken>((e, _, _, _) =>
                {
                    IndexWritten = e;
                    return Task.FromResult(new Mock<Response>().Object);
                });

            var serviceClient = new Mock<TableServiceClient>();
            serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.Sessions)).Returns(sessions.Object);
            serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.Events)).Returns(events.Object);
            serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.SessionsIndex)).Returns(index.Object);
            Sut = new TableStorageService(serviceClient.Object, NullLogger<TableStorageService>.Instance);
        }
    }
}

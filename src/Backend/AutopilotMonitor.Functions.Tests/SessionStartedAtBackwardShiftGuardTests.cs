using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Plausibility guard for the session anchor (session df1fcf47): ONE do_telemetry event whose
/// OccurredUtc inherited a tz-shifted IME log line 8h in the past dragged StartedAt back ~7h
/// and inflated the WG Part 1 DurationSeconds from ~1h16m to 8h15m. The earliest-event
/// timestamp — whether from the ingest batch or the Events-table probe — may only re-anchor
/// StartedAt / the duration start when it sits within
/// <see cref="TableStorageService.MaxStartedAtBackwardShift"/> of the current anchor.
/// </summary>
public class SessionStartedAtBackwardShiftGuardTests
{
    private const string TenantId  = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    private static readonly DateTime StartedAt = new(2026, 7, 29, 18, 45, 15, DateTimeKind.Utc);

    // ==================================================== StartedAt alignment ====

    [Fact]
    public async Task IncrementEventCount_rejects_backward_shift_beyond_window()
    {
        var harness = new Harness(SessionRow());

        // The df1fcf47 shape: one event 8h before the session's real start.
        var skewed = StartedAt.AddHours(-7);
        var result = await harness.Sut.IncrementSessionEventCountAsync(
            TenantId, SessionId, increment: 1,
            earliestEventTimestamp: skewed, latestEventTimestamp: StartedAt.AddMinutes(10));

        Assert.NotNull(result);
        Assert.NotNull(harness.Written);
        Assert.False(harness.Written!.ContainsKey("StartedAt"));
    }

    [Fact]
    public async Task IncrementEventCount_accepts_backward_shift_within_window()
    {
        var harness = new Harness(SessionRow());

        // Legitimate pre-registration backlog: minutes, not hours.
        var earlier = StartedAt.AddMinutes(-30);
        var result = await harness.Sut.IncrementSessionEventCountAsync(
            TenantId, SessionId, increment: 1,
            earliestEventTimestamp: earlier, latestEventTimestamp: StartedAt.AddMinutes(10));

        Assert.NotNull(result);
        Assert.NotNull(harness.Written);
        Assert.Equal(new DateTimeOffset(earlier), harness.Written!.GetDateTimeOffset("StartedAt"));
    }

    // ============================================= Pending (WG Part 1) duration ====

    [Fact]
    public async Task Pending_duration_ignores_skewed_stored_earliest_event()
    {
        // Exact df1fcf47 replica: the poisoned event is already IN the Events table
        // (probe returns it) AND rides in the batch as earliestEventTimestamp.
        var poisoned = StartedAt.AddHours(-7);
        var lastEvent = new DateTime(2026, 7, 29, 20, 1, 9, DateTimeKind.Utc);
        var harness = new Harness(SessionRow(), earliestStoredEvent: poisoned);

        var ok = await harness.Sut.UpdateSessionStatusAsync(
            TenantId, SessionId, SessionStatus.Pending,
            earliestEventTimestamp: poisoned, latestEventTimestamp: lastEvent);

        Assert.True(ok);
        Assert.NotNull(harness.Written);
        // Duration anchors on StartedAt (18:45:15 → 20:01:09 = 4554s), not on the skewed
        // event (which would give 29690s less the 7h offset — the original inflation).
        Assert.Equal((int)(lastEvent - StartedAt).TotalSeconds, harness.Written!.GetInt32("DurationSeconds"));
        Assert.False(harness.Written.ContainsKey("StartedAt"));
    }

    [Fact]
    public async Task Pending_duration_uses_plausible_stored_earliest_event()
    {
        var earliest = StartedAt.AddMinutes(-5);
        var lastEvent = StartedAt.AddMinutes(70);
        var harness = new Harness(SessionRow(), earliestStoredEvent: earliest);

        var ok = await harness.Sut.UpdateSessionStatusAsync(
            TenantId, SessionId, SessionStatus.Pending,
            latestEventTimestamp: lastEvent);

        Assert.True(ok);
        Assert.NotNull(harness.Written);
        Assert.Equal((int)(lastEvent - earliest).TotalSeconds, harness.Written!.GetInt32("DurationSeconds"));
    }

    // ============================================================ Harness ====

    private static TableEntity SessionRow()
    {
        var entity = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"] = new DateTimeOffset(StartedAt),
            ["Status"]    = "InProgress",
            ["EventCount"] = 10,
        };
        entity.ETag = new ETag("0xEXISTING");
        return entity;
    }

    /// <summary>
    /// SDK-mock harness (same shape as <see cref="StoreSessionReregistrationPreserveTests"/>):
    /// Sessions table configured for read + merge-capture; Events table optionally serves the
    /// earliest-event probe; SessionsIndex stays unconfigured — its sync is swallow-all in the
    /// SUT and irrelevant here.
    /// </summary>
    private sealed class Harness
    {
        public TableStorageService Sut { get; }
        public TableEntity? Written { get; private set; }

        public Harness(TableEntity existing, DateTime? earliestStoredEvent = null)
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
            var eventRows = earliestStoredEvent.HasValue
                ? new[]
                {
                    new TableEntity($"{TenantId}_{SessionId}", $"{earliestStoredEvent.Value:yyyyMMddHHmmssfff}_0000000001")
                    {
                        ["OccurredUtc"] = new DateTimeOffset(earliestStoredEvent.Value),
                    },
                }
                : Array.Empty<TableEntity>();
            events.Setup(t => t.QueryAsync<TableEntity>(
                    It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .Returns(AsyncPageable<TableEntity>.FromPages(new[]
                {
                    Page<TableEntity>.FromValues(eventRows, null, new Mock<Response>().Object),
                }));

            var serviceClient = new Mock<TableServiceClient>();
            serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.Sessions)).Returns(sessions.Object);
            serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.Events)).Returns(events.Object);
            // SessionsIndex intentionally unconfigured → null client → SUT swallows.

            Sut = new TableStorageService(serviceClient.Object, NullLogger<TableStorageService>.Instance);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The agent hot path reads the Sessions row projected (<see cref="SessionRowProjections"/>). A
/// select-strict harness — it hands back only the requested columns, unlike the SDK mocks of the
/// other session tests — pins every list against its consumers: the counter merge's write, its
/// index mirror and its snapshot, the ingest-slice mapper, the signal-only read, and the guard
/// row's readers (status prefetch, kill-switch serial, owner policy) come out identical whether
/// the row arrives whole or projected. A column a consumer reads but the list lacks fails here,
/// not in production as a silent null. Fixtures carry every column a real row carries, so the
/// projection has something to drop.
/// </summary>
public class SessionRowProjectionTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    private static readonly TimeSpan Ahead = TimeSpan.FromHours(9);
    private static readonly DateTime ServerStart = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DeviceStart = ServerStart + Ahead;

    private const string Thumb1 = "AA11BB22CC33DD44EE55FF6677889900AABBCCDD";
    private const string Thumb2 = "0011223344556677889900AABBCCDDEEFF001122";
    private const string Dev1 = "0f4e1c2d-1111-4aaa-8bbb-000000000001";

    public enum RowKind { RunningWhiteGlovePart2, Completed }

    public static IEnumerable<object[]> RowKinds => new[] { new object[] { RowKind.RunningWhiteGlovePart2 }, new object[] { RowKind.Completed } };

    // ============================================================== counter merge ====

    [Theory]
    [MemberData(nameof(RowKinds))]
    public async Task Counter_merge_write_index_mirror_and_snapshot_are_identical_whole_or_projected(RowKind kind)
    {
        var whole = new Harness(FullRow(kind), honourSelect: false);
        var projected = new Harness(FullRow(kind), honourSelect: true);

        var wholeSnapshot = await Increment(whole.Sut);
        var projectedSnapshot = await Increment(projected.Sut);

        // The production read passes the list, and the strict harness really dropped columns.
        Assert.NotNull(projected.LastSelect);
        Assert.Equal(SessionRowProjections.IngestSnapshot, projected.LastSelect!);
        Assert.True(projected.LastReadColumnCount < whole.LastReadColumnCount,
            $"projected read returned {projected.LastReadColumnCount} columns, whole read {whole.LastReadColumnCount}");

        Assert.NotNull(whole.Written);
        Assert.NotNull(projected.Written);
        Assert.Equal(whole.WrittenETag, projected.WrittenETag); // the CAS ETag survives the projection
        AssertSameEntity(whole.Written!, projected.Written!);
        Assert.NotNull(whole.IndexWritten);
        Assert.NotNull(projected.IndexWritten);
        AssertSameEntity(whole.IndexWritten!, projected.IndexWritten!);

        Assert.NotNull(wholeSnapshot);
        Assert.NotNull(projectedSnapshot);
        AssertSameSnapshot(wholeSnapshot!, projectedSnapshot!);
    }

    [Fact]
    public async Task Counter_merge_on_the_running_row_exercises_the_tracker_and_the_counters()
    {
        // Guards the fixture itself: every pinned column must have a visible effect on this write,
        // otherwise the equivalence above would pass vacuously.
        var harness = new Harness(FullRow(RowKind.RunningWhiteGlovePart2), honourSelect: true);

        var snapshot = await Increment(harness.Sut);

        var written = harness.Written!;
        Assert.Equal(2 + 3, written.GetInt32("EventCount"));
        Assert.Equal(3 + 1, written.GetInt32("PlatformScriptCount"));
        Assert.Equal(1 + 1, written.GetInt32("RemediationScriptCount"));
        Assert.Equal(1 + 1, written.GetInt32("RebootCount"));
        Assert.False(written.ContainsKey("StartedAt"), "a later earliest event must not move the anchor");
        Assert.False(written.ContainsKey("LastEventAt"), "an older latest event must not move LastEventAt");
        Assert.True(written.ContainsKey(TableStorageService.ServerTimeStateColumn), "the tracker continues on a running row");
        Assert.False(written.ContainsKey("StartedAtServer"), "an unchanged twin is not rewritten");
        Assert.Equal("2516000000000000000_" + SessionId, harness.IndexWritten!.RowKey); // the stored IndexRowKey, not a recomputed one
        Assert.Equal(SessionStatus.InProgress, snapshot!.Status);
        Assert.Equal(5, snapshot.EventCount);
        Assert.Equal("Succeeded", snapshot.AdminMarkedAction);
        Assert.Equal("2.0.1478", snapshot.AgentVersion);
        Assert.Equal("1.99.1", snapshot.ImeAgentVersion);
        Assert.True(snapshot.DurationSeconds >= 600, "WhiteGlove Part 2 running: stored Part 1 plus the running Part 2");
    }

    [Fact]
    public async Task Counter_merge_on_the_completed_row_leaves_the_tracker_alone()
    {
        var harness = new Harness(FullRow(RowKind.Completed), honourSelect: true);

        var snapshot = await Increment(harness.Sut);

        Assert.False(harness.Written!.ContainsKey(TableStorageService.ServerTimeStateColumn), "CompletedAt stops the tracker");
        Assert.Equal(SessionStatus.Succeeded, snapshot!.Status);
        Assert.Equal(2400, snapshot.DurationSeconds);
        Assert.Equal(new DateTime(2026, 1, 1, 17, 40, 0, DateTimeKind.Utc), snapshot.CompletedAt);
    }

    // ===================================================================== mapper ====

    [Theory]
    [MemberData(nameof(RowKinds))]
    public void Ingest_slice_mapper_matches_the_canonical_mapper_and_needs_only_its_projection(RowKind kind)
    {
        var sut = new Harness(FullRow(kind), honourSelect: true).Sut;
        var row = FullRow(kind);

        var fromSlice = sut.MapToIngestSnapshot(row);
        var fromSummary = SessionIngestSnapshot.From(sut.MapToSessionSummary(row));
        var fromProjected = sut.MapToIngestSnapshot(Project(row, SessionRowProjections.IngestSnapshot));

        AssertSameSnapshot(fromSummary, fromSlice);
        AssertSameSnapshot(fromSlice, fromProjected);
    }

    [Theory]
    [MemberData(nameof(RowKinds))]
    public async Task Signal_only_snapshot_read_uses_the_same_projection_and_mapping(RowKind kind)
    {
        var harness = new Harness(FullRow(kind), honourSelect: true);

        var snapshot = await harness.Sut.GetSessionIngestSnapshotAsync(TenantId, SessionId);

        Assert.Equal(SessionRowProjections.IngestSnapshot, harness.LastSelect!);
        Assert.NotNull(snapshot);
        AssertSameSnapshot(harness.Sut.MapToIngestSnapshot(FullRow(kind)), snapshot!);
    }

    // ================================================================== guard row ====

    [Fact]
    public void Guard_row_readers_see_the_same_off_the_projected_row()
    {
        var full = FullRow(RowKind.RunningWhiteGlovePart2);
        var projected = Project(full, SessionRowProjections.GuardRow.Concat(SessionDeletionGuard.LockColumns));

        Assert.True(projected.Count < full.Count, "the guard projection must drop columns");
        Assert.Equal(SessionRowProjections.TryReadStatus(full), SessionRowProjections.TryReadStatus(projected));
        Assert.Equal(SessionStatus.InProgress, SessionRowProjections.TryReadStatus(projected));
        Assert.Equal(full.GetString("SerialNumber"), projected.GetString("SerialNumber"));
        Assert.Equal(full.GetString("DeletionState"), projected.GetString("DeletionState"));
    }

    [Theory]
    [MemberData(nameof(OwnerScenarios))]
    public void Owner_policy_decides_the_same_off_the_projected_row(string scenario, SessionOwner? rowOwner, SecurityValidationResult caller)
    {
        var full = FullRow(RowKind.RunningWhiteGlovePart2, rowOwner);
        var projected = Project(full, SessionRowProjections.GuardRow.Concat(SessionDeletionGuard.LockColumns));
        var now = ServerStart.AddHours(1);

        var fromFull = SessionOwnershipPolicy.Evaluate(full, caller, now);
        var fromProjected = SessionOwnershipPolicy.Evaluate(projected, caller, now);

        Assert.True(fromFull.Outcome == fromProjected.Outcome, $"{scenario}: {fromFull.Outcome} vs {fromProjected.Outcome}");
        Assert.Equal(fromFull.Rejected, fromProjected.Rejected);
        Assert.Equal(fromFull.OwnerToStamp?.Kind, fromProjected.OwnerToStamp?.Kind);
        Assert.Equal(fromFull.OwnerToStamp?.Thumbprint, fromProjected.OwnerToStamp?.Thumbprint);
        Assert.Equal(fromFull.OwnerToStamp?.DeviceId, fromProjected.OwnerToStamp?.DeviceId);
        Assert.Equal(fromFull.OwnerToStamp?.BootstrapCode, fromProjected.OwnerToStamp?.BootstrapCode);
        Assert.Equal(fromFull.OwnerToStamp?.Serial, fromProjected.OwnerToStamp?.Serial);
    }

    public static IEnumerable<object?[]> OwnerScenarios()
    {
        yield return new object?[] { "legacy row, cert caller with the row's serial", null, Cert(Thumb1, Dev1) };
        yield return new object?[] { "cert-owned row, the same cert", CertOwner(Thumb1, Dev1), Cert(Thumb1, Dev1) };
        yield return new object?[] { "cert-owned row, a foreign cert", CertOwner(Thumb1, Dev1), Cert(Thumb2, "0f4e1c2d-2222-4aaa-8bbb-000000000002", "SN-OTHER") };
        yield return new object?[] { "bootstrap-owned row, the cert that follows", BootstrapOwner("ABCD1234"), Cert(Thumb1, Dev1) };
        yield return new object?[] { "cert-owned row, a bootstrap caller", CertOwner(Thumb1, Dev1), Bootstrap("ABCD1234") };
    }

    [Fact]
    public void Lock_check_works_on_the_projected_row()
    {
        var full = FullRow(RowKind.RunningWhiteGlovePart2);
        full["DeletionState"] = SessionDeletionState.Preparing;
        full["PendingDeletionManifestId"] = "MANIFEST-1";
        var projected = Project(full, SessionRowProjections.GuardRow.Concat(SessionDeletionGuard.LockColumns));
        var guard = new SessionDeletionGuard(new Mock<ISessionDeletionInventoryReader>().Object, NullLogger<SessionDeletionGuard>.Instance);

        var ex = Assert.Throws<SessionDeletionLockedException>(() => guard.ThrowIfLocked(projected, "V2.IngestTelemetry"));

        Assert.Equal(SessionDeletionState.Preparing, ex.CurrentState);
        Assert.Equal("MANIFEST-1", ex.ManifestId);
    }

    [Fact]
    public async Task Projected_guard_read_asks_for_the_caller_columns_plus_the_lock_columns()
    {
        var reader = new Mock<ISessionDeletionInventoryReader>();
        IEnumerable<string>? asked = null;
        reader.Setup(r => r.GetSessionRowAsync(TenantId, SessionId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
              .Returns<string, string, IEnumerable<string>, CancellationToken>((_, _, select, _) =>
              {
                  asked = select.ToList();
                  return Task.FromResult<TableEntity?>(Project(FullRow(RowKind.RunningWhiteGlovePart2), select));
              });
        var guard = new SessionDeletionGuard(reader.Object, NullLogger<SessionDeletionGuard>.Instance);

        var row = await guard.EnsureWritableAndGetRowAsync(TenantId, SessionId, "V2.IngestTelemetry", SessionRowProjections.GuardRow);

        Assert.NotNull(asked);
        Assert.Equal(
            SessionRowProjections.GuardRow.Concat(SessionDeletionGuard.LockColumns).OrderBy(c => c, StringComparer.Ordinal),
            asked!.OrderBy(c => c, StringComparer.Ordinal));
        Assert.NotNull(row);
        Assert.Equal(SessionStatus.InProgress, SessionRowProjections.TryReadStatus(row));
        Assert.Equal("SN-1", row!.GetString("SerialNumber"));
        Assert.False(row.ContainsKey("DeviceName"), "the projected row carries only the requested columns");
        reader.Verify(r => r.GetSessionRowAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Projected_guard_read_still_refuses_a_locked_row_and_consults_the_tombstone_when_absent()
    {
        var locked = new Mock<ISessionDeletionInventoryReader>();
        locked.Setup(r => r.GetSessionRowAsync(TenantId, SessionId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new TableEntity(TenantId, SessionId)
              {
                  ["DeletionState"] = SessionDeletionState.Queued,
                  ["PendingDeletionManifestId"] = "MANIFEST-2",
              });
        var lockedGuard = new SessionDeletionGuard(locked.Object, NullLogger<SessionDeletionGuard>.Instance);
        var ex = await Assert.ThrowsAsync<SessionDeletionLockedException>(
            () => lockedGuard.EnsureWritableAndGetRowAsync(TenantId, SessionId, "RegisterSession", SessionRowProjections.GuardRow));
        Assert.Equal("MANIFEST-2", ex.ManifestId);

        var absent = new Mock<ISessionDeletionInventoryReader>();
        absent.Setup(r => r.GetSessionRowAsync(TenantId, SessionId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((TableEntity?)null);
        absent.Setup(r => r.GetActiveSessionTombstoneAsync(TenantId, SessionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync((TableEntity?)null);
        var absentGuard = new SessionDeletionGuard(absent.Object, NullLogger<SessionDeletionGuard>.Instance);
        var row = await absentGuard.EnsureWritableAndGetRowAsync(TenantId, SessionId, "RegisterSession", SessionRowProjections.GuardRow);
        Assert.Null(row);
        absent.Verify(r => r.GetActiveSessionTombstoneAsync(TenantId, SessionId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Projection_lists_carry_what_their_consumers_name()
    {
        Assert.Contains("Status", SessionRowProjections.GuardRow);
        Assert.Contains("SerialNumber", SessionRowProjections.GuardRow);
        Assert.All(SessionOwner.Columns.All, c => Assert.Contains(c, SessionRowProjections.GuardRow));
        Assert.Equal(SessionRowProjections.GuardRow.Length, SessionRowProjections.GuardRow.Distinct(StringComparer.Ordinal).Count());

        Assert.Contains(TableStorageService.ServerTimeStateColumn, SessionRowProjections.IngestSnapshot);
        Assert.Contains(TableStorageService.StartedAtServerColumn, SessionRowProjections.IngestSnapshot);
        Assert.Contains(TableStorageService.ResumedAtServerColumn, SessionRowProjections.IngestSnapshot);
        Assert.Contains("CompletedAt", SessionRowProjections.IngestSnapshot);
        Assert.Contains("IndexRowKey", SessionRowProjections.IngestSnapshot);
        Assert.Equal(SessionRowProjections.IngestSnapshot.Length, SessionRowProjections.IngestSnapshot.Distinct(StringComparer.Ordinal).Count());
    }

    // ==================================================================== helpers ====

    /// <summary>The counter merge of a second upload: later events, an older latest event, one of each counter.</summary>
    private static Task<SessionIngestSnapshot?> Increment(TableStorageService sut) =>
        sut.IncrementSessionEventCountAsync(
            TenantId, SessionId, increment: 3,
            earliestEventTimestamp: DeviceStart.AddMinutes(1),   // later than StartedAt: no anchor shift
            latestEventTimestamp: DeviceStart.AddMinutes(19),    // older than LastEventAt: no move
            currentPhase: null,
            platformScriptIncrement: 1, remediationScriptIncrement: 1, rebootIncrement: 1,
            upload: SecondUpload());

    /// <summary>
    /// A Sessions row with every column a real row carries. The running kind is WhiteGlove Part 2
    /// in flight (stored Part 1 duration + ResumedAt drive the live duration) with the tracker
    /// state of its first upload and the matching twin; the completed kind has CompletedAt, a
    /// stored duration and an admin mark.
    /// </summary>
    private static TableEntity FullRow(RowKind kind, SessionOwner? owner = null)
    {
        var tracker = new ServerTime.Tracker();
        tracker.Absorb(FirstUpload().Select(ServerTime.Event.From).ToList());

        var row = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"] = new DateTimeOffset(DeviceStart),
            ["LastEventAt"] = new DateTimeOffset(DeviceStart.AddMinutes(20)),
            ["LastIngestAt"] = new DateTimeOffset(ServerStart.AddMinutes(20)),
            ["EventCount"] = 2,
            ["PlatformScriptCount"] = 3,
            ["RemediationScriptCount"] = 1,
            ["RebootCount"] = 1,
            ["IndexRowKey"] = "2516000000000000000_" + SessionId, // deliberately not the key StartedAt would give
            ["CurrentPhase"] = 4,
            ["CurrentPhaseDetail"] = "Installing apps",
            ["FailureReason"] = string.Empty,
            ["DiagnosticsBlobName"] = "diag-" + SessionId + ".zip",
            ["DiagnosticsBlobDestination"] = "platform",
            ["IsPreProvisioned"] = true,
            ["IsHybridJoin"] = false,
            ["IsUserDriven"] = false,
            ["IsSelfDeployingProfile"] = false,
            ["IsCloudPc"] = false,
            ["EnrollmentType"] = "v2",
            ["PendingActionsJson"] = "[{\"Type\":\"collect_diagnostics\",\"Reason\":\"rule\"}]",
            ["AgentVersion"] = "2.0.1478",
            ["ImeAgentVersion"] = "1.99.1",
            [TableStorageService.ServerTimeStateColumn] = tracker.ToJson(),
            ["StartedAtServer"] = new DateTimeOffset(tracker.StartedAtServer!.Value),
            // Columns the hot path never needs — the projection must drop them without a trace.
            ["DeviceName"] = "DESKTOP-TEST01",
            ["SerialNumber"] = "SN-1",
            ["Manufacturer"] = "Contoso",
            ["Model"] = "Model X",
            ["OsName"] = "Windows 11 Enterprise",
            ["OsBuild"] = "26100.4000",
            ["OsDisplayVersion"] = "24H2",
            ["GeoCountry"] = "DE",
            ["GeoRegion"] = "HE",
            ["GeoCity"] = "Frankfurt",
            ["GeoLoc"] = "50.11,8.68",
            ["ConnectionType"] = "Ethernet",
            ["ValidatedBy"] = "AutopilotV1",
            ["VerdictPath"] = "ime_complete",
            ["FailureSnapshotJson"] = new string('x', 2048),
            ["AvgApiLatencyMs"] = 123.4,
            ["ApiRequestCount"] = 17,
            ["DeletionState"] = SessionDeletionState.None,
        };

        if (kind == RowKind.RunningWhiteGlovePart2)
        {
            row["Status"] = "InProgress";
            row["ResumedAt"] = new DateTimeOffset(DeviceStart.AddMinutes(10));
            row["DurationSeconds"] = 600; // Part 1, stored
            row["AdminMarkedAction"] = "Succeeded";
        }
        else
        {
            row["Status"] = "Succeeded";
            row["CompletedAt"] = new DateTimeOffset(DeviceStart.AddMinutes(40));
            row["CompletedAtServer"] = new DateTimeOffset(ServerStart.AddMinutes(40));
            row["DurationSeconds"] = 2400;
            row["AdminMarkedAction"] = "Succeeded";
            row["ReconcileReason"] = "ime_complete";
            row["CompletionSource"] = "agent";
        }

        if (owner != null)
            SessionOwnershipPolicy.ApplyTo(row, owner);

        row.ETag = new ETag("0xEXISTING");
        return row;
    }

    private static List<EnrollmentEvent> FirstUpload() => new()
    {
        Event(1, "agent_started", "Agent", DeviceStart, sent: DeviceStart.AddSeconds(10), received: ServerStart.AddSeconds(10)),
        Event(2, "performance_snapshot", "PerformanceCollector", DeviceStart.AddSeconds(5), sent: DeviceStart.AddSeconds(10), received: ServerStart.AddSeconds(10)),
    };

    private static List<EnrollmentEvent> SecondUpload() => new()
    {
        Event(3, "app_install_started", "ImeLogTracker", DeviceStart.AddSeconds(30), sent: DeviceStart.AddSeconds(60), received: ServerStart.AddSeconds(60)),
        Event(4, "performance_snapshot", "PerformanceCollector", DeviceStart.AddSeconds(40), sent: DeviceStart.AddSeconds(60), received: ServerStart.AddSeconds(60)),
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

    private static SecurityValidationResult Cert(string thumb, string? deviceId, string? serial = "SN-1") => new()
    {
        IsValid = true,
        CertificateThumbprint = thumb,
        IntuneDeviceId = deviceId,
        SerialNumber = serial,
        ValidatedBy = ValidatorType.AutopilotV1,
    };

    private static SecurityValidationResult Bootstrap(string code, string? serial = "SN-1") => new()
    {
        IsValid = true,
        IsBootstrapAuth = true,
        BootstrapShortCode = code,
        SerialNumber = serial,
        ValidatedBy = ValidatorType.Bootstrap,
    };

    private static SessionOwner CertOwner(string thumb, string? deviceId, string serial = "SN-1") =>
        new() { Kind = SessionOwner.Kinds.Cert, Thumbprint = thumb, DeviceId = deviceId, Serial = serial, BoundAt = ServerStart };

    private static SessionOwner BootstrapOwner(string code, string serial = "SN-1") =>
        new() { Kind = SessionOwner.Kinds.Bootstrap, BootstrapCode = code, Serial = serial, BoundAt = ServerStart };

    /// <summary>What a select does on the service: keys, ETag and timestamp survive, every other column is gone.</summary>
    private static TableEntity Project(TableEntity row, IEnumerable<string>? select)
    {
        if (select == null)
            return row;
        var keep = new HashSet<string>(select, StringComparer.Ordinal);
        var projected = new TableEntity(row.PartitionKey, row.RowKey) { ETag = row.ETag, Timestamp = row.Timestamp };
        foreach (var kv in row)
        {
            if (keep.Contains(kv.Key))
                projected[kv.Key] = kv.Value;
        }
        return projected;
    }

    private static readonly string[] SystemKeys = { "PartitionKey", "RowKey", "Timestamp", "odata.etag" };

    private static void AssertSameEntity(TableEntity expected, TableEntity actual)
    {
        var expectedKeys = expected.Keys.Except(SystemKeys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var actualKeys = actual.Keys.Except(SystemKeys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedKeys, actualKeys);
        Assert.Equal(expected.PartitionKey, actual.PartitionKey);
        Assert.Equal(expected.RowKey, actual.RowKey);
        foreach (var key in expectedKeys)
        {
            if (key == "LastIngestAt")
            {
                var delta = expected.GetDateTimeOffset(key)!.Value - actual.GetDateTimeOffset(key)!.Value;
                Assert.True(Math.Abs(delta.TotalSeconds) < 10, $"{key}: {delta}");
                continue;
            }
            Assert.True(Equals(expected[key], actual[key]), $"{key}: {expected[key]} vs {actual[key]}");
        }
    }

    private static void AssertSameSnapshot(SessionIngestSnapshot expected, SessionIngestSnapshot actual)
    {
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.CurrentPhase, actual.CurrentPhase);
        Assert.Equal(expected.CurrentPhaseDetail, actual.CurrentPhaseDetail);
        Assert.Equal(expected.FailureReason, actual.FailureReason);
        Assert.Equal(expected.EventCount, actual.EventCount);
        Assert.Equal(expected.CompletedAt, actual.CompletedAt);
        Assert.Equal(expected.DiagnosticsBlobName, actual.DiagnosticsBlobName);
        Assert.Equal(expected.IsPreProvisioned, actual.IsPreProvisioned);
        Assert.Equal(expected.AdminMarkedAction, actual.AdminMarkedAction);
        Assert.Equal(expected.PendingActionsJson, actual.PendingActionsJson);
        Assert.Equal(expected.AgentVersion, actual.AgentVersion);
        Assert.Equal(expected.ImeAgentVersion, actual.ImeAgentVersion);
        // The live duration of a running session is computed from the clock; the two mappings run
        // milliseconds apart.
        Assert.NotNull(expected.DurationSeconds);
        Assert.NotNull(actual.DurationSeconds);
        Assert.True(Math.Abs(expected.DurationSeconds!.Value - actual.DurationSeconds!.Value) <= 5,
            $"DurationSeconds: {expected.DurationSeconds} vs {actual.DurationSeconds}");
    }

    /// <summary>
    /// SDK-mock harness over the real <see cref="TableStorageService"/>. With <c>honourSelect</c>
    /// the Sessions point read returns only the columns the caller selected (what the service
    /// does); without it the whole row (what the other session harnesses do). Merges on Sessions
    /// and SessionsIndex are captured.
    /// </summary>
    private sealed class Harness
    {
        public TableStorageService Sut { get; }
        public TableEntity? Written { get; private set; }
        public ETag WrittenETag { get; private set; }
        public TableEntity? IndexWritten { get; private set; }
        public IEnumerable<string>? LastSelect { get; private set; }
        public int LastReadColumnCount { get; private set; }

        public Harness(TableEntity existing, bool honourSelect)
        {
            var sessions = new Mock<TableClient>();
            sessions.Setup(t => t.GetEntityIfExistsAsync<TableEntity>(
                    TenantId, SessionId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, IEnumerable<string>, CancellationToken>((_, _, select, _) =>
                {
                    LastSelect = select?.ToList();
                    var served = honourSelect ? Project(existing, select) : existing;
                    LastReadColumnCount = served.Count;
                    var response = new Mock<NullableResponse<TableEntity>>();
                    response.SetupGet(r => r.HasValue).Returns(true);
                    response.SetupGet(r => r.Value).Returns(served);
                    return Task.FromResult(response.Object);
                });
            sessions.Setup(t => t.UpdateEntityAsync(
                    It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, ETag, TableUpdateMode, CancellationToken>((e, etag, _, _) =>
                {
                    Written = e;
                    WrittenETag = etag;
                    return Task.FromResult(new Mock<Response>().Object);
                });

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
            serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.SessionsIndex)).Returns(index.Object);
            Sut = new TableStorageService(serviceClient.Object, NullLogger<TableStorageService>.Instance);
        }
    }
}

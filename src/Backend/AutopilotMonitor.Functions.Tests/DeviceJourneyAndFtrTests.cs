using AutopilotMonitor.Functions.Functions.Metrics;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// F2 PR4 (insights spec §F2): device-key normalization (junk serials), terminal-only chain
/// maintenance (upsert, cap, deletion-ref pruning), journey grouping (first-success end,
/// 30-day gap, Incomplete attempts, WG-Pending stays open), the pure daily FTR aggregation
/// core (<see cref="MaintenanceService.BuildDeviceJourneyAggregates"/>) and the persistence
/// round-trips (table-storage-serialization rule — every property must survive Store→Map).
/// </summary>
public class DeviceJourneyAndFtrTests
{
    private static readonly DateTime T0 = new(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);
    private const string TenantA = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string TenantB = "bbbbbbbb-0000-0000-0000-000000000002";

    private static SessionSummary Session(
        string sessionId,
        SessionStatus status = SessionStatus.Succeeded,
        DateTime? startedAt = null,
        DateTime? completedAt = null,
        int? durationSeconds = 1800,
        string? serial = "PF4X1ABC",
        string tenantId = TenantA,
        bool wg = false,
        string enrollmentType = "v1",
        string? adminMarkedAction = null)
        => new()
        {
            TenantId = tenantId,
            SessionId = sessionId,
            SerialNumber = serial!,
            Status = status,
            StartedAt = startedAt ?? T0,
            CompletedAt = completedAt ?? (startedAt ?? T0).AddSeconds(durationSeconds ?? 0),
            DurationSeconds = durationSeconds,
            IsPreProvisioned = wg,
            EnrollmentType = enrollmentType,
            AdminMarkedAction = adminMarkedAction,
            Manufacturer = "Contoso",
            Model = "Laptop 5",
        };

    private static DeviceSessionRef Ref(
        string sessionId, SessionStatus status, DateTime startedAt, DateTime? completedAt = null)
        => new()
        {
            SessionId = sessionId,
            Status = status.ToString(),
            StartedAt = startedAt,
            CompletedAt = completedAt ?? startedAt.AddMinutes(30),
        };

    // ── serial normalization / junk exclusion ───────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]                      // shorter than 4 chars
    [InlineData("0")]
    [InlineData("None")]
    [InlineData("INVALID")]
    [InlineData("Unknown")]                  // the agent's WMI-failure sentinel (audit Q5)
    [InlineData("unknown")]
    [InlineData("System Serial Number")]
    [InlineData("TO BE FILLED BY O.E.M.")]
    [InlineData("Default String")]
    [InlineData("  Default string  ")]       // junk match happens after trim + case-fold
    public void NormalizeSerial_JunkAndPlaceholders_YieldNull(string? raw)
    {
        Assert.Null(DeviceJourneyCalculator.NormalizeSerial(raw));
    }

    [Theory]
    [InlineData("PF4X1ABC", "pf4x1abc")]
    [InlineData("  PF4X1ABC  ", "pf4x1abc")] // trim
    [InlineData("pf4x1abc", "pf4x1abc")]
    [InlineData("1234", "1234")]             // exactly 4 chars is a valid identity
    public void NormalizeSerial_RealSerials_TrimAndCaseFold(string raw, string expected)
    {
        Assert.Equal(expected, DeviceJourneyCalculator.NormalizeSerial(raw));
    }

    // ── terminal set / ref building ─────────────────────────────────────────

    [Theory]
    [InlineData(SessionStatus.Succeeded, true)]
    [InlineData(SessionStatus.Failed, true)]
    [InlineData(SessionStatus.Incomplete, true)]
    [InlineData(SessionStatus.InProgress, false)]
    [InlineData(SessionStatus.Pending, false)]      // WG part 1 sealed — an OPEN session
    [InlineData(SessionStatus.Stalled, false)]
    [InlineData(SessionStatus.AwaitingUser, false)]
    [InlineData(SessionStatus.Unknown, false)]
    public void IsTerminal_MatchesTheF2TerminalSet(SessionStatus status, bool expected)
    {
        Assert.Equal(expected, DeviceJourneyCalculator.IsTerminal(status));
    }

    [Fact]
    public void BuildSessionRef_MapsAllFields_DurationIsAuthoritativeDurationSeconds()
    {
        // CompletedAt deliberately diverges from StartedAt + DurationSeconds (25 % of terminal
        // sessions in production) — the ref must carry DurationSeconds verbatim.
        var session = Session("s-1", startedAt: T0, completedAt: T0.AddHours(5), durationSeconds: 1800,
            wg: true, enrollmentType: "v1", adminMarkedAction: "Succeeded");

        var reference = DeviceJourneyCalculator.BuildSessionRef(session)!;

        Assert.Equal("s-1", reference.SessionId);
        Assert.Equal(T0, reference.StartedAt);
        Assert.Equal(T0.AddHours(5), reference.CompletedAt);
        Assert.Equal("Succeeded", reference.Status);
        Assert.Equal("v1", reference.EnrollmentType);
        Assert.True(reference.IsPreProvisioned);
        Assert.Equal(1800, reference.DurationSeconds);
        Assert.True(reference.AdminMarked);
    }

    [Fact]
    public void BuildSessionRef_WhiteGlovePending_YieldsNull_NeverAnAttempt()
    {
        var pending = Session("s-1", status: SessionStatus.Pending, wg: true, durationSeconds: null);
        Assert.Null(DeviceJourneyCalculator.BuildSessionRef(pending));
    }

    [Fact]
    public void BuildSessionRef_Incomplete_KeepsNullDuration()
    {
        var incomplete = Session("s-1", status: SessionStatus.Incomplete, durationSeconds: null);
        var reference = DeviceJourneyCalculator.BuildSessionRef(incomplete)!;
        Assert.Equal("Incomplete", reference.Status);
        Assert.Null(reference.DurationSeconds);
        Assert.False(reference.AdminMarked);
    }

    // ── chain maintenance ───────────────────────────────────────────────────

    [Fact]
    public void MergeChain_UpsertsBySessionId_ReclassificationReplacesTheEntry()
    {
        var existing = new List<DeviceSessionRef> { Ref("s-1", SessionStatus.Incomplete, T0) };
        var update = Ref("s-1", SessionStatus.Succeeded, T0);

        var merged = DeviceJourneyCalculator.MergeChain(existing, new[] { update });

        var entry = Assert.Single(merged);
        Assert.Equal("Succeeded", entry.Status);
    }

    [Fact]
    public void MergeChain_OrdersByStartedAt_TiebreakSessionId_AndCapsToMostRecent()
    {
        var refs = new List<DeviceSessionRef>();
        for (var i = 0; i < 25; i++)
            refs.Add(Ref($"s-{i:D2}", SessionStatus.Failed, T0.AddDays(i)));
        // Same StartedAt as s-24 — the ordinal sessionId tiebreak must keep the order stable.
        refs.Add(Ref("s-00-twin", SessionStatus.Failed, T0.AddDays(24)));

        var merged = DeviceJourneyCalculator.MergeChain(null, refs);

        Assert.Equal(20, merged.Count);                       // cap 20 most recent
        Assert.Equal("s-06", merged[0].SessionId);            // s-00..s-05 dropped (oldest)
        Assert.Equal(T0.AddDays(24), merged[^1].StartedAt);
        Assert.Equal("s-24", merged[^1].SessionId);           // "s-00-twin" < "s-24" ordinal → sorts first
        Assert.Equal("s-00-twin", merged[^2].SessionId);
    }

    [Fact]
    public void MergeChain_DropsNonTerminalRefs_Defensively()
    {
        var corrupt = new List<DeviceSessionRef>
        {
            Ref("s-1", SessionStatus.Succeeded, T0),
            new() { SessionId = "s-2", Status = "Pending", StartedAt = T0.AddDays(1) },
            new() { SessionId = "s-3", Status = "garbage", StartedAt = T0.AddDays(2) },
        };

        var merged = DeviceJourneyCalculator.MergeChain(corrupt, Array.Empty<DeviceSessionRef>());

        Assert.Equal("s-1", Assert.Single(merged).SessionId);
    }

    [Fact]
    public void RemoveSessionRefs_DropsDeletedSessions_EmptyChainSignalsRowDeletion()
    {
        var chain = new List<DeviceSessionRef>
        {
            Ref("s-1", SessionStatus.Failed, T0),
            Ref("s-2", SessionStatus.Succeeded, T0.AddDays(1)),
        };

        var partial = DeviceJourneyCalculator.RemoveSessionRefs(
            chain, new HashSet<string>(StringComparer.Ordinal) { "s-1" });
        Assert.Equal("s-2", Assert.Single(partial).SessionId);

        var empty = DeviceJourneyCalculator.RemoveSessionRefs(
            chain, new HashSet<string>(StringComparer.Ordinal) { "s-1", "s-2" });
        Assert.Empty(empty);
    }

    // ── journey grouping ────────────────────────────────────────────────────

    [Fact]
    public void GroupJourneys_SingleSuccess_IsOneCompletedFirstTimeRightJourney()
    {
        var journeys = DeviceJourneyCalculator.GroupJourneys(
            new[] { Ref("s-1", SessionStatus.Succeeded, T0) });

        var journey = Assert.Single(journeys);
        Assert.True(journey.Completed);
        Assert.Single(journey.Attempts);
        Assert.Equal("s-1", journey.CompletingRef!.SessionId);
    }

    [Fact]
    public void GroupJourneys_FailAndIncompleteAreAttempts_JourneyEndsWithFirstSuccess()
    {
        var journeys = DeviceJourneyCalculator.GroupJourneys(new[]
        {
            Ref("s-1", SessionStatus.Failed, T0),
            Ref("s-2", SessionStatus.Incomplete, T0.AddHours(2)),
            Ref("s-3", SessionStatus.Succeeded, T0.AddHours(4)),
        });

        var journey = Assert.Single(journeys);
        Assert.True(journey.Completed);
        Assert.Equal(3, journey.Attempts.Count); // Incomplete is a non-successful attempt
    }

    [Fact]
    public void GroupJourneys_SessionAfterSuccess_StartsANewJourney_Redeployment()
    {
        var journeys = DeviceJourneyCalculator.GroupJourneys(new[]
        {
            Ref("s-1", SessionStatus.Succeeded, T0),
            Ref("s-2", SessionStatus.Failed, T0.AddDays(2)),
        });

        Assert.Equal(2, journeys.Count);
        Assert.True(journeys[0].Completed);
        Assert.False(journeys[1].Completed);   // open — no failed verdict, just not done yet
        Assert.Null(journeys[1].CompletingRef);
    }

    [Fact]
    public void GroupJourneys_FailuresOnly_JourneyStaysOpen_NeverCompleted()
    {
        var journeys = DeviceJourneyCalculator.GroupJourneys(new[]
        {
            Ref("s-1", SessionStatus.Failed, T0),
            Ref("s-2", SessionStatus.Failed, T0.AddDays(1)),
        });

        var journey = Assert.Single(journeys);
        Assert.False(journey.Completed);
        Assert.Equal(2, journey.Attempts.Count);
    }

    [Fact]
    public void GroupJourneys_GapOver30Days_StartsANewJourney_AbandonedStaysUncompleted()
    {
        var journeys = DeviceJourneyCalculator.GroupJourneys(new[]
        {
            Ref("s-1", SessionStatus.Failed, T0, completedAt: T0.AddHours(1)),
            Ref("s-2", SessionStatus.Failed, T0.AddHours(1).AddDays(31)),
            Ref("s-3", SessionStatus.Succeeded, T0.AddHours(1).AddDays(31).AddHours(3)),
        });

        Assert.Equal(2, journeys.Count);
        Assert.False(journeys[0].Completed);            // shelved/abandoned — never counts for FTR
        Assert.Single(journeys[0].Attempts);
        Assert.True(journeys[1].Completed);
        Assert.Equal(2, journeys[1].Attempts.Count);
    }

    [Fact]
    public void GroupJourneys_GapBoundary_Exactly30DaysStaysOneJourney_StrictlyMoreSplits()
    {
        var prevEnd = T0.AddHours(1);

        var exactly30 = DeviceJourneyCalculator.GroupJourneys(new[]
        {
            Ref("s-1", SessionStatus.Failed, T0, completedAt: prevEnd),
            Ref("s-2", SessionStatus.Failed, prevEnd.AddDays(30)),
        });
        Assert.Single(exactly30); // spec: "> 30 days" — the boundary itself does not split

        var over30 = DeviceJourneyCalculator.GroupJourneys(new[]
        {
            Ref("s-1", SessionStatus.Failed, T0, completedAt: prevEnd),
            Ref("s-2", SessionStatus.Failed, prevEnd.AddDays(30).AddHours(1)),
        });
        Assert.Equal(2, over30.Count);
    }

    [Fact]
    public void GroupJourneys_GapMeasuredFromCompletedAt_NotStartedAt()
    {
        // Previous attempt STARTED 40 days before the next one but went terminal only 10 days
        // before it — the gap is 10 days, one journey. StartedAt-based measuring would split.
        var journeys = DeviceJourneyCalculator.GroupJourneys(new[]
        {
            Ref("s-1", SessionStatus.Failed, T0.AddDays(-40), completedAt: T0.AddDays(-10)),
            Ref("s-2", SessionStatus.Succeeded, T0),
        });

        var journey = Assert.Single(journeys);
        Assert.Equal(2, journey.Attempts.Count);
    }

    [Fact]
    public void GroupJourneys_WhiteGloveIsOneSessionRow_OneAttempt_NoStitching()
    {
        // WG part 1 + part 2 share one session row (second-sweep finding): a completed WG
        // enrollment is exactly one ref and one first-time-right attempt.
        var wg = Session("s-1", wg: true, durationSeconds: 5400, completedAt: T0.AddDays(6));
        var reference = DeviceJourneyCalculator.BuildSessionRef(wg)!;

        var journeys = DeviceJourneyCalculator.GroupJourneys(new[] { reference });

        var journey = Assert.Single(journeys);
        Assert.True(journey.Completed);
        Assert.Single(journey.Attempts);
        Assert.Equal(5400, journey.Attempts[0].DurationSeconds); // pause-free combined duration, verbatim
    }

    [Fact]
    public void Derive_CurrentJourneyAttempts_IsTheLastJourneys_OpenOrCompleted()
    {
        var (journeyCount, currentAttempts) = DeviceJourneyCalculator.Derive(new[]
        {
            Ref("s-1", SessionStatus.Succeeded, T0),
            Ref("s-2", SessionStatus.Failed, T0.AddDays(1)),
            Ref("s-3", SessionStatus.Failed, T0.AddDays(2)),
        });

        Assert.Equal(2, journeyCount);
        Assert.Equal(2, currentAttempts); // the open redeployment journey has 2 attempts so far

        Assert.Equal((0, 0), DeviceJourneyCalculator.Derive(Array.Empty<DeviceSessionRef>()));
    }

    // ── history row assembly (display precedence) ───────────────────────────

    [Fact]
    public void BuildDeviceHistoryRow_DisplayFields_FollowTheNewestChainEntry()
    {
        var newest = Session("s-2", serial: " PF4X1ABC ", startedAt: T0.AddDays(1));
        var chain = new List<DeviceSessionRef>
        {
            Ref("s-1", SessionStatus.Failed, T0),
            Ref("s-2", SessionStatus.Succeeded, T0.AddDays(1)),
        };

        var row = TableStorageService.BuildDeviceHistoryRow(TenantA, "pf4x1abc", chain, existing: null, newest);

        Assert.Equal("PF4X1ABC", row.SerialNumber); // trimmed original casing
        Assert.Equal("Contoso", row.Manufacturer);
        Assert.Equal("Laptop 5", row.Model);
        Assert.Equal(2, row.CurrentJourneyAttempts);
        Assert.Equal(1, row.JourneyCount);
        Assert.Equal(DeviceJourneyCalculator.CurrentVersion, row.JourneyVersion);
    }

    [Fact]
    public void BuildDeviceHistoryRow_BackfillingAnOlderSession_KeepsExistingDisplayFields()
    {
        var older = Session("s-1", serial: "pf4x1abc", startedAt: T0);
        older.Model = "Laptop 4"; // stale model from the older enrollment
        var chain = new List<DeviceSessionRef>
        {
            Ref("s-1", SessionStatus.Failed, T0),
            Ref("s-2", SessionStatus.Succeeded, T0.AddDays(1)),
        };
        var existing = new DeviceHistory
        {
            TenantId = TenantA, SerialKey = "pf4x1abc",
            SerialNumber = "PF4X1ABC", Manufacturer = "Contoso", Model = "Laptop 5",
        };

        var row = TableStorageService.BuildDeviceHistoryRow(TenantA, "pf4x1abc", chain, existing, older);

        Assert.Equal("PF4X1ABC", row.SerialNumber); // not regressed to the older session's data
        Assert.Equal("Laptop 5", row.Model);
    }

    // ── persistence round-trips ─────────────────────────────────────────────

    [Fact]
    public void DeviceHistoryEntity_RoundTripsAllFields_IncludingChainJson()
    {
        var history = new DeviceHistory
        {
            TenantId = TenantA,
            SerialKey = "pf4x1abc",
            SerialNumber = "PF4X1ABC",
            Manufacturer = "Contoso",
            Model = "Laptop 5",
            CurrentJourneyAttempts = 2,
            JourneyCount = 3,
            JourneyVersion = 1,
            LastUpdated = T0,
            Chain = new List<DeviceSessionRef>
            {
                new()
                {
                    SessionId = "s-1", StartedAt = T0, CompletedAt = T0.AddHours(5),
                    Status = "Succeeded", EnrollmentType = "v2", IsPreProvisioned = true,
                    DurationSeconds = 1800, AdminMarked = true,
                },
                new()
                {
                    SessionId = "s-2", StartedAt = T0.AddDays(1), CompletedAt = null,
                    Status = "Incomplete", EnrollmentType = "v1", DurationSeconds = null,
                },
            },
        };

        var mapped = TableStorageService.MapToDeviceHistory(
            TableStorageService.BuildDeviceHistoryEntity(history));

        Assert.Equal(TenantA, mapped.TenantId);
        Assert.Equal("pf4x1abc", mapped.SerialKey);
        Assert.Equal("PF4X1ABC", mapped.SerialNumber);
        Assert.Equal("Contoso", mapped.Manufacturer);
        Assert.Equal("Laptop 5", mapped.Model);
        Assert.Equal(2, mapped.CurrentJourneyAttempts);
        Assert.Equal(3, mapped.JourneyCount);
        Assert.Equal(1, mapped.JourneyVersion);
        Assert.Equal(T0, mapped.LastUpdated);

        Assert.Equal(2, mapped.Chain.Count);
        var first = mapped.Chain[0];
        Assert.Equal("s-1", first.SessionId);
        Assert.Equal(T0, first.StartedAt);
        Assert.Equal(T0.AddHours(5), first.CompletedAt);
        Assert.Equal("Succeeded", first.Status);
        Assert.Equal("v2", first.EnrollmentType);
        Assert.True(first.IsPreProvisioned);
        Assert.Equal(1800, first.DurationSeconds);
        Assert.True(first.AdminMarked);
        var second = mapped.Chain[1];
        Assert.Null(second.CompletedAt);      // Incomplete: no terminal timestamp requirement
        Assert.Null(second.DurationSeconds);  // Incomplete: deliberately no duration — stays null
        Assert.False(second.AdminMarked);
    }

    [Fact]
    public void DeviceHistoryEntity_RowKeyEncoding_MakesForbiddenKeyCharsSafe_AndRoundTrips()
    {
        // '/', '\', '#', '?' are forbidden in Table Storage keys — a defensive encoding keeps a
        // pathological serial writable, and the mapper restores the exact normalized serial.
        var weird = "ab/cd\\ef#1?x";
        var history = new DeviceHistory { TenantId = TenantA, SerialKey = weird };

        var entity = TableStorageService.BuildDeviceHistoryEntity(history);
        Assert.All(new[] { "/", "\\", "#", "?" }, c => Assert.DoesNotContain(c, entity.RowKey));

        Assert.Equal(weird, TableStorageService.MapToDeviceHistory(entity).SerialKey);

        // Ordinary serials stay human-readable.
        Assert.Equal("pf4x1abc", TableStorageService.SerialRowKey("pf4x1abc"));
    }

    [Fact]
    public void DeviceJourneyAggregateEntity_RoundTripsAllFields_RowKeyIsTheDate()
    {
        var aggregate = new DeviceJourneyDailyAggregate
        {
            TenantId = TenantA,
            Date = "2026-07-20",
            JourneyVersion = 1,
            CompletedJourneyCount = 23,
            FirstTimeRightCount = 19,
            ExcludedSessionCount = 2,
            ComputedAt = T0,
            AttemptHistogram = new List<DeviceJourneyAttemptBucket>
            {
                new() { Attempts = 1, JourneyCount = 19 },
                new() { Attempts = 2, JourneyCount = 3 },
                new() { Attempts = 4, JourneyCount = 1 },
            },
        };

        var entity = TableStorageService.BuildDeviceJourneyAggregateEntity(aggregate);
        Assert.Equal("2026-07-20", entity.RowKey);

        var mapped = TableStorageService.MapToDeviceJourneyAggregate(entity);
        Assert.Equal(TenantA, mapped.TenantId);
        Assert.Equal("2026-07-20", mapped.Date);
        Assert.Equal(1, mapped.JourneyVersion);
        Assert.Equal(23, mapped.CompletedJourneyCount);
        Assert.Equal(19, mapped.FirstTimeRightCount);
        Assert.Equal(2, mapped.ExcludedSessionCount);
        Assert.Equal(T0, mapped.ComputedAt);
        Assert.Equal(3, mapped.AttemptHistogram.Count);
        Assert.Equal(1, mapped.AttemptHistogram[0].Attempts);
        Assert.Equal(19, mapped.AttemptHistogram[0].JourneyCount);
        Assert.Equal(4, mapped.AttemptHistogram[2].Attempts);
    }

    // ── daily FTR aggregation core ──────────────────────────────────────────

    private static readonly DateTime WindowStart = T0.Date.AddDays(-30);
    private static readonly DateTime WindowEnd = T0.Date.AddDays(1);

    private static DeviceHistory History(string tenantId, params DeviceSessionRef[] chain)
        => new() { TenantId = tenantId, SerialKey = Guid.NewGuid().ToString("N"), Chain = chain.ToList() };

    [Fact]
    public void FtrAggregates_BucketByCompletingSessionsStartDate_AndMirrorGlobal()
    {
        var histories = new List<DeviceHistory>
        {
            // Tenant A device 1: fail → success (2 attempts, completes on T0 date).
            History(TenantA,
                Ref("a1-1", SessionStatus.Failed, T0.AddDays(-1)),
                Ref("a1-2", SessionStatus.Succeeded, T0)),
            // Tenant A device 2: first-time right on the same date.
            History(TenantA, Ref("a2-1", SessionStatus.Succeeded, T0.AddHours(2))),
            // Tenant B device: first-time right on a different date.
            History(TenantB, Ref("b1-1", SessionStatus.Succeeded, T0.AddDays(-3))),
        };

        var rows = MaintenanceService.BuildDeviceJourneyAggregates(
            histories, Array.Empty<SessionSummary>(), WindowStart, WindowEnd, T0);

        // Tenant A (1 date) + Tenant B (1 date) + global (2 dates) = 4 rows.
        Assert.Equal(4, rows.Count);

        var tenantARow = rows.Single(r => r.TenantId == TenantA);
        Assert.Equal(T0.ToString("yyyy-MM-dd"), tenantARow.Date);
        Assert.Equal(2, tenantARow.CompletedJourneyCount);
        Assert.Equal(1, tenantARow.FirstTimeRightCount);
        Assert.Equal(DeviceJourneyCalculator.CurrentVersion, tenantARow.JourneyVersion);
        Assert.Equal(2, tenantARow.AttemptHistogram.Count);
        Assert.Equal(1, tenantARow.AttemptHistogram[0].Attempts);
        Assert.Equal(1, tenantARow.AttemptHistogram[0].JourneyCount);
        Assert.Equal(2, tenantARow.AttemptHistogram[1].Attempts);

        var globalOnT0 = rows.Single(r => r.TenantId == "global" && r.Date == T0.ToString("yyyy-MM-dd"));
        Assert.Equal(2, globalOnT0.CompletedJourneyCount); // tenant B's journey sits on its own date
    }

    [Fact]
    public void FtrAggregates_OpenAndAbandonedJourneys_NeverCount()
    {
        var histories = new List<DeviceHistory>
        {
            // Open journey: failures only (e.g. a WG device still waiting for its user session
            // has, at most, prior failed attempts in the chain — the Pending row itself never
            // becomes a ref).
            History(TenantA, Ref("a1-1", SessionStatus.Failed, T0)),
            // Abandoned journey (31d gap, no success) followed by a completed journey.
            History(TenantA,
                Ref("a2-1", SessionStatus.Failed, T0.AddDays(-20), completedAt: T0.AddDays(-20).AddHours(1)),
                Ref("a2-2", SessionStatus.Succeeded, T0.AddDays(11).AddHours(2))),
        };

        var rows = MaintenanceService.BuildDeviceJourneyAggregates(
            histories, Array.Empty<SessionSummary>(), WindowStart, T0.Date.AddDays(12), T0);

        // Only the completed journey produced rows: tenant + global on one date.
        Assert.Equal(2, rows.Count);
        var tenantRow = rows.Single(r => r.TenantId == TenantA);
        Assert.Equal(1, tenantRow.CompletedJourneyCount);
        Assert.Equal(1, tenantRow.FirstTimeRightCount); // the abandoned attempt belongs to a DIFFERENT journey
        Assert.Equal(T0.AddDays(11).ToString("yyyy-MM-dd"), tenantRow.Date);
    }

    [Fact]
    public void FtrAggregates_JourneysCompletedOutsideTheWindow_AreSkipped()
    {
        var histories = new List<DeviceHistory>
        {
            History(TenantA, Ref("a1-1", SessionStatus.Succeeded, WindowStart.AddDays(-1))),
        };

        var rows = MaintenanceService.BuildDeviceJourneyAggregates(
            histories, Array.Empty<SessionSummary>(), WindowStart, WindowEnd, T0);

        Assert.Empty(rows);
    }

    [Fact]
    public void FtrAggregates_JunkSerialExclusions_ProduceDisclosureRows()
    {
        var excluded = new List<SessionSummary>
        {
            Session("x-1", serial: "Unknown", startedAt: T0),
            Session("x-2", serial: "Unknown", startedAt: T0.AddHours(1)),
            Session("x-3", serial: "Unknown", startedAt: WindowStart.AddDays(-2)), // outside window — ignored
        };

        var rows = MaintenanceService.BuildDeviceJourneyAggregates(
            Array.Empty<DeviceHistory>(), excluded, WindowStart, WindowEnd, T0);

        Assert.Equal(2, rows.Count); // tenant + global disclosure rows, no journey claim
        var tenantRow = rows.Single(r => r.TenantId == TenantA);
        Assert.Equal(0, tenantRow.CompletedJourneyCount);
        Assert.Equal(0, tenantRow.FirstTimeRightCount);
        Assert.Empty(tenantRow.AttemptHistogram);
        Assert.Equal(2, tenantRow.ExcludedSessionCount);
        Assert.Equal(2, rows.Single(r => r.TenantId == "global").ExcludedSessionCount);
    }

    [Fact]
    public void FtrAggregates_TwoCompletedJourneysOfOneDevice_SameDay_BothCount()
    {
        // Redeploy on the same day: success closes journey 1, the next success closes journey 2.
        var histories = new List<DeviceHistory>
        {
            History(TenantA,
                Ref("a1-1", SessionStatus.Succeeded, T0),
                Ref("a1-2", SessionStatus.Succeeded, T0.AddHours(3))),
        };

        var rows = MaintenanceService.BuildDeviceJourneyAggregates(
            histories, Array.Empty<SessionSummary>(), WindowStart, WindowEnd, T0);

        var tenantRow = rows.Single(r => r.TenantId == TenantA);
        Assert.Equal(2, tenantRow.CompletedJourneyCount);
        Assert.Equal(2, tenantRow.FirstTimeRightCount);
    }

    // ── attempt number (session banner, PR5) ────────────────────────────────

    [Fact]
    public void ComputeAttemptNumber_TerminalSessionInChain_ReturnsPositionWithinItsJourney()
    {
        var chain = new[]
        {
            Ref("s-1", SessionStatus.Failed, T0),
            Ref("s-2", SessionStatus.Succeeded, T0.AddHours(2)),
            Ref("s-3", SessionStatus.Failed, T0.AddDays(5)), // redeployment → new journey
        };

        Assert.Equal(1, DeviceJourneyCalculator.ComputeAttemptNumber(chain, "s-1", T0));
        Assert.Equal(2, DeviceJourneyCalculator.ComputeAttemptNumber(chain, "s-2", T0.AddHours(2)));
        Assert.Equal(1, DeviceJourneyCalculator.ComputeAttemptNumber(chain, "s-3", T0.AddDays(5)));
    }

    [Fact]
    public void ComputeAttemptNumber_LiveSession_ContinuesTheOpenJourney()
    {
        var chain = new[] { Ref("s-1", SessionStatus.Failed, T0) };
        // s-2 is still InProgress — not a chain ref; the virtual-attempt rule places it.
        Assert.Equal(2, DeviceJourneyCalculator.ComputeAttemptNumber(chain, "s-2", T0.AddDays(1)));
    }

    [Fact]
    public void ComputeAttemptNumber_LiveSession_AfterCompletedJourney_IsAttemptOne()
    {
        var chain = new[] { Ref("s-1", SessionStatus.Succeeded, T0) };
        Assert.Equal(1, DeviceJourneyCalculator.ComputeAttemptNumber(chain, "s-2", T0.AddDays(2)));
    }

    [Fact]
    public void ComputeAttemptNumber_LiveSession_AfterThirtyDayGap_IsAttemptOne()
    {
        var prevEnd = T0.AddHours(1);
        var chain = new[] { Ref("s-1", SessionStatus.Failed, T0, completedAt: prevEnd) };
        Assert.Equal(1, DeviceJourneyCalculator.ComputeAttemptNumber(chain, "s-2", prevEnd.AddDays(31)));
        // Inside the gap boundary the open journey continues instead.
        Assert.Equal(2, DeviceJourneyCalculator.ComputeAttemptNumber(chain, "s-3", prevEnd.AddDays(29)));
    }

    [Fact]
    public void ComputeAttemptNumber_EmptyChain_YieldsNull_NoGuessedPosition()
    {
        Assert.Null(DeviceJourneyCalculator.ComputeAttemptNumber(
            Array.Empty<DeviceSessionRef>(), "s-1", T0));
    }

    // ── fleet response helpers (PR5) ────────────────────────────────────────

    [Fact]
    public void SumAggregates_SumsAdditiveCounts_AndMergesHistograms()
    {
        var daily = new List<DeviceJourneyDailyAggregate>
        {
            new()
            {
                CompletedJourneyCount = 2, FirstTimeRightCount = 1, ExcludedSessionCount = 1,
                AttemptHistogram = new List<DeviceJourneyAttemptBucket>
                {
                    new() { Attempts = 1, JourneyCount = 1 },
                    new() { Attempts = 2, JourneyCount = 1 },
                },
            },
            new()
            {
                CompletedJourneyCount = 1, FirstTimeRightCount = 1, ExcludedSessionCount = 0,
                AttemptHistogram = new List<DeviceJourneyAttemptBucket> { new() { Attempts = 1, JourneyCount = 1 } },
            },
        };

        var totals = DeviceJourneyMetricsResponseBuilder.SumAggregates(daily);

        Assert.Equal(3, totals.CompletedJourneys);
        Assert.Equal(2, totals.FirstTimeRight);
        Assert.Equal(66.7, totals.FtrRatePct);
        Assert.Equal(1, totals.ExcludedSessions);
        Assert.Equal(2, totals.AttemptHistogram.Count);
        Assert.Equal(2, totals.AttemptHistogram.Single(b => b.Attempts == 1).JourneyCount);
        Assert.Equal(1, totals.AttemptHistogram.Single(b => b.Attempts == 2).JourneyCount);
    }

    [Fact]
    public void SumAggregates_NoCompletedJourneys_RateIsNull_NeverZero()
    {
        var totals = DeviceJourneyMetricsResponseBuilder.SumAggregates(new List<DeviceJourneyDailyAggregate>
        {
            new() { CompletedJourneyCount = 0, FirstTimeRightCount = 0, ExcludedSessionCount = 3 },
        });

        Assert.Equal(0, totals.CompletedJourneys);
        Assert.Null(totals.FtrRatePct);
        Assert.Equal(3, totals.ExcludedSessions);
    }

    [Theory]
    [InlineData(null, 30)]     // default window
    [InlineData("garbage", 30)]
    [InlineData("7", 7)]
    [InlineData("0", 1)]       // clamped low
    [InlineData("999", 180)]   // clamped to aggregate retention
    public void ClampDays_DefaultsAndClamps(string? raw, int expected)
    {
        Assert.Equal(expected, DeviceJourneyMetricsResponseBuilder.ClampDays(raw));
    }

    [Fact]
    public void InclusiveWindowStart_YieldsExactlyNCalendarDays()
    {
        // Codex review: both range ends are inclusive, so "today - days" returned N+1 keys —
        // days=1 summed yesterday AND today.
        var today = new DateTime(2026, 7, 27);
        Assert.Equal(today, DeviceJourneyMetricsResponseBuilder.InclusiveWindowStart(today, 1));
        Assert.Equal(today.AddDays(-29), DeviceJourneyMetricsResponseBuilder.InclusiveWindowStart(today, 30));
    }

    private static DeviceHistory RepeaterHistory(
        string serial, int attempts, params DeviceSessionRef[] chain)
        => new()
        {
            TenantId = TenantA,
            SerialKey = serial,
            SerialNumber = serial.ToUpperInvariant(),
            Chain = chain.ToList(),
            CurrentJourneyAttempts = attempts,
            JourneyCount = 1,
        };

    [Fact]
    public void SelectRepeatDevices_FiltersGate_WindowsOnNewestRef_AndPicksNewestFailure()
    {
        var histories = new List<DeviceHistory>
        {
            // Single attempt — not a repeat device.
            RepeaterHistory("clean-1", attempts: 1, Ref("c1", SessionStatus.Succeeded, T0)),
            // Repeat device, active in window: newest failed ref carries the reason lookup.
            RepeaterHistory("hot-1", attempts: 3,
                Ref("h1", SessionStatus.Failed, T0.AddDays(-2)),
                Ref("h2", SessionStatus.Failed, T0.AddDays(-1)),
                Ref("h3", SessionStatus.Incomplete, T0)),
            // Repeat device, but its pain is OLDER than the window — current lists only.
            RepeaterHistory("old-1", attempts: 4,
                Ref("o1", SessionStatus.Failed, T0.AddDays(-40))),
            // Lower attempt count → sorts after hot-1.
            RepeaterHistory("warm-1", attempts: 2,
                Ref("w1", SessionStatus.Failed, T0.AddDays(-3)),
                Ref("w2", SessionStatus.Succeeded, T0.AddDays(-2))),
        };

        var selected = DeviceJourneyMetricsResponseBuilder.SelectRepeatDevices(histories, T0.AddDays(-30));

        Assert.Equal(2, selected.Count);
        Assert.Equal("hot-1", selected[0].History.SerialKey);   // attempts desc
        Assert.Equal("h3", selected[0].Newest.SessionId);
        Assert.Equal("h2", selected[0].NewestFailed!.SessionId); // last FAILED, not last ref
        Assert.Equal("warm-1", selected[1].History.SerialKey);
    }

    [Fact]
    public void SelectRepeatDevices_CapsAtTen_ByAttemptsThenRecency()
    {
        var histories = new List<DeviceHistory>();
        for (var i = 0; i < 12; i++)
        {
            histories.Add(RepeaterHistory($"dev-{i:D2}", attempts: 2 + i,
                Ref($"r-{i}", SessionStatus.Failed, T0.AddDays(-i))));
        }

        var selected = DeviceJourneyMetricsResponseBuilder.SelectRepeatDevices(histories, T0.AddDays(-30));

        Assert.Equal(10, selected.Count);
        Assert.Equal("dev-11", selected[0].History.SerialKey); // highest attempts first
        Assert.DoesNotContain(selected, c => c.History.SerialKey == "dev-00");
        Assert.DoesNotContain(selected, c => c.History.SerialKey == "dev-01");
    }
}

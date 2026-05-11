using AutopilotMonitor.Functions.Functions.Sessions;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests the pure aggregation pass that powers the dashboard stats cards.
/// HTTP-trigger end (auth, repo wiring) is covered by the live runtime; here
/// we only assert the deterministic counting + today/window-boundary logic.
/// </summary>
public class SessionStatsAggregationTests
{
    private static SessionSummary MakeSession(
        SessionStatus status,
        DateTime startedAt,
        int? durationSeconds = null)
        => new()
        {
            SessionId = Guid.NewGuid().ToString(),
            TenantId = "00000000-0000-0000-0000-000000000001",
            Status = status,
            StartedAt = startedAt,
            DurationSeconds = durationSeconds,
        };

    [Fact]
    public void Empty_session_list_yields_zero_stats()
    {
        var stats = TableStorageService.AggregateSessionStats(Array.Empty<SessionSummary>(), days: 7);

        Assert.Equal(7, stats.Days);
        Assert.Equal(0, stats.ActiveCount);
        Assert.Equal(0, stats.TotalLastNDays);
        Assert.Equal(0, stats.SucceededLastNDays);
        Assert.Equal(0, stats.FailedLastNDays);
        Assert.Equal(0, stats.SuccessRatePct);
        Assert.Equal(0, stats.AvgDurationMinutes);
        Assert.Equal(0, stats.TotalToday);
        Assert.Equal(0, stats.FailedToday);
    }

    [Fact]
    public void Active_count_is_InProgress_only()
    {
        // "Currently enrolling" must match LIVE activity. Pending (WhiteGlove
        // pre-prov complete, awaiting user power-on) and Stalled (>60min no
        // progress) are non-terminal but NOT actively enrolling — Pending in
        // particular can sit for days/weeks and would dominate the count for
        // any tenant with regular WhiteGlove provisioning.
        var now = DateTime.UtcNow;
        var sessions = new[]
        {
            MakeSession(SessionStatus.InProgress, now.AddHours(-1)),
            MakeSession(SessionStatus.InProgress, now.AddHours(-2)),
            MakeSession(SessionStatus.Pending, now.AddHours(-3)),
            MakeSession(SessionStatus.Stalled, now.AddHours(-4)),
            MakeSession(SessionStatus.Succeeded, now.AddHours(-5), 600),
            MakeSession(SessionStatus.Failed, now.AddHours(-6)),
            MakeSession(SessionStatus.Unknown, now.AddHours(-7)),
        };

        var stats = TableStorageService.AggregateSessionStats(sessions, days: 7);

        Assert.Equal(2, stats.ActiveCount);
    }

    [Fact]
    public void Success_rate_is_succeeded_over_terminal_only()
    {
        var now = DateTime.UtcNow;
        // 3 succeeded + 1 failed → 75% (NOT 60% — InProgress must not pull rate down)
        var sessions = new[]
        {
            MakeSession(SessionStatus.Succeeded, now.AddHours(-1), 600),
            MakeSession(SessionStatus.Succeeded, now.AddHours(-2), 700),
            MakeSession(SessionStatus.Succeeded, now.AddHours(-3), 800),
            MakeSession(SessionStatus.Failed, now.AddHours(-4)),
            MakeSession(SessionStatus.InProgress, now.AddHours(-5)),
        };

        var stats = TableStorageService.AggregateSessionStats(sessions, days: 7);

        Assert.Equal(75, stats.SuccessRatePct);
    }

    [Fact]
    public void Success_rate_zero_when_no_terminal_sessions_in_window()
    {
        var now = DateTime.UtcNow;
        var sessions = new[]
        {
            MakeSession(SessionStatus.InProgress, now.AddHours(-1)),
            MakeSession(SessionStatus.Stalled, now.AddHours(-2)),
        };

        var stats = TableStorageService.AggregateSessionStats(sessions, days: 7);

        Assert.Equal(0, stats.SuccessRatePct);
    }

    [Fact]
    public void Avg_duration_uses_succeeded_only_with_positive_duration()
    {
        var now = DateTime.UtcNow;
        var sessions = new[]
        {
            MakeSession(SessionStatus.Succeeded, now.AddHours(-1), 1800), // 30 min
            MakeSession(SessionStatus.Succeeded, now.AddHours(-2), 600),  // 10 min
            MakeSession(SessionStatus.Succeeded, now.AddHours(-3), null), // missing duration — skipped
            MakeSession(SessionStatus.Failed,    now.AddHours(-4), 7200), // failed — skipped (often runaway durations skew the avg)
            MakeSession(SessionStatus.InProgress, now.AddHours(-5), 60),  // active — skipped
        };

        var stats = TableStorageService.AggregateSessionStats(sessions, days: 7);

        // (30 + 10) / 2 = 20 minutes
        Assert.Equal(20, stats.AvgDurationMinutes);
    }

    [Fact]
    public void Today_counters_use_UTC_midnight_boundary()
    {
        var utcMidnight = DateTime.UtcNow.Date;
        var sessions = new[]
        {
            MakeSession(SessionStatus.Succeeded, utcMidnight.AddHours(2), 600),  // today
            MakeSession(SessionStatus.Failed,    utcMidnight.AddHours(5)),       // today
            MakeSession(SessionStatus.Succeeded, utcMidnight.AddSeconds(-1)),    // yesterday (one tick before UTC midnight)
            MakeSession(SessionStatus.Failed,    utcMidnight.AddDays(-1).AddHours(10)), // yesterday
        };

        var stats = TableStorageService.AggregateSessionStats(sessions, days: 7);

        Assert.Equal(2, stats.TotalToday);
        Assert.Equal(1, stats.FailedToday);
    }

    [Fact]
    public void Total_last_n_days_equals_input_list_count()
    {
        // The drain caller is responsible for the days-window scope —
        // aggregation just trusts whatever it gets and reports the total it saw.
        var now = DateTime.UtcNow;
        var sessions = new[]
        {
            MakeSession(SessionStatus.Succeeded, now.AddDays(-1), 600),
            MakeSession(SessionStatus.Failed,    now.AddDays(-2)),
            MakeSession(SessionStatus.InProgress, now.AddDays(-3)),
        };

        var stats = TableStorageService.AggregateSessionStats(sessions, days: 7);

        Assert.Equal(3, stats.TotalLastNDays);
    }

    [Fact]
    public void ComputedAt_is_set_to_utc_now_within_tolerance()
    {
        var before = DateTime.UtcNow;
        var stats = TableStorageService.AggregateSessionStats(Array.Empty<SessionSummary>(), days: 7);
        var after = DateTime.UtcNow;

        Assert.InRange(stats.ComputedAt, before.AddSeconds(-1), after.AddSeconds(1));
    }

    // ── Days-query parsing on the HTTP layer ────────────────────────────────

    [Fact]
    public void TryParseDays_defaults_to_seven_when_missing()
    {
        var ok = GetSessionStatsFunction.TryParseDays(null, out var days, out var error);
        Assert.True(ok);
        Assert.Equal(7, days);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("366")]
    [InlineData("banana")]
    public void TryParseDays_rejects_invalid_input(string raw)
    {
        var ok = GetSessionStatsFunction.TryParseDays(raw, out _, out var error);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("7", 7)]
    [InlineData("30", 30)]
    [InlineData("365", 365)]
    public void TryParseDays_accepts_valid_input(string raw, int expected)
    {
        var ok = GetSessionStatsFunction.TryParseDays(raw, out var days, out var error);
        Assert.True(ok);
        Assert.Equal(expected, days);
        Assert.Null(error);
    }
}

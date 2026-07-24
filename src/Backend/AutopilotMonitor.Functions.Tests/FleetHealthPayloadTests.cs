using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Covers the server-side Fleet Health aggregation in
/// <see cref="MetricsMath.BuildFleetHealthPayload"/>, the single source of truth shared by the
/// tenant (metrics/fleet-health) and global (global/metrics/fleet-health) functions. Success and
/// failure rates follow the SLA convention — over finished enrollments (Succeeded + Failed) only,
/// so in-flight sessions never dilute them. Average duration stays over non-in-progress sessions
/// that carry a duration (failures included).
/// </summary>
public class FleetHealthPayloadTests
{
    private static SessionSummary S(
        SessionStatus status,
        int? durationSeconds = null,
        string manufacturer = "Contoso",
        string model = "Laptop",
        string? failureReason = null,
        DateTime? startedAt = null)
        => new()
        {
            TenantId = Guid.NewGuid().ToString(),
            SessionId = Guid.NewGuid().ToString(),
            Status = status,
            DurationSeconds = durationSeconds,
            Manufacturer = manufacturer,
            Model = model,
            FailureReason = failureReason!,
            StartedAt = startedAt ?? DateTime.UtcNow,
        };

    [Fact]
    public void EmptyInput_YieldsZeroedStats_AndOneDailyPointPerDay()
    {
        var payload = MetricsMath.BuildFleetHealthPayload(new List<SessionSummary>(), days: 7);

        Assert.True(payload.Success);
        Assert.Equal(7, payload.Days);
        Assert.Equal(0, payload.Stats.Total);
        Assert.Equal(0d, payload.Stats.SuccessRate);
        Assert.Equal(0, payload.Stats.AvgDurationMinutes);
        Assert.Empty(payload.FailureReasons);
        Assert.Empty(payload.ModelHealth);
        Assert.Empty(payload.SlowestModels);
        Assert.Empty(payload.TopFailingModels);
        // Timeline always spans the full window so empty days still render.
        Assert.Equal(7, payload.DailyData.Count);
        Assert.All(payload.DailyData, p => Assert.Equal(0, p.Success + p.Failed));
    }

    [Fact]
    public void SuccessRate_IsOverFinishedSessions_InFlightAndIncompleteExcluded()
    {
        var sessions = new List<SessionSummary>
        {
            S(SessionStatus.Succeeded),
            S(SessionStatus.Succeeded),
            S(SessionStatus.Failed),
            S(SessionStatus.InProgress),
            S(SessionStatus.Pending),
            S(SessionStatus.Incomplete), // terminal but non-failure — stays out of the rate
        };

        var payload = MetricsMath.BuildFleetHealthPayload(sessions, days: 7);

        Assert.Equal(6, payload.Stats.Total);
        Assert.Equal(2, payload.Stats.Succeeded);
        Assert.Equal(1, payload.Stats.Failed);
        Assert.Equal(1, payload.Stats.InProgress);
        Assert.Equal(1, payload.Stats.Incomplete);
        // 2 / (2 + 1) = 66.7% (over finished), NOT 2 / 6 = 33.3% (over total).
        Assert.Equal(66.7d, payload.Stats.SuccessRate);
    }

    [Fact]
    public void SuccessRate_IsZero_WhenNothingFinishedYet()
    {
        var sessions = new List<SessionSummary>
        {
            S(SessionStatus.InProgress),
            S(SessionStatus.Pending),
        };

        var payload = MetricsMath.BuildFleetHealthPayload(sessions, days: 7);

        // No finished enrollments → no rate; clients render "—" off Succeeded + Failed == 0.
        Assert.Equal(0d, payload.Stats.SuccessRate);
    }

    [Fact]
    public void AvgDuration_IncludesFailedWithDuration_ButExcludesInProgress()
    {
        var sessions = new List<SessionSummary>
        {
            S(SessionStatus.Succeeded, durationSeconds: 600),   // 10 min
            S(SessionStatus.Failed, durationSeconds: 1200),     // 20 min — counted
            S(SessionStatus.InProgress, durationSeconds: 9999), // excluded despite duration
            S(SessionStatus.Succeeded, durationSeconds: 0),     // excluded: no positive duration
        };

        var payload = MetricsMath.BuildFleetHealthPayload(sessions, days: 7);

        // (600 + 1200) / 2 / 60 = 15 min.
        Assert.Equal(15, payload.Stats.AvgDurationMinutes);
    }

    [Fact]
    public void ModelHealth_GroupsByManufacturerModel_RanksByVolume_TopSix()
    {
        var sessions = new List<SessionSummary>();
        // 7 distinct models with descending volume 7..1 → only top 6 returned.
        for (int volume = 7; volume >= 1; volume--)
            for (int i = 0; i < volume; i++)
                sessions.Add(S(SessionStatus.Succeeded, model: $"Model{volume}"));

        var payload = MetricsMath.BuildFleetHealthPayload(sessions, days: 30);

        Assert.Equal(6, payload.ModelHealth.Count);
        Assert.Equal("Contoso Model7", payload.ModelHealth[0].Model);
        Assert.Equal(7, payload.ModelHealth[0].Total);
        Assert.Equal(7, payload.ModelHealth[0].Succeeded);
        // The smallest (volume 1) is dropped by the top-6 cut.
        Assert.DoesNotContain(payload.ModelHealth, m => m.Model == "Contoso Model1");
    }

    [Fact]
    public void ModelHealth_CountsFailedSeparately_InFlightInTotalOnly()
    {
        var sessions = new List<SessionSummary>
        {
            S(SessionStatus.Succeeded, model: "X"),
            S(SessionStatus.Failed, model: "X"),
            S(SessionStatus.InProgress, model: "X"),
        };

        var payload = MetricsMath.BuildFleetHealthPayload(sessions, days: 7);

        var m = Assert.Single(payload.ModelHealth);
        // Total keeps the full device count; the client derives the rate from
        // Succeeded / (Succeeded + Failed), so the in-flight session must land
        // in Total but in neither outcome bucket.
        Assert.Equal(3, m.Total);
        Assert.Equal(1, m.Succeeded);
        Assert.Equal(1, m.Failed);
    }

    [Fact]
    public void ModelKey_FallsBackToUnknown_WhenManufacturerAndModelBlank()
    {
        var payload = MetricsMath.BuildFleetHealthPayload(
            new List<SessionSummary> { S(SessionStatus.Succeeded, manufacturer: "", model: "") },
            days: 7);

        Assert.Equal("Unknown", Assert.Single(payload.ModelHealth).Model);
    }

    [Fact]
    public void SlowestModels_RankByAvgDuration_SucceededOnly()
    {
        var sessions = new List<SessionSummary>
        {
            S(SessionStatus.Succeeded, durationSeconds: 600, model: "Fast"),   // 10 min
            S(SessionStatus.Succeeded, durationSeconds: 600, model: "Fast"),   // avg 10 min
            S(SessionStatus.Succeeded, durationSeconds: 1800, model: "Slow"),  // 30 min
            S(SessionStatus.Failed, durationSeconds: 9999, model: "Slow"),     // ignored (not succeeded)
        };

        var payload = MetricsMath.BuildFleetHealthPayload(sessions, days: 30);

        Assert.Equal(2, payload.SlowestModels.Count);
        Assert.Equal("Contoso Slow", payload.SlowestModels[0].Model);
        Assert.Equal(30, payload.SlowestModels[0].AvgMinutes);
        Assert.Equal(1, payload.SlowestModels[0].Count);
        Assert.Equal("Contoso Fast", payload.SlowestModels[1].Model);
        Assert.Equal(10, payload.SlowestModels[1].AvgMinutes);
    }

    [Fact]
    public void TopFailingModels_OnlyModelsWithFailures_RankedByFailedCount()
    {
        var sessions = new List<SessionSummary>
        {
            // Model A: 1 failed / 2 finished → 50%; the 2 in-flight sessions widen Total
            // but must not dilute the rate (over-total it would read 25%).
            S(SessionStatus.Failed, model: "A"),
            S(SessionStatus.Succeeded, model: "A"),
            S(SessionStatus.InProgress, model: "A"),
            S(SessionStatus.Pending, model: "A"),
            // Model B: 2 failed of 2 finished → 100%
            S(SessionStatus.Failed, model: "B"),
            S(SessionStatus.Failed, model: "B"),
            // Model C: no failures → excluded entirely
            S(SessionStatus.Succeeded, model: "C"),
        };

        var payload = MetricsMath.BuildFleetHealthPayload(sessions, days: 30);

        Assert.Equal(2, payload.TopFailingModels.Count);
        Assert.Equal("Contoso B", payload.TopFailingModels[0].Model);
        Assert.Equal(2, payload.TopFailingModels[0].Failed);
        Assert.Equal(100, payload.TopFailingModels[0].FailureRate);
        Assert.Equal("Contoso A", payload.TopFailingModels[1].Model);
        Assert.Equal(4, payload.TopFailingModels[1].Total);
        Assert.Equal(50, payload.TopFailingModels[1].FailureRate);
        Assert.DoesNotContain(payload.TopFailingModels, m => m.Model == "Contoso C");
    }

    [Fact]
    public void FailureReasons_GroupByPrefix_KeepFullestText_AndTakeTopFive()
    {
        var sessions = new List<SessionSummary>();
        // 5 distinct short reasons with descending frequency 5..1.
        for (int freq = 5; freq >= 1; freq--)
            for (int i = 0; i < freq; i++)
                sessions.Add(S(SessionStatus.Failed, failureReason: $"Reason {freq}"));

        // Two variants that share the first 50 characters collapse into one group;
        // the longest variant is kept for display so the UI can show the full text.
        var prefix = new string('x', 50);
        var shortVariant = prefix + " short";
        var longVariant = prefix + " a much longer tail that must survive intact";
        for (int i = 0; i < 6; i++)
            sessions.Add(S(SessionStatus.Failed, failureReason: shortVariant));
        sessions.Add(S(SessionStatus.Failed, failureReason: longVariant));

        var payload = MetricsMath.BuildFleetHealthPayload(sessions, days: 30);

        Assert.Equal(5, payload.FailureReasons.Count);
        // The grouped cluster (6 + 1 = 7) outranks every "Reason N".
        Assert.Equal(7, payload.FailureReasons[0].Count);
        // The full, longest text survives end-to-end — no 50-char truncation.
        Assert.Equal(longVariant, payload.FailureReasons[0].Reason);
        Assert.DoesNotContain("...", payload.FailureReasons[0].Reason);
    }

    [Fact]
    public void DailyData_BucketsByStartedAtDay_NewestLast()
    {
        var today = DateTime.UtcNow;
        var yesterday = today.AddDays(-1);
        var sessions = new List<SessionSummary>
        {
            S(SessionStatus.Succeeded, startedAt: today),
            S(SessionStatus.Failed, startedAt: today),
            S(SessionStatus.Succeeded, startedAt: yesterday),
        };

        var payload = MetricsMath.BuildFleetHealthPayload(sessions, days: 7);

        Assert.Equal(7, payload.DailyData.Count);
        var last = payload.DailyData[^1];   // today is newest → last entry
        Assert.Equal(today.ToString("yyyy-MM-dd"), last.Date);
        Assert.Equal(1, last.Success);
        Assert.Equal(1, last.Failed);

        var prev = payload.DailyData[^2];
        Assert.Equal(yesterday.ToString("yyyy-MM-dd"), prev.Date);
        Assert.Equal(1, prev.Success);
        Assert.Equal(0, prev.Failed);
    }
}

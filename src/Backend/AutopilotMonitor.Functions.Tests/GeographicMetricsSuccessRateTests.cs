using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Functions.Functions.Metrics;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the success-rate semantics of the geographic aggregation
/// (<see cref="GetGeographicMetricsFunction.ComputeGeographicMetrics"/>): the rate is an outcome
/// quota over finished enrollments (Succeeded + Failed) following the SLA convention. In-flight
/// sessions (InProgress/Pending/Stalled/AwaitingUser) and Incomplete (terminal, non-failure) sit
/// in SessionCount but never dilute the rate — the customer-visible symptom this pins against was
/// a site mid-rollout showing a poor rate despite zero actual failures.
/// </summary>
public class GeographicMetricsSuccessRateTests
{
    private static SessionSummary S(SessionStatus status, string city = "Frankfurt")
        => new()
        {
            TenantId = "00000000-0000-0000-0000-000000000fe0",
            SessionId = Guid.NewGuid().ToString(),
            Status = status,
            StartedAt = DateTime.UtcNow.AddHours(-2),
            GeoCountry = "DE",
            GeoRegion = "Hessen",
            GeoCity = city,
            GeoLoc = "50.11,8.68",
        };

    [Fact]
    public void SuccessRate_IsOverFinishedSessions_InFlightAndIncompleteExcluded()
    {
        var sessions = new List<SessionSummary>
        {
            S(SessionStatus.Succeeded),
            S(SessionStatus.Succeeded),
            S(SessionStatus.Succeeded),
            S(SessionStatus.Failed),
            S(SessionStatus.InProgress),
            S(SessionStatus.Pending),
            S(SessionStatus.AwaitingUser),
            S(SessionStatus.Incomplete), // terminal but non-failure — stays out of the rate
        };

        var result = GetGeographicMetricsFunction.ComputeGeographicMetrics(
            sessions, new List<AppInstallSummary>(), "city");

        var loc = Assert.Single(result.Locations);
        Assert.Equal(8, loc.SessionCount);
        Assert.Equal(3, loc.Succeeded);
        Assert.Equal(1, loc.Failed);
        // 3 / (3 + 1) = 75% (over finished), NOT 3 / 8 = 37.5% (over all sessions).
        Assert.Equal(75d, loc.SuccessRate);
    }

    [Fact]
    public void SuccessRate_IsZero_WhenNothingFinishedYet()
    {
        var sessions = new List<SessionSummary>
        {
            S(SessionStatus.InProgress),
            S(SessionStatus.Pending),
        };

        var result = GetGeographicMetricsFunction.ComputeGeographicMetrics(
            sessions, new List<AppInstallSummary>(), "city");

        var loc = Assert.Single(result.Locations);
        // No finished enrollments → no rate; the frontend renders "—" off Succeeded + Failed == 0.
        Assert.Equal(0d, loc.SuccessRate);
        Assert.Equal(0, loc.Succeeded + loc.Failed);
        Assert.Equal(2, loc.SessionCount);
    }

    [Fact]
    public void SuccessRate_IsPerLocation_NotGlobal()
    {
        var sessions = new List<SessionSummary>
        {
            // Frankfurt: 1 / 1 finished → 100% despite the in-flight session.
            S(SessionStatus.Succeeded, city: "Frankfurt"),
            S(SessionStatus.InProgress, city: "Frankfurt"),
            // Berlin: 1 / 2 finished → 50%.
            S(SessionStatus.Succeeded, city: "Berlin"),
            S(SessionStatus.Failed, city: "Berlin"),
        };

        var result = GetGeographicMetricsFunction.ComputeGeographicMetrics(
            sessions, new List<AppInstallSummary>(), "city");

        var frankfurt = result.Locations.Single(l => l.City == "Frankfurt");
        var berlin = result.Locations.Single(l => l.City == "Berlin");
        Assert.Equal(100d, frankfurt.SuccessRate);
        Assert.Equal(50d, berlin.SuccessRate);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Functions.Functions.Metrics;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the API-latency aggregation of
/// <see cref="GetGeographicMetricsFunction.ComputeGeographicMetrics"/>: the avg figures are
/// REQUEST-WEIGHTED averages — Σ(sessionAvg·requests)/Σ(requests) — not averages of session
/// averages, so a long session with many uploads counts proportionally. The median figures
/// (upper median over per-session averages, duration convention) are the robust display
/// statistic and the baseline for <c>ApiLatencyVsGlobalPct</c>: one corrupt session average
/// (a request spanning a sleep/hibernate gap) must not drag a whole location (2026-08-15).
/// Sessions without latency data (agents predating <c>AvgApiLatencyMs</c>) drop out entirely.
/// </summary>
public class GeographicMetricsApiLatencyTests
{
    private static SessionSummary S(string country, string city, double? avgLatencyMs, int? requestCount)
        => new()
        {
            TenantId = "00000000-0000-0000-0000-000000000fe0",
            SessionId = Guid.NewGuid().ToString(),
            Status = SessionStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddHours(-2),
            GeoCountry = country,
            GeoRegion = country,
            GeoCity = city,
            GeoLoc = "0,0",
            AvgApiLatencyMs = avgLatencyMs,
            ApiRequestCount = requestCount,
        };

    [Fact]
    public void LocationLatency_IsRequestWeighted_NotAverageOfAverages()
    {
        var sessions = new List<SessionSummary>
        {
            S("ID", "Jakarta", 1000, 300),
            S("ID", "Jakarta", 500, 100),
        };

        var result = GetGeographicMetricsFunction.ComputeGeographicMetrics(
            sessions, new List<AppInstallSummary>(), "country");

        var loc = Assert.Single(result.Locations);
        // (1000·300 + 500·100) / 400 = 875 — an unweighted mean would say 750.
        Assert.Equal(875d, loc.AvgApiLatencyMs);
        // Upper median of [500, 1000].
        Assert.Equal(1000d, loc.MedianApiLatencyMs);
        Assert.Equal(2, loc.ApiLatencySessionCount);
    }

    [Fact]
    public void MedianLatency_IsRobustAgainstCorruptSessionAverage()
    {
        // Real-world shape (Amersfoort 2026-08): one session whose average was inflated to
        // ~23 minutes per request by a sleep-spanning sample, among ordinary ~150-200 ms peers.
        var sessions = new List<SessionSummary>
        {
            S("NL", "Amersfoort", 150, 100),
            S("NL", "Amersfoort", 170, 80),
            S("NL", "Amersfoort", 200, 120),
            S("NL", "Amersfoort", 1_382_551.5, 115),
        };

        var result = GetGeographicMetricsFunction.ComputeGeographicMetrics(
            sessions, new List<AppInstallSummary>(), "country");

        var loc = Assert.Single(result.Locations);
        // The weighted mean is wrecked by the corrupt session…
        Assert.True(loc.AvgApiLatencyMs > 100_000);
        // …the median is not: upper median of [150, 170, 200, 1382551.5] = 200.
        Assert.Equal(200d, loc.MedianApiLatencyMs);
        Assert.Equal(200d, result.GlobalAverages.MedianApiLatencyMs);
        // Median vs global median (same single location) → no deviation.
        Assert.Equal(0d, loc.ApiLatencyVsGlobalPct);
    }

    [Fact]
    public void SessionsWithoutLatencyData_DropOutOfWeighting_ButStayInSessionCount()
    {
        var sessions = new List<SessionSummary>
        {
            S("DE", "Frankfurt", 200, 100),
            S("DE", "Frankfurt", avgLatencyMs: null, requestCount: null), // pre-feature agent
            S("DE", "Frankfurt", avgLatencyMs: 0, requestCount: 50),      // zero = no signal
        };

        var result = GetGeographicMetricsFunction.ComputeGeographicMetrics(
            sessions, new List<AppInstallSummary>(), "country");

        var loc = Assert.Single(result.Locations);
        Assert.Equal(3, loc.SessionCount);
        Assert.Equal(1, loc.ApiLatencySessionCount);
        Assert.Equal(200d, loc.AvgApiLatencyMs);
        Assert.Equal(200d, loc.MedianApiLatencyMs);
    }

    [Fact]
    public void NoLatencyDataAnywhere_YieldsZeroes_NotDivisionByZero()
    {
        var sessions = new List<SessionSummary> { S("DE", "Frankfurt", null, null) };

        var result = GetGeographicMetricsFunction.ComputeGeographicMetrics(
            sessions, new List<AppInstallSummary>(), "country");

        var loc = Assert.Single(result.Locations);
        Assert.Equal(0d, loc.AvgApiLatencyMs);
        Assert.Equal(0d, loc.MedianApiLatencyMs);
        Assert.Equal(0, loc.ApiLatencySessionCount);
        Assert.Equal(0d, loc.ApiLatencyVsGlobalPct);
        Assert.Equal(0d, result.GlobalAverages.AvgApiLatencyMs);
        Assert.Equal(0d, result.GlobalAverages.MedianApiLatencyMs);
    }

    [Fact]
    public void GlobalLatency_WeightsAcrossLocations_AndVsGlobalPctCompares()
    {
        var sessions = new List<SessionSummary>
        {
            S("DE", "Frankfurt", 200, 100),
            S("ID", "Jakarta", 800, 100),
        };

        var result = GetGeographicMetricsFunction.ComputeGeographicMetrics(
            sessions, new List<AppInstallSummary>(), "country");

        // Equal request weights → global = (200 + 800) / 2 = 500.
        Assert.Equal(500d, result.GlobalAverages.AvgApiLatencyMs);
        // Upper median of [200, 800].
        Assert.Equal(800d, result.GlobalAverages.MedianApiLatencyMs);

        var de = result.Locations.Single(l => l.Country == "DE");
        var id = result.Locations.Single(l => l.Country == "ID");
        Assert.Equal(-75d, de.ApiLatencyVsGlobalPct); // median 200 vs global median 800
        Assert.Equal(0d, id.ApiLatencyVsGlobalPct);   // median 800 vs global median 800
    }

    [Fact]
    public void LocationWithoutData_KeepsZeroPct_EvenWhenGlobalHasData()
    {
        var sessions = new List<SessionSummary>
        {
            S("DE", "Frankfurt", 200, 100),
            S("FR", "Paris", null, null),
        };

        var result = GetGeographicMetricsFunction.ComputeGeographicMetrics(
            sessions, new List<AppInstallSummary>(), "country");

        var fr = result.Locations.Single(l => l.Country == "FR");
        Assert.Equal(0d, fr.AvgApiLatencyMs);
        Assert.Equal(0d, fr.MedianApiLatencyMs);
        Assert.Equal(0d, fr.ApiLatencyVsGlobalPct); // no data ≠ "0 ms fast"
    }
}

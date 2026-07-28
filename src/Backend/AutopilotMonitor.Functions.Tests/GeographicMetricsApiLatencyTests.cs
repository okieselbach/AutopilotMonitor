using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Functions.Functions.Metrics;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the API-latency aggregation of
/// <see cref="GetGeographicMetricsFunction.ComputeGeographicMetrics"/> (2026-07-28): both the
/// per-location and the global figure are REQUEST-WEIGHTED averages —
/// Σ(sessionAvg·requests)/Σ(requests) — not averages of session averages, so a long session
/// with many uploads counts proportionally. Sessions without latency data (agents predating
/// <c>AvgApiLatencyMs</c>) drop out of the weighting entirely.
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
        Assert.Equal(2, loc.ApiLatencySessionCount);
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
    }

    [Fact]
    public void NoLatencyDataAnywhere_YieldsZeroes_NotDivisionByZero()
    {
        var sessions = new List<SessionSummary> { S("DE", "Frankfurt", null, null) };

        var result = GetGeographicMetricsFunction.ComputeGeographicMetrics(
            sessions, new List<AppInstallSummary>(), "country");

        var loc = Assert.Single(result.Locations);
        Assert.Equal(0d, loc.AvgApiLatencyMs);
        Assert.Equal(0, loc.ApiLatencySessionCount);
        Assert.Equal(0d, loc.ApiLatencyVsGlobalPct);
        Assert.Equal(0d, result.GlobalAverages.AvgApiLatencyMs);
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

        var de = result.Locations.Single(l => l.Country == "DE");
        var id = result.Locations.Single(l => l.Country == "ID");
        Assert.Equal(-60d, de.ApiLatencyVsGlobalPct); // 200 vs 500
        Assert.Equal(60d, id.ApiLatencyVsGlobalPct);  // 800 vs 500
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
        Assert.Equal(0d, fr.ApiLatencyVsGlobalPct); // no data ≠ "0 ms fast"
    }
}

using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the per-session API-latency projection (2026-07-28): the V2 agent emits cumulative
/// counters <c>net_total_latency_ms</c> / <c>net_total_requests</c> in every
/// <c>agent_metrics_snapshot</c>; ingest derives the session-wide average from the LAST
/// snapshot of the batch and persists it via <c>UpdateSessionNetworkLatencyAsync</c>
/// (last-write-wins on cumulative counters → replay-idempotent).
/// Wire-format note: the ingest body is parsed with Newtonsoft into
/// <c>Dictionary&lt;string, object&gt;</c>, so integer JSON numbers arrive as boxed
/// <c>long</c> — fixtures mirror that, not the agent's in-memory types.
/// </summary>
public class IngestApiLatencyProjectionTests
{
    private static EnrollmentEvent Snapshot(object? totalLatencyMs, object? totalRequests)
    {
        var evt = new EnrollmentEvent
        {
            EventType = "agent_metrics_snapshot",
            Data = new Dictionary<string, object>
            {
                ["agent_cpu_percent"] = 3.62,
                ["net_requests"] = 50L,
                ["net_avg_latency_ms"] = 513.0,
            },
        };
        if (totalLatencyMs != null) evt.Data["net_total_latency_ms"] = totalLatencyMs;
        if (totalRequests != null) evt.Data["net_total_requests"] = totalRequests;
        return evt;
    }

    [Fact]
    public void CumulativeCounters_YieldSessionAverage()
    {
        var events = new List<EnrollmentEvent> { Snapshot(51_300L, 100L) };

        Assert.True(EventIngestProcessor.TryComputeSessionApiLatency(events, out var avg, out var count));
        Assert.Equal(513.0, avg);
        Assert.Equal(100, count);
    }

    [Fact]
    public void AverageIsRounded_ToOneDecimal()
    {
        var events = new List<EnrollmentEvent> { Snapshot(1_000L, 3L) };

        Assert.True(EventIngestProcessor.TryComputeSessionApiLatency(events, out var avg, out _));
        Assert.Equal(333.3, avg);
    }

    [Fact]
    public void LastSnapshotOfBatch_Wins()
    {
        // Cumulative counters grow monotonically — the last snapshot carries the most
        // complete session average.
        var events = new List<EnrollmentEvent>
        {
            Snapshot(51_300L, 100L),
            new EnrollmentEvent { EventType = "download_progress" },
            Snapshot(120_000L, 200L),
        };

        Assert.True(EventIngestProcessor.TryComputeSessionApiLatency(events, out var avg, out var count));
        Assert.Equal(600.0, avg);
        Assert.Equal(200, count);
    }

    [Fact]
    public void OldAgentSnapshot_WithoutTotalLatencyField_DoesNotMatch()
    {
        // Agents predating net_total_latency_ms emit net_total_requests only.
        var events = new List<EnrollmentEvent> { Snapshot(totalLatencyMs: null, totalRequests: 100L) };

        Assert.False(EventIngestProcessor.TryComputeSessionApiLatency(events, out _, out _));
    }

    [Fact]
    public void SnapshotWithLatencyButMissingRequests_DoesNotMatch()
    {
        var events = new List<EnrollmentEvent> { Snapshot(51_300L, totalRequests: null) };

        Assert.False(EventIngestProcessor.TryComputeSessionApiLatency(events, out _, out _));
    }

    [Fact]
    public void ZeroRequests_DoesNotMatch()
    {
        // No requests yet (first snapshot before the first upload completes) — no division.
        var events = new List<EnrollmentEvent> { Snapshot(0L, 0L) };

        Assert.False(EventIngestProcessor.TryComputeSessionApiLatency(events, out _, out _));
    }

    [Fact]
    public void MatchingLastSnapshot_SkipsOlderAgentSnapshotAfterIt()
    {
        // A field-less (old-schema) snapshot later in the batch must not shadow an earlier
        // one that carries the counters — the predicate selects the last MATCHING snapshot.
        var events = new List<EnrollmentEvent>
        {
            Snapshot(51_300L, 100L),
            Snapshot(totalLatencyMs: null, totalRequests: 150L),
        };

        Assert.True(EventIngestProcessor.TryComputeSessionApiLatency(events, out var avg, out var count));
        Assert.Equal(513.0, avg);
        Assert.Equal(100, count);
    }

    [Fact]
    public void BatchWithoutSnapshots_DoesNotMatch()
    {
        var events = new List<EnrollmentEvent>
        {
            new EnrollmentEvent { EventType = "esp_phase_changed" },
            new EnrollmentEvent { EventType = "app_install_started" },
        };

        Assert.False(EventIngestProcessor.TryComputeSessionApiLatency(events, out _, out _));
    }

    // -------- TryGetDouble coercion ladder (mirrors PlatformMetricsService.GetDouble) --------

    [Theory]
    [InlineData(513.5)]         // double — decimal JSON number
    [InlineData(513)]           // int — in-memory constructed events (tests, V1 paths)
    [InlineData(513L)]          // long — Newtonsoft wire form for integer JSON numbers
    [InlineData(513f)]          // float
    public void TryGetDouble_CoercesBoxedNumericPrimitives(object boxed)
    {
        var data = new Dictionary<string, object> { ["k"] = boxed };

        Assert.True(EventIngestProcessor.TryGetDouble(data, "k", out var value));
        Assert.Equal(Convert.ToDouble(boxed), value);
    }

    [Fact]
    public void TryGetDouble_ParsesStrings_InvariantCulture()
    {
        var data = new Dictionary<string, object> { ["k"] = "513.5" };

        Assert.True(EventIngestProcessor.TryGetDouble(data, "k", out var value));
        Assert.Equal(513.5, value);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void TryGetDouble_RejectsUnparseableStrings(string raw)
    {
        var data = new Dictionary<string, object> { ["k"] = raw };

        Assert.False(EventIngestProcessor.TryGetDouble(data, "k", out _));
    }

    [Fact]
    public void TryGetDouble_MissingKey_ReturnsFalse()
    {
        Assert.False(EventIngestProcessor.TryGetDouble(new Dictionary<string, object>(), "k", out _));
    }
}

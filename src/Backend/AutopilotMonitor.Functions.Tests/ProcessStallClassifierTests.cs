using System;
using AutopilotMonitor.Functions.Telemetry;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The stall classifier turns two runtime snapshots around a late timer tick into a cause.
/// GC wins when its pause clock covers at least half the drift; queued work or thread injection
/// means starvation; otherwise the drift stays unexplained rather than guessed.
/// </summary>
public class ProcessStallClassifierTests
{
    private static ProcessSnapshot Snapshot(
        double gcPauseMs = 0, int gen2 = 0, int threads = 8, long pending = 0, long completed = 0)
        => new(
            GcPause: TimeSpan.FromMilliseconds(gcPauseMs),
            Gen0: 0, Gen1: 0, Gen2: gen2,
            ThreadPoolThreads: threads,
            PendingWorkItems: pending,
            CompletedWorkItems: completed,
            WorkingSetBytes: 0, HeapSizeBytes: 0, CommittedBytes: 0,
            LastGcGeneration: 0, LastGcPause: TimeSpan.Zero, LastGcCompacted: false);

    [Fact]
    public void Gc_pause_covering_half_the_drift_is_a_gc_stall()
    {
        var before = Snapshot(gcPauseMs: 1000, gen2: 3);
        var after = Snapshot(gcPauseMs: 6200, gen2: 4, pending: 12);

        var report = ProcessStallClassifier.Classify(TimeSpan.FromSeconds(10), before, after);

        Assert.Equal(StallCause.Gc, report.Cause);
        Assert.Equal(TimeSpan.FromMilliseconds(5200), report.GcPauseDelta);
        Assert.Equal(1, report.Gen2Delta);
    }

    [Fact]
    public void Queued_work_without_gc_is_thread_pool_starvation()
    {
        var before = Snapshot(gcPauseMs: 1000);
        var after = Snapshot(gcPauseMs: 1100, pending: 40);

        var report = ProcessStallClassifier.Classify(TimeSpan.FromSeconds(8), before, after);

        Assert.Equal(StallCause.ThreadPool, report.Cause);
    }

    [Fact]
    public void Thread_injection_without_gc_is_thread_pool_starvation()
    {
        var before = Snapshot(threads: 8);
        var after = Snapshot(threads: 11);

        var report = ProcessStallClassifier.Classify(TimeSpan.FromSeconds(3), before, after);

        Assert.Equal(StallCause.ThreadPool, report.Cause);
        Assert.Equal(3, report.ThreadPoolThreadsDelta);
    }

    [Fact]
    public void Drift_neither_clock_explains_stays_unknown()
    {
        var before = Snapshot(gcPauseMs: 500, threads: 8, completed: 100);
        var after = Snapshot(gcPauseMs: 900, threads: 8, completed: 130);

        var report = ProcessStallClassifier.Classify(TimeSpan.FromSeconds(5), before, after);

        Assert.Equal(StallCause.Unknown, report.Cause);
        Assert.Equal(30, report.CompletedWorkItemsDelta);
    }

    [Fact]
    public void A_wrapped_pause_clock_never_yields_a_negative_delta()
    {
        var before = Snapshot(gcPauseMs: 900);
        var after = Snapshot(gcPauseMs: 100);

        var report = ProcessStallClassifier.Classify(TimeSpan.FromSeconds(2), before, after);

        Assert.Equal(TimeSpan.Zero, report.GcPauseDelta);
        Assert.Equal(StallCause.Unknown, report.Cause);
    }
}

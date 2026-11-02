using System;
using AutopilotMonitor.Functions.Telemetry;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The memory metric is a percentage of the limits the GC works against, so a reading keeps its
/// meaning when the instance size changes, and an alert can name one number for every size.
/// </summary>
public class ProcessMemoryPressureTests
{
    [Fact]
    public void Heap_is_committed_memory_against_the_heap_limit()
    {
        var snapshot = default(ProcessSnapshot) with { CommittedBytes = 1305, TotalAvailableMemoryBytes = 1740 };

        Assert.Equal(75.0, ProcessMemoryPressure.HeapPercent(snapshot));
    }

    [Fact]
    public void Load_is_memory_load_against_the_high_load_threshold()
    {
        var snapshot = default(ProcessSnapshot) with { MemoryLoadBytes = 2088, HighMemoryLoadThresholdBytes = 2088 };

        Assert.Equal(100.0, ProcessMemoryPressure.LoadPercent(snapshot));
    }

    [Fact]
    public void A_value_past_the_limit_is_not_capped()
    {
        var snapshot = default(ProcessSnapshot) with { MemoryLoadBytes = 2320, HighMemoryLoadThresholdBytes = 2088 };

        Assert.Equal(111.1, ProcessMemoryPressure.LoadPercent(snapshot));
    }

    [Fact]
    public void Before_the_first_gc_there_is_no_reading()
    {
        var snapshot = default(ProcessSnapshot) with { WorkingSetBytes = 500 };

        Assert.Null(ProcessMemoryPressure.HeapPercent(snapshot));
        Assert.Null(ProcessMemoryPressure.LoadPercent(snapshot));
    }

    [Fact]
    public void A_captured_snapshot_after_a_gc_has_both_readings()
    {
        GC.Collect();

        var snapshot = ProcessSnapshot.Capture();

        Assert.True(ProcessMemoryPressure.HeapPercent(snapshot) > 0);
        Assert.True(ProcessMemoryPressure.LoadPercent(snapshot) > 0);
    }
}

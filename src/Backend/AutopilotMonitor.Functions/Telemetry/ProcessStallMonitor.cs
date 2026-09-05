using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Telemetry;

/// <summary>
/// Point-in-time reading of the runtime counters a process-wide pause leaves traces in. Two
/// consecutive snapshots around a late timer tick say WHAT paused the process: the GC pause
/// clock (<see cref="GC.GetTotalPauseDuration"/>, exact) or the thread pool (queued work,
/// thread injection).
/// </summary>
public readonly record struct ProcessSnapshot(
    TimeSpan GcPause,
    int Gen0,
    int Gen1,
    int Gen2,
    int ThreadPoolThreads,
    long PendingWorkItems,
    long CompletedWorkItems,
    long WorkingSetBytes,
    long HeapSizeBytes,
    long CommittedBytes,
    int LastGcGeneration,
    TimeSpan LastGcPause,
    bool LastGcCompacted)
{
    public static ProcessSnapshot Capture()
    {
        var info = GC.GetGCMemoryInfo();
        var lastPause = TimeSpan.Zero;
        foreach (var pause in info.PauseDurations)
            lastPause += pause;

        return new ProcessSnapshot(
            GcPause: GC.GetTotalPauseDuration(),
            Gen0: GC.CollectionCount(0),
            Gen1: GC.CollectionCount(1),
            Gen2: GC.CollectionCount(2),
            ThreadPoolThreads: ThreadPool.ThreadCount,
            PendingWorkItems: ThreadPool.PendingWorkItemCount,
            CompletedWorkItems: ThreadPool.CompletedWorkItemCount,
            WorkingSetBytes: Environment.WorkingSet,
            HeapSizeBytes: info.HeapSizeBytes,
            CommittedBytes: info.TotalCommittedBytes,
            LastGcGeneration: info.Generation,
            LastGcPause: lastPause,
            LastGcCompacted: info.Compacted);
    }
}

/// <summary>What a stall report says caused the pause.</summary>
public static class StallCause
{
    /// <summary>At least half of the drift was GC pause time.</summary>
    public const string Gc = "gc";

    /// <summary>Work was queued or the pool injected threads: starvation, not GC.</summary>
    public const string ThreadPool = "threadpool";

    /// <summary>Neither clock explains the drift (CPU steal, host pause, page-in).</summary>
    public const string Unknown = "unknown";
}

/// <summary>One classified stall: the drift plus the counter deltas that explain it.</summary>
public sealed record StallReport(
    TimeSpan Drift,
    string Cause,
    TimeSpan GcPauseDelta,
    int Gen0Delta,
    int Gen1Delta,
    int Gen2Delta,
    int ThreadPoolThreadsDelta,
    long CompletedWorkItemsDelta,
    ProcessSnapshot After);

/// <summary>
/// Pure classification so the rule is unit-testable without a live process pause.
/// </summary>
public static class ProcessStallClassifier
{
    /// <summary>Fraction of the drift the GC pause clock must cover to blame the GC.</summary>
    internal const double GcShareThreshold = 0.5;

    public static StallReport Classify(TimeSpan drift, ProcessSnapshot before, ProcessSnapshot after)
    {
        var gcPauseDelta = after.GcPause - before.GcPause;
        if (gcPauseDelta < TimeSpan.Zero) gcPauseDelta = TimeSpan.Zero;

        var threadsDelta = after.ThreadPoolThreads - before.ThreadPoolThreads;

        string cause;
        if (drift > TimeSpan.Zero && gcPauseDelta.Ticks >= drift.Ticks * GcShareThreshold)
            cause = StallCause.Gc;
        else if (after.PendingWorkItems > 0 || threadsDelta > 0)
            cause = StallCause.ThreadPool;
        else
            cause = StallCause.Unknown;

        return new StallReport(
            Drift: drift,
            Cause: cause,
            GcPauseDelta: gcPauseDelta,
            Gen0Delta: after.Gen0 - before.Gen0,
            Gen1Delta: after.Gen1 - before.Gen1,
            Gen2Delta: after.Gen2 - before.Gen2,
            ThreadPoolThreadsDelta: threadsDelta,
            CompletedWorkItemsDelta: after.CompletedWorkItems - before.CompletedWorkItems,
            After: after);
    }
}

/// <summary>
/// Detects process-wide pauses in the worker and reports what caused them. Production showed
/// episodes where every in-flight request on ONE instance finished at the same instant after
/// 5–10 s, with no slow storage call, no warning, no cold start and idle CPU (2026-09-03 12:15
/// and 19:00) — a pause of the whole process, invisible to per-minute performance counters.
/// <para>
/// Mechanism: a 1 s timer tick measures its own lateness. A tick that arrives
/// <see cref="StallThreshold"/> or more late means the process did not run continuations for
/// that long. Two <see cref="ProcessSnapshot"/>s around the tick attribute the drift: the GC
/// pause clock is exact, so a drift the GC covers is a GC pause; queued work or thread
/// injection means thread-pool starvation (the tick's own continuation waits in that queue);
/// anything else is <see cref="StallCause.Unknown"/>. Cost: one delay per second per instance.
/// </para>
/// <para>
/// Output: <c>customEvents</c> <see cref="EventName"/> with the deltas (sampling bypassed —
/// the events are rare and every one is wanted) and the pre-aggregated metric
/// <see cref="MetricName"/> (value = drift ms, dimension <c>cause</c>) for the trend. Pairs with
/// the <c>GcPauseMs</c> request dimension stamped by RequestTelemetryMiddleware, which says how
/// much of one slow request was GC pause. Query:
/// <c>customEvents | where name == 'ProcessStall' | project timestamp, cloud_RoleInstance, customDimensions</c>.
/// </para>
/// </summary>
public sealed class ProcessStallMonitor : BackgroundService
{
    public const string EventName = "ProcessStall";
    public const string MetricName = "ProcessStall";

    internal static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    /// <summary>Lateness of one tick that counts as a stall.</summary>
    internal static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(1);

    private readonly TelemetryClient? _telemetry;
    private readonly Metric? _metric;
    private readonly BackendBuildInfo _buildInfo;
    private readonly ILogger<ProcessStallMonitor> _logger;

    public ProcessStallMonitor(BackendBuildInfo buildInfo, ILogger<ProcessStallMonitor> logger, TelemetryClient? telemetry = null)
    {
        _buildInfo = buildInfo;
        _logger = logger;
        _telemetry = telemetry;
        _metric = telemetry?.GetMetric(MetricName, "cause");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var previous = ProcessSnapshot.Capture();
        var stopwatch = new Stopwatch();

        while (!stoppingToken.IsCancellationRequested)
        {
            stopwatch.Restart();
            try
            {
                await Task.Delay(Tick, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var drift = stopwatch.Elapsed - Tick;
            var current = ProcessSnapshot.Capture();

            if (drift >= StallThreshold)
            {
                try
                {
                    Report(ProcessStallClassifier.Classify(drift, previous, current));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ProcessStallMonitor: failed to report a stall");
                }
            }

            previous = current;
        }
    }

    private void Report(StallReport report)
    {
        var driftMs = Math.Round(report.Drift.TotalMilliseconds);
        _metric?.TrackValue(driftMs, report.Cause);

        if (_telemetry == null) return;

        var after = report.After;
        var evt = new EventTelemetry(EventName);
        var props = evt.Properties;
        props["driftMs"] = Format(driftMs);
        props["cause"] = report.Cause;
        props["gcPauseMs"] = Format(Math.Round(report.GcPauseDelta.TotalMilliseconds));
        props["gen0"] = Format(report.Gen0Delta);
        props["gen1"] = Format(report.Gen1Delta);
        props["gen2"] = Format(report.Gen2Delta);
        props["lastGcGeneration"] = Format(after.LastGcGeneration);
        props["lastGcPauseMs"] = Format(Math.Round(after.LastGcPause.TotalMilliseconds));
        props["lastGcCompacted"] = after.LastGcCompacted ? "true" : "false";
        props["heapMb"] = Format(after.HeapSizeBytes >> 20);
        props["committedMb"] = Format(after.CommittedBytes >> 20);
        props["workingSetMb"] = Format(after.WorkingSetBytes >> 20);
        props["threadPoolThreads"] = Format(after.ThreadPoolThreads);
        props["threadPoolThreadsDelta"] = Format(report.ThreadPoolThreadsDelta);
        props["pendingWorkItems"] = Format(after.PendingWorkItems);
        props["completedWorkItemsDelta"] = Format(report.CompletedWorkItemsDelta);
        props["instance"] = Environment.MachineName;
        props["version"] = _buildInfo.Version;

        // Rare by construction, and every one is evidence: opt out of the worker's adaptive
        // sampling the same way the canonical request record does.
        ((ISupportSampling)evt).SamplingPercentage = 100;
        _telemetry.TrackEvent(evt);
    }

    private static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);
}

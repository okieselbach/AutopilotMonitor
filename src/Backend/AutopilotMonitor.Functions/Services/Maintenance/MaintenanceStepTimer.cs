using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace AutopilotMonitor.Functions.Services.Maintenance
{
    /// <summary>
    /// Per-step wall-clock and memory of one maintenance run, reported on the
    /// <c>MaintenanceCompleted</c> ops event. Worker-side timing logs never reach App Insights,
    /// and the run's composition is what decides which steps leave the 2h timer next.
    /// A step that throws is still recorded (its time counts), then the exception propagates.
    /// </summary>
    public sealed class MaintenanceStepTimer
    {
        private readonly Dictionary<string, long> _steps = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _peakMb = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _allocMb = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Func<MaintenanceMemoryReading> _readMemory;

        public MaintenanceStepTimer() : this(MaintenanceMemoryReading.Capture)
        {
        }

        internal MaintenanceStepTimer(Func<MaintenanceMemoryReading> readMemory)
        {
            _readMemory = readMemory;
        }

        /// <summary>Durations in declaration order (ms, rounded).</summary>
        public IReadOnlyDictionary<string, long> Steps => _steps;

        public MaintenanceMemoryReport MemoryReport()
        {
            var reading = _readMemory();
            return new MaintenanceMemoryReport(_peakMb, _allocMb, reading.GcHeapLimitBytes >> 20, reading.GcHighLoadBytes >> 20);
        }

        public async Task RunAsync(string step, Func<Task> body)
        {
            var before = _readMemory();
            var sw = Stopwatch.StartNew();
            try
            {
                await body().ConfigureAwait(false);
            }
            finally
            {
                Record(step, sw, before);
            }
        }

        public async Task<T> RunAsync<T>(string step, Func<Task<T>> body)
        {
            var before = _readMemory();
            var sw = Stopwatch.StartNew();
            try
            {
                return await body().ConfigureAwait(false);
            }
            finally
            {
                Record(step, sw, before);
            }
        }

        private void Record(string step, Stopwatch sw, MaintenanceMemoryReading before)
        {
            _steps[step] = sw.ElapsedMilliseconds;

            var after = _readMemory();
            _peakMb[step] = after.PeakWorkingSetBytes >> 20;
            _allocMb[step] = (after.AllocatedBytes - before.AllocatedBytes) >> 20;
        }
    }

    /// <summary>
    /// <see cref="StepsPeakMb"/> is the process's peak working set after each step: the step where
    /// it jumps set the peak, without depending on when a counter happened to sample.
    /// <see cref="StepsAllocMb"/> is what the process allocated during each step.
    /// </summary>
    public sealed record MaintenanceMemoryReport(
        IReadOnlyDictionary<string, long> StepsPeakMb,
        IReadOnlyDictionary<string, long> StepsAllocMb,
        long GcHeapLimitMb,
        long GcHighLoadMb);

    internal readonly record struct MaintenanceMemoryReading(
        long PeakWorkingSetBytes,
        long AllocatedBytes,
        long GcHeapLimitBytes,
        long GcHighLoadBytes)
    {
        internal static MaintenanceMemoryReading Capture()
        {
            var info = GC.GetGCMemoryInfo();
            return new MaintenanceMemoryReading(
                ReadPeakWorkingSet(),
                GC.GetTotalAllocatedBytes(precise: false),
                info.TotalAvailableMemoryBytes,
                info.HighMemoryLoadThresholdBytes);
        }

        // A maintenance run must not fail on its own telemetry.
        private static long ReadPeakWorkingSet()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return process.PeakWorkingSet64;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}

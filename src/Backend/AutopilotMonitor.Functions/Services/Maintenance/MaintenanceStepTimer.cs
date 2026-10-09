using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace AutopilotMonitor.Functions.Services.Maintenance
{
    /// <summary>
    /// Per-step wall-clock of one maintenance run, reported as <c>stepsMs</c> on the
    /// <c>MaintenanceCompleted</c> ops event. Worker-side timing logs never reach App Insights,
    /// and the run's composition is what decides which steps leave the 2h timer next.
    /// A step that throws is still recorded (its time counts), then the exception propagates.
    /// </summary>
    public sealed class MaintenanceStepTimer
    {
        private readonly Dictionary<string, long> _steps = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Durations in declaration order (ms, rounded).</summary>
        public IReadOnlyDictionary<string, long> Steps => _steps;

        public async Task RunAsync(string step, Func<Task> body)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await body().ConfigureAwait(false);
            }
            finally
            {
                _steps[step] = sw.ElapsedMilliseconds;
            }
        }

        public async Task<T> RunAsync<T>(string step, Func<Task<T>> body)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                return await body().ConfigureAwait(false);
            }
            finally
            {
                _steps[step] = sw.ElapsedMilliseconds;
            }
        }
    }
}

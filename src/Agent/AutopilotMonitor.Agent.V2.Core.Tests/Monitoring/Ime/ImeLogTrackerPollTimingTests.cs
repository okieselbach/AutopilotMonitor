using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// The poll loop's timing maxima (session c3ecb568: the loop was not scheduled for 26 s at
    /// 100 % VM CPU and nothing in the telemetry could show it): the longest pass and the
    /// longest pause between two passes, on a monotonic clock, restart-safe like the other
    /// health counters.
    /// </summary>
    public sealed class ImeLogTrackerPollTimingTests
    {
        /// <summary>Monotonic readings the tracker takes at the start and the end of every pass.</summary>
        private sealed class Clock
        {
            private readonly Queue<long> _readings = new Queue<long>();
            public void Enqueue(params long[] ms) { foreach (var m in ms) _readings.Enqueue(m); }
            public long Next() => _readings.Dequeue();
        }

        private static ImeLogTracker Build(TempDirectory tmp, Clock clock)
        {
            var tracker = new ImeLogTracker(tmp.Path, new List<ImeLogPattern>(), new AgentLogger(tmp.Path, AgentLogLevel.Info), stateDirectory: tmp.Path);
            tracker.MonotonicMillisProvider = clock.Next;
            return tracker;
        }

        [Fact]
        public async Task Longest_pass_and_longest_pause_are_kept_as_session_maxima()
        {
            using var tmp = new TempDirectory();
            var clock = new Clock();
            var tracker = Build(tmp, clock);

            // Pass 1 takes 30 ms; the loop is not scheduled again for 26.1 s; pass 2 takes 10 ms,
            // pass 3 follows after the normal 100 ms.
            clock.Enqueue(0, 30, 26130, 26140, 26240, 26245);
            await tracker.RunPollPassAsync(CancellationToken.None);
            await tracker.RunPollPassAsync(CancellationToken.None);
            await tracker.RunPollPassAsync(CancellationToken.None);

            var health = tracker.GetHealthSnapshot();
            Assert.Equal(30, health.PassMaxMs);
            Assert.Equal(26100, health.PassGapMaxMs);
            tracker.Dispose();
        }

        [Fact]
        public void A_fresh_tracker_reports_zero()
        {
            using var tmp = new TempDirectory();
            var tracker = Build(tmp, new Clock());

            var health = tracker.GetHealthSnapshot();
            Assert.Equal(0, health.PassMaxMs);
            Assert.Equal(0, health.PassGapMaxMs);
            tracker.Dispose();
        }

        [Fact]
        public async Task Maxima_survive_a_restart_and_are_never_lowered()
        {
            using var tmp = new TempDirectory();
            var clock = new Clock();
            var tracker1 = Build(tmp, clock);
            clock.Enqueue(0, 40, 5140, 5150);
            await tracker1.RunPollPassAsync(CancellationToken.None);
            await tracker1.RunPollPassAsync(CancellationToken.None);
            tracker1.SaveStateForTest();
            tracker1.Dispose();

            var tracker2 = Build(tmp, clock);
            tracker2.LoadStateForTest();
            Assert.Equal(40, tracker2.GetHealthSnapshot().PassMaxMs);
            Assert.Equal(5100, tracker2.GetHealthSnapshot().PassGapMaxMs);

            // The first pass after a restart has no pause to measure; a calmer session afterwards
            // never lowers what the session already saw.
            clock.Enqueue(6000, 6010, 6110, 6115);
            await tracker2.RunPollPassAsync(CancellationToken.None);
            await tracker2.RunPollPassAsync(CancellationToken.None);
            var health = tracker2.GetHealthSnapshot();
            Assert.Equal(40, health.PassMaxMs);
            Assert.Equal(5100, health.PassGapMaxMs);
            tracker2.Dispose();
        }
    }
}

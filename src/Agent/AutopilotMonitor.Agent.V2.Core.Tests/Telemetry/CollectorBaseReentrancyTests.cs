using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Telemetry
{
    /// <summary>
    /// Regression coverage for the duplicate <c>performance_snapshot</c> events observed on
    /// session c4c8d206-… (Bayer / Lenovo T14 Gen 5): two snapshots fired with timestamps
    /// 0–1ms apart, the second one carrying a fresh-baseline net delta of 0. Root cause:
    /// <see cref="System.Threading.Timer"/> is documented to dispatch callbacks on different
    /// ThreadPool threads when a callback exceeds the period, and the
    /// <c>PerformanceCollector</c>'s <c>Collect()</c> reads shared baseline fields without
    /// any coordination. After the long stall the OS network re-init slowed
    /// <c>NetworkInterface.GetAllNetworkInterfaces()</c> enough to overlap the next 30s tick
    /// → two parallel <c>Collect()</c> calls → race.
    /// <para>
    /// The fix lives in <c>CollectorBase.CollectSafe</c>: any tick that arrives while a
    /// previous <c>Collect()</c> is still running is dropped via an
    /// <see cref="Interlocked.CompareExchange(ref int, int, int)"/> gate. These tests pin the
    /// gate behaviour so a future "let's just remove the guard, it never fires" refactor
    /// has a hard signal.
    /// </para>
    /// <para>
    /// Threading-sensitive: the test exercises the <see cref="Interlocked"/> gate via
    /// real Tasks + SpinUntil. Under the parallel xUnit pool the ThreadPool gets
    /// contended by unrelated tests and the SpinUntil times out before the gate
    /// closes. Serialised against the other threading-sensitive classes via
    /// SerialThreading.
    /// </para>
    /// </summary>
    [Collection("SerialThreading")]
    public sealed class CollectorBaseReentrancyTests
    {
        private static AgentLogger NewLogger(TempDirectory tmp)
            => new AgentLogger(Path.Combine(tmp.Path, "logs"), AgentLogLevel.Debug);

        private static InformationalEventPost NewPost()
            => new InformationalEventPost(
                new FakeSignalIngressSink(),
                new VirtualClock(new DateTime(2026, 4, 29, 17, 16, 0, DateTimeKind.Utc)));

        [Fact]
        public void CollectSafe_runs_collect_when_no_previous_call_is_in_flight()
        {
            using var tmp = new TempDirectory();
            var collector = new BlockingCollector(NewPost(), NewLogger(tmp));

            collector.ReleaseImmediately = true;
            collector.CollectSafe();

            Assert.Equal(1, collector.CollectInvocationCount);
        }

        [Fact]
        public async Task CollectSafe_skips_when_previous_collect_is_still_running()
        {
            using var tmp = new TempDirectory();
            var collector = new BlockingCollector(NewPost(), NewLogger(tmp));

            // First call enters Collect() and parks on the gate, simulating a slow tick.
            var firstTick = Task.Run(() => collector.CollectSafe());
            Assert.True(collector.WaitUntilInsideCollect(TimeSpan.FromSeconds(2)),
                "First Collect did not enter within the timeout.");

            // Second tick fires while the first is still parked — must be dropped.
            collector.CollectSafe();

            // Release the first one and wait for it to finish.
            collector.Release();
            await firstTick;

            Assert.Equal(1, collector.CollectInvocationCount);
        }

        [Fact]
        public void CollectSafe_resets_gate_after_collect_throws()
        {
            using var tmp = new TempDirectory();
            var collector = new BlockingCollector(NewPost(), NewLogger(tmp));

            // First tick throws — base must catch and reset the gate so the next tick runs.
            collector.ThrowOnNextCollect = true;
            collector.ReleaseImmediately = true;
            collector.CollectSafe();
            Assert.Equal(1, collector.CollectInvocationCount);

            // Second tick must execute; the gate is no longer locked.
            collector.CollectSafe();
            Assert.Equal(2, collector.CollectInvocationCount);
        }

        [Fact]
        public async Task Concurrent_CollectSafe_calls_serialize_through_the_gate()
        {
            // Stress the Interlocked gate: many parallel ticks against a slow Collect must
            // produce exactly one Collect invocation while the slow one is in flight.
            using var tmp = new TempDirectory();
            var collector = new BlockingCollector(NewPost(), NewLogger(tmp));

            var firstTick = Task.Run(() => collector.CollectSafe());
            Assert.True(collector.WaitUntilInsideCollect(TimeSpan.FromSeconds(2)),
                "First Collect did not enter within the timeout.");

            // 16 concurrent racing ticks — none of them should manage to enter Collect.
            var racers = new Task[16];
            for (int i = 0; i < racers.Length; i++)
                racers[i] = Task.Run(() => collector.CollectSafe());
            await Task.WhenAll(racers);

            collector.Release();
            await firstTick;

            Assert.Equal(1, collector.CollectInvocationCount);

            // After the slow one finished the gate is open again — next tick runs normally.
            collector.ReleaseImmediately = true;
            collector.CollectSafe();
            Assert.Equal(2, collector.CollectInvocationCount);
        }

        [Fact]
        public void Stop_then_Dispose_logs_Stopped_once()
        {
            // Hosts call Stop and then Dispose, which stops again; the log used to show
            // "[X] Stopped" twice in the same millisecond.
            using var tmp = new TempDirectory();
            var collector = new BlockingCollector(NewPost(), NewLogger(tmp));

            collector.Start();
            collector.Stop();
            collector.Dispose();

            var log = File.ReadAllText(Path.Combine(tmp.Path, "logs", "agent.log"));
            Assert.Equal(1, CountOccurrences(log, "[BlockingCollector] Stopped"));
        }

        [Fact]
        public void Dispose_without_Start_logs_no_Stopped_line()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp);
            var collector = new BlockingCollector(NewPost(), logger);
            logger.Info("marker"); // make sure the log file exists

            collector.Dispose();

            var log = File.ReadAllText(Path.Combine(tmp.Path, "logs", "agent.log"));
            Assert.DoesNotContain("[BlockingCollector] Stopped", log);
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0, index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }
            return count;
        }

        /// <summary>
        /// Test-only collector subclass: signals when <see cref="Collect"/> is entered and
        /// blocks until the test releases it, so we can exercise the reentrancy gate
        /// deterministically without relying on real timer scheduling.
        /// </summary>
        private sealed class BlockingCollector : CollectorBase
        {
            private readonly ManualResetEventSlim _insideCollect = new ManualResetEventSlim(false);
            private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
            private int _collectInvocationCount;

            public BlockingCollector(InformationalEventPost post, AgentLogger logger)
                : base(
                      sessionId: Guid.NewGuid().ToString(),
                      tenantId: Guid.NewGuid().ToString(),
                      post: post,
                      logger: logger,
                      intervalSeconds: 30)
            {
            }

            public int CollectInvocationCount => Volatile.Read(ref _collectInvocationCount);

            /// <summary>When <c>true</c>, <see cref="Collect"/> returns immediately.</summary>
            public bool ReleaseImmediately { get; set; }

            /// <summary>One-shot: next <see cref="Collect"/> call throws.</summary>
            public bool ThrowOnNextCollect { get; set; }

            public bool WaitUntilInsideCollect(TimeSpan timeout) => _insideCollect.Wait(timeout);

            public void Release() => _release.Set();

            protected override void Collect()
            {
                Interlocked.Increment(ref _collectInvocationCount);
                _insideCollect.Set();

                if (ThrowOnNextCollect)
                {
                    ThrowOnNextCollect = false;
                    throw new InvalidOperationException("simulated collect failure");
                }

                if (ReleaseImmediately) return;

                _release.Wait(TimeSpan.FromSeconds(5));
                _release.Reset();
                _insideCollect.Reset();
            }
        }
    }
}

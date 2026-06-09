#nullable enable
using System;
using System.IO;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Office
{
    /// <summary>
    /// OfficeBinaryWatcher — the completion-proof watcher. Regression cover for field session c2171821,
    /// where a permanent one-shot latch silenced the watcher after a single early raise that probed
    /// not-yet, so the later genuine completion was missed. Asserts: the watcher re-raises (NOT one-shot),
    /// the bounded defensive re-probe (<see cref="OfficeBinaryWatcher.ScheduleRecheck"/>) re-raises on a
    /// cadence and stops at its bound, is idempotent, the initial scan raises when binaries already
    /// exist, a filesystem create raises, and Dispose cancels the re-probe.
    /// </summary>
    public sealed class OfficeBinaryWatcherTests
    {
        private static readonly string[] CoreBinaries = { "WINWORD.EXE", "EXCEL.EXE", "POWERPNT.EXE", "OUTLOOK.EXE" };

        private static AgentLogger NewLogger(string dir) => new AgentLogger(dir, AgentLogLevel.Info);

        // Lay down {installPath}\root\Office16\EXCEL.EXE — the layout the on-disk probe enumerates.
        private static void LayDownCoreBinary(string installPath)
        {
            var versionDir = Path.Combine(installPath, "root", "Office16");
            Directory.CreateDirectory(versionDir);
            File.WriteAllText(Path.Combine(versionDir, "EXCEL.EXE"), "stub");
        }

        [Fact]
        public void ScheduleRecheck_re_raises_repeatedly_proving_not_one_shot()
        {
            using var tmp = new TempDirectory();
            using var watcher = new OfficeBinaryWatcher(tmp.Path, CoreBinaries, NewLogger(tmp.Path),
                recheckIntervalMs: 30, maxRechecks: 5);

            int raises = 0;
            watcher.BinaryAppeared += (_, __) => Interlocked.Increment(ref raises);

            watcher.Start();           // dir exists, no binaries → arms the FS watcher, no raise
            Assert.Equal(0, Volatile.Read(ref raises));

            watcher.ScheduleRecheck(); // binaries still absent → the re-probe keeps re-raising up to max
            SpinUntil(() => Volatile.Read(ref raises) >= 2, 2000);

            Assert.True(Volatile.Read(ref raises) >= 2,
                $"expected the re-probe to re-raise (not one-shot); got {raises}");
        }

        [Fact]
        public void ScheduleRecheck_is_bounded_and_stops_after_max_attempts()
        {
            using var tmp = new TempDirectory();
            using var watcher = new OfficeBinaryWatcher(tmp.Path, CoreBinaries, NewLogger(tmp.Path),
                recheckIntervalMs: 25, maxRechecks: 3);

            int raises = 0;
            watcher.BinaryAppeared += (_, __) => Interlocked.Increment(ref raises);

            watcher.Start();
            watcher.ScheduleRecheck();

            // Give it well beyond max*interval, then assert it has stopped and never exceeded the bound.
            Thread.Sleep(400);
            int settled = Volatile.Read(ref raises);
            Thread.Sleep(200);

            Assert.Equal(settled, Volatile.Read(ref raises)); // no further raises — bounded
            Assert.True(settled <= 3, $"re-probe exceeded its bound: {settled}");
            Assert.True(settled >= 1, "re-probe never fired");
        }

        [Fact]
        public void ScheduleRecheck_is_idempotent_single_timer()
        {
            using var tmp = new TempDirectory();
            using var watcher = new OfficeBinaryWatcher(tmp.Path, CoreBinaries, NewLogger(tmp.Path),
                recheckIntervalMs: 30, maxRechecks: 2);

            int raises = 0;
            watcher.BinaryAppeared += (_, __) => Interlocked.Increment(ref raises);

            watcher.Start();
            watcher.ScheduleRecheck();
            watcher.ScheduleRecheck(); // a second arm must NOT add a second timer
            watcher.ScheduleRecheck();

            Thread.Sleep(400);
            // One timer bounded at 2 attempts → at most 2 raises. A leaked second timer would double it.
            Assert.True(Volatile.Read(ref raises) <= 2, $"expected a single timer (<=2 raises); got {raises}");
        }

        [Fact]
        public void Initial_scan_raises_when_binaries_already_present()
        {
            using var tmp = new TempDirectory();
            LayDownCoreBinary(tmp.Path); // Office already on disk (update-over-existing / armed late)
            using var watcher = new OfficeBinaryWatcher(tmp.Path, CoreBinaries, NewLogger(tmp.Path));

            int raises = 0;
            watcher.BinaryAppeared += (_, __) => Interlocked.Increment(ref raises);

            watcher.Start(); // synchronous initial scan finds the binary

            Assert.True(Volatile.Read(ref raises) >= 1, "initial scan did not raise for a present binary");
        }

        [Fact]
        public void Filesystem_create_raises()
        {
            using var tmp = new TempDirectory();
            using var watcher = new OfficeBinaryWatcher(tmp.Path, CoreBinaries, NewLogger(tmp.Path));

            using var raised = new ManualResetEventSlim(false);
            watcher.BinaryAppeared += (_, __) => raised.Set();

            watcher.Start();          // arms the FS watcher on the (empty) install tree
            LayDownCoreBinary(tmp.Path); // C2R lays a core binary down → FS create event

            Assert.True(raised.Wait(TimeSpan.FromSeconds(5)),
                "filesystem create of a core binary did not raise BinaryAppeared");
        }

        [Fact]
        public void Dispose_cancels_the_recheck_timer()
        {
            using var tmp = new TempDirectory();
            var watcher = new OfficeBinaryWatcher(tmp.Path, CoreBinaries, NewLogger(tmp.Path),
                recheckIntervalMs: 30, maxRechecks: 20);

            int raises = 0;
            watcher.BinaryAppeared += (_, __) => Interlocked.Increment(ref raises);

            watcher.Start();
            watcher.ScheduleRecheck();
            watcher.Dispose(); // must cancel the bounded re-probe

            int afterDispose = Volatile.Read(ref raises);
            Thread.Sleep(250);

            Assert.Equal(afterDispose, Volatile.Read(ref raises)); // no raises after dispose
        }

        private static void SpinUntil(Func<bool> condition, int timeoutMs)
        {
            var deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                if (condition()) return;
                Thread.Sleep(10);
            }
        }
    }
}

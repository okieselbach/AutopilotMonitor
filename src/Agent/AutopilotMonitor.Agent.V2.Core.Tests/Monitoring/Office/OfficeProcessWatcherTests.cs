#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

#pragma warning disable xUnit1031 // the tests wait on tasks on purpose: a blocked call is the failure they look for

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Office
{
    /// <summary>
    /// <see cref="OfficeProcessWatcher"/> with stand-in worker processes handed in through the
    /// startup probe (the WMI start trace is never armed here, as in the sibling watchers' tests).
    /// Covers the worker window (started / stopped) and the two ways a worker's exit used to go
    /// wrong: disposing the watcher while the exit is being delivered, and a worker that was
    /// already gone when the watcher subscribed.
    /// </summary>
    [Collection("SerialThreading")] // Process.Exited is delivered on the shared ThreadPool
    public sealed class OfficeProcessWatcherTests : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly AgentLogger _logger;
        private readonly List<Process> _spawned = new List<Process>();

        public OfficeProcessWatcherTests()
        {
            _logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
        }

        public void Dispose()
        {
            foreach (var p in _spawned)
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
                try { p.Dispose(); } catch { }
            }
            _tmp.Dispose();
        }

        /// <summary>A stand-in worker that blocks on stdin until killed.</summary>
        private Process Spawn()
        {
            var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            Assert.NotNull(p);
            _spawned.Add(p!);
            return p!;
        }

        /// <summary>A second handle on the same process — what the watcher would have opened itself.</summary>
        private static Process HandleOn(Process child)
        {
            var handle = Process.GetProcessById(child.Id);
            handle.EnableRaisingEvents = true;
            return handle;
        }

        /// <summary>Starts the watcher with <paramref name="handle"/> as the one running worker.</summary>
        private static void StartWith(OfficeProcessWatcher watcher, Process handle)
        {
            watcher.WorkerSource = () => new[] { handle };
            watcher.Start(armWmi: false);
        }

        [Fact]
        public void A_running_worker_is_reported_before_Start_returns_and_its_exit_closes_the_window()
        {
            using var watcher = new OfficeProcessWatcher(_logger, settleSeconds: 0);
            using var started = new ManualResetEventSlim(false);
            using var stopped = new ManualResetEventSlim(false);
            watcher.Started += (_, __) => started.Set();
            watcher.Stopped += (_, __) => stopped.Set();
            var child = Spawn();

            StartWith(watcher, HandleOn(child));

            Assert.True(started.IsSet, "Started was not raised by the startup probe");
            Assert.True(watcher.IsActive);

            child.Kill();

            Assert.True(stopped.Wait(Timeout), "Stopped was not raised after the last worker exited");
            Assert.False(watcher.IsActive);
        }

        [Fact]
        public void Dispose_returns_while_the_exit_of_a_tracked_worker_is_being_delivered()
        {
            var watcher = new OfficeProcessWatcher(_logger, settleSeconds: 0);
            var child = Spawn();
            var handle = HandleOn(child);

            // Subscribed before the watcher, so .NET runs it first — and .NET holds its own lock on
            // the Process object for as long as it delivers Exited.
            using var delivering = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            handle.Exited += (_, __) => { delivering.Set(); release.Wait(Timeout); };
            StartWith(watcher, handle);

            Task dispose;
            try
            {
                child.Kill();
                Assert.True(delivering.Wait(Timeout), "the worker's exit was not delivered");

                dispose = Task.Run(() => watcher.Dispose());
                Thread.Sleep(300); // let Dispose reach the Process object
            }
            finally
            {
                release.Set();
            }

            Assert.True(dispose.Wait(Timeout),
                "Dispose and the exit delivery blocked each other: the Process is disposed under the watcher's lock");
        }

        [Fact]
        public void A_worker_that_was_already_gone_when_the_watcher_subscribed_still_closes_the_window()
        {
            using var watcher = new OfficeProcessWatcher(_logger, settleSeconds: 0);
            using var stopped = new ManualResetEventSlim(false);
            watcher.Stopped += (_, __) => stopped.Set();

            var child = Spawn();
            var handle = HandleOn(child);
            using var exitDelivered = new ManualResetEventSlim(false);
            handle.Exited += (_, __) => exitDelivered.Set();
            child.Kill();
            // .NET raises Exited once per Process object. After this, a later subscriber never hears it.
            Assert.True(exitDelivered.Wait(Timeout), "the stand-in's exit was not delivered");

            StartWith(watcher, handle);

            Assert.True(stopped.Wait(Timeout),
                "the window never closed: the worker exited before the watcher subscribed to Exited");
            Assert.False(watcher.IsActive);
        }

        [Fact]
        public void A_traced_id_that_belongs_to_another_program_is_not_tracked()
        {
            // The start trace arrives late: by then the id can belong to an unrelated process.
            using var watcher = new OfficeProcessWatcher(_logger, settleSeconds: 0);
            watcher.WorkerSource = () => Array.Empty<Process>();
            watcher.Start(armWmi: false);
            var child = Spawn();

            watcher.TrackTracedProcess(child.Id, "OfficeC2RClient");

            Assert.False(watcher.IsActive);
        }

        [Fact]
        public void A_traced_id_that_still_carries_the_expected_name_is_tracked()
        {
            using var watcher = new OfficeProcessWatcher(_logger, settleSeconds: 0);
            watcher.WorkerSource = () => Array.Empty<Process>();
            watcher.Start(armWmi: false);
            var child = Spawn();

            watcher.TrackTracedProcess(child.Id, "cmd");

            Assert.True(watcher.IsActive);
        }
    }
}

#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Microsoft.Win32;
using Xunit;

#pragma warning disable xUnit1031 // the tests wait on tasks on purpose: a blocked call is the failure they look for

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Office
{
    /// <summary>
    /// <see cref="RegistryChangeWatcher"/> against a real key under HKCU (no elevation needed).
    /// The watcher's lock is a leaf lock: <c>Changed</c> is raised with the lock free, so a
    /// handler can take other locks or dispose the watcher, and <c>Dispose</c> never waits for a
    /// handler. One handler runs at a time, and a change that arrives while it runs is reported
    /// after it returns.
    /// </summary>
    [Collection("SerialThreading")] // callbacks arrive on the shared ThreadPool
    public sealed class RegistryChangeWatcherTests : IDisposable
    {
        private const string RootSubKey = @"SOFTWARE\AutopilotMonitor.Tests\OfficeRegistryChangeWatcher";
        private const string TargetSubKey = RootSubKey + @"\Target";
        private const uint RegNotifyThreadAgnostic = 0x10000000;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly AgentLogger _logger;

        public RegistryChangeWatcherTests()
        {
            _logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            DeleteTestKeys();
            using (var root = Registry.CurrentUser.CreateSubKey(RootSubKey)) Assert.NotNull(root);
        }

        public void Dispose()
        {
            DeleteTestKeys();
            _tmp.Dispose();
        }

        private static void DeleteTestKeys()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(RootSubKey, throwOnMissingSubKey: false); }
            catch { /* teardown noise must not fail a test */ }
        }

        private static void CreateTarget()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(TargetSubKey)) Assert.NotNull(key);
        }

        private static void WriteValue(string name)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(TargetSubKey, writable: true))
            {
                Assert.NotNull(key);
                key!.SetValue(name, Guid.NewGuid().ToString("N"));
            }
        }

        private RegistryChangeWatcher NewWatcher() =>
            new RegistryChangeWatcher(RegistryHive.CurrentUser, TargetSubKey, _logger);

        [Fact]
        public void Changed_fires_when_a_value_is_written_under_the_target()
        {
            CreateTarget();
            using var watcher = NewWatcher();
            using var fired = new ManualResetEventSlim(false);
            watcher.Changed += (_, __) => fired.Set();

            watcher.Start();
            WriteValue("Probe");

            Assert.True(fired.Wait(Timeout), "Changed did not fire for a value write under the target key");
        }

        [Fact]
        public void Changed_fires_once_a_missing_target_key_appears()
        {
            using var watcher = NewWatcher();
            using var fired = new ManualResetEventSlim(false);
            watcher.Changed += (_, __) => fired.Set();

            watcher.Start(); // target absent: watches the parent for a new child key
            CreateTarget();

            Assert.True(fired.Wait(Timeout), "Changed did not fire when the target key appeared");
        }

        [Fact]
        public void Dispose_from_another_thread_returns_while_a_handler_is_running()
        {
            CreateTarget();
            var watcher = NewWatcher();
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            watcher.Changed += (_, __) => { entered.Set(); release.Wait(Timeout); };

            try
            {
                watcher.Start();
                WriteValue("Probe");
                Assert.True(entered.Wait(Timeout), "handler was not invoked");

                var dispose = Task.Run(() => watcher.Dispose());

                Assert.True(dispose.Wait(TimeSpan.FromSeconds(5)),
                    "Dispose waited for the running handler: the watcher's lock is held while Changed is raised");
            }
            finally
            {
                release.Set();
                watcher.Dispose();
            }
        }

        [Fact]
        public void A_handler_may_dispose_its_own_watcher()
        {
            CreateTarget();
            var watcher = NewWatcher();
            using var disposedInHandler = new ManualResetEventSlim(false);
            int calls = 0;
            watcher.Changed += (_, __) =>
            {
                Interlocked.Increment(ref calls);
                watcher.Dispose();
                disposedInHandler.Set();
            };

            watcher.Start();
            WriteValue("First");
            Assert.True(disposedInHandler.Wait(Timeout), "handler did not return from disposing its watcher");

            WriteValue("Second");
            Thread.Sleep(300);
            Assert.Equal(1, Volatile.Read(ref calls)); // a disposed watcher stays quiet
        }

        [Fact]
        public void Handlers_never_overlap_and_a_change_during_a_handler_is_reported_after_it()
        {
            CreateTarget();
            using var watcher = NewWatcher();
            using var firstEntered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            using var second = new ManualResetEventSlim(false);
            int calls = 0, running = 0, maxRunning = 0;
            watcher.Changed += (_, __) =>
            {
                var now = Interlocked.Increment(ref running);
                int seen;
                while (now > (seen = Volatile.Read(ref maxRunning)))
                    Interlocked.CompareExchange(ref maxRunning, now, seen);

                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstEntered.Set();
                    release.Wait(Timeout);
                }
                else
                {
                    second.Set();
                }
                Interlocked.Decrement(ref running);
            };

            watcher.Start();
            WriteValue("First");
            Assert.True(firstEntered.Wait(Timeout), "handler was not invoked");

            WriteValue("DuringHandler");
            Thread.Sleep(300);
            Assert.Equal(1, Volatile.Read(ref calls)); // no second handler while the first runs

            release.Set();

            Assert.True(second.Wait(Timeout), "the change that arrived during the handler was never reported");
            Assert.Equal(1, Volatile.Read(ref maxRunning));
        }

        [Fact]
        public void Target_watch_is_not_tied_to_the_arming_thread()
        {
            using var watcher = NewWatcher();

            // Re-arming happens on rotating ThreadPool threads. Without this flag Windows signals
            // the event when such a thread ends, which looks like a change that never happened.
            Assert.True((watcher.EffectivePrimaryFilter & RegNotifyThreadAgnostic) != 0,
                "the target watch must carry REG_NOTIFY_THREAD_AGNOSTIC");
        }
    }
}

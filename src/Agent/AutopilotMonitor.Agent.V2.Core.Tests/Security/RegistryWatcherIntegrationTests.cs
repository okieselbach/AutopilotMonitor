using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using Microsoft.Win32;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Security
{
    [CollectionDefinition(nameof(RegistryWatcherIntegrationTestCollection), DisableParallelization = true)]
    public sealed class RegistryWatcherIntegrationTestCollection { }

    /// <summary>
    /// Real <see cref="RegistryWatcher"/> integration test against
    /// <c>HKCU\SOFTWARE\AutopilotMonitor.Tests\WatcherIntegration</c>. Exercises the
    /// Win32 <c>RegNotifyChangeKeyValue</c> plumbing end-to-end:
    /// <list type="bullet">
    /// <item><description>opens the watched key, registers async notification</description></item>
    /// <item><description>another thread writes a value / creates a sub-key</description></item>
    /// <item><description>watcher thread receives the kernel signal and raises Changed</description></item>
    /// </list>
    /// <para>
    /// HKCU is used (not HKLM) so the test runs without elevation — anyone with a user
    /// session can write into their own hive. The watcher itself is hive-agnostic, so
    /// proving the plumbing on HKCU also proves it on HKLM (where production watches
    /// MDM enrollment + AAD-join keys).
    /// </para>
    /// <para>
    /// All test data lives under the dedicated test sub-key and is deleted on teardown.
    /// Tests run single-threaded — the 150 ms sleep that lets the watcher thread enter
    /// <c>RegNotifyChangeKeyValue</c> before the test thread writes is unreliable on a
    /// box busy with parallel xUnit work.
    /// </para>
    /// </summary>
    [Collection(nameof(RegistryWatcherIntegrationTestCollection))]
    public sealed class RegistryWatcherIntegrationTests : IDisposable
    {
        private const string TestSubKey = @"SOFTWARE\AutopilotMonitor.Tests\WatcherIntegration";

        public RegistryWatcherIntegrationTests()
        {
            // Ensure a clean slate — leftover state from a prior aborted run would skew
            // the test (e.g. existing values changing the watcher's "what's new" notion).
            CleanupTestKey();
            using (var key = Registry.CurrentUser.CreateSubKey(TestSubKey))
            {
                Assert.NotNull(key);
            }
        }

        public void Dispose() => CleanupTestKey();

        private static void CleanupTestKey()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(TestSubKey, throwOnMissingSubKey: false);
            }
            catch
            {
                // Cleanup-best-effort — never fail the test on teardown noise.
            }
        }

        [Fact]
        public void Watcher_fires_when_value_is_written()
        {
            using var fired = new ManualResetEventSlim(false);

            using var watcher = new RegistryWatcher(
                hive: RegistryHive.CurrentUser,
                subKey: TestSubKey,
                watchSubtree: false,
                view: RegistryView.Default,
                filter: RegistryNativeMethods.RegChangeNotifyFilter.LastSet
                      | RegistryNativeMethods.RegChangeNotifyFilter.ThreadAgnostic,
                trace: null);

            watcher.Changed += (_, __) => fired.Set();
            watcher.Start();

            // Give the watcher a moment to enter RegNotifyChangeKeyValue. Without this,
            // the write below can race ahead of the registration and the kernel won't
            // record an "outstanding" change for our handle.
            Thread.Sleep(150);

            using (var key = Registry.CurrentUser.OpenSubKey(TestSubKey, writable: true))
            {
                Assert.NotNull(key);
                key!.SetValue("ProbeValue", "hello");
            }

            Assert.True(fired.Wait(TimeSpan.FromSeconds(5)),
                "RegistryWatcher.Changed did not fire within 5s of value write");
        }

        [Fact]
        public void Watcher_fires_when_subtree_subkey_is_created()
        {
            using var fired = new ManualResetEventSlim(false);

            using var watcher = new RegistryWatcher(
                hive: RegistryHive.CurrentUser,
                subKey: TestSubKey,
                watchSubtree: true, // recursive — subkey writes anywhere below count
                view: RegistryView.Default,
                filter: RegistryNativeMethods.RegChangeNotifyFilter.Name
                      | RegistryNativeMethods.RegChangeNotifyFilter.LastSet
                      | RegistryNativeMethods.RegChangeNotifyFilter.ThreadAgnostic,
                trace: null);

            watcher.Changed += (_, __) => fired.Set();
            watcher.Start();

            Thread.Sleep(150);

            // Mirrors the production case: CloudDomainJoin\TenantInfo doesn't exist
            // yet at agent start; AAD join later writes a sub-key under the parent.
            using (var sub = Registry.CurrentUser.CreateSubKey($@"{TestSubKey}\NewChild"))
            {
                Assert.NotNull(sub);
                sub!.SetValue("Marker", 1);
            }

            Assert.True(fired.Wait(TimeSpan.FromSeconds(5)),
                "RegistryWatcher.Changed did not fire within 5s of sub-key creation");
        }

        [Fact]
        public void Watcher_fires_repeatedly_for_successive_changes()
        {
            int fireCount = 0;
            using var fired = new AutoResetEvent(false);

            using var watcher = new RegistryWatcher(
                hive: RegistryHive.CurrentUser,
                subKey: TestSubKey,
                watchSubtree: false,
                view: RegistryView.Default,
                filter: RegistryNativeMethods.RegChangeNotifyFilter.LastSet
                      | RegistryNativeMethods.RegChangeNotifyFilter.ThreadAgnostic,
                trace: null);

            watcher.Changed += (_, __) =>
            {
                Interlocked.Increment(ref fireCount);
                fired.Set();
            };
            watcher.Start();

            Thread.Sleep(150);

            using (var key = Registry.CurrentUser.OpenSubKey(TestSubKey, writable: true))
            {
                key!.SetValue("V1", "a");
                Assert.True(fired.WaitOne(TimeSpan.FromSeconds(5)), "first change");

                // RegNotifyChangeKeyValue is single-shot — the watcher loop re-arms
                // after each Changed. Verify the second write also wakes us.
                Thread.Sleep(50);
                key.SetValue("V2", "b");
                Assert.True(fired.WaitOne(TimeSpan.FromSeconds(5)), "second change");
            }

            Assert.True(fireCount >= 2, $"expected ≥2 fire events, saw {fireCount}");
        }

        [Fact]
        public void Watcher_forces_ThreadAgnostic_even_when_caller_omits_it()
        {
            // Regression guard for REG_NOTIFY_THREAD_AGNOSTIC. In the ThreadPool-wait model the
            // first RegNotifyChangeKeyValue is issued on the (transient) Start() caller thread and
            // re-armed on rotating ThreadPool threads. Without this flag the kernel ties the
            // registration to the issuing thread and silently cancels the notification once that
            // thread ends/recycles — the watcher goes quiet in production. Several callers
            // (AadJoinWatcher / ProvisioningStatusTracker / RealmJoinWatcher) omit it in their
            // explicit filter, so the watcher MUST OR it in itself.
            //
            // Asserted on the effective filter rather than via a live thread-death test: without
            // the flag the kernel emits a *spurious* signal when the arming thread dies, which a
            // behavioural test cannot distinguish from a real change — so only the effective-filter
            // invariant is a reliable guard. (RegChangeNotifyFilter is internal, so the cases are
            // iterated in-body rather than via [Theory] parameters.)
            var callerFilters = new[]
            {
                RegistryNativeMethods.RegChangeNotifyFilter.LastSet,
                RegistryNativeMethods.RegChangeNotifyFilter.Name,
                RegistryNativeMethods.RegChangeNotifyFilter.LastSet | RegistryNativeMethods.RegChangeNotifyFilter.Name,
            };

            foreach (var callerFilter in callerFilters)
            {
                using var watcher = new RegistryWatcher(
                    hive: RegistryHive.CurrentUser,
                    subKey: TestSubKey,
                    watchSubtree: false,
                    view: RegistryView.Default,
                    filter: callerFilter, // deliberately WITHOUT ThreadAgnostic
                    trace: null);

                Assert.True(
                    watcher.EffectiveFilter.HasFlag(RegistryNativeMethods.RegChangeNotifyFilter.ThreadAgnostic),
                    $"RegistryWatcher must force REG_NOTIFY_THREAD_AGNOSTIC into its effective filter (caller passed {callerFilter})");
                // The caller's requested bits must be preserved alongside the forced flag.
                Assert.Equal(callerFilter, watcher.EffectiveFilter & callerFilter);
            }
        }

        [Fact]
        public void Stop_after_RequestStop_still_waits_for_the_in_flight_callback()
        {
            // Regression guard for the Thread.Join() parity of the RequestStop -> queued-Stop path
            // (the RealmJoinWatcher appearance-handler shape). RequestStop() must NOT consume the
            // registration; otherwise a Stop()/Dispose() that runs right after sees a null handle,
            // skips the blocking Unregister, and returns while the Changed callback is still
            // executing. Here we park the callback, RequestStop, then assert Stop() blocks until the
            // callback completes.
            using var inHandler = new ManualResetEventSlim(false);
            using var releaseHandler = new ManualResetEventSlim(false);
            int handlerCompleted = 0;

            using var watcher = new RegistryWatcher(
                hive: RegistryHive.CurrentUser,
                subKey: TestSubKey,
                watchSubtree: false,
                view: RegistryView.Default,
                filter: RegistryNativeMethods.RegChangeNotifyFilter.LastSet,
                trace: null);

            watcher.Changed += (_, __) =>
            {
                inHandler.Set();
                releaseHandler.Wait(TimeSpan.FromSeconds(5));
                Volatile.Write(ref handlerCompleted, 1);
            };
            watcher.Start();

            Thread.Sleep(150);
            using (var key = Registry.CurrentUser.OpenSubKey(TestSubKey, writable: true))
            {
                key!.SetValue("Trigger", "1");
            }

            Assert.True(inHandler.Wait(TimeSpan.FromSeconds(5)), "Changed callback never entered");

            // Callback is now parked inside the handler. RequestStop must leave the registration
            // live so the subsequent Stop() can wait on it.
            watcher.RequestStop();

            // Let the handler finish a little later, on another thread, so Stop() is forced to block.
            var releaser = new Thread(() => { Thread.Sleep(250); releaseHandler.Set(); }) { IsBackground = true };
            releaser.Start();

            watcher.Stop();

            // If Stop() honored the in-flight callback it only returned after the handler completed.
            Assert.Equal(1, Volatile.Read(ref handlerCompleted));
            releaser.Join();
        }
    }
}

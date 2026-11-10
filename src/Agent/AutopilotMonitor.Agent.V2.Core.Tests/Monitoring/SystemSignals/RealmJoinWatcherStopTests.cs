#nullable enable
using System;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Microsoft.Win32;
using Xunit;

#pragma warning disable xUnit1031 // the test waits on a task on purpose: a blocked call is the failure it looks for

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// <see cref="RealmJoinWatcher.Stop"/> while the "RealmJoin key appeared" callback is in
    /// flight. The callback runs on a real registry watcher over the signed-in user's
    /// <c>HKU\&lt;sid&gt;\SOFTWARE</c> (writable without elevation); the key probes are injected.
    /// </summary>
    [Collection("SerialThreading")] // registry callbacks arrive on the shared ThreadPool
    public sealed class RealmJoinWatcherStopTests : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private readonly TempDirectory _tmp = new TempDirectory();
        // A new direct child of HKCU\SOFTWARE is what the parent watch reacts to.
        private readonly string _probeKey = @"SOFTWARE\AutopilotMonitor.Tests.RealmJoinStop." + Guid.NewGuid().ToString("N");

        public void Dispose()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(_probeKey, throwOnMissingSubKey: false); }
            catch { /* teardown noise must not fail a test */ }
            _tmp.Dispose();
        }

        [Fact]
        public void Stop_returns_while_the_key_appeared_callback_is_running()
        {
            var sid = WindowsIdentity.GetCurrent().User!.Value;
            var userRealmJoinPath = sid + "\\" + RealmJoinInfo.UserRealmJoinSubPath;

            var appearedProbe = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            int armed = 0;
            bool KeyExists(RegistryHive hive, string subPath)
            {
                // Everything is "absent" while the watcher arms. Afterwards the user-scope
                // RealmJoin key "exists": the callback sees it and goes on to switch watchers.
                if (hive != RegistryHive.Users || subPath != userRealmJoinPath || Volatile.Read(ref armed) == 0)
                    return false;
                appearedProbe.Set();
                release.Wait(Timeout);
                return true;
            }

            var watcher = new RealmJoinWatcher(
                new AgentLogger(_tmp.Path, AgentLogLevel.Info),
                enumeratePackageIds: (_, __) => Array.Empty<string>(),
                tryReadPackage: (RegistryHive _, string __, string ___, out RealmJoinPackageSnapshot snapshot) =>
                {
                    snapshot = default!;
                    return false;
                },
                keyExists: KeyExists,
                readVersionInfo: () => default,
                readUninstallDisplayVersion: () => null,
                readDeploymentPhase: () => null);

            // Production order: the SID is recorded, then RealmJoin reaches the running phase and
            // the watcher arms its parent watch on HKU\<sid>\SOFTWARE.
            watcher.ArmHku(sid);
            watcher.TriggerNotifyRealmJoinPresenceFromTest(RealmJoinInfo.PhaseRunningThresholdMin);
            Volatile.Write(ref armed, 1);

            Task stop;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(_probeKey)) Assert.NotNull(key);
                Assert.True(appearedProbe.Wait(Timeout), "the parent watch did not report the new key");

                stop = Task.Run(() => watcher.Stop());
                Thread.Sleep(300); // let Stop reach the registry watcher whose callback is parked
            }
            finally
            {
                release.Set();
            }

            var returned = stop.Wait(Timeout);
            // A blocked Stop holds the watcher's lock for good; disposing it would hang the run.
            if (returned) watcher.Dispose();
            Assert.True(returned,
                "Stop and the key-appeared callback blocked each other: watchers are disposed under the state lock");
        }
    }
}

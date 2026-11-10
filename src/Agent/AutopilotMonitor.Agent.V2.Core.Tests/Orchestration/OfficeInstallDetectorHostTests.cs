#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared;
using Microsoft.Win32;
using Xunit;

#pragma warning disable xUnit1031 // the tests wait on tasks on purpose: a blocked call is the failure they look for

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// <see cref="OfficeInstallDetectorHost"/> with its real watchers: the registry watcher on a
    /// key under HKCU, the binary watcher on a temp folder, snapshots injected. Stopping the host
    /// must never wait for a watcher callback, and a completion that coincides with a registry
    /// callback must not leave the host unable to stop.
    /// </summary>
    [Collection("SerialThreading")] // registry + filesystem callbacks arrive on the shared ThreadPool
    public sealed class OfficeInstallDetectorHostTests : IDisposable
    {
        private const string RegistrySubKey = @"SOFTWARE\AutopilotMonitor.Tests\OfficeInstallDetectorHost";
        private static readonly DateTime At = new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly FakeSignalIngressSink _sink = new FakeSignalIngressSink();
        private readonly string _installPath;
        private Func<OfficeC2RSnapshot> _snapshot;

        public OfficeInstallDetectorHostTests()
        {
            DeleteTestKey();
            using (var key = Registry.CurrentUser.CreateSubKey(RegistrySubKey)) Assert.NotNull(key);
            _installPath = Path.Combine(_tmp.Path, "Office");
            Directory.CreateDirectory(_installPath);
            _snapshot = Idle;
        }

        public void Dispose()
        {
            DeleteTestKey();
            _tmp.Dispose();
        }

        private static void DeleteTestKey()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(RegistrySubKey, throwOnMissingSubKey: false); }
            catch { /* teardown noise must not fail a test */ }
        }

        private static void TouchRegistry()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RegistrySubKey, writable: true))
            {
                Assert.NotNull(key);
                key!.SetValue("Probe", Guid.NewGuid().ToString("N"));
            }
        }

        private OfficeInstallDetectorHost NewHost() =>
            new OfficeInstallDetectorHost(
                sessionId: "S1",
                tenantId: "T1",
                ingress: _sink,
                clock: new VirtualClock(At),
                logger: new AgentLogger(_tmp.Path, AgentLogLevel.Info),
                settleSeconds: 0,
                statePersistence: null,
                resumeState: null,
                registryHive: RegistryHive.CurrentUser,
                registrySubKey: RegistrySubKey,
                snapshotProvider: () => _snapshot());

        // No install scenario, no install path: the detector stays idle and the host keeps
        // raising OfficeExpected on registry changes.
        private static OfficeC2RSnapshot Idle() => new OfficeC2RSnapshot { ConfigurationKeyPresent = true };

        private OfficeC2RSnapshot Installing()
        {
            var s = new OfficeC2RSnapshot
            {
                ConfigurationKeyPresent = true,
                ActiveScenarioPresent = true,
                ActiveScenarioName = "INSTALL",
                InstallScenarioPresent = true,
                InstallScenarioName = "INSTALL",
                InstallationPath = _installPath,
                VersionToReport = "16.0.1.1",
            };
            s.Products.Add("O365ProPlusRetail");
            return s;
        }

        private int OfficeEvents(string eventType) => _sink.Posted.Count(p =>
            p.Payload != null
            && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
            && et == eventType);

        [Fact]
        public void Stop_returns_while_a_registry_handler_is_running()
        {
            var host = NewHost();
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            host.OfficeExpected += (_, __) => { entered.Set(); release.Wait(Timeout); };

            try
            {
                host.Start();
                TouchRegistry();
                Assert.True(entered.Wait(Timeout), "the registry change did not reach the host");

                var stop = Task.Run(() => host.Stop());

                Assert.True(stop.Wait(TimeSpan.FromSeconds(5)),
                    "Stop waited for the running registry handler");
            }
            finally
            {
                release.Set();
            }
        }

        [Fact]
        public void A_completion_during_a_running_registry_handler_does_not_block_Stop()
        {
            var host = NewHost();
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            host.OfficeExpected += (_, __) => { entered.Set(); release.Wait(Timeout); };

            try
            {
                // 1) A registry callback is in flight (parked in the host's OfficeExpected handler).
                host.Start();
                TouchRegistry();
                Assert.True(entered.Wait(Timeout), "the registry change did not reach the host");

                // 2) The install starts and completes meanwhile: the binary watcher sees the core
                //    binary, the detector reports completed, the host tears its watchers down.
                _snapshot = Installing;
                host.SubmitDoSample(new OfficeDoSample { JobCount = 1, FileSize = 100, TotalBytesDownloaded = 10 });
                Assert.Equal(1, OfficeEvents(Constants.EventTypes.OfficeInstallStarted));

                var versionDir = Path.Combine(_installPath, "root", "Office16");
                Directory.CreateDirectory(versionDir);
                File.WriteAllText(Path.Combine(versionDir, "EXCEL.EXE"), "stub");
                Assert.True(
                    SpinWait.SpinUntil(() => OfficeEvents(Constants.EventTypes.OfficeInstallCompleted) == 1, Timeout),
                    "the install was not reported as completed");

                // 3) The agent stops while that registry callback is still running.
                var stop = Task.Run(() => host.Stop());

                Assert.True(stop.Wait(TimeSpan.FromSeconds(5)),
                    "Stop waited behind the completion's teardown, which waited for the running registry handler");
            }
            finally
            {
                release.Set();
            }

            Assert.Equal(1, OfficeEvents(Constants.EventTypes.OfficeInstallCompleted));
        }
    }
}

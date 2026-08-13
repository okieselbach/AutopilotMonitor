using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Enforces the comment-only ordering invariants of <see cref="DefaultComponentFactory"/> by
    /// building the REAL host list (construction only — no host is started) and asserting the
    /// relative positions of the order-sensitive host types. The orchestrator starts hosts in
    /// list order, so list order IS start order:
    /// <list type="bullet">
    ///   <item>DeviceInfoHost before EspAndHelloHost — L9 (delta review 2026-07-02): DeviceInfoHost
    ///     must subscribe to SignalPosted before EspAndHelloHost.Start can post the registry-backfill
    ///     EspPhaseChanged(DeviceSetup), or the enrollment-start re-collect never runs.</item>
    ///   <item>OsBuildChangeHost before WindowsUpdateWatcherHost — the WU channel census reads
    ///     OsBuildChangeHost.BuildChanged at its own start.</item>
    ///   <item>DeliveryOptimizationHost before OfficeInstallDetectorHost — the DO host must have
    ///     subscribed to the Office process-start signal before the watcher starts, or an install
    ///     already in flight misses its wake.</item>
    ///   <item>DesktopArrivalHost before AadJoinHost — the AadJoinHost ctor takes the desktop
    ///     host's RequestResetForRealUserSwitch callback, so the target must exist first.</item>
    /// </list>
    /// </summary>
    public sealed class DefaultComponentFactoryOrderingTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();

        public void Dispose() => _tmp.Dispose();

        /// <summary>
        /// Build the production host list with default remote config (all order-sensitive hosts
        /// enabled by default) and system-boundary fakes only: temp state dir, fake ingress,
        /// virtual clock, no network metrics, no telemetry spool.
        /// </summary>
        private CollectorSurfaces CreateSurfaces()
        {
            var factory = new DefaultComponentFactory(
                agentConfig: new AgentConfiguration
                {
                    ApiBaseUrl = "https://example",
                    TenantId = "t",
                    SessionId = "s",
                },
                remoteConfig: new AgentConfigResponse
                {
                    Collectors = CollectorConfiguration.CreateDefault(),
                },
                networkMetrics: null,
                agentVersion: "test",
                stateDirectory: _tmp.Path,
                startupEventGate: null);

            return factory.CreateCollectorHosts(
                sessionId: "s",
                tenantId: "t",
                logger: new AgentLogger(_tmp.Path, AgentLogLevel.Info),
                whiteGloveSealingPatternIds: Array.Empty<string>(),
                ingress: new FakeSignalIngressSink(),
                clock: new VirtualClock(new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc)),
                telemetrySpool: null,
                timelineEvents: null);
        }

        private static int IndexOf<THost>(IReadOnlyList<ICollectorHost> hosts) where THost : ICollectorHost
        {
            var indices = hosts
                .Select((host, index) => (host, index))
                .Where(t => t.host is THost)
                .Select(t => t.index)
                .ToArray();
            Assert.True(
                indices.Length == 1,
                $"Expected exactly one {typeof(THost).Name} in the host list, found {indices.Length}. " +
                $"Hosts: [{string.Join(", ", hosts.Select(h => h.GetType().Name))}]");
            return indices[0];
        }

        private static void AssertBefore<TFirst, TSecond>(IReadOnlyList<ICollectorHost> hosts, string reason)
            where TFirst : ICollectorHost
            where TSecond : ICollectorHost
        {
            var first = IndexOf<TFirst>(hosts);
            var second = IndexOf<TSecond>(hosts);
            Assert.True(
                first < second,
                $"{typeof(TFirst).Name} (index {first}) must come before {typeof(TSecond).Name} (index {second}): {reason}");
        }

        [Fact]
        public void Order_sensitive_hosts_keep_their_documented_relative_order()
        {
            var surfaces = CreateSurfaces();
            var hosts = surfaces.Hosts;
            try
            {
                AssertBefore<DeviceInfoHost, EspAndHelloHost>(
                    hosts,
                    "L9 — DeviceInfoHost must subscribe to SignalPosted before EspAndHelloHost's " +
                    "registry backfill can post EspPhaseChanged(DeviceSetup).");

                AssertBefore<OsBuildChangeHost, WindowsUpdateWatcherHost>(
                    hosts,
                    "the Windows Update channel census reads OsBuildChangeHost.BuildChanged at its own start.");

                AssertBefore<DeliveryOptimizationHost, OfficeInstallDetectorHost>(
                    hosts,
                    "the DO host must subscribe to the Office process-start signal before the watcher starts.");

                AssertBefore<DesktopArrivalHost, AadJoinHost>(
                    hosts,
                    "AadJoinHost wires its onRealUserJoined callback to the already-constructed DesktopArrivalHost.");

                // Closure-latch sanity (factory lines ~139/297): the EspAndHelloHost probes close
                // over the ImeLogHost reference, which is assigned during the same factory call —
                // the host must therefore exist in the returned list.
                Assert.Equal(1, hosts.Count(h => h is ImeLogHost));
            }
            finally
            {
                foreach (var host in hosts)
                {
                    try { host.Dispose(); } catch { /* construction-only test — best-effort cleanup */ }
                }
            }
        }
    }
}

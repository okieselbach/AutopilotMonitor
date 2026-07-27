using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;
using DeviceInfoCollector = AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo.DeviceInfoCollector;

namespace AutopilotMonitor.Agent.V2.Core.Tests.DeviceInfo
{
    /// <summary>
    /// Audit Q2 — <see cref="DeviceInfoCollector.RefreshEspConfiguration"/>
    /// re-reads the ESP blocking lists and re-emits <c>esp_config_detected</c> exactly when the
    /// payload changed: the registry lists grow progressively (one timestamped subkey per CSP
    /// status write) and the user-scope lists appear only after sign-in, so the early emissions
    /// are structurally partial. The StartupEventGate keeps an unchanged re-read silent.
    /// Probe overrides are static slots → serialized with the other probe-driven suites.
    /// </summary>
    [Collection("SerialThreading")]
    public sealed class DeviceInfoCollectorEspRefreshTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);

        private const string DeviceApp1 = "11111111-1111-1111-1111-111111111111";
        private const string DeviceApp2 = "22222222-2222-2222-2222-222222222222";
        private const string UserApp1 = "33333333-3333-3333-3333-333333333333";

        private static EspTrackingInfoSnapshot Snapshot(string[] deviceApps, string[] userApps)
        {
            var all = deviceApps.Concat(userApps).Distinct().ToArray();
            return new EspTrackingInfoSnapshot(
                msiProductCodes: Array.Empty<string>(),
                modernAppPfns: Array.Empty<string>(),
                win32AppIds: all,
                userWin32AppIds: userApps,
                msiCount: 0,
                modernCount: 0,
                win32Count: all.Length);
        }

        private static IReadOnlyList<FakeSignalIngressSink.PostedSignal> EspConfigEvents(FakeSignalIngressSink ingress) =>
            ingress.Posted
                .Where(p => p.Kind == DecisionSignalKind.InformationalEvent
                            && p.Payload != null
                            && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                            && et == SharedEventTypes.EspConfigDetected)
                .ToList();

        private static IReadOnlyList<string> TypedList(FakeSignalIngressSink.PostedSignal signal, string key)
        {
            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(signal.TypedPayload);
            return Assert.IsAssignableFrom<IReadOnlyList<string>>(data[key]);
        }

        [Fact]
        public void Refresh_reemits_when_lists_grew_and_stays_silent_when_unchanged()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var ingress = new FakeSignalIngressSink();
            var clock = new VirtualClock(T0);
            var collector = new DeviceInfoCollector(
                "session-1", "tenant-1",
                new InformationalEventPost(ingress, clock), logger,
                signalIngress: null, clock: clock,
                startupGate: new StartupEventGate(tmp.Path, logger));

            using var skip = new EspSkipConfigurationProbe.ScopedFullOverride(
                _ => new EspFirstSyncSnapshot(
                    skipUser: false, skipDevice: false,
                    blockInStatusPage: null, syncFailureTimeoutMinutes: null));

            // Early read (agent start / DeviceSetup detection): partial device list, empty user list.
            using (new EspTrackingInfoProbe.ScopedOverride(_ => Snapshot(new[] { DeviceApp1 }, Array.Empty<string>())))
            {
                collector.CollectEspConfiguration();
            }
            var afterEarly = EspConfigEvents(ingress);
            Assert.Single(afterEarly);
            Assert.Empty(TypedList(afterEarly[0], "espTrackedUserWin32AppIds"));

            // Apps sub-phase opened: the device list grew and sign-in populated the user list.
            using (new EspTrackingInfoProbe.ScopedOverride(_ => Snapshot(new[] { DeviceApp1, DeviceApp2 }, new[] { UserApp1 })))
            {
                collector.RefreshEspConfiguration(DeviceInfoHost.EspConfigTriggerAppsUser);
            }
            var afterGrowth = EspConfigEvents(ingress);
            Assert.Equal(2, afterGrowth.Count);
            Assert.Contains(DeviceApp2, TypedList(afterGrowth[1], "espTrackedWin32AppIds"));
            Assert.Equal(new[] { UserApp1 }, TypedList(afterGrowth[1], "espTrackedUserWin32AppIds"));

            // A later trigger with an UNCHANGED registry state re-reads but emits nothing —
            // the StartupEventGate suppresses the identical payload.
            using (new EspTrackingInfoProbe.ScopedOverride(_ => Snapshot(new[] { DeviceApp1, DeviceApp2 }, new[] { UserApp1 })))
            {
                collector.RefreshEspConfiguration(DeviceInfoHost.EspConfigTriggerAccountSetup);
            }
            Assert.Equal(2, EspConfigEvents(ingress).Count);
        }
    }
}

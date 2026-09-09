using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring
{
    /// <summary>
    /// Session 683f1eff — a Shell-Core 62407 failure (<c>WhiteGlove_Failed</c>) arrived four
    /// seconds into the provisioning tracker's 30-s settle window and terminalised the session
    /// with nothing but its failure type: the HRESULT (0x80070652) and the failed subcategory
    /// (Apps) the registry had already named never reached the terminal event, the backend
    /// FailureReason or the termination-time app classification. The coordinator now inherits
    /// the pending settle detail onto the Shell-Core args and records the terminal-failure
    /// snapshot from it.
    /// </summary>
    public sealed class EspAndHelloTrackerShellCoreFailureInheritanceTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 9, 8, 18, 11, 6, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public FakeSignalIngressSink TrackerPostSink { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);
            public List<EspFailureDetectedEventArgs> Forwarded { get; } = new List<EspFailureDetectedEventArgs>();

            public Fixture() { Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Debug); }

            public EspAndHelloTracker BuildCoordinator(Func<EspFailureDetectedEventArgs?> pendingProbe)
            {
                var coordinator = new EspAndHelloTracker(
                    sessionId: "S1",
                    tenantId: "T1",
                    post: new InformationalEventPost(TrackerPostSink, Clock),
                    logger: Logger,
                    skipConfigProbe: () => ((bool?)false, (bool?)false),
                    accountSetupActivityProbe: () => false,
                    pendingEspFailureProbe: pendingProbe);
                coordinator.EspFailureDetected += (_, args) => Forwarded.Add(args);
                return coordinator;
            }

            public FakeSignalIngressSink.PostedSignal TerminalSignal() =>
                Assert.Single(Ingress.Posted, p => p.Kind == DecisionSignalKind.EspTerminalFailure);

            public void Dispose() { Tmp.Dispose(); }
        }

        private static EspFailureDetectedEventArgs PendingAppsFailure() => new EspFailureDetectedEventArgs(
            failureType: "Provisioning_DeviceSetup_Apps_Failed",
            errorCode: "0x80070652",
            failedSubcategory: "Apps",
            category: "DeviceSetup",
            likelyCulpritApps: new[] { "ESP Finalizer US" });

        [Fact]
        public void ShellCore_failure_inside_the_settle_window_inherits_the_registry_detail()
        {
            using var f = new Fixture();
            using var coordinator = f.BuildCoordinator(PendingAppsFailure);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerShellCoreEspFailureForTest("WhiteGlove_Failed");

            var args = Assert.Single(f.Forwarded);
            Assert.Equal("WhiteGlove_Failed", args.FailureType);   // the signal that terminalised
            Assert.Equal("0x80070652", args.ErrorCode);
            Assert.Equal("Apps", args.FailedSubcategory);
            Assert.Equal("DeviceSetup", args.Category);
            Assert.Equal(new[] { "ESP Finalizer US" }, args.LikelyCulpritApps);

            // Wire-through: the EspTerminalFailure signal payload carries the detail the
            // DecisionEngine copies onto enrollment_failed.
            var signal = f.TerminalSignal();
            Assert.Equal("WhiteGlove_Failed", signal.Payload!["failureType"]);
            Assert.Equal("0x80070652", signal.Payload["errorCode"]);
            Assert.Equal("Apps", signal.Payload["failedSubcategory"]);
            Assert.Equal("DeviceSetup", signal.Payload["category"]);
            Assert.Equal("ESP Finalizer US", signal.Payload["likelyCulpritApps"]);

            // Termination-time classification reads the same context.
            Assert.NotNull(coordinator.LastEspTerminalFailure);
            Assert.Equal("0x80070652", coordinator.LastEspTerminalFailure.ErrorCode);
            Assert.True(coordinator.LastEspTerminalFailure.IsAppsSubcategory);
        }

        [Fact]
        public void ShellCore_failure_without_a_pending_window_stays_bare()
        {
            using var f = new Fixture();
            using var coordinator = f.BuildCoordinator(() => null);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerShellCoreEspFailureForTest("ESPProgress_Timeout");

            var args = Assert.Single(f.Forwarded);
            Assert.Equal("ESPProgress_Timeout", args.FailureType);
            Assert.Null(args.ErrorCode);
            Assert.Null(args.FailedSubcategory);
            Assert.Null(args.Category);
            Assert.Empty(args.LikelyCulpritApps);
            Assert.Null(coordinator.LastEspTerminalFailure);

            var signal = f.TerminalSignal();
            Assert.False(signal.Payload!.ContainsKey("errorCode"));
            Assert.False(signal.Payload.ContainsKey("failedSubcategory"));
        }

        [Fact]
        public void A_throwing_probe_degrades_to_the_bare_failure()
        {
            using var f = new Fixture();
            using var coordinator = f.BuildCoordinator(() => throw new InvalidOperationException("probe"));

            coordinator.TriggerShellCoreEspFailureForTest("WhiteGlove_Failed");

            var args = Assert.Single(f.Forwarded);
            Assert.Equal("WhiteGlove_Failed", args.FailureType);
            Assert.Null(args.ErrorCode);
        }
    }
}

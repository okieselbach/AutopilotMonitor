using System;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Transport;
using AutopilotMonitor.DecisionCore.Classifiers;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Harness
{
    /// <summary>
    /// Shared rig for every <c>EnrollmentOrchestrator*Tests</c> file. Previously each of the
    /// six test files declared its own inner <c>Rig</c>/<c>Build</c> class with an overlapping
    /// constructor signature; consolidating eliminates ~200 LOC of boilerplate and keeps the
    /// orchestrator-construction surface in one place.
    /// <para>
    /// The rig owns its own <see cref="TempDirectory"/>, a <see cref="VirtualClock"/> starting
    /// at <paramref name="clockStart"/>, a fake uploader, and an empty classifier list. Tests
    /// that need additional orchestrator parameters pass them to <see cref="Build"/>; everything
    /// else takes a sensible default (drain interval = 1 day so periodic drain doesn't fire
    /// during short tests).
    /// </para>
    /// </summary>
    internal sealed class EnrollmentOrchestratorRig : IDisposable
    {
        public static readonly DateTime DefaultClockStart = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        public TempDirectory Tmp { get; } = new TempDirectory();
        public VirtualClock Clock { get; }
        public AgentLogger Logger { get; }
        public FakeBackendTelemetryUploader Uploader { get; } = new FakeBackendTelemetryUploader();
        public List<IClassifier> Classifiers { get; } = new List<IClassifier>();
        public string StateDir => Path.Combine(Tmp.Path, "State");
        public string TransportDir => Path.Combine(Tmp.Path, "Transport");

        public EnrollmentOrchestratorRig(DateTime? clockStart = null)
        {
            Clock = new VirtualClock(clockStart ?? DefaultClockStart);
            Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
        }

        public EnrollmentOrchestrator Build(
            IComponentFactory? componentFactory = null,
            IReadOnlyCollection<string>? whiteGloveSealingPatternIds = null,
            TimeSpan? drainInterval = null,
            TimeSpan? terminalDrainTimeout = null,
            TimeSpan? agentMaxLifetime = null,
            TimeSpan? collectorHostStopTimeout = null) =>
            new EnrollmentOrchestrator(
                sessionId: "S1",
                tenantId: "T1",
                stateDirectory: StateDir,
                transportDirectory: TransportDir,
                clock: Clock,
                logger: Logger,
                uploader: Uploader,
                classifiers: Classifiers,
                componentFactory: componentFactory,
                whiteGloveSealingPatternIds: whiteGloveSealingPatternIds,
                drainInterval: drainInterval ?? TimeSpan.FromDays(1),
                terminalDrainTimeout: terminalDrainTimeout ?? TimeSpan.FromSeconds(2),
                agentMaxLifetime: agentMaxLifetime,
                collectorHostStopTimeout: collectorHostStopTimeout);

        public void Dispose() => Tmp.Dispose();
    }
}

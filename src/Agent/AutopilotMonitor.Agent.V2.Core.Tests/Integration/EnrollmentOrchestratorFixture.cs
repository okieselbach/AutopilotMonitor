using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Transport;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Integration
{
    /// <summary>
    /// End-to-end integration-test fixture for the V2 Agent runtime.
    /// Plan §4.x M4.4.5.g.
    /// <para>
    /// <b>Drives the real pipeline</b>: DecisionEngine + DecisionStepProcessor + SignalIngress +
    /// EffectRunner + Persistence (SignalLog/Journal/Snapshot) + TelemetryTransport + production
    /// Classifiers (WhiteGloveSealingClassifier).
    /// </para>
    /// <para>
    /// <b>System-boundary fakes only</b>: HTTP upload (<see cref="FakeBackendTelemetryUploader"/>),
    /// wall-clock (<see cref="VirtualClock"/>), file-system (isolated <see cref="TempDirectory"/>).
    /// No collectors are wired — tests drive signals directly via
    /// <see cref="PostFixture"/> / <see cref="Post"/>, which mirrors the Collector→Adapter→Ingress
    /// path but lets the test control timing.
    /// </para>
    /// <para>
    /// <b>Deadline caveat</b>: the real <see cref="DeadlineScheduler"/> runs. Effect-scheduled
    /// deadlines use wall-clock timers; fixture signals with <c>DeadlineFired</c>-Kind are
    /// posted manually (same as the M3 <c>ClassifierAwareReplayHarness</c>) — the real timers
    /// would fire in the wall-clock future, long after test shutdown.
    /// </para>
    /// </summary>
    internal sealed class EnrollmentOrchestratorFixture : IDisposable
    {
        public const string SessionId = "session-anon-integration";
        public const string TenantId = "tenant-anon-integration";

        public TempDirectory Tmp { get; } = new TempDirectory();
        public VirtualClock Clock { get; }
        public AgentLogger Logger { get; }
        public FakeBackendTelemetryUploader Uploader { get; } = new FakeBackendTelemetryUploader();
        public EnrollmentOrchestrator Orchestrator { get; }
        public string StateDir { get; }
        public string TransportDir { get; }

        public EnrollmentOrchestratorFixture()
        {
            Clock = new VirtualClock(new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc));
            Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
            StateDir = Path.Combine(Tmp.Path, "State");
            TransportDir = Path.Combine(Tmp.Path, "Transport");

            // Uploader is scripted OK so terminal drains succeed.
            Uploader.QueueOk(100);

            var classifiers = new List<IClassifier>
            {
                new WhiteGloveSealingClassifier(),
            };

            Orchestrator = new EnrollmentOrchestrator(
                sessionId: SessionId,
                tenantId: TenantId,
                stateDirectory: StateDir,
                transportDirectory: TransportDir,
                clock: Clock,
                logger: Logger,
                uploader: Uploader,
                classifiers: classifiers,
                // No collectors — tests drive the Ingress directly.
                componentFactory: null,
                drainInterval: TimeSpan.FromDays(1),
                terminalDrainTimeout: TimeSpan.FromSeconds(2));
        }

        public void Start() => Orchestrator.Start();

        public void Stop() => Orchestrator.Stop();

        /// <summary>
        /// Post one signal via the IngressSink, mirroring the Collector→Adapter path.
        /// </summary>
        public void Post(DecisionSignal signal)
        {
            if (signal == null) throw new ArgumentNullException(nameof(signal));
            Orchestrator.IngressSink.Post(
                kind: signal.Kind,
                occurredAtUtc: signal.OccurredAtUtc,
                sourceOrigin: signal.SourceOrigin,
                evidence: signal.Evidence,
                payload: signal.Payload,
                kindSchemaVersion: signal.KindSchemaVersion);
        }

        /// <summary>
        /// Load an M3 fixture (e.g. <c>"userdriven-happy-v1.jsonl"</c>) and replay every signal
        /// in order. After each post, wait until:
        /// <list type="bullet">
        ///   <item><c>processor.CurrentState.StepIndex</c> has advanced — ApplyStep fully
        ///         completed (journal + effects + snapshot + state-forward)</item>
        ///   <item>Ingress queue is empty — any <c>ClassifierVerdictIssued</c> posted by the
        ///         EffectRunner has been processed</item>
        /// </list>
        /// <para>
        /// <b>Why not <see cref="ISignalLogWriter.LastOrdinal"/>?</b> That counter is updated
        /// inside <c>SignalIngress.ProcessItem</c> BEFORE the EffectRunner runs — so a wait
        /// on LastOrdinal can release before any synthetic signal (e.g. a classifier verdict)
        /// has even been posted. <c>StepIndex</c> on the other hand is only incremented after
        /// <c>DecisionStepProcessor.ApplyStep</c> completes, by which time the EffectRunner has
        /// synchronously posted any <c>RunClassifier</c>-triggered synthetic.
        /// </para>
        /// </summary>
        public void PostFixture(string fixtureFilename)
        {
            var signals = FixtureLoader.Load(fixtureFilename);
            var ingress = GetIngress();

            foreach (var sig in signals)
            {
                int stepIndexBefore = Orchestrator.CurrentState.StepIndex;
                Post(sig);

                if (!SpinWait.SpinUntil(
                    () => Orchestrator.CurrentState.StepIndex > stepIndexBefore
                          && ingress.ApproximateQueueLength == 0,
                    5000))
                {
                    throw new TimeoutException(
                        $"Fixture replay stalled after {sig.Kind} (stepIndex before={stepIndexBefore}, " +
                        $"now={Orchestrator.CurrentState.StepIndex}, stage={Orchestrator.CurrentState.Stage}, " +
                        $"queueLen={ingress.ApproximateQueueLength}). Fixture: {fixtureFilename}.");
                }
            }

            // Plan §5 Fix 6: fixtures end with the real-world "both-prerequisites-resolved"
            // signal, which now parks the reducer in Finalizing with a wall-clock-future
            // FinalizingGrace deadline. The real DeadlineScheduler would fire that deadline
            // in 5 s, but tests use a VirtualClock + skip wall-clock waits. Post a synthetic
            // DeadlineFired signal here to flush the grace window deterministically — mirrors
            // what <see cref="Harness.ReplayHarness"/> does for the DecisionCore scenario tests.
            // The per-signal wait above has a well-known race: <c>ApproximateQueueLength</c>
            // drops to 0 the moment the ingress worker dequeues an item, BEFORE Reduce +
            // ApplyStep run. A synthetic signal posted by the EffectRunner of the PRIOR step
            // can also satisfy "stepIndex > stepIndexBefore" before the current signal has been
            // applied. Wait here for the reducer's <see cref="DecisionState.LastAppliedSignalOrdinal"/>
            // to reach the signal log's last-appended ordinal — the authoritative "reducer has
            // caught up" watermark.
            var signalLog = GetSignalLog();
            if (!SpinWait.SpinUntil(
                () => ingress.ApproximateQueueLength == 0
                      && Orchestrator.CurrentState.LastAppliedSignalOrdinal >= signalLog.LastOrdinal,
                5000))
            {
                throw new TimeoutException(
                    $"Fixture replay did not settle (queueLen={ingress.ApproximateQueueLength}, " +
                    $"lastAppliedOrdinal={Orchestrator.CurrentState.LastAppliedSignalOrdinal}, " +
                    $"signalLog.LastOrdinal={signalLog.LastOrdinal}). Fixture: {fixtureFilename}.");
            }
            if (Orchestrator.CurrentState.Stage == SessionStage.Finalizing)
            {
                var finalizingDeadline = Orchestrator.CurrentState.Deadlines
                    .FirstOrDefault(d => string.Equals(d.Name, DeadlineNames.FinalizingGrace, StringComparison.Ordinal));
                if (finalizingDeadline != null)
                {
                    int stepIndexBefore = Orchestrator.CurrentState.StepIndex;
                    Orchestrator.IngressSink.Post(
                        kind: DecisionSignalKind.DeadlineFired,
                        occurredAtUtc: finalizingDeadline.DueAtUtc,
                        sourceOrigin: "integration_fixture",
                        evidence: new Evidence(
                            kind: EvidenceKind.Synthetic,
                            identifier: $"integration-fixture-deadline-fire:{DeadlineNames.FinalizingGrace}",
                            summary: "Auto-fired FinalizingGrace deadline at end-of-fixture"),
                        payload: new Dictionary<string, string>
                        {
                            [SignalPayloadKeys.Deadline] = DeadlineNames.FinalizingGrace,
                        });

                    if (!SpinWait.SpinUntil(
                        () => Orchestrator.CurrentState.StepIndex > stepIndexBefore
                              && ingress.ApproximateQueueLength == 0,
                        5000))
                    {
                        throw new TimeoutException(
                            $"FinalizingGrace auto-fire stalled (stepIndex before={stepIndexBefore}, " +
                            $"now={Orchestrator.CurrentState.StepIndex}, stage={Orchestrator.CurrentState.Stage}, " +
                            $"queueLen={ingress.ApproximateQueueLength}). Fixture: {fixtureFilename}.");
                    }
                }
            }
        }

        public AutopilotMonitor.Agent.V2.Core.Orchestration.SignalIngress GetIngress()
        {
            var field = typeof(EnrollmentOrchestrator).GetField(
                "_ingress",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (AutopilotMonitor.Agent.V2.Core.Orchestration.SignalIngress)field!.GetValue(Orchestrator)!;
        }

        /// <summary>Waits for <see cref="DecisionState.Stage"/> to match any of <paramref name="stages"/>.</summary>
        public bool WaitForStage(int timeoutMs, params SessionStage[] stages)
        {
            var target = new HashSet<SessionStage>(stages);
            return SpinWait.SpinUntil(
                () => target.Contains(Orchestrator.CurrentState.Stage),
                timeoutMs);
        }

        public ISignalLogWriter GetSignalLog()
        {
            var field = typeof(EnrollmentOrchestrator).GetField(
                "_signalLog",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (ISignalLogWriter)field!.GetValue(Orchestrator)!;
        }

        public IJournalWriter GetJournal()
        {
            var field = typeof(EnrollmentOrchestrator).GetField(
                "_journal",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (IJournalWriter)field!.GetValue(Orchestrator)!;
        }

        public TelemetrySpool GetSpool()
        {
            var field = typeof(EnrollmentOrchestrator).GetField(
                "_spool",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (TelemetrySpool)field!.GetValue(Orchestrator)!;
        }

        /// <summary>Returns the last journal transition (or null if empty).</summary>
        public AutopilotMonitor.DecisionCore.Engine.DecisionTransition? LastTransition()
        {
            var all = GetJournal().ReadAll();
            return all.Count == 0 ? null : all[all.Count - 1];
        }

        /// <summary>Returns all event-kind items that were enqueued for the backend (post-drain or not).</summary>
        public IReadOnlyList<TelemetryItem> AllEventItemsInSpool()
        {
            return GetSpool()
                .Peek(int.MaxValue)
                .Where(i => i.Kind == TelemetryItemKind.Event)
                .ToArray();
        }

        public void Dispose()
        {
            try { Orchestrator.Dispose(); }
            catch { /* best-effort */ }
            Tmp.Dispose();
        }
    }
}

#nullable enable
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Factory-Seam für Collector-Hosts. Plan §4.x M4.4.5.c / §5.10 (single-rail enforcement).
    /// <para>
    /// Der <see cref="EnrollmentOrchestrator"/> delegiert an diese Factory, weil die konkreten
    /// Collector-Ctoren heavy Production-Deps (Event-Log-Watcher, WMI, Registry) haben — Tests
    /// liefern eine Fake-Factory, Prod liefert eine Default-Implementation, die die Tracker
    /// + Hosts baut. Nach PR #10 gibt es keinen <c>Action&lt;EnrollmentEvent&gt;</c>-Parameter
    /// mehr — alle Collectors posten via <paramref name="ingress"/> (Signal-Rail) statt
    /// direkt an den <see cref="Telemetry.Events.TelemetryEventEmitter"/>.
    /// </para>
    /// <para>
    /// <paramref name="whiteGloveSealingPatternIds"/> wird an den <c>ImeLogTrackerAdapter</c>
    /// durchgereicht (Sealing-Emission fire-once nur für diese Pattern-IDs). Leer / null
    /// = Feature off, M3-kompatibel. Plan §4.x M4.4.5.e.
    /// </para>
    /// </summary>
    public interface IComponentFactory
    {
        /// <summary>
        /// Baut alle Collector-Hosts. Jeder Host ist selbst dafür zuständig, seine
        /// <c>InformationalEventPost</c> aus (ingress, clock) zu konstruieren und Collector-Events
        /// als <c>InformationalEvent</c>-Signals über <paramref name="ingress"/> zu posten.
        /// <para>
        /// Returns a <see cref="CollectorSurfaces"/> bundle (hosts + typed read-model surfaces)
        /// instead of a bare host list (ARCH-F4): the factory holds no post-creation state and
        /// the orchestrator never needs to downcast this seam. Test fakes return
        /// <c>new CollectorSurfaces(hosts)</c>.
        /// </para>
        /// <para>
        /// <paramref name="telemetrySpool"/> flows into the <c>PeriodicCollectorLifecycleHost</c>
        /// → <c>AgentSelfMetricsCollector</c> so <c>agent_metrics_snapshot</c> can surface
        /// <c>spool.pendingItemCount</c> / <c>spool.fileSizeBytes</c>. Nullable — fakes and
        /// spool-less configurations pass null and the metrics fields are simply absent.
        /// </para>
        /// <para>
        /// <paramref name="timelineEvents"/> flows into the <c>GatherRuleExecutorHost</c> so
        /// <c>phase_change</c> / <c>phase_exit</c> / <c>on_event</c> gather triggers key off the
        /// ENGINE-REDUCED emitted timeline (post-reduce, RealmJoin gate respected) instead of raw
        /// pre-reduce signals — see <see cref="TimelineEventStream"/>. Nullable — fakes pass null
        /// and the host's phase/event triggers degrade off (startup + interval rules still run).
        /// </para>
        /// </summary>
        CollectorSurfaces CreateCollectorHosts(
            string sessionId,
            string tenantId,
            AgentLogger logger,
            IReadOnlyCollection<string> whiteGloveSealingPatternIds,
            ISignalIngressSink ingress,
            IClock clock,
            Transport.Telemetry.ITelemetrySpool? telemetrySpool,
            TimelineEventStream? timelineEvents = null);
    }
}

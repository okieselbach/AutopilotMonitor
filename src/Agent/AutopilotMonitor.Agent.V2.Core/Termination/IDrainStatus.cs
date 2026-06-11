#nullable enable
using System;

namespace AutopilotMonitor.Agent.V2.Core.Termination
{
    /// <summary>
    /// Drain observability for <see cref="EnrollmentTerminationHandler.DrainSpool"/> (ARCH-F2
    /// grouping — replaces the <c>pendingItemCountAccessor</c> /
    /// <c>ingressPendingSignalCountAccessor</c> / <c>spoolDrainPeriod</c> constructor
    /// parameters). Production implementation: <see cref="OrchestratorDrainStatus"/>.
    /// <para>
    /// The two <c>CanObserve*</c> capability flags replace the old "accessor is null" wiring
    /// variant: when neither surface is observable the handler keeps the legacy V1-parity
    /// blind delay for the full <see cref="SpoolDrainPeriod"/> instead of polling.
    /// </para>
    /// </summary>
    public interface IDrainStatus
    {
        /// <summary>
        /// Bounded budget shared by the two drain phases (ingress, then spool). V1 parity:
        /// production uses 10 s (up to 20 × 500 ms drains before shutdown.exe);
        /// <see cref="TimeSpan.Zero"/> disables draining entirely (test default).
        /// </summary>
        TimeSpan SpoolDrainPeriod { get; }

        /// <summary>
        /// Codex Finding 2 (2026-04-30): the termination handler is dispatched off the ingress
        /// worker, so it can wait for the worker to actually process the lifecycle events the
        /// handler posts (agent_shutting_down, whiteglove_part1_complete, analyzer events)
        /// BEFORE polling the spool — without this the spool-empty check would trivially fire
        /// on already-uploaded items while the just-posted events are still in the ingress
        /// channel. <c>false</c> = ingress not observable → phase skipped.
        /// </summary>
        bool CanObserveIngress { get; }

        /// <summary>
        /// Signals accepted by the ingress but not yet fully processed. Only read when
        /// <see cref="CanObserveIngress"/> is true; a throwing getter degrades to a bounded
        /// sleep inside the handler's poll loop.
        /// </summary>
        long IngressPendingSignalCount { get; }

        /// <summary>
        /// Option 1 (WG Part 1 graceful-exit hardening, 2026-04-30): when observable, the
        /// termination handler polls the spool's pending count during <c>DrainSpool</c> and
        /// exits the wait as soon as it returns 0 (spool fully acknowledged by the backend),
        /// falling back to the bounded <see cref="SpoolDrainPeriod"/> timeout if the spool
        /// never drains. <c>false</c> = legacy blind-delay behaviour.
        /// </summary>
        bool CanObserveSpool { get; }

        /// <summary>
        /// Spool items not yet acknowledged by the backend. Only read when
        /// <see cref="CanObserveSpool"/> is true.
        /// </summary>
        int SpoolPendingItemCount { get; }
    }
}

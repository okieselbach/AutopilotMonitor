#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Einzige Komponente mit Entscheidungs-relevanten Timern. Plan §2.6 / L.7 (Timer-Monopol).
    /// <para>
    /// Collectors / SignalAdapter / Orchestrator dürfen <b>keine</b> eigenen
    /// <c>System.Threading.Timer</c>-Instanzen für Session-Entscheidungen halten.
    /// <see cref="Schedule"/> und <see cref="Cancel"/> sind die einzige API.
    /// </para>
    /// </summary>
    public interface IDeadlineScheduler : IDisposable
    {
        /// <summary>
        /// Plant ein Deadline ein. Ist bereits eines mit gleichem <see cref="ActiveDeadline.Name"/>
        /// aktiv, wird es ersetzt (alter Timer gecancelt, neuer gestartet).
        /// </summary>
        void Schedule(ActiveDeadline deadline);

        /// <summary>
        /// Cancelt ein aktives Deadline. Unbekannter Name ist no-op.
        /// </summary>
        void Cancel(string name);

        /// <summary>
        /// True wenn Deadline <paramref name="name"/> aktuell eingeplant ist.
        /// </summary>
        bool IsScheduled(string name);

        /// <summary>
        /// Momentaufnahme aller aktiven Deadlines.
        /// </summary>
        IReadOnlyList<ActiveDeadline> ActiveDeadlines { get; }

        /// <summary>
        /// Wird vom Timer-Thread geraised, wenn ein Deadline fällig wird. Subscriber
        /// ist typischerweise der Orchestrator, der daraus ein synthetisches
        /// <c>DeadlineFired</c>-Signal mit <c>OccurredAtUtc = deadline.DueAtUtc</c> erzeugt; die
        /// Fälligkeit reist zusätzlich im Payload (<c>SignalPayloadKeys.DeadlineDueAtUtc</c>).
        /// </summary>
        event EventHandler<DeadlineFiredEventArgs>? Fired;

        /// <summary>
        /// Bulk-Rehydration nach Restart (Plan §2.6 Restart-Recovery).
        /// <c>remaining = DueAtUtc - clock.UtcNow</c>; bei <c>remaining ≤ 0</c> wird das Deadline
        /// sofort gefeuert, sonst ein neuer Timer gestartet. Alle Deadlines werden in einem
        /// einzigen Durchgang verarbeitet, Reihenfolge über Queue an ThreadPool — kein Dispatch
        /// aus dem aufrufenden Thread.
        /// </summary>
        void RehydrateFromSnapshot(IEnumerable<ActiveDeadline> deadlines);
    }
}

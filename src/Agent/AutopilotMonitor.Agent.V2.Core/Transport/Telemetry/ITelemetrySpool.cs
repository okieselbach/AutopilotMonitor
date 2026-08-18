#nullable enable
using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// Persistente Queue für <see cref="TelemetryItem"/>s. Plan §2.7a.
    /// <para>
    /// Invarianten:
    /// <list type="bullet">
    ///   <item><b>TelemetryItemId-Vergabe</b>: streng monoton, single-writer — <see cref="Enqueue"/> vergibt
    ///   den Cursor atomar unter Lock.</item>
    ///   <item><b>Sofort-Flush</b> (L.12): jeder erfolgreiche Enqueue steht auf Disk bevor die Methode zurückkehrt.</item>
    ///   <item><b>Idempotenz</b>: <see cref="MarkUploaded"/> mit kleinerer/gleicher ID als der bereits
    ///   persistierte Cursor ist no-op (keine Regression bei Doppel-Aufrufen).</item>
    /// </list>
    /// </para>
    /// </summary>
    public interface ITelemetrySpool
    {
        /// <summary>
        /// Persistiert <paramref name="draft"/>, vergibt <c>TelemetryItemId</c> (und — wenn
        /// <see cref="TelemetryItemDraft.IsSessionScoped"/> — <c>SessionTraceOrdinal</c>), gibt das fertige
        /// <see cref="TelemetryItem"/> zurück.
        /// </summary>
        TelemetryItem Enqueue(TelemetryItemDraft draft);

        /// <summary>
        /// Liest alle noch nicht als uploaded markierten Items in <c>TelemetryItemId</c>-Reihenfolge,
        /// maximal <paramref name="max"/> Stück. Liste kann leer sein.
        /// </summary>
        IReadOnlyList<TelemetryItem> Peek(int max);

        /// <summary>
        /// Markiert alle Items bis einschließlich <paramref name="upToItemIdInclusive"/> als hochgeladen.
        /// Monoton steigend — Aufrufe mit kleinerem Wert sind no-op (Idempotenz-Garantie).
        /// </summary>
        void MarkUploaded(long upToItemIdInclusive);

        /// <summary>Höchste bisher vergebene <c>TelemetryItemId</c>; -1 wenn Spool leer.</summary>
        long LastAssignedItemId { get; }

        /// <summary>Cursor: bis zu dieser ID inklusive wurde bereits erfolgreich hochgeladen; -1 initial.</summary>
        long LastUploadedItemId { get; }

        /// <summary>
        /// Number of items currently in the spool that have not yet been marked as uploaded.
        /// Reflects the in-memory tail cache; equals
        /// <c>LastAssignedItemId - LastUploadedItemId</c> in steady state.
        /// </summary>
        int PendingItemCount { get; }

        /// <summary>
        /// Highest <see cref="PendingItemCount"/> ever observed during this process lifetime
        /// (sticky high-water — never decreases). Useful as an early indicator of upload
        /// stalls on long sessions, where a steadily growing pending queue surfaces before
        /// an absolute size threshold trips.
        /// </summary>
        int PeakPendingItemCount { get; }

        /// <summary>
        /// Serialized on-disk byte size of the pending (not-yet-uploaded) tail — the actual
        /// upload backlog. Unlike <see cref="SpoolFileSizeBytes"/>, which grows append-only
        /// over the whole session regardless of upload progress, this shrinks back to 0 as
        /// <see cref="MarkUploaded"/> advances the cursor, so it is the correct input for
        /// back-pressure thresholds.
        /// </summary>
        long PendingBytes { get; }

        /// <summary>
        /// Current size of the on-disk <c>spool.jsonl</c> file in bytes. Returns 0 when the
        /// file does not yet exist. Cheap (single <c>FileInfo.Length</c> call) and safe to
        /// poll from periodic collectors. Append-only cumulative — informational only, NOT a
        /// backlog measure (use <see cref="PendingBytes"/> for that).
        /// </summary>
        long SpoolFileSizeBytes { get; }

        /// <summary>
        /// Raised after <see cref="Enqueue"/> persists an item whose <see cref="TelemetryItemDraft.RequiresImmediateFlush"/>
        /// was <c>true</c>. The orchestrator uses this to wake the drain loop early — without a
        /// wakeup, <c>ImmediateUpload=true</c> items wait for the full periodic drain interval
        /// (default 30 s) before reaching the backend, making the first burst of lifecycle events
        /// (<c>agent_started</c>, <c>enrollment_type_detected</c>, first <c>esp_phase_changed</c>, …)
        /// appear as a 30 s gap in the UI timeline.
        /// </summary>
        event EventHandler? ImmediateFlushRequested;
    }
}

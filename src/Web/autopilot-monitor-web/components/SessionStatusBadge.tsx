/**
 * Read-only session status pill. The single source for the inline copies that used to live in the dashboard
 * SessionTable and the geographic-performance sessions page. Pure presentational — colors mirror the backend
 * SessionStatus enum (Succeeded/Failed/InProgress/Pending/Stalled; anything else → Unknown). A "timed out"
 * failure gets a small ⏱️ affordance. When {@link adminMarkedAction} is set (an administrator manually flipped
 * the terminal state), a small "manual" badge is appended. When {@link reconcileReason} is set (the BACKEND
 * declared the success — timeout-sweep reconcile or late-completion upgrade), a small "reconciled" badge is
 * appended with the justification as tooltip — otherwise the bare pill is returned unchanged.
 */

const STATUS_CONFIG: Record<string, { color: string; text: string }> = {
  InProgress: { color: "bg-blue-100 text-blue-800 dark:bg-blue-900/40 dark:text-blue-300", text: "In Progress" },
  Pending: { color: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300", text: "Pending" },
  Stalled: { color: "bg-orange-100 text-orange-800 dark:bg-orange-900/40 dark:text-orange-300", text: "Stalled" },
  // Non-terminal: Device Setup done, waiting on the user / Account-Setup phase. Blue-ish "still going".
  AwaitingUser: { color: "bg-sky-100 text-sky-800 dark:bg-sky-900/40 dark:text-sky-300", text: "Awaiting User" },
  Succeeded: { color: "bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300", text: "Succeeded" },
  Failed: { color: "bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300", text: "Failed" },
  // Terminal but NOT a failure — no completion or explicit failure signal. Neutral slate, clearly not red.
  Incomplete: { color: "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300", text: "Incomplete" },
  Unknown: { color: "bg-gray-100 text-gray-800 dark:bg-gray-700 dark:text-gray-300", text: "Unknown" },
};

export function SessionStatusBadge({
  status,
  failureReason,
  adminMarkedAction,
  reconcileReason,
}: {
  status: string;
  failureReason?: string | null;
  /** Set when an admin manually marked the session ("Succeeded" | "Failed") — appends a "manual" badge. */
  adminMarkedAction?: string | null;
  /** Set when the backend declared the success (sweep reconcile / late-completion upgrade) — appends a "reconciled" badge. */
  reconcileReason?: string | null;
}) {
  const config = STATUS_CONFIG[status] || STATUS_CONFIG.Unknown;
  // The ⏱️ affordance marks the silence/timeout family: a "timed out" Failed, or any Incomplete
  // (which is, by definition, a session that went silent without a completion).
  const isTimeout =
    (status === "Failed" && !!failureReason?.toLowerCase().includes("timed out")) ||
    status === "Incomplete";

  const pill = (
    <span
      className={`px-2 inline-flex items-center gap-1 text-xs leading-5 font-semibold rounded-full ${config.color}`}
      title={failureReason || undefined}
    >
      {config.text}
      {isTimeout && <span title={failureReason || undefined} className="inline-flex items-center">&#9201;&#65039;</span>}
    </span>
  );

  // Backend-declared success: adminMarkedAction wins (the "manual" badge already attributes the
  // flip); a reconciled badge on top would double-attribute the same verdict.
  const showReconciled = !adminMarkedAction && status === "Succeeded" && !!reconcileReason;

  if (!adminMarkedAction && !showReconciled) return pill;

  return (
    <span className="inline-flex items-center gap-1.5">
      {pill}
      {adminMarkedAction ? (
        <span
          className="px-1.5 py-0.5 text-[10px] leading-4 font-semibold rounded border border-gray-300 bg-gray-50 text-gray-600 dark:border-gray-600 dark:bg-gray-700 dark:text-gray-300"
          title={`Manually marked as ${adminMarkedAction} by administrator`}
        >
          manual
        </span>
      ) : (
        <span
          className="px-1.5 py-0.5 text-[10px] leading-4 font-semibold rounded border border-sky-300 bg-sky-50 text-sky-700 dark:border-sky-700 dark:bg-sky-900/40 dark:text-sky-300"
          title={reconcileReason || undefined}
        >
          reconciled
        </span>
      )}
    </span>
  );
}

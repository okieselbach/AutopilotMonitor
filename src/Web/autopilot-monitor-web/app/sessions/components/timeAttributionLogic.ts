import type { OsUpdateSpan } from "@/lib/generated/wire-types.generated";
import type { OsUpdateOutcome } from "@/lib/osUpdateLive";

/** The words the Windows Update slice names an outcome with; "unknown" names nothing. */
const OUTCOME_LABELS: Record<OsUpdateOutcome, string | null> = {
  installed: "installed",
  failed: "failed",
  skipped: "skipped",
  unknown: null,
};

/**
 * What the OOBE quality update did, for the Windows Update slice: the outcome, the KBs that
 * reached "Installed", the ones it serviced without installing, and its restarts — e.g.
 * "failed · not installed: KB5124007 · 1 restart". The server computed every part; this only
 * joins the lists. Rows written before the outcome existed carry none and name no outcome.
 */
export function osUpdateDetail(osUpdates: ReadonlyArray<Partial<OsUpdateSpan>>): string {
  const outcomes = distinct(osUpdates.map(u => OUTCOME_LABELS[u.outcome as OsUpdateOutcome] ?? null));
  const installed = distinct(osUpdates.flatMap(u => u.kbs ?? []));
  const notInstalled = distinct(osUpdates.flatMap(u => u.notInstalledKbs ?? []));
  const restarts = osUpdates.reduce((sum, u) => sum + (u.rebootCount ?? 0), 0);

  return [
    outcomes.join(", "),
    installed.join(", "),
    notInstalled.length > 0 ? `not installed: ${notInstalled.join(", ")}` : "",
    restarts > 0 ? `${restarts} restart${restarts !== 1 ? "s" : ""}` : "",
  ].filter(Boolean).join(" · ");
}

function distinct(values: ReadonlyArray<string | null>): string[] {
  return Array.from(new Set(values.filter((v): v is string => !!v)));
}

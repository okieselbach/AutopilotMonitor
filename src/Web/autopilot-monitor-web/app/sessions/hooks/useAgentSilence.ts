"use client";

import { useEffect, useState } from "react";
import { isAgentSilent, isTerminalStatus, nextSilenceCheckMs } from "@/lib/sessionStatus";

const CHECK_INTERVAL_MS = 30_000;

/**
 * Whether the agent of an unfinished session has gone quiet, re-checked while the page stays open. Only
 * in a visible tab: a tab waking up has not caught up with the events yet and would flash "No data".
 * New data clears it at once, since the answer is derived from the latest ingest time at every render.
 */
export function useAgentSilence(status: string | null | undefined, lastIngestAtMs: number | null): boolean {
  const [checkedAtMs, setCheckedAtMs] = useState(() => Date.now());

  useEffect(() => {
    if (isTerminalStatus(status)) return;
    const id = setInterval(() => {
      if (document.visibilityState !== "visible") return;
      setCheckedAtMs(previous => nextSilenceCheckMs(previous, Date.now(), status, lastIngestAtMs));
    }, CHECK_INTERVAL_MS);
    return () => clearInterval(id);
  }, [status, lastIngestAtMs]);

  return isAgentSilent(status, lastIngestAtMs, checkedAtMs);
}

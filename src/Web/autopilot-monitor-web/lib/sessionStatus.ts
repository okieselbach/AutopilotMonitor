// Session status helpers. Mirror of the backend SessionStatus enum
// (src/Shared/AutopilotMonitor.Shared/Models/SessionApiModels.cs).
// Non-terminal: InProgress, Pending, Stalled, AwaitingUser, Unknown.
// Terminal: Succeeded, Failed, Incomplete.
//
// AwaitingUser (Device Setup done, user/Account-Setup phase pending) and Incomplete
// (terminal, non-failure — no completion or explicit failure was observed) come from the
// timeout reclassification, see tasks/enrollment-status-reclassification.md.

export function isTerminalStatus(status: string | null | undefined): boolean {
  return status === "Succeeded" || status === "Failed" || status === "Incomplete";
}

// How long an unfinished session may stay quiet before its rows stop claiming to be live. A live
// agent reports every 30 s while anything happens; its periodic collectors stop after 15 min without
// a real event, and an OS-update restart was quiet for 12 min in the field.
export const AGENT_SILENCE_THRESHOLD_MS = 20 * 60 * 1000;

/**
 * True when nothing has arrived from the agent of an unfinished session for the threshold. Measured
 * on the server clock: device timestamps can be hours off. A Stalled session counts without a
 * timestamp, the sweep has already seen the silence. An agent that reported the stall itself is
 * still talking, so its app row keeps its timer until it goes quiet too.
 */
export function isAgentSilent(
  status: string | null | undefined,
  lastIngestAtMs: number | null,
  nowMs: number,
): boolean {
  if (isTerminalStatus(status)) return false;
  if (lastIngestAtMs == null) return status === "Stalled";
  return nowMs - lastIngestAtMs >= AGENT_SILENCE_THRESHOLD_MS;
}

/** The clock value to keep after a check: the old one unless the answer flips, so a quiet page does not render. */
export function nextSilenceCheckMs(
  previousMs: number,
  nowMs: number,
  status: string | null | undefined,
  lastIngestAtMs: number | null,
): number {
  return isAgentSilent(status, lastIngestAtMs, previousMs) === isAgentSilent(status, lastIngestAtMs, nowMs)
    ? previousMs
    : nowMs;
}

const FROZEN_ROW_SUBJECT = { installing: "install", downloading: "download", running: "script" } as const;

/**
 * The words for a row frozen at the agent's last report. `agentSilent`: the session has not ended and
 * data may still come. Otherwise the session ended without the outcome.
 */
export function frozenRowText(activity: keyof typeof FROZEN_ROW_SUBJECT, agentSilent: boolean) {
  const subject = FROZEN_ROW_SUBJECT[activity];
  return agentSilent
    ? {
        label: "No data",
        countLabel: "no data",
        countTitle: `Still ${activity} at the agent's last report. No data has arrived since, so the current state is unknown. Not a failure.`,
        rowTitle: `Still ${activity} at the agent's last report. No data has arrived since. The ${subject} may well have finished.`,
      }
    : {
        label: "Incomplete",
        countLabel: "incomplete",
        countTitle: `Still ${activity} when the agent stopped observing — the outcome was never seen. Not a failure.`,
        rowTitle: `Still ${activity} when the agent stopped observing. The outcome is unknown — the ${subject} may well have finished after the session ended.`,
      };
}

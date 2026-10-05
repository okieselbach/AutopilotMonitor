import { osUpdateLiveState, type OsUpdateLiveFacts, type OsUpdateLiveState } from "@/lib/osUpdateLive";
import { osUpdateDetail } from "./timeAttributionLogic";

/** Silence after which the hint says when the device last reported (asleep or offline, it reports nothing). */
export const DEVICE_SILENCE_NOTE_MS = 15 * 60 * 1000;

export interface OsUpdateLiveHintText {
  state: OsUpdateLiveState;
  /** What is happening — announced when it changes, so it carries no clock. */
  phrase: string;
  /** The time right after the phrase; it ticks. */
  elapsed: string;
  /** The device is restarting for the update — announced when it starts. */
  restarting: boolean;
  /** What the update did, once it no longer runs: duration, outcome, KBs, restarts. */
  detail: string | null;
  /** When the device last reported, after a long silence. */
  silence: string | null;
}

/** Whole minutes, as the Enrollment Progress card writes durations: "<1m", "14m", "1h 5m". */
export function formatLiveDuration(ms: number): string {
  const minutes = Math.floor(Math.max(0, ms) / 60_000);
  if (minutes < 1) return "<1m";
  if (minutes < 60) return `${minutes}m`;
  const rest = minutes % 60;
  return rest === 0 ? `${Math.floor(minutes / 60)}h` : `${Math.floor(minutes / 60)}h ${rest}m`;
}

/**
 * The hint line for the OOBE quality update at `nowMs` (the device clock, see `deviceNowMs`), or
 * null when there is none: "Windows Update running · 14m", "Waiting for sign-in after the Windows
 * Update restart · 25m" with what the update did, "Windows Update failed after 25m".
 */
export function osUpdateLiveHintText(facts: OsUpdateLiveFacts, nowMs: number): OsUpdateLiveHintText | null {
  const state = osUpdateLiveState(facts, nowMs);
  if (state === null) return null;

  const { interval } = facts;
  const took = formatLiveDuration(interval.endMs - interval.startMs);
  const silentMs = facts.lastReportMs === null ? null : nowMs - facts.lastReportMs;
  const silence = silentMs !== null && silentMs >= DEVICE_SILENCE_NOTE_MS ? `last device report ${formatLiveDuration(silentMs)} ago` : null;

  switch (state) {
    case "updating":
      return {
        state,
        phrase: "Windows Update running",
        elapsed: ` · ${formatLiveDuration(nowMs - interval.startMs)}`,
        restarting: facts.restarting,
        detail: null,
        silence,
      };
    case "awaiting_sign_in":
      return {
        state,
        phrase: "Waiting for sign-in after the Windows Update restart",
        elapsed: ` · ${formatLiveDuration(nowMs - interval.endMs)}`,
        restarting: false,
        detail: [`update ${took}`, osUpdateDetail([interval])].filter(Boolean).join(" · "),
        silence,
      };
    case "ended":
      return {
        state,
        phrase: interval.outcome === "skipped" ? "Windows Update skipped" : "Windows Update failed",
        elapsed: ` after ${took}`,
        restarting: false,
        // The phrase names the outcome already; the KBs it left behind follow.
        detail: osUpdateDetail([{ kbs: interval.kbs, notInstalledKbs: interval.notInstalledKbs }]) || null,
        silence,
      };
  }
}

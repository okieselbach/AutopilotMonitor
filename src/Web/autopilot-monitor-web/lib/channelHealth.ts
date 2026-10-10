import { formatRelativeTime } from "@/lib/push/pushFormat";
import type { NotificationChannelHealthDto } from "@/lib/generated/wire-types.generated";

/**
 * Colour of a channel status; existing families only. No Tailwind class strings here — lib/ is
 * outside the Tailwind content globs, so the classes live in the ChannelEditor and key off the tone.
 */
export type ChannelHealthTone = "green" | "amber" | "red" | "gray";

/** What the channel editor shows for one channel's delivery status. */
export interface ChannelHealthView {
  tone: ChannelHealthTone;
  label: string;
  /** Hover text: when the channel last delivered and what failed last. */
  title: string;
  /** Guidance line under the card header — only while the channel is in error. */
  hint: string | null;
}

function shortDate(iso: string): string {
  const time = Date.parse(iso);
  return Number.isNaN(time) ? "" : new Date(time).toLocaleDateString(undefined, { month: "short", day: "numeric" });
}

/**
 * Maps the server's status (the one rule lives in the backend evaluator) onto label, colour and
 * guidance. `nowMs` is the time the status was fetched, so relative times stay pure.
 */
export function describeChannelHealth(health: NotificationChannelHealthDto, nowMs: number): ChannelHealthView {
  const lastDelivery = health.lastSuccessUtc
    ? `Last delivery ${formatRelativeTime(health.lastSuccessUtc, nowMs)}`
    : "Nothing delivered yet";
  const lastFailure = health.lastFailureUtc
    ? `last failure ${formatRelativeTime(health.lastFailureUtc, nowMs)}${health.lastError ? `: ${health.lastError}` : ""}`
    : null;
  const title = lastFailure ? `${lastDelivery}; ${lastFailure}.` : `${lastDelivery}.`;

  switch (health.status) {
    case "Ok":
      return { tone: "green", label: "Operating normally", title, hint: null };
    case "Degraded": {
      const rate = health.recentAttempts > 0 ? Math.round((health.recentFailures / health.recentAttempts) * 100) : 0;
      return {
        tone: "amber",
        label: `Failure rate ${rate}% (${health.recentFailures} of ${health.recentAttempts})`,
        title,
        hint: null,
      };
    }
    case "Failing": {
      const since = health.failingSinceUtc ? ` since ${shortDate(health.failingSinceUtc)}` : "";
      return {
        tone: "red",
        label: "Error",
        title,
        hint: `The last ${health.consecutiveFailures} deliveries failed${since}: ${health.lastError ?? "no answer"}. Fix the destination, then send a test.`,
      };
    }
    default:
      return {
        tone: "gray",
        label: "No deliveries yet",
        title: "Nothing was sent to this destination since it was last changed. Send a test to check it.",
        hint: null,
      };
  }
}

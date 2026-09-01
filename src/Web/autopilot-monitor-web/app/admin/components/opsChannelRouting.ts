import type { OpsAlertRule } from "../AdminConfigContext";
import type { NotificationChannel } from "@/app/settings/types";

/**
 * Rule → channel binding, kept out of the component because getting it wrong misroutes alerts
 * silently: an event reaching a channel it should not (a tenant-facing sales webhook receiving
 * security noise) looks exactly like a working configuration from the UI.
 *
 * Mirror of the backend's OpsAlertDispatchService.ResolveTargets — same "empty means all"
 * convention, so what the editor shows is what the dispatcher does.
 */

/**
 * Toggles one channel on a rule. An empty selection means "all channels", so the first explicit
 * pick starts from the EMPTY set rather than from everything — otherwise a rule could never be
 * narrowed, only widened. Ids of channels that no longer exist are pruned on every edit.
 */
export function toggleChannelBinding(
  rule: OpsAlertRule,
  channelId: string,
  channels: NotificationChannel[],
): string[] {
  const current = rule.notifyChannelIds ?? [];
  const next = current.includes(channelId)
    ? current.filter((id) => id !== channelId)
    : [...current, channelId];

  return next.filter((id) => channels.some((c) => c.id === id));
}

/**
 * The channels a rule currently reaches, for display. Empty binding = every enabled channel;
 * a binding whose ids all vanished reaches nothing (it must NOT fall back to broadcasting —
 * that is the backend's rule too).
 */
export function resolveRuleTargets(
  rule: OpsAlertRule,
  channels: NotificationChannel[],
): NotificationChannel[] {
  const enabled = channels.filter((c) => c.enabled);
  const ids = rule.notifyChannelIds ?? [];
  if (ids.length === 0) return enabled;
  return enabled.filter((c) => ids.includes(c.id));
}

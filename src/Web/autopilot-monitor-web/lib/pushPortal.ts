/**
 * Pure helpers of the portal side of the Web Push channel (plan push-relay): the provider
 * constant, the pairing-code grouping shown next to the QR code, the human names of the wire's
 * closed platform and status lists, and the "is a Push channel enabled" gate the hosts use to
 * decide whether the devices panel renders at all (K4: the channel is the one switch).
 *
 * No Tailwind class strings live here — lib/ is outside the Tailwind content globs, so the chip
 * classes stay in the component and key off {@link statusTone}.
 */

/** WebhookProviderType.Push — reserved for Push even if the channel were ever removed. */
export const PUSH_PROVIDER = 50;

/** Which route family a panel talks to: `push/…` (own tenant) or `global/push/…` (platform). */
export type PushScope = "tenant" | "platform";

/** The wire carries `status` as a string; these are its documented values (PushDeviceDto). */
export type PushDeviceStatus = "Pending" | "Active" | "Paused" | "Stale";

/** PairingStatusResponse.status values. */
export type PairingStatus = "Pending" | "Redeemed" | "Confirmed" | "Rejected" | "Expired";

/** Colour family of a status chip: green = delivering, orange = unclear, gray = idle. */
export type StatusTone = "green" | "orange" | "gray";

export function isPushProvider(providerType: number | undefined): boolean {
  return providerType === PUSH_PROVIDER;
}

/**
 * "ABCDEFGHJKM" → "ABCD-EFGH-JKM": groups of four for reading aloud or typing; the receiver
 * strips the dashes again (normalizePairingCode). Whitespace and existing dashes are ignored so
 * a pre-grouped code is not double-grouped.
 */
export function formatPairingCode(code: string): string {
  const raw = code.replace(/[\s-]+/g, "").toUpperCase();
  const groups: string[] = [];
  for (let i = 0; i < raw.length; i += 4) groups.push(raw.slice(i, i + 4));
  return groups.join("-");
}

/** Short human name of a receiver platform (the wire's closed list, K13); unknown → "Other". */
export function platformLabel(platform: string): string {
  switch (platform) {
    case "ios-homescreen":
      return "iPhone / iPad";
    case "android-chrome":
      return "Android";
    case "windows-chromium":
      return "Windows";
    case "macos-safari":
      return "Mac";
    case "firefox":
      return "Firefox";
    default:
      return "Other";
  }
}

/**
 * Same mapping as the receiver's DEVICE_STATUS_CHIP: Active = green; Pending/Stale = orange
 * (unresolved — a confirmation or opening the receiver app fixes it); Paused = gray (parked
 * until the owner signs in again, or the receiver re-subscribes after transport failures).
 */
export function statusTone(status: string): StatusTone {
  switch (status) {
    case "Active":
      return "green";
    case "Pending":
    case "Stale":
      return "orange";
    default:
      return "gray";
  }
}

/** The one-line explanation shown under a device's status chip. */
export function describePortalDeviceStatus(status: string): string {
  switch (status) {
    case "Active":
      return "Receives alerts.";
    case "Pending":
      return "Waiting for confirmation.";
    case "Paused":
      return "Paused — the owner has not signed in for a while.";
    case "Stale":
      return "The push service refused the last delivery; opening the app on the device repairs it.";
    default:
      return "";
  }
}

/** The minimal channel shape the gate needs (NotificationChannel and the ops channel list both fit). */
export interface PushChannelLike {
  providerType: number;
  enabled: boolean;
}

/**
 * True when the SAVED channel list carries an enabled Push channel. Accepts the parsed list or
 * the stored JSON string (TenantConfiguration.notificationChannelsJson /
 * AdminConfiguration.opsNotificationChannelsJson); a blank, malformed or redacted string is
 * "no channel", never an exception — the panel simply does not render.
 */
export function hasEnabledPushChannel(channels: readonly PushChannelLike[] | string | null | undefined): boolean {
  const list = typeof channels === "string" || channels == null ? parseChannelList(channels) : channels;
  return list.some((c) => c && c.enabled === true && isPushProvider(c.providerType));
}

function parseChannelList(json: string | null | undefined): PushChannelLike[] {
  if (!json || !json.trim()) return [];
  try {
    const parsed: unknown = JSON.parse(json);
    return Array.isArray(parsed) ? (parsed as PushChannelLike[]) : [];
  } catch {
    return [];
  }
}

/** How long a pairing code is valid (PushPairingService grant length, K7: ten minutes). */
export const PAIRING_GRANT_SECONDS = 600;

/**
 * Whole seconds of the grant still left, floored at 0, measured from the moment the create
 * response arrived on THIS clock — never from the server's `expiresUtc`, because a PC clock ten
 * minutes ahead would otherwise show "expired" at once. The server alone decides expiry; the
 * local zero is only a label. NaN input counts as run out.
 */
export function pairingSecondsLeft(receivedAtMs: number, nowMs: number): number {
  const elapsed = Math.floor((nowMs - receivedAtMs) / 1000);
  if (Number.isNaN(elapsed)) return 0;
  return Math.max(0, PAIRING_GRANT_SECONDS - Math.max(0, elapsed));
}

/** "9:58" style countdown for the pairing dialog. */
export function formatCountdown(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds));
  const minutes = Math.floor(s / 60);
  const rest = s % 60;
  return `${minutes}:${rest.toString().padStart(2, "0")}`;
}

/** The pairing link without its code fragment — the page the user types the code into. */
export function pairPageUrl(pairingUrl: string): string {
  const hash = pairingUrl.indexOf("#");
  return hash >= 0 ? pairingUrl.slice(0, hash) : pairingUrl;
}

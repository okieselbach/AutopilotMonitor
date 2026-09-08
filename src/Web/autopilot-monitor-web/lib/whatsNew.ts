/**
 * "What's new" — the portal's rendering of the two customer changelogs.
 *
 * Data: /whats-new.json, generated at build time from the docs repo by
 * scripts/whats-new/build-whats-new.js. The docs markdown stays the only authoritative
 * text; this module only types the payload and counts what a signed-in user has not seen
 * yet. Anonymous visitors on the public site get the panel without a counter: they have no
 * identity to attach a seen mark to, and a counter that reads 9+ for every first visit would
 * claim urgency nobody missed.
 */

export const WHATS_NEW_URL = "/whats-new.json";
export const WHATS_NEW_CHANNELS = ["platform", "agent"] as const;
export type WhatsNewChannel = (typeof WHATS_NEW_CHANNELS)[number];

/** First visit of a signed-in user (no mark yet): entries younger than this count as unseen. */
export const FIRST_VISIT_WINDOW_DAYS = 30;

export interface WhatsNewEntry {
  id: string;
  /** ISO timestamp — when the bullet was committed to the docs repo. */
  addedUtc: string;
  /** The `## <Period>` heading the bullet lives under, e.g. "September 2026". */
  period: string;
  /** Platform bullets carry a bold lead; agent bullets are a single sentence (null). */
  title: string | null;
  /** Inline markdown: **bold**, `code`, [label](https://…). */
  body: string;
  /** First docs link of the bullet — the "Read update" target — or null. */
  link: string | null;
}

export interface WhatsNewChannelData {
  docsUrl: string;
  /**
   * Period blocks in the order the changelog lists them; inside one block newest first,
   * ties in authored order. The generator guarantees this — the panel renders the array as
   * it comes, so an entry the badge counts is always at the top of its period.
   */
  entries: WhatsNewEntry[];
}

export interface WhatsNewPayload {
  schemaVersion: 1;
  generatedUtc: string;
  docsCommit: string | null;
  channels: Record<WhatsNewChannel, WhatsNewChannelData>;
}

/** ISO timestamp per channel of the newest entry the user has seen; null = never. */
export type WhatsNewSeen = Record<WhatsNewChannel, string | null>;

export const NEVER_SEEN: WhatsNewSeen = { platform: null, agent: null };

// ── Payload guard ────────────────────────────────────────────

function isEntry(value: unknown): value is WhatsNewEntry {
  if (!value || typeof value !== "object") return false;
  const e = value as Record<string, unknown>;
  return (
    typeof e.id === "string" &&
    typeof e.addedUtc === "string" &&
    !Number.isNaN(Date.parse(e.addedUtc)) &&
    typeof e.period === "string" &&
    (e.title === null || typeof e.title === "string") &&
    typeof e.body === "string" &&
    (e.link === null || typeof e.link === "string")
  );
}

function isChannel(value: unknown): value is WhatsNewChannelData {
  if (!value || typeof value !== "object") return false;
  const c = value as Record<string, unknown>;
  return (
    typeof c.docsUrl === "string" &&
    Array.isArray(c.entries) &&
    c.entries.every(isEntry)
  );
}

/**
 * Validates the fetched JSON. Anything off-shape yields null — the panel then shows
 * its docs links instead of half-rendered content, and no badge is computed from it.
 */
export function parseWhatsNewPayload(value: unknown): WhatsNewPayload | null {
  if (!value || typeof value !== "object") return null;
  const p = value as Record<string, unknown>;
  if (p.schemaVersion !== 1) return null;
  if (typeof p.generatedUtc !== "string") return null;
  if (!(p.docsCommit === null || typeof p.docsCommit === "string")) return null;
  const channels = p.channels as Record<string, unknown> | undefined;
  if (!channels || typeof channels !== "object") return null;
  for (const name of WHATS_NEW_CHANNELS) {
    if (!isChannel(channels[name])) return null;
  }
  return value as WhatsNewPayload;
}

// ── Unseen logic ─────────────────────────────────────────────

/**
 * Entries newer than the seen mark. Without a mark (first visit, or a browser before the
 * backend deploy) the last FIRST_VISIT_WINDOW_DAYS count — bounded, but enough to show
 * how often the platform moves.
 */
export function unseenEntries(entries: WhatsNewEntry[], seenUtc: string | null, now: Date = new Date()): WhatsNewEntry[] {
  const threshold =
    seenUtc !== null && !Number.isNaN(Date.parse(seenUtc))
      ? Date.parse(seenUtc)
      : now.getTime() - FIRST_VISIT_WINDOW_DAYS * 86_400_000;
  return entries.filter(e => Date.parse(e.addedUtc) > threshold);
}

export function countUnseen(entries: WhatsNewEntry[], seenUtc: string | null, now: Date = new Date()): number {
  return unseenEntries(entries, seenUtc, now).length;
}

/**
 * The mark to store once a channel has been viewed: the newest `addedUtc` among the
 * LOADED entries — not `now`. A bullet committed before the web deploy that carried
 * it would otherwise be marked seen without ever having been shown.
 */
export function nextSeenMark(entries: WhatsNewEntry[]): string | null {
  let max: string | null = null;
  for (const e of entries) {
    if (max === null || Date.parse(e.addedUtc) > Date.parse(max)) max = e.addedUtc;
  }
  return max;
}

/** Later of two ISO timestamps; null-safe. Keeps a mark monotonic across tabs. */
export function laterMark(a: string | null, b: string | null): string | null {
  if (a === null) return b;
  if (b === null) return a;
  return Date.parse(b) > Date.parse(a) ? b : a;
}

/** "9+" cap identical to the notification bell. */
export function formatBadgeCount(n: number): string {
  return n > 9 ? "9+" : String(n);
}

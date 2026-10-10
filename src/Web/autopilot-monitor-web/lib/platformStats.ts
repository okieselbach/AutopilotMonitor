import type { PlatformStats } from "@/lib/generated/wire-types.generated";

/**
 * The landing page's platform stats. A public blob container holds a manifest
 * (`platform-stats.json`, rewritten by the backend maintenance run every 2 hours) that names the
 * versioned file with the numbers (`latest`, relative to the manifest).
 *
 * Read twice: once by `next build`, which bakes the numbers of that moment into the static HTML
 * (crawlers and LLM fetchers run no JS and would otherwise see only a skeleton), and again by
 * every browser, which swaps in the live numbers (components/landing/StatsBand.tsx). Both reads
 * share the URL, fetch and formatting rules here.
 */

interface PlatformStatsManifest {
  latest: string;
  generatedAtUtc: string;
}

/**
 * The versioned file. The backend writes it field by field under the names of the wire type
 * PlatformStats; every field stays optional because the body is parsed, not trusted.
 */
export type PlatformStatsPayload = Partial<PlatformStats>;

export interface StatItem {
  label: string;
  value: string;
}

/** The numbers baked into the static HTML at build time. */
export interface PlatformStatsSnapshot {
  items: StatItem[];
  /** When the backend computed them (the payload's lastUpdated): page marker and build log. */
  asOf: string | null;
}

export function resolvePlatformStatsManifestUrl(rawUrl?: string): string {
  const trimmed = rawUrl?.trim();
  if (!trimmed) {
    return "/platform-stats.json";
  }

  const hashIndex = trimmed.indexOf("#");
  const withoutHash = hashIndex >= 0 ? trimmed.slice(0, hashIndex) : trimmed;
  const hash = hashIndex >= 0 ? trimmed.slice(hashIndex) : "";

  const queryIndex = withoutHash.indexOf("?");
  const basePath = queryIndex >= 0 ? withoutHash.slice(0, queryIndex) : withoutHash;
  const query = queryIndex >= 0 ? withoutHash.slice(queryIndex) : "";

  if (/\.json$/i.test(basePath)) {
    return trimmed;
  }

  const normalizedBasePath = basePath.endsWith("/") ? basePath.slice(0, -1) : basePath;
  const manifestPath = `${normalizedBasePath}/platform-stats.json`;
  return `${manifestPath}${query}${hash}`;
}

export const PLATFORM_STATS_MANIFEST_URL =
  resolvePlatformStatsManifestUrl(process.env.NEXT_PUBLIC_PLATFORM_STATS_MANIFEST_URL);

/** Budget for both build-time requests together: a slow blob must not stall the deploy. */
const BUILD_FETCH_TIMEOUT_MS = 10_000;

/** 8,341,206 → "8.3M"; smaller values keep their grouped form. */
function compact(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  return n.toLocaleString("en-US");
}

function grouped(n: number): string {
  return n.toLocaleString("en-US");
}

/** The band's figures in display order. */
const STAT_FIELDS: { field: keyof PlatformStats; label: string; format: (n: number) => string }[] = [
  { field: "totalEnrollments", label: "enrollments monitored", format: grouped },
  { field: "issuesDetected", label: "issues detected", format: grouped },
  { field: "totalSignedUpTenants", label: "organisations", format: grouped },
  { field: "uniqueDeviceModels", label: "device models", format: grouped },
  { field: "totalEventsProcessed", label: "events processed", format: compact },
];

/**
 * The band's items. Only finite positive numbers count: the values end up in the static HTML,
 * and a zero means "not computed yet", not a figure to publish.
 */
export function toStatItems(payload: PlatformStatsPayload): StatItem[] {
  return STAT_FIELDS.flatMap(({ field, label, format }) => {
    const value = payload[field];
    return typeof value === "number" && Number.isFinite(value) && value > 0
      ? [{ label, value: format(value) }]
      : [];
  });
}

/** One read: the numbers and the versioned file they came from (named in the build log). */
interface PlatformStatsRead {
  payload: PlatformStatsPayload;
  file: string;
}

/**
 * Manifest, then the file it names. Throws with the reason; the callers decide what to report.
 * Each hop takes its own request options: the manifest changes with every publish, a versioned
 * file never does.
 */
async function readPlatformStats(
  manifestUrl: string,
  manifestInit: RequestInit,
  payloadInit: RequestInit = manifestInit,
): Promise<PlatformStatsRead> {
  const manifestResponse = await fetch(manifestUrl, manifestInit);
  if (!manifestResponse.ok) throw new Error(`${manifestUrl} answered HTTP ${manifestResponse.status}`);
  const manifest = (await manifestResponse.json()) as Partial<PlatformStatsManifest> | null;
  if (typeof manifest?.latest !== "string" || !manifest.latest) {
    throw new Error(`${manifestUrl} names no versioned file`);
  }

  // Relative to the manifest as served (after redirects); a hand-built Response has no url.
  const base = manifestResponse.url || manifestUrl;
  const payloadUrl = new URL(manifest.latest, base);
  if (payloadUrl.origin !== new URL(base).origin) {
    throw new Error(`${manifestUrl} names a file on another origin`);
  }

  const payloadResponse = await fetch(payloadUrl.toString(), payloadInit);
  if (!payloadResponse.ok) throw new Error(`${payloadUrl} answered HTTP ${payloadResponse.status}`);
  const payload: unknown = await payloadResponse.json();
  if (typeof payload !== "object" || payload === null) throw new Error(`${payloadUrl} holds no stats object`);
  return { payload: payload as PlatformStatsPayload, file: manifest.latest };
}

/**
 * The browser's read. The manifest is revalidated on every load; the versioned file it names
 * comes from the browser cache once fetched: the backend writes a new name per publish and serves
 * it immutable (MaintenanceService.PlatformStatsVersionedFileName).
 */
export async function fetchLivePlatformStats(
  manifestUrl: string = PLATFORM_STATS_MANIFEST_URL,
): Promise<PlatformStatsPayload | null> {
  try {
    return (await readPlatformStats(manifestUrl, { cache: "no-cache" }, { cache: "default" })).payload;
  } catch {
    // The band is decorative: a failed read only means no live numbers.
    return null;
  }
}

/**
 * The build's read (app/page.tsx), once per `next build`. Only for an absolute URL: local and CI
 * builds have none (the relative fallback exists only in a browser) and bake nothing.
 * `no-store` keeps the read out of Next's fetch cache, which would otherwise hand the next build
 * on the same machine the previous numbers; the page is `force-static`, so `no-store` stays
 * legal in the static export. Fail-soft: without a snapshot the page ships the skeleton and
 * browsers load the numbers live, as before; deploy-web.yml reports it as a warning.
 */
export async function loadPlatformStatsSnapshot(
  manifestUrl: string = PLATFORM_STATS_MANIFEST_URL,
): Promise<PlatformStatsSnapshot | null> {
  if (!/^https?:\/\//i.test(manifestUrl)) return null;
  try {
    const { payload, file } = await readPlatformStats(manifestUrl, {
      cache: "no-store",
      signal: AbortSignal.timeout(BUILD_FETCH_TIMEOUT_MS),
    });
    const items = toStatItems(payload);
    if (items.length === 0) throw new Error(`${file} holds no positive figure`);
    const asOf = typeof payload.lastUpdated === "string" ? payload.lastUpdated : null;
    console.log(`Platform stats baked into the landing page from ${file} (as of ${asOf ?? "unknown"}).`);
    return { items, asOf };
  } catch (err) {
    // Node's fetch reports every network failure as "fetch failed" and keeps the reason in `cause`.
    const cause = err instanceof Error && err.cause instanceof Error ? ` (${err.cause.message})` : "";
    const reason = err instanceof Error ? `${err.message}${cause}` : String(err);
    console.warn(`Platform stats not baked into the landing page; browsers still load them live. ${reason}`);
    return null;
  }
}

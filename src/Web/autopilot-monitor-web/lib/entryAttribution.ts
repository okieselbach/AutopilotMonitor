/**
 * The channel a page load on the public host came in through: the referrer's host and the UTM
 * tags of the URL the document was loaded with. Both stay the same for the whole page load —
 * a client-side navigation changes neither `document.referrer` nor the navigation entry — so
 * nothing is stored. Stamped on page views and custom events, counted per channel, never per
 * person (D-304, D-305).
 */

export type EntryAttribution = Readonly<Record<string, string>>;

/** Only these three tags: `utm_content` and `utm_term` are where mail tools put recipient ids. */
const UTM_PROPERTIES = [
  ["utm_source", "entryUtmSource"],
  ["utm_medium", "entryUtmMedium"],
  ["utm_campaign", "entryUtmCampaign"],
] as const;

const UTM_VALUE = /^[a-z0-9][a-z0-9._-]{0,63}$/;
const MAX_REFERRER_HOST = 100;

/** `baseType` of the SDK items that carry the entry: page views and custom events. */
const STAMPED_BASE_TYPES: readonly string[] = ["PageviewData", "EventData"];

/** The referrer's host only, never its path or query; `direct` without a referrer. */
function referrerHost(referrer: string): string {
  if (!referrer) return "direct";
  try {
    const host = new URL(referrer).host.toLowerCase();
    return host ? host.slice(0, MAX_REFERRER_HOST) : "unknown";
  } catch {
    return "unknown";
  }
}

export function entryAttribution(referrer: string, documentUrl: string): EntryAttribution {
  const entry: Record<string, string> = { entryReferrer: referrerHost(referrer) };
  let params: URLSearchParams;
  try {
    params = new URL(documentUrl).searchParams;
  } catch {
    return entry;
  }
  for (const [param, property] of UTM_PROPERTIES) {
    const value = params.get(param);
    if (value === null) continue;
    const normalized = value.trim().toLowerCase();
    entry[property] = UTM_VALUE.test(normalized) ? normalized : "other";
  }
  return entry;
}

/** This page load's entry, read in the browser. */
export function readEntryAttribution(): EntryAttribution {
  const navigation = performance.getEntriesByType("navigation")[0];
  return entryAttribution(document.referrer, navigation?.name || window.location.href);
}

/** The fields of an ITelemetryItem the stamp reads and writes. */
interface EnvelopeLike {
  baseType?: string;
  data?: Record<string, unknown>;
}

/**
 * For `addTelemetryInitializer`: adds the entry to a page view or custom event, in place. Other
 * item kinds are left alone. Returns true when it stamped, for the tests — the initializer must
 * not pass it on, a returned false drops the item.
 */
export function stampEntryAttribution(envelope: EnvelopeLike, entry: EntryAttribution): boolean {
  if (!envelope.baseType || !STAMPED_BASE_TYPES.includes(envelope.baseType)) return false;
  envelope.data = Object.assign(envelope.data ?? {}, entry);
  return true;
}

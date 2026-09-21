import { GRAPH_URL } from "@/utils/config";

/**
 * Loads the signed-in user's Entra ID profile photo straight from Microsoft Graph.
 * The photo stays in the browser: it is never sent to the Autopilot Monitor backend
 * (the privacy page states this — keep both in sync).
 *
 * Purely decorative, so every failure resolves to null and the caller falls back to
 * initials. It must never prompt, redirect or throw.
 */

// 64px covers the 28/32px avatar circles on high-density displays.
const PHOTO_URL = `${GRAPH_URL}/v1.0/me/photos/64x64/$value`;

const STORAGE_PREFIX = "apm.userPhoto.";
/** Stored when Graph answered 404, so an account without a photo is asked once per tab. */
const NO_PHOTO = "none";

type PhotoStorage = Pick<Storage, "getItem" | "setItem">;

const loads = new Map<string, Promise<string | null>>();

function browserStorage(): PhotoStorage | null {
  try {
    return typeof window === "undefined" ? null : window.sessionStorage;
  } catch {
    return null;
  }
}

function toDataUrl(contentType: string, bytes: Uint8Array): string {
  let binary = "";
  for (let i = 0; i < bytes.length; i += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  }
  return `data:${contentType};base64,${btoa(binary)}`;
}

async function fetchPhoto(
  storageKey: string,
  acquireGraphToken: () => Promise<string>,
  storage: PhotoStorage | null,
): Promise<string | null> {
  const token = await acquireGraphToken();
  const response = await fetch(PHOTO_URL, { headers: { Authorization: `Bearer ${token}` } });

  if (response.status === 404) {
    try { storage?.setItem(storageKey, NO_PHOTO); } catch { /* quota/blocked — ask again next load */ }
    return null;
  }
  const contentType = response.headers.get("Content-Type") ?? "";
  if (!response.ok || !contentType.startsWith("image/")) return null;

  const dataUrl = toDataUrl(contentType, new Uint8Array(await response.arrayBuffer()));
  try { storage?.setItem(storageKey, dataUrl); } catch { /* quota/blocked — ask again next load */ }
  return dataUrl;
}

/**
 * Resolves to a data: URL, or null when the account has no photo or anything failed.
 * One Graph request per account and tab: concurrent callers share the in-flight load,
 * the result (including "no photo") is kept in sessionStorage across reloads.
 */
export function loadUserPhoto(
  accountId: string,
  acquireGraphToken: () => Promise<string>,
  storage: PhotoStorage | null = browserStorage(),
): Promise<string | null> {
  const existing = loads.get(accountId);
  if (existing) return existing;

  const storageKey = STORAGE_PREFIX + accountId;
  let cached: string | null = null;
  try { cached = storage?.getItem(storageKey) ?? null; } catch { /* blocked storage — fetch */ }

  const usable = cached === NO_PHOTO || (cached?.startsWith("data:image/") ?? false);

  const load = usable
    ? Promise.resolve(cached === NO_PHOTO ? null : cached)
    : fetchPhoto(storageKey, acquireGraphToken, storage).catch(() => {
        // Transient (token, network, CSP): do not pin the failure for the page's lifetime.
        loads.delete(accountId);
        return null;
      });

  loads.set(accountId, load);
  return load;
}

export function __resetUserPhotoForTests(): void {
  loads.clear();
}

import { asGuidOrUndefined } from "@/lib/inputValidation";
import type { TenantRole } from "@/types/auth";

/**
 * "View as tenant": a Global Admin opens one tenant's portal exactly as a member of that tenant sees it,
 * read-only, in this tab only. `?viewAs=<tenant id>` (optional `&viewAsRole=Operator|Viewer`) arms it. The
 * selection lives in sessionStorage — it survives the sign-in redirect of a new tab and dies with the tab —
 * and every API call carries it as two headers. The backend admits them only for a Global Admin, answers as
 * the emulated member and refuses every write; that is the whole enforcement. Leaving reloads the page, so no
 * cached response of the viewed tenant outlives the mode (the per-tab caches are keyed by URL alone).
 */

export const VIEW_AS_PARAM = "viewAs";
export const VIEW_AS_ROLE_PARAM = "viewAsRole";

const STORAGE_KEY = "am_view_as";
/** One-shot reason a refused view-as ended with, shown once after the reload. */
const NOTICE_KEY = "am_view_as_notice";

const ROLES: readonly TenantRole[] = ["Admin", "Operator", "Viewer"];

export interface ViewAsSelection {
  /** The viewed tenant (lowercase GUID). */
  tenantId: string;
  role: TenantRole;
}

function parseRole(value: string | null | undefined): TenantRole | null {
  if (!value) return "Admin";
  const wanted = value.trim().toLowerCase();
  return ROLES.find((role) => role.toLowerCase() === wanted) ?? null;
}

function parseSelection(tenantValue: string | null | undefined, roleValue: string | null | undefined): ViewAsSelection | null {
  const tenantId = asGuidOrUndefined(tenantValue);
  const role = parseRole(roleValue);
  return tenantId && role ? { tenantId: tenantId.toLowerCase(), role } : null;
}

/** The selection a query string asks for; null when it names none or an unusable one. */
export function readViewAsParams(search: string): ViewAsSelection | null {
  const params = new URLSearchParams(search);
  return parseSelection(params.get(VIEW_AS_PARAM), params.get(VIEW_AS_ROLE_PARAM));
}

// The selection is fixed for the life of the page (arming and leaving both reload), so one read serves
// every request; undefined = not read yet.
let cached: ViewAsSelection | null | undefined;

/** This tab's view-as selection, or null on an ordinary tab. */
export function readViewAs(): ViewAsSelection | null {
  if (cached !== undefined) return cached;
  if (typeof window === "undefined") return null;
  try {
    const stored = JSON.parse(window.sessionStorage.getItem(STORAGE_KEY) ?? "null") as Partial<ViewAsSelection> | null;
    cached = stored ? parseSelection(stored.tenantId, stored.role) : null;
  } catch {
    cached = null;
  }
  return cached;
}

/** Reads `?viewAs=` into this tab's storage. Call before the first auth/me can fire. */
export function captureViewAsFromUrl(): void {
  if (typeof window === "undefined") return;
  const selection = readViewAsParams(window.location.search);
  if (!selection) return;
  try {
    window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify(selection));
    cached = selection;
  } catch {
    // Storage unavailable: the tab stays an ordinary one.
  }
}

/** The address bar without the view-as parameters, or null when it carries none. */
export function stripViewAsParams(pathWithQuery: string): string | null {
  const url = new URL(pathWithQuery, "https://portal.invalid");
  if (!url.searchParams.has(VIEW_AS_PARAM) && !url.searchParams.has(VIEW_AS_ROLE_PARAM)) return null;
  url.searchParams.delete(VIEW_AS_PARAM);
  url.searchParams.delete(VIEW_AS_ROLE_PARAM);
  return url.pathname + url.search + url.hash;
}

/** The headers every API call of a view-as tab carries; none on an ordinary tab. */
export function viewAsHeaders(): Record<string, string> {
  const selection = readViewAs();
  return selection ? { "X-View-As-Tenant": selection.tenantId, "X-View-As-Role": selection.role } : {};
}

/**
 * How far the view-as bar (components/ViewAsBar.tsx) pushes the navbar down right now, in px; 0 while none is
 * shown. CSS offsets add --view-as-offset (globals.css); offsets computed in JS add this.
 */
export function viewAsBarHeight(): number {
  return document.querySelector<HTMLElement>("[data-view-as-bar]")?.offsetHeight ?? 0;
}

function clearViewAs(): void {
  cached = null;
  try {
    window.sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    // Nothing stored that could be removed.
  }
}

/** Leaves view-as: this tab becomes an ordinary one on the same page. */
export function exitViewAs(): void {
  clearViewAs();
  window.location.assign(window.location.pathname);
}

/** The backend refused the view-as itself: leave it and say why once, after the reload. */
export function abandonViewAs(reason: string): void {
  clearViewAs();
  try {
    window.sessionStorage.setItem(NOTICE_KEY, reason);
  } catch {
    // The reason is lost; the tab still returns to the ordinary view.
  }
  window.location.reload();
}

/** The reason the last view-as of this tab was refused, handed out once. */
export function takeViewAsNotice(): string | null {
  try {
    const notice = window.sessionStorage.getItem(NOTICE_KEY);
    if (notice !== null) window.sessionStorage.removeItem(NOTICE_KEY);
    return notice;
  } catch {
    return null;
  }
}

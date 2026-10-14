import type { Route } from "next";
import type { GetAppFleetContextResponse } from "@/lib/generated/wire-types.generated";
import { appDetailUrl } from "@/lib/routes";
import { humanizeKey } from "@/lib/evidenceChips";

/** The fleet tile of "Around the failure". */
export interface FleetView {
  title: string;
  detail: string;
  /** The app's page, filtered to the same window; null when that page would show another tenant. */
  href: Route | null;
}

// Failure types the agent writes instead of an error code when the ESP ended the install.
const FAILURE_TYPES: Record<string, string> = {
  esp_apps_timeout: "ESP timeout",
  esp_apps_detection_failure: "detection failure",
  esp_apps_install_failure: "install failure",
};

function codeText(code: string): string {
  return FAILURE_TYPES[code] ?? (/^[a-z]+(_[a-z]+)+$/.test(code) ? humanizeKey(code).toLowerCase() : code);
}

/**
 * The fleet tile, or null when no other enrollment of the window had the app. The read behind it
 * fails soft, so an empty answer may be a failed read: the page never claims "nowhere else".
 */
export function fleetView(context: GetAppFleetContextResponse | null, href: Route | null): FleetView | null {
  if (!context) return null;
  const { installed, skipped, failed, incomplete, inProgress } = context;
  if (installed + skipped + failed + incomplete + inProgress === 0) return null;

  const codes = context.topFailureCodes
    .slice(0, 2)
    .map((c) => `${codeText(c.code)}${c.count > 1 ? ` ×${c.count}` : ""}`)
    .join(", ");
  const parts = [
    failed > 0 && `${failed} failed${codes ? ` (${codes})` : ""}`,
    installed > 0 && `${installed} installed`,
    inProgress > 0 && `${inProgress} still installing`,
    incomplete > 0 && `${incomplete} not finished`,
    skipped > 0 && `${skipped} skipped`,
  ].filter(Boolean);
  return {
    title: "Same app in other enrollments",
    detail: `Last ${context.windowDays} days: ${parts.join(" · ")}`,
    href,
  };
}

/**
 * The app page's link for the fleet tile when that page shows the viewed tenant: it follows
 * `tenantId` only for a Global Admin or Reader in Global Admin mode, shows a member the own tenant,
 * and ignores the URL for a delegated (MSP) scope.
 */
export function fleetSessionsHref(args: {
  appName: string;
  source?: string;
  windowDays: number;
  viewedTenantId: string;
  ownTenantId: string | null | undefined;
  globalAdminMode: boolean;
  delegatedScope: boolean;
}): Route | null {
  if (args.delegatedScope || !args.viewedTenantId) return null;
  const days = String(args.windowDays);
  if (args.globalAdminMode) return appDetailUrl(args.appName, { days, source: args.source, tenantId: args.viewedTenantId });
  const own = !!args.ownTenantId && args.ownTenantId.toLowerCase() === args.viewedTenantId.toLowerCase();
  return own ? appDetailUrl(args.appName, { days, source: args.source }) : null;
}

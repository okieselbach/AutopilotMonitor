import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { API_URL_PROD, BLOB_URL_PROD, DOCS_URL, ENTRA_LOGIN_URL } from "../config";

/**
 * Guards staticwebapp.config.json — since the static export it carries the
 * production redirects, the legacy path rewrites, and ALL security headers.
 * The hardcodedUrls guard only scans .ts/.tsx, so the JSON needs its own
 * consistency test:
 *  (a) every absolute own/Microsoft host in it comes from the utils/config.ts
 *      registry (no drift when a host migrates),
 *  (b) the CSP never ships 'unsafe-eval' (dev-only concession),
 *  (c) route order: /docs specifics before the /docs/* catch-all, and each
 *      exact detail/inspector rewrite before its family wildcard (SWA matches
 *      in array order — a wrong order silently shadows routes).
 */

interface SwaRoute {
  route: string;
  redirect?: string;
  rewrite?: string;
  serve?: string;
  statusCode?: number;
}

interface SwaConfig {
  routes: SwaRoute[];
  globalHeaders: Record<string, string>;
  responseOverrides?: Record<string, { rewrite: string }>;
}

const CONFIG_PATH = join(__dirname, "..", "..", "staticwebapp.config.json");
const config = JSON.parse(readFileSync(CONFIG_PATH, "utf-8")) as SwaConfig;

// Fixed third-party hosts that legitimately appear in the CSP but have no
// registry entry (they are not "our" hosts and never migrate with us).
const ALLOWED_THIRD_PARTY_HOSTS = [
  "*.tile.openstreetmap.org",
  "*.service.signalr.net",
  "js.monitor.azure.com",
  "*.in.applicationinsights.azure.com",
];

const REGISTRY_HOSTS = [DOCS_URL, API_URL_PROD, BLOB_URL_PROD, ENTRA_LOGIN_URL].map(
  (u) => new URL(u).host,
);

describe("staticwebapp.config.json guard", () => {
  it("every absolute host is registry-backed or an allowed third party", () => {
    const raw = readFileSync(CONFIG_PATH, "utf-8");
    const hosts = [...raw.matchAll(/(?:https?|wss):\/\/([^/\s'";]+)/g)].map((m) => m[1]);
    const violations = hosts.filter(
      (h) => !REGISTRY_HOSTS.includes(h) && !ALLOWED_THIRD_PARTY_HOSTS.includes(h),
    );
    expect(violations, `Unregistered host(s) in staticwebapp.config.json: ${violations.join(", ")}`).toEqual([]);
  });

  it("CSP has no unsafe-eval", () => {
    expect(config.globalHeaders["Content-Security-Policy"]).not.toContain("unsafe-eval");
  });

  it("ships all five security headers", () => {
    for (const h of [
      "X-Content-Type-Options",
      "Permissions-Policy",
      "X-Frame-Options",
      "Referrer-Policy",
      "Content-Security-Policy",
    ]) {
      expect(config.globalHeaders[h], `missing header ${h}`).toBeTruthy();
    }
  });

  it("docs specifics come before the /docs/* catch-all", () => {
    const routes = config.routes.map((r) => r.route);
    const catchAll = routes.indexOf("/docs/*");
    expect(catchAll).toBeGreaterThan(-1);
    for (const r of routes.filter((x) => x.startsWith("/docs/") && x !== "/docs/*")) {
      expect(routes.indexOf(r), `${r} must precede /docs/*`).toBeLessThan(catchAll);
    }
  });

  it("exact list/detail/inspector rewrites precede their family wildcard", () => {
    const routes = config.routes.map((r) => r.route);
    // The base LIST routes must be exact entries BEFORE the wildcard: SWA's
    // trailingSlash:auto normalizes /apps to /apps/, which the /apps/* wildcard
    // matches — without the exact entry the list page serves the DETAIL html
    // (production incident 2026-07-29: /apps rendered the app-detail view).
    const pairs: Array<[string, string]> = [
      ["/sessions", "/sessions/*"],
      ["/sessions/inspector", "/sessions/*"],
      ["/diagnosis", "/diagnosis/*"],
      ["/apps", "/apps/*"],
      ["/apps/detail", "/apps/*"],
      ["/admin/backups", "/admin/backups/*"],
      ["/admin/backups/detail", "/admin/backups/*"],
      ["/admin/customs-archive", "/admin/customs-archive/*"],
      ["/admin/customs-archive/detail", "/admin/customs-archive/*"],
    ];
    for (const [exact, wildcard] of pairs) {
      expect(routes.indexOf(exact), `${exact} missing`).toBeGreaterThan(-1);
      expect(routes.indexOf(wildcard), `${wildcard} missing`).toBeGreaterThan(-1);
      expect(
        routes.indexOf(exact),
        `${exact} must precede ${wildcard}`,
      ).toBeLessThan(routes.indexOf(wildcard));
    }
  });

  it("has a 404 responseOverride to the exported 404 page", () => {
    expect(config.responseOverrides?.["404"]?.rewrite).toBe("/404.html");
  });

  it("legacy path wildcards rewrite to the pages LegacyPathRedirect corrects", () => {
    const byRoute = Object.fromEntries(config.routes.map((r) => [r.route, r]));
    expect(byRoute["/sessions/*"]?.rewrite).toBe("/sessions/index.html");
    expect(byRoute["/diagnosis/*"]?.rewrite).toBe("/diagnosis/index.html");
    expect(byRoute["/apps/*"]?.rewrite).toBe("/apps/detail/index.html");
    expect(byRoute["/admin/backups/*"]?.rewrite).toBe("/admin/backups/detail/index.html");
    expect(byRoute["/admin/customs-archive/*"]?.rewrite).toBe("/admin/customs-archive/detail/index.html");
  });
});

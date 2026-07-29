import { describe, it, expect } from "vitest";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { PUBLIC_PATH_PREFIXES, isPublicPath } from "../hostRouting";

/**
 * Guards the public/portal split in hostRouting.ts.
 *
 * Failure mode this pins (prod incident 2026-07-29): /sla was listed as a
 * public path while its page renders ProtectedRoute. On the public host
 * ProtectedRoute stands down, so the page waited forever for a sign-in —
 * a silent dead-end no unit test covered.
 *
 *  (a) No route whose page (or any ancestor layout) renders ProtectedRoute
 *      may ever satisfy isPublicPath.
 *  (b) Every entry in PUBLIC_PATH_PREFIXES must exist somewhere real — an
 *      unprotected app page, an app-root metadata file, a public/ asset, or
 *      a staticwebapp.config.json route. Catches typos and stale entries.
 *  (c) Behavioral pins for isPublicPath's matching rules.
 */

const WEB_ROOT = join(__dirname, "..", "..");
const APP_DIR = join(WEB_ROOT, "app");
const PUBLIC_DIR = join(WEB_ROOT, "public");

interface AppPage {
  route: string;
  isProtected: boolean;
}

/** Walks the app router tree; a page is protected when its page.tsx or any
 *  ancestor layout.tsx renders ProtectedRoute. Route groups "(...)" add no
 *  path segment. */
function collectPages(
  dir: string,
  segments: string[],
  ancestorProtected: boolean,
  acc: AppPage[],
): AppPage[] {
  const layoutPath = join(dir, "layout.tsx");
  const chainProtected =
    ancestorProtected ||
    (existsSync(layoutPath) && readFileSync(layoutPath, "utf-8").includes("ProtectedRoute"));

  const pagePath = join(dir, "page.tsx");
  if (existsSync(pagePath)) {
    acc.push({
      route: segments.length === 0 ? "/" : "/" + segments.join("/"),
      isProtected: chainProtected || readFileSync(pagePath, "utf-8").includes("ProtectedRoute"),
    });
  }

  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (!entry.isDirectory()) continue;
    const nextSegments =
      entry.name.startsWith("(") && entry.name.endsWith(")")
        ? segments
        : [...segments, entry.name];
    collectPages(join(dir, entry.name), nextSegments, chainProtected, acc);
  }
  return acc;
}

const pages = collectPages(APP_DIR, [], false, []);
const pageByRoute = new Map(pages.map((p) => [p.route, p]));

const swaRoutes: string[] = (
  JSON.parse(readFileSync(join(WEB_ROOT, "staticwebapp.config.json"), "utf-8")) as {
    routes: Array<{ route: string }>;
  }
).routes.map((r) => r.route);

describe("hostRouting public/portal guard", () => {
  it("detects the app router tree and the ProtectedRoute marker (self-check)", () => {
    // If either collapses to zero the guards below would pass vacuously.
    expect(pages.length).toBeGreaterThan(20);
    const sla = pageByRoute.get("/sla");
    expect(sla, "/sla page missing — incident pin needs updating").toBeTruthy();
    expect(sla!.isProtected, "/sla no longer renders ProtectedRoute — update this guard").toBe(true);
  });

  it("no ProtectedRoute page is classified as public", () => {
    const violations = pages
      .filter((p) => p.isProtected && isPublicPath(p.route))
      .map((p) => p.route);
    expect(
      violations,
      `Portal page(s) listed as public — on www ProtectedRoute stands down and the page dead-ends (the /sla incident): ${violations.join(", ")}`,
    ).toEqual([]);
  });

  it("every public prefix maps to something real and unprotected", () => {
    const appRootFiles = readdirSync(APP_DIR);
    for (const prefix of PUBLIC_PATH_PREFIXES) {
      const page = pageByRoute.get(prefix);
      if (page) {
        expect(
          page.isProtected,
          `${prefix} is in PUBLIC_PATH_PREFIXES but its page renders ProtectedRoute`,
        ).toBe(false);
        continue;
      }
      // Not an app page: must be app-root metadata (robots.ts, icon.svg,
      // opengraph-image.png, …), a public/ asset, or a SWA redirect/rewrite.
      const base = prefix.slice(1);
      const metadataName = base.split(".")[0];
      const exists =
        appRootFiles.some((f) => f.startsWith(metadataName + ".")) ||
        existsSync(join(PUBLIC_DIR, base)) ||
        swaRoutes.includes(prefix) ||
        swaRoutes.includes(prefix + "/*");
      expect(
        exists,
        `${prefix} is in PUBLIC_PATH_PREFIXES but resolves to no app page, metadata file, public/ asset, or staticwebapp.config.json route`,
      ).toBe(true);
    }
  });

  it("pins isPublicPath matching semantics", () => {
    // The landing page is public; the incident route is portal.
    expect(isPublicPath("/")).toBe(true);
    expect(isPublicPath("/sla")).toBe(false);
    expect(isPublicPath("/sla/anything")).toBe(false);
    // Core portal surface stays portal.
    for (const r of ["/dashboard", "/sessions", "/settings", "/admin/metrics", "/progress"]) {
      expect(isPublicPath(r), `${r} must be portal`).toBe(false);
    }
    // Prefix matching: subpaths and generated asset variants match…
    expect(isPublicPath("/about")).toBe(true);
    expect(isPublicPath("/docs/setup")).toBe(true);
    expect(isPublicPath("/icon-192.png")).toBe(true);
    expect(isPublicPath("/opengraph-image.png")).toBe(true);
    // …but sibling names that merely share a prefix string do not.
    expect(isPublicPath("/aboutx")).toBe(false);
  });
});

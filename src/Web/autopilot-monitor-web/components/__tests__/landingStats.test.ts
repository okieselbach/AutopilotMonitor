import { describe, it, expect, vi } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { prerender } from "react-dom/static";

// LandingNavbar and LoginButton read the auth context; the public pages render signed out.
vi.mock("@/contexts/AuthContext", () => ({
  useAuth: () => ({ login: async () => undefined, isAuthenticated: false, user: null, getAccessToken: async () => null }),
}));
// AuthGate only redirects signed-in visitors (it needs the app router) and renders no links.
vi.mock("@/components/landing/AuthGate", () => ({ AuthGate: () => null }));

const { SNAPSHOT } = vi.hoisted(() => ({
  SNAPSHOT: {
    items: [
      { label: "enrollments monitored", value: "12,481" },
      { label: "events processed", value: "8.3M" },
    ],
    asOf: "2026-10-02T14:00:03.1234567Z",
  },
}));
// The blob read itself is covered in lib/__tests__/platformStats.test.ts.
vi.mock("@/lib/platformStats", async importOriginal => ({
  ...(await importOriginal<typeof import("@/lib/platformStats")>()),
  loadPlatformStatsSnapshot: async () => SNAPSHOT,
}));

import LandingRoute from "@/app/page";
import { StatsBand } from "@/components/landing/StatsBand";

/**
 * Crawlers and LLM fetchers run no JS: the platform stats only reach them when the build renders
 * the numbers into the landing page's HTML (lib/platformStats.ts, app/page.tsx).
 */
describe("platform stats in the landing page's static HTML", () => {
  it("the band renders the baked numbers and marks the snapshot", () => {
    const html = renderToStaticMarkup(createElement(StatsBand, { snapshot: SNAPSHOT }));
    expect(html).toContain(">12,481<");
    expect(html).toContain(">enrollments monitored<");
    expect(html).toContain(">8.3M<");
    expect(html).toContain(`data-stats-snapshot="${SNAPSHOT.asOf}"`);
    expect(html).not.toContain("animate-pulse");
  });

  it("without a snapshot the band shows the loading skeleton and no marker", () => {
    const html = renderToStaticMarkup(createElement(StatsBand, { snapshot: null }));
    expect(html).toContain('data-track-section="stats"');
    expect(html).toContain("animate-pulse");
    expect(html).not.toContain("data-stats-snapshot");
  });

  // A Suspense boundary around the read would still resolve at build time, but React moves a
  // resolved boundary of a page this size into a hidden div at the end of the body.
  it("the page renders the numbers in place, not in a hidden streaming segment", async () => {
    const { prelude } = await prerender(createElement(LandingRoute));
    const html = await new Response(prelude).text();

    const bandStart = html.indexOf('data-track-section="stats"');
    const nextSection = html.indexOf('data-track-section="just_ask"');
    const figure = html.indexOf(">12,481<");
    expect(bandStart).toBeGreaterThan(-1);
    expect(figure).toBeGreaterThan(bandStart);
    expect(figure).toBeLessThan(nextSection);
    expect(html).toContain(`data-stats-snapshot="${SNAPSHOT.asOf}"`);
    expect(html).not.toMatch(/<div hidden id="S:/);
  });
});

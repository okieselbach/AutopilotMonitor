import { describe, it, expect, vi } from "vitest";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { createElement, type ComponentType } from "react";
import { renderToStaticMarkup } from "react-dom/server";

// LandingNavbar and LoginButton read the auth context; the public pages render signed out.
vi.mock("@/contexts/AuthContext", () => ({
  useAuth: () => ({ login: async () => undefined, isAuthenticated: false, user: null, getAccessToken: async () => null }),
}));
// AuthGate only redirects signed-in visitors (it needs the app router) and renders no links.
vi.mock("@/components/landing/AuthGate", () => ({ AuthGate: () => null }));

import { LandingPage } from "@/components/landing/LandingPage";
import GetStartedPage from "@/app/get-started/page";
import AiPage from "@/app/ai/page";
import robots from "@/app/robots";
import sitemap from "@/app/sitemap";
import { QUESTIONS } from "@/components/ai/questions";
import { TRACK_ID_PATTERN, isSectionSeen, trackIdOf } from "@/lib/clickTracking";
import { sitePageUrl } from "@/utils/config";

/**
 * The UX report counts clicks and section views on the marketing pages (D-305). A link or button
 * without a data-track id is invisible there, so every one carries an id; the What's new bell is
 * the exception, it sends whats_new_opened itself.
 */

const render = (Page: ComponentType) => renderToStaticMarkup(createElement(Page));
// app/page.tsx only awaits the build-time stats and renders this body; the sync renderer cannot await.
const Landing = () => createElement(LandingPage, { statsSnapshot: null });
const trackIds = (html: string) => [...html.matchAll(/data-track="([^"]+)"/g)].map(m => m[1]);
const sectionIds = (html: string) => [...html.matchAll(/data-track-section="([^"]+)"/g)].map(m => m[1]);
const untracked = (html: string) =>
  [...html.matchAll(/<(?:a|button)\b[^>]*>/g)]
    .map(m => m[0])
    .filter(tag => !/\sdata-track="/.test(tag) && !/aria-label="What&#x27;s new"/.test(tag));

const PAGES: { name: string; Page: ComponentType; sections: string[] }[] = [
  {
    name: "landing",
    Page: Landing,
    sections: ["hero", "stats", "just_ask", "story", "capabilities", "comparison", "how_it_works", "testimonials", "final_cta"],
  },
  { name: "get-started", Page: GetStartedPage, sections: ["steps", "signup"] },
  { name: "ai", Page: AiPage, sections: ["hero", "structured", "questions", "architecture", "connect", "closing"] },
];

describe("marketing page tracking", () => {
  for (const { name, Page, sections } of PAGES) {
    it(`${name}: every link and button carries a valid, unique data-track id`, () => {
      const html = render(Page);
      expect(untracked(html)).toEqual([]);
      const ids = trackIds(html);
      for (const id of ids) expect(id).toMatch(TRACK_ID_PATTERN);
      expect(ids.filter((id, i) => ids.indexOf(id) !== i)).toEqual([]);
    });

    it(`${name}: marks its sections`, () => {
      expect(sectionIds(render(Page))).toEqual(sections);
    });
  }

  it("the landing links to the AI page from the navbar, Then just ask and the footer", () => {
    const ids = trackIds(render(Landing));
    expect(ids).toEqual(expect.arrayContaining(["nav_ai", "just_ask_ai", "footer_ai"]));
  });

  it("the AI page carries every question text in its static HTML", () => {
    const html = render(AiPage);
    const escape = (text: string) => text.replace(/&/g, "&amp;").replace(/'/g, "&#x27;");
    for (const q of QUESTIONS) expect(html, q.id).toContain(escape(q.text));
  });

  it("the AI page is in the sitemap, robots.txt and llms.txt", () => {
    expect(sitemap().map(e => e.url)).toContain(sitePageUrl("/ai"));
    const allow = robots().rules;
    expect(Array.isArray(allow) ? allow[0].allow : allow.allow).toContain("/ai");
    const llms = readFileSync(join(__dirname, "..", "..", "public", "llms.txt"), "utf-8");
    expect(llms).toContain(sitePageUrl("/ai"));
  });
});

describe("clickTracking helpers", () => {
  it("reads the id of the nearest marked element", () => {
    const target = { closest: (selector: string) => (selector === "[data-track]" ? { getAttribute: () => "nav_ai" } : null) };
    expect(trackIdOf(target)).toBe("nav_ai");
    expect(trackIdOf({ closest: () => null })).toBeNull();
    expect(trackIdOf(null)).toBeNull();
  });

  it("counts a section once a third is visible, or half the viewport for tall sections", () => {
    expect(isSectionSeen(0.34, 100, 800)).toBe(true);
    expect(isSectionSeen(0.2, 420, 800)).toBe(true);
    expect(isSectionSeen(0.2, 200, 800)).toBe(false);
    expect(isSectionSeen(0, 0, 0)).toBe(false);
  });
});

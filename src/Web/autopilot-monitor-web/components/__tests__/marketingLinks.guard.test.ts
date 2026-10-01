import { describe, it, expect, vi, beforeAll, afterAll } from "vitest";
import { createElement, type ComponentType } from "react";
import { renderToStaticMarkup } from "react-dom/server";

// LandingNavbar and LoginButton read the auth context; the public pages render signed out.
vi.mock("@/contexts/AuthContext", () => ({
  useAuth: () => ({ login: async () => undefined, isAuthenticated: false, user: null, getAccessToken: async () => null }),
}));
// AuthGate only redirects signed-in visitors (it needs the app router) and renders no links.
vi.mock("@/components/landing/AuthGate", () => ({ AuthGate: () => null }));

import LandingPage from "@/app/page";
import GetStartedPage from "@/app/get-started/page";
import AiPage from "@/app/ai/page";
import AboutPage from "@/app/about/page";
import PlansPage from "@/app/plans/page";
import BuyPage from "@/app/buy/page";
import HelpPage from "@/app/help/page";
import PrivacyPage from "@/app/privacy/page";
import TermsPage from "@/app/terms/page";

/**
 * The export serves each page as <path>/index.html, and the host answers a link without the
 * trailing slash with a 301 first. <Link> appends the slash itself; a plain <a> has to carry it.
 */

const PAGES: Record<string, ComponentType> = {
  landing: LandingPage,
  "get-started": GetStartedPage,
  ai: AiPage,
  about: AboutPage,
  plans: PlansPage,
  buy: BuyPage,
  help: HelpPage,
  privacy: PrivacyPage,
  terms: TermsPage,
};

// The build inlines trailingSlash as __NEXT_TRAILING_SLASH; without it <Link> would drop the slash here.
beforeAll(() => {
  process.env.__NEXT_TRAILING_SLASH = "true";
});
afterAll(() => {
  delete process.env.__NEXT_TRAILING_SLASH;
});

const internalHrefs = (html: string) => [...html.matchAll(/<a\b[^>]*?\shref="(\/[^"]*)"/g)].map(m => m[1]);

/** A page path without its trailing slash; the root and files such as /llms.txt are exempt. */
const missesSlash = (href: string) => {
  const path = href.split(/[?#]/)[0];
  return path !== "/" && !path.endsWith("/") && !/\.[a-z0-9]+$/i.test(path);
};

describe("marketing page links", () => {
  for (const [name, Page] of Object.entries(PAGES)) {
    it(`${name}: every internal link ends with a slash`, () => {
      const hrefs = internalHrefs(renderToStaticMarkup(createElement(Page)));
      expect(hrefs.length).toBeGreaterThan(0);
      expect(hrefs.filter(missesSlash)).toEqual([]);
    });
  }

  it("treats a bare page path as missing its slash, the root and files not", () => {
    expect(["/plans", "/terms#cookies", "/help?x=1"].map(missesSlash)).toEqual([true, true, true]);
    expect(["/plans/", "/", "/#story", "/llms.txt", "/ai/#architecture"].map(missesSlash)).toEqual([false, false, false, false, false]);
  });
});

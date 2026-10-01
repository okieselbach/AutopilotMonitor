import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { renderToStaticMarkup } from "react-dom/server";
import robots from "../../robots";
import sitemap from "../../sitemap";
import EventFollowupPage, { generateMetadata, generateStaticParams } from "../[slug]/page";
import { COMMUNITY_COMPANIONS, FOLLOWUP_EVENTS, WALKTHROUGH_EMAIL, walkthroughMailto } from "../events";
import { trackIdOf } from "../eventClicks";
import { sitePageUrl } from "@/utils/config";

/**
 * Event follow-up pages are reached only through the follow-up email: never indexed, never
 * listed for crawlers, and every call to action counted by one click event (data-track).
 */

const WEB_ROOT = join(__dirname, "..", "..", "..");

const params = (slug: string) => ({ params: Promise.resolve({ slug }) });

async function renderEventPage(slug: string): Promise<string> {
  return renderToStaticMarkup(await EventFollowupPage(params(slug)));
}

/** Width and height from a PNG's IHDR chunk. */
function pngSize(publicPath: string): { width: number; height: number } {
  const bytes = readFileSync(join(WEB_ROOT, "public", publicPath));
  return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) };
}

describe("event follow-up pages", () => {
  it("prerender exactly the listed events, with unique URL-safe slugs", () => {
    const slugs = FOLLOWUP_EVENTS.map((e) => e.slug);
    expect(slugs.length).toBeGreaterThan(0);
    expect(new Set(slugs).size).toBe(slugs.length);
    for (const slug of slugs) expect(slug).toMatch(/^[a-z0-9]+(?:-[a-z0-9]+)*$/);
    expect(generateStaticParams()).toEqual(slugs.map((slug) => ({ slug })));
  });

  it("are noindex/nofollow with their own canonical URL", async () => {
    for (const event of FOLLOWUP_EVENTS) {
      const metadata = await generateMetadata(params(event.slug));
      expect(metadata.robots).toEqual({ index: false, follow: false, googleBot: { index: false, follow: false } });
      expect(metadata.alternates?.canonical).toBe(sitePageUrl(`/events/${event.slug}`));
      expect(metadata.title).toBe(event.name);
    }
  });

  it("stay out of the sitemap, robots.txt and llms.txt", () => {
    expect(sitemap().filter((entry) => entry.url.includes("/events"))).toEqual([]);
    for (const rule of [robots().rules].flat()) {
      expect([rule.allow ?? []].flat().filter((path) => path.startsWith("/events"))).toEqual([]);
      // A disallow would hide the noindex tag from crawlers.
      expect([rule.disallow ?? []].flat().filter((path) => path.startsWith("/events"))).toEqual([]);
    }
    expect(readFileSync(join(WEB_ROOT, "public", "llms.txt"), "utf-8")).not.toContain("/events");
  });

  it("declare the real size of their artwork", () => {
    for (const event of FOLLOWUP_EVENTS) {
      for (const art of event.art ? [event.art.hero, event.art.starPointer] : []) {
        expect(pngSize(art.src), art.src).toEqual({ width: art.width, height: art.height });
      }
    }
  });

  it("mark every call to action for the click event", async () => {
    for (const event of FOLLOWUP_EVENTS) {
      const html = await renderEventPage(event.slug);
      const ids = [...html.matchAll(/data-track="([^"]+)"/g)].map((m) => m[1]);
      expect(ids.sort()).toEqual(
        [
          "logo",
          "header_get_started",
          "get_started",
          "walkthrough_email",
          "docs",
          "github_star",
          ...COMMUNITY_COMPANIONS.map((c) => c.id),
        ].sort(),
      );
      expect(html).toContain(`href="${walkthroughMailto(event).replace(/&/g, "&amp;")}"`);
      expect(html).toContain(WALKTHROUGH_EMAIL);
      for (const companion of COMMUNITY_COMPANIONS) expect(html).toContain(`href="${companion.href}"`);
    }
  });

  it("name the event in the walkthrough mail subject", () => {
    expect(walkthroughMailto(FOLLOWUP_EVENTS[0])).toBe(
      `mailto:${WALKTHROUGH_EMAIL}?subject=Walkthrough%20request%20-%20${encodeURIComponent(FOLLOWUP_EVENTS[0].name)}`,
    );
  });
});

describe("trackIdOf", () => {
  const element = (track: string | null) => ({
    closest: (selector: string) => {
      expect(selector).toBe("[data-track]");
      return track === null ? null : { getAttribute: () => track };
    },
  });

  it("reads the id of the nearest marked element", () => {
    expect(trackIdOf(element("docs"))).toBe("docs");
  });

  it("ignores clicks outside any marked element, an empty id, and a missing target", () => {
    expect(trackIdOf(element(null))).toBeNull();
    expect(trackIdOf(element(""))).toBeNull();
    expect(trackIdOf(null)).toBeNull();
  });
});

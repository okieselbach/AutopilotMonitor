import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import nextConfig from "../../next.config";
import sitemap from "../../app/sitemap";
import { SITE_URL, sitePageUrl } from "../config";

/**
 * Public pages are served at /page/ only: the static export writes
 * page/index.html (next.config trailingSlash: true), SWA's trailingSlash "auto"
 * 301-redirects /page to /page/, and Next emits /page/ as the canonical URL.
 * Page URLs handed to crawlers outside Next metadata (sitemap, llms.txt) must
 * use that form, or each one is a redirect to a different canonical URL
 * (Search Console: "Page with redirect").
 */

const WEB_ROOT = join(__dirname, "..", "..");

/** Served form: SITE_URL origin, path "/" or "/segment/.../". */
function expectServedForm(url: string): void {
  const parsed = new URL(url);
  expect(parsed.origin, url).toBe(SITE_URL);
  expect(parsed.pathname, url).toMatch(/^\/(?:[^/]+\/)*$/);
}

describe("public page URLs use the served /page/ form", () => {
  it("the export and SWA serve public pages at /page/", () => {
    expect(nextConfig.trailingSlash).toBe(true);
    const swa = JSON.parse(readFileSync(join(WEB_ROOT, "staticwebapp.config.json"), "utf-8"));
    expect(swa.trailingSlash).toBe("auto");
  });

  it("sitePageUrl appends the trailing slash exactly once", () => {
    expect(sitePageUrl("/")).toBe(`${SITE_URL}/`);
    expect(sitePageUrl("/plans")).toBe(`${SITE_URL}/plans/`);
    expect(sitePageUrl("/plans/")).toBe(`${SITE_URL}/plans/`);
  });

  it("sitemap lists every page in the served form", () => {
    const urls = sitemap().map((entry) => entry.url);
    expect(urls.length).toBeGreaterThan(0);
    for (const url of urls) expectServedForm(url);
  });

  it("llms.txt links site pages in the served form", () => {
    const llms = readFileSync(join(WEB_ROOT, "public", "llms.txt"), "utf-8");
    const links = [...llms.matchAll(/\]\(([^)\s]+)\)/g)]
      .map((match) => match[1])
      .filter((url) => new URL(url).origin === SITE_URL);
    expect(links.length).toBeGreaterThan(0);
    for (const url of links) expectServedForm(url);
  });
});

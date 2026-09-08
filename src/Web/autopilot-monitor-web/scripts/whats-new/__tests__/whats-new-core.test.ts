import { describe, expect, it } from "vitest";
import core from "../whats-new-core.js";

const {
  parseChangelog,
  splitTitle,
  slugifyHeading,
  buildSummaryUrlMap,
  resolveLink,
  rewriteLinks,
  parseBlamePorcelain,
  stableId,
  buildPayload,
  emptyPayload,
} = core;

// Synthetic docs bundle — never copied from the real changelog, so the test does not
// silently drift with the content it is meant to exercise.
const SUMMARY = [
  "# Table of contents",
  "",
  "* [Welcome](README.md)",
  "* [Plans](plans.md)",
  "",
  "## Portal Guide",
  "",
  "* [Dashboard & Sessions](portal-guide/dashboard-and-sessions.md)",
  "",
  "## Rules",
  "",
  "* [Analyze Rules](rules/analyze-rules/README.md)",
  "  * [Concepts](rules/analyze-rules/concepts.md)",
  "",
  "## Trust & Security",
  "",
  "* [Security FAQ](trust/security-faq.md)",
  "",
  "## Troubleshooting & Support",
  "",
  "* [FAQ](troubleshooting/faq.md)",
  "* [How to Purchase](troubleshooting-and-support/how-to-purchase/README.md)",
  "  * [Marketplace](troubleshooting-and-support/how-to-purchase/microsoft-marketplace.md)",
  "",
  "## Changelog",
  "",
  "* [Agent Changelog](changelog/agent-changelog.md)",
  "* [Platform Changelog](changelog/platform-changelog.md)",
].join("\n");

const PLATFORM_MD = [
  "---",
  "type: Changelog",
  "description: >-",
  "  Two-line frontmatter,",
  "  newest first.",
  "---",
  "",
  "# Platform Changelog",
  "",
  "Intro paragraph with a * star that is not a bullet.",
  "",
  "## September 2026",
  "",
  "* **Filter widgets by priority** — Every chip filters the list. See [Dashboard](../portal-guide/dashboard-and-sessions.md#search-syntax) and [Concepts](../rules/analyze-rules/concepts.md).",
  "* **Wrapped bullet** — First line",
  "  continues here with `code` and a [release](https://github.com/okieselbach/AutopilotMonitor/releases).",
  "* **Unknown page** — Links to [somewhere](../troubleshooting/new-page.md) and [outside](http://insecure.example).",
  "",
  "## August 2026",
  "",
  "* **Older** — Older body. See [FAQ](../troubleshooting/faq.md).",
  "",
  "## July 2026",
  "",
  "* **July** — July body.",
  "",
  "## June 2026",
  "",
  "* **Dropped** — Outside the three-period window.",
].join("\r\n");

const AGENT_MD = [
  "# Agent Changelog",
  "",
  "**Current versions:** ![badge](https://img.shields.io/x)",
  "",
  "## September 2026",
  "",
  "* Diagnostics upload events now report how many files the package skipped",
  "* Keep-awake no longer releases early (see the [platform changelog](platform-changelog.md))",
].join("\n");

function blameFor(lines: Record<number, number | null>): string {
  // Minimal --line-porcelain shape: header, author-time, tab-prefixed content.
  return Object.entries(lines)
    .map(([line, time]) => {
      const hash = time === null ? "0".repeat(40) : "a".repeat(40);
      return [`${hash} ${line} ${line} 1`, ...(time === null ? [] : [`author-time ${time}`]), "\tcontent"].join("\n");
    })
    .join("\n");
}

describe("parseChangelog", () => {
  it("skips frontmatter and intro, keeps blocks in file order, folds continuation lines", () => {
    const blocks = parseChangelog(PLATFORM_MD);
    expect(blocks.map(b => b.period)).toEqual(["September 2026", "August 2026", "July 2026", "June 2026"]);
    expect(blocks[0].bullets).toHaveLength(3);
    expect(blocks[0].bullets[1].text).toBe(
      "**Wrapped bullet** — First line continues here with `code` and a [release](https://github.com/okieselbach/AutopilotMonitor/releases).",
    );
  });

  it("records the 1-based source line of each bullet's first line (CRLF input)", () => {
    const blocks = parseChangelog(PLATFORM_MD);
    expect(blocks[0].bullets.map(b => b.line)).toEqual([14, 15, 17]);
  });

  it("ignores the agent badge line and reads sentence bullets", () => {
    const blocks = parseChangelog(AGENT_MD);
    expect(blocks).toHaveLength(1);
    expect(blocks[0].bullets).toHaveLength(2);
  });
});

describe("splitTitle", () => {
  it("splits the platform lead at the em dash", () => {
    expect(splitTitle("**Title here** — Body text.")).toEqual({ title: "Title here", body: "Body text." });
  });
  it("returns null title for a plain sentence", () => {
    expect(splitTitle("Agent reports something")).toEqual({ title: null, body: "Agent reports something" });
  });
  it("does not treat a bold word inside the sentence as a title", () => {
    expect(splitTitle("Now **bold** inside").title).toBeNull();
  });
});

describe("GitBook URL map", () => {
  const map = buildSummaryUrlMap(SUMMARY);

  it("slugifies group headings the way GitBook does", () => {
    expect(slugifyHeading("Troubleshooting & Support")).toBe("troubleshooting-and-support");
    expect(slugifyHeading("Trust & Security")).toBe("trust-and-security");
    expect(slugifyHeading("Getting Started")).toBe("getting-started");
  });

  it("maps root pages, grouped pages, README folders and nested pages", () => {
    expect(map.get("README.md")).toBe("/");
    expect(map.get("plans.md")).toBe("/plans");
    expect(map.get("portal-guide/dashboard-and-sessions.md")).toBe("/portal-guide/dashboard-and-sessions");
    expect(map.get("troubleshooting/faq.md")).toBe("/troubleshooting-and-support/faq");
    expect(map.get("rules/analyze-rules/README.md")).toBe("/rules/analyze-rules");
    expect(map.get("rules/analyze-rules/concepts.md")).toBe("/rules/analyze-rules/concepts");
    expect(map.get("troubleshooting-and-support/how-to-purchase/microsoft-marketplace.md")).toBe(
      "/troubleshooting-and-support/how-to-purchase/microsoft-marketplace",
    );
    expect(map.get("changelog/platform-changelog.md")).toBe("/changelog/platform-changelog");
  });
});

describe("resolveLink / rewriteLinks", () => {
  const urlMap = buildSummaryUrlMap(SUMMARY);
  const folderSlugMap = new Map([["troubleshooting", "troubleshooting-and-support"]]);
  const ctx = { docsUrl: "https://docs.example", fromDir: "changelog", urlMap, folderSlugMap };

  it("resolves relative docs links with anchors and sibling links", () => {
    expect(resolveLink("../portal-guide/dashboard-and-sessions.md#search-syntax", ctx)).toBe(
      "https://docs.example/portal-guide/dashboard-and-sessions#search-syntax",
    );
    expect(resolveLink("platform-changelog.md", ctx)).toBe("https://docs.example/changelog/platform-changelog");
  });

  it("passes https through, drops http and same-page anchors", () => {
    expect(resolveLink("https://github.com/x/y", ctx)).toBe("https://github.com/x/y");
    expect(resolveLink("http://insecure.example", ctx)).toBeNull();
    expect(resolveLink("#anchor", ctx)).toBeNull();
    expect(resolveLink("mailto:a@b.c", ctx)).toBeNull();
  });

  it("falls back to the folder's group slug for pages missing from SUMMARY", () => {
    expect(resolveLink("../troubleshooting/new-page.md", ctx)).toBe("https://docs.example/troubleshooting-and-support/new-page");
    expect(resolveLink("../nowhere/page.md", ctx)).toBeNull();
  });

  it("rewrites every link, unwraps unresolvable ones, returns the first URL", () => {
    const { text, link } = rewriteLinks("See [A](../troubleshooting/faq.md) and [B](http://x) and [C](https://c).", ctx);
    expect(text).toBe("See [A](https://docs.example/troubleshooting-and-support/faq) and B and [C](https://c).");
    expect(link).toBe("https://docs.example/troubleshooting-and-support/faq");
  });
});

describe("parseBlamePorcelain", () => {
  it("maps line numbers to author-time and uncommitted lines to null", () => {
    const map = parseBlamePorcelain(blameFor({ 14: 1757000000, 15: null, 17: 1757100000 }));
    expect(map.get(14)).toBe(1757000000);
    expect(map.get(15)).toBeNull();
    expect(map.get(17)).toBe(1757100000);
    expect(map.get(16)).toBeUndefined();
  });
});

describe("buildPayload", () => {
  const now = new Date("2026-09-08T10:00:00.000Z");
  const payload = buildPayload({
    docsUrl: "https://docs.example/",
    docsCommit: "abc1234",
    summary: SUMMARY,
    now,
    channels: {
      platform: { markdown: PLATFORM_MD, blame: blameFor({ 14: 1757200000, 15: null, 17: 1757100000, 22: 1756000000, 26: 1755000000, 30: 1754000000 }) },
      agent: { markdown: AGENT_MD, blame: blameFor({ 7: 1757300000, 8: 1757250000 }) },
    },
  });

  it("carries schema, commit and channel docs URLs", () => {
    expect(payload.schemaVersion).toBe(1);
    expect(payload.docsCommit).toBe("abc1234");
    expect(payload.generatedUtc).toBe(now.toISOString());
    expect(payload.channels.platform.docsUrl).toBe("https://docs.example/changelog/platform-changelog");
    expect(payload.channels.agent.docsUrl).toBe("https://docs.example/changelog/agent-changelog");
  });

  it("keeps the three newest periods only and preserves file order", () => {
    const periods = payload.channels.platform.entries.map(e => e.period);
    expect(periods).toEqual(["September 2026", "September 2026", "September 2026", "August 2026", "July 2026"]);
  });

  it("dates bullets from blame and uncommitted ones from now", () => {
    const [first, second] = payload.channels.platform.entries;
    expect(first.addedUtc).toBe(new Date(1757200000 * 1000).toISOString());
    expect(second.addedUtc).toBe(now.toISOString());
  });

  it("splits platform titles, rewrites links and picks the first as the read-more target", () => {
    const first = payload.channels.platform.entries[0];
    expect(first.title).toBe("Filter widgets by priority");
    expect(first.body).toBe(
      "Every chip filters the list. See [Dashboard](https://docs.example/portal-guide/dashboard-and-sessions#search-syntax) and [Concepts](https://docs.example/rules/analyze-rules/concepts).",
    );
    expect(first.link).toBe("https://docs.example/portal-guide/dashboard-and-sessions#search-syntax");
  });

  it("leaves agent bullets untitled with the sentence as body", () => {
    const [a, b] = payload.channels.agent.entries;
    expect(a.title).toBeNull();
    expect(a.body).toBe("Diagnostics upload events now report how many files the package skipped");
    expect(a.link).toBeNull();
    expect(b.link).toBe("https://docs.example/changelog/platform-changelog");
  });

  it("gives every entry a stable id that differs between entries", () => {
    const ids = payload.channels.platform.entries.map(e => e.id);
    expect(new Set(ids).size).toBe(ids.length);
    expect(stableId("x")).toBe(stableId("x"));
    expect(stableId("x")).not.toBe(stableId("y"));
  });
});

describe("emptyPayload", () => {
  it("has the full shape with no entries", () => {
    const p = emptyPayload("https://docs.example");
    expect(p.channels.platform.entries).toEqual([]);
    expect(p.channels.agent.entries).toEqual([]);
    expect(p.channels.agent.docsUrl).toBe("https://docs.example/changelog/agent-changelog");
    expect(p.docsCommit).toBeNull();
  });
});

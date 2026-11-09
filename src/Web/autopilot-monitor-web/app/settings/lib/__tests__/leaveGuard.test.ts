/**
 * Which clicks the unsaved-changes guard may hold. Only a plain left click on a link that opens an
 * in-app page in this tab counts; a new tab, a download or another site must never be held, and a
 * page inside the settings area must never ask, because the edits stay there.
 */
import { describe, expect, it } from "vitest";
import { leavesSettings, sameTabLinkTarget, type ClickedLink, type LinkClick } from "../leaveGuard";

const ORIGIN = "https://portal.example.com";
const PLAIN: LinkClick = { button: 0, metaKey: false, ctrlKey: false, shiftKey: false, altKey: false, defaultPrevented: false };
const LINK: ClickedLink = { href: `${ORIGIN}/dashboard/`, target: "", download: false };

describe("sameTabLinkTarget", () => {
  it("a plain click on an in-app link opens its path in this tab", () => {
    expect(sameTabLinkTarget(PLAIN, LINK, ORIGIN)).toBe("/dashboard/");
    expect(sameTabLinkTarget(PLAIN, { ...LINK, href: `${ORIGIN}/sessions?id=1#timeline` }, ORIGIN)).toBe("/sessions?id=1#timeline");
    expect(sameTabLinkTarget(PLAIN, { ...LINK, target: "_self" }, ORIGIN)).toBe("/dashboard/");
  });

  it.each<[string, LinkClick]>([
    ["middle button", { ...PLAIN, button: 1 }],
    ["Ctrl", { ...PLAIN, ctrlKey: true }],
    ["Cmd", { ...PLAIN, metaKey: true }],
    ["Shift", { ...PLAIN, shiftKey: true }],
    ["Alt", { ...PLAIN, altKey: true }],
    ["already handled", { ...PLAIN, defaultPrevented: true }],
  ])("a click with %s is left alone", (_, click) => {
    expect(sameTabLinkTarget(click, LINK, ORIGIN)).toBeNull();
  });

  it.each<[string, ClickedLink]>([
    ["a new tab", { ...LINK, target: "_blank" }],
    ["a download", { ...LINK, download: true }],
    ["another site", { ...LINK, href: "https://docs.example.org/settings" }],
    ["an unparsable address", { ...LINK, href: "http://[broken" }],
  ])("a link to %s is left alone", (_, link) => {
    expect(sameTabLinkTarget(PLAIN, link, ORIGIN)).toBeNull();
  });
});

describe("leavesSettings", () => {
  it.each(["/settings", "/settings/", "/settings/agent/settings", "/settings/tenant/notifications/?x=1", "/settings/reporting/mcp-usage#top"])(
    "%s stays in the settings area",
    (path) => expect(leavesSettings(path)).toBe(false),
  );

  it.each(["/", "/dashboard/", "/settingsx", "/admin/settings/global", "/sessions?id=1"])(
    "%s leaves it",
    (path) => expect(leavesSettings(path)).toBe(true),
  );
});

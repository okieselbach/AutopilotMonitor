import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { join } from "node:path";

/**
 * The session page takes its session from `?id=`, so a link from one session to another
 * keeps the route mounted. Its content must be keyed by that id: the event list is
 * append-only (see mergeNewEvents), and without a remount the previous session's events
 * stay in it and mix into the new session's timeline.
 */
describe("session page remounts per session", () => {
  const source = readFileSync(join(__dirname, "..", "page.tsx"), "utf8");

  it("keys its content by the session id from the URL", () => {
    expect(source).toContain('const id = useSearchParams()?.get("id") ?? "";');
    expect(source).toContain("<SessionDetailContent key={id} />");
  });

  it("renders the content from that one keyed place only", () => {
    expect(source.match(/<SessionDetailContent\b/g)).toHaveLength(1);
  });
});

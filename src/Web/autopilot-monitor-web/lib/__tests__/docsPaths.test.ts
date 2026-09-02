import { describe, it, expect } from "vitest";
import { DOCS_PATHS, DOCS_TOP_LEVEL_SEGMENTS } from "../docsPaths";

/**
 * GitBook publishes the first URL segment from the SUMMARY.md group heading, not the folder
 * name (`troubleshooting/` on disk is `/troubleshooting-and-support/` on the web). A path written
 * from the repo layout 404s silently in production — this test makes that a CI failure.
 */
describe("DOCS_PATHS", () => {
  const entries = Object.entries(DOCS_PATHS);

  it("every path is root-relative, lowercase, with an optional single anchor", () => {
    for (const [key, path] of entries) {
      expect(path, key).toMatch(/^\/[a-z0-9-]+(\/[a-z0-9-]+)*(#[a-z0-9-]+)?$/);
      expect(path, key).not.toMatch(/\.md/);
      expect(path, key).not.toMatch(/--/);
    }
  });

  it("every path starts with a published top-level segment", () => {
    const allowed = new Set<string>(DOCS_TOP_LEVEL_SEGMENTS);
    for (const [key, path] of entries) {
      const first = path.slice(1).split(/[/#]/)[0];
      expect(allowed.has(first), `${key}: "${first}" is not a published docs section`).toBe(true);
    }
  });

  it("does not use on-disk folder names that GitBook renames", () => {
    for (const [key, path] of entries) {
      expect(path, key).not.toMatch(/^\/(troubleshooting|trust)\//);
    }
  });
});

/**
 * The committed web copy of the error-code catalog must be exactly what the sync script
 * produces from the Shared source (same discipline as sharedManifestParity: a stale copy
 * means the prebuild step was skipped), and the sync must reject a catalog that breaks
 * the schema-2 contract instead of shipping it.
 */
import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import path from "node:path";

const require = createRequire(import.meta.url);
const sync = require("../../scripts/sync-error-codes.js") as {
  validate: (rawText: string) => { schemaVersion: number; entries: Record<string, unknown>; enforcementStates?: Record<string, unknown> };
  transform: (catalog: ReturnType<typeof sync.validate>) => { schemaVersion: number; entries: Record<string, Record<string, unknown>> };
  SOURCE: string;
  DEST: string;
};

const sourceText = readFileSync(sync.SOURCE, "utf8");

describe("error-codes sync", () => {
  it("committed web copy equals the sync output of the Shared catalog", () => {
    const expected = JSON.stringify(sync.transform(sync.validate(sourceText))) + "\n";
    const actual = readFileSync(sync.DEST, "utf8");
    expect(actual).toBe(expected);
  });

  it("shortens msdoc sources and drops enforcement states for the browser", () => {
    const catalog = sync.validate(sourceText);
    const out = sync.transform(catalog);
    expect(catalog.enforcementStates).toBeDefined();
    expect("enforcementStates" in out).toBe(false);
    expect(out.entries["0x80070005"].source).toBe("msdoc");
    expect(out.entries["0x87d30000"].source).toMatch(/^ime:/);
    expect(out.entries["1603"].symbol).toBe("ERROR_INSTALL_FAILURE");
  });

  it("rejects a duplicate key on the raw text", () => {
    const dup = sourceText.replace('    "1603": ', '    "1603": {"description":"x","confidence":"high","source":"winerror.h","category":"msi"},\n    "1603": ');
    expect(() => sync.validate(dup)).toThrow(/duplicate key 1603/);
  });

  it("rejects a category outside the vocabulary", () => {
    const bad = sourceText.replace('"category":"msi"', '"category":"installer"');
    expect(() => sync.validate(bad)).toThrow(/category installer/);
  });

  it("rejects a source outside the convention", () => {
    const bad = sourceText.replace('"source":"winerror.h"', '"source":"MVP Blog"');
    expect(() => sync.validate(bad)).toThrow(/source MVP Blog/);
  });

  it("rejects a schema version other than 2", () => {
    expect(() => sync.validate(sourceText.replace('"schemaVersion": 2', '"schemaVersion": 1'))).toThrow(/schemaVersion 2/);
  });

  it("the source path is the Shared resource, not a copy", () => {
    expect(path.normalize(sync.SOURCE)).toMatch(/AutopilotMonitor\.Shared[\\/]Resources[\\/]error-codes\.json$/);
  });
});

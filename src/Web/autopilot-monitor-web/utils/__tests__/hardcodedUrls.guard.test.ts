import { describe, it, expect } from "vitest";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative } from "node:path";

/**
 * Enforces the URL registry: every well-known own or Microsoft host must be
 * referenced through utils/config.ts (or derived from it, like hostRouting.ts),
 * never as a repeated string literal. The EU cutover missed hardcoded copies of
 * the blob host precisely because they did not go through a registry — this
 * test makes that class of drift a CI failure instead of a production surprise.
 *
 * Comment lines are ignored (docs may cite URLs); test files are excluded
 * because tests use literals deliberately as independent oracles.
 */

const ENFORCED_HOSTS = [
  "portal.autopilotmonitor.com",
  "www.autopilotmonitor.com",
  "docs.autopilotmonitor.com",
  "download.autopilotmonitor.com",
  "mcp.autopilotmonitor.com",
  "autopilotmonitor-api-eu.azurewebsites.net",
  "autopilotmonitor.blob.core.windows.net",
  "autopilotmonitoreu.blob.core.windows.net",
  "graph.microsoft.com",
  "login.microsoftonline.com",
] as const;

const WEB_ROOT = join(__dirname, "..", "..");

/** The registry itself — the only file allowed to carry the literals. */
const REGISTRY_FILES = [join("utils", "config.ts")];

const SCAN_DIRS = ["app", "components", "lib", "utils"];
const SCAN_ROOT_FILES = ["next.config.ts", "middleware.ts"];

function collectFiles(dir: string, acc: string[]): void {
  for (const entry of readdirSync(dir)) {
    if (entry === "node_modules" || entry === "__tests__" || entry.startsWith(".")) continue;
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) {
      collectFiles(full, acc);
    } else if (/\.(ts|tsx)$/.test(entry) && !/\.test\.(ts|tsx)$/.test(entry)) {
      acc.push(full);
    }
  }
}

describe("hardcoded URL guard", () => {
  it("well-known hosts only appear in the registry (utils/config.ts)", () => {
    const files: string[] = [];
    for (const dir of SCAN_DIRS) collectFiles(join(WEB_ROOT, dir), files);
    for (const f of SCAN_ROOT_FILES) {
      try {
        if (statSync(join(WEB_ROOT, f)).isFile()) files.push(join(WEB_ROOT, f));
      } catch {
        // optional root file absent — fine
      }
    }

    const violations: string[] = [];
    for (const file of files) {
      const rel = relative(WEB_ROOT, file).replace(/\\/g, "/");
      if (REGISTRY_FILES.some((r) => rel === r.replace(/\\/g, "/"))) continue;

      const lines = readFileSync(file, "utf-8").split("\n");
      lines.forEach((line, i) => {
        const trimmed = line.trimStart();
        // Comment approximation: full-line // comments and JSDoc/block bodies.
        // A URL after code on the same line still counts — that is the guarded case.
        if (trimmed.startsWith("//") || trimmed.startsWith("*") || trimmed.startsWith("/*")) return;
        for (const host of ENFORCED_HOSTS) {
          if (trimmed.includes(host)) violations.push(`${rel}:${i + 1}: ${host}`);
        }
      });
    }

    expect(
      violations,
      "Hardcoded well-known host(s) found outside utils/config.ts — import the constant instead:\n  " +
        violations.join("\n  "),
    ).toEqual([]);
  });
});

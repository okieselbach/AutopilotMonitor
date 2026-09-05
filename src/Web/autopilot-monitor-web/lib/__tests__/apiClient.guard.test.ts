import { describe, it, expect } from "vitest";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import {
  AUTHENTICATEDFETCH_BASELINE,
  DEDUPEDAUTHFETCH_BASELINE,
  JSONPARSE_BASELINE,
  PERMANENT,
  TOKENEXPIRED_BASELINE,
} from "./apiClient.guard.baseline";

/**
 * Ratchet for the one API call layer (lib/apiClient.ts, D-203): pages, components, hooks and
 * contexts call the backend through fetchJson<T>/fetchOk/fetchBlob and render failures through
 * describeApiError/apiErrorText/notifyError. Raw `authenticatedFetch(`, `.json()` parses and
 * `instanceof TokenExpiredError` branches are legacy; their per-file counts live in the baseline
 * module and may only go down. A count above its baseline (or a file not in the baseline) is a new
 * raw site; a count below it is a stale baseline entry that must be lowered — so the baseline
 * always states the true remaining debt.
 *
 * Comment lines are ignored; test files are excluded (they stub fetch deliberately).
 */

const WEB_ROOT = join(__dirname, "..", "..");
const SCAN_DIRS = ["app", "components", "hooks", "contexts"];
/** The lib-level hook is itself a fetchJson consumer and the one place allowed to spell TokenExpiredError. */
const EXEMPT_FILES = new Set(["hooks/useAuthenticatedFetch.ts"]);

interface Pattern {
  name: string;
  regex: RegExp;
  baseline: Record<string, number>;
  remedy: string;
}

const PATTERNS: Pattern[] = [
  {
    name: "authenticatedFetch(",
    regex: /\bauthenticatedFetch\(/g,
    baseline: AUTHENTICATEDFETCH_BASELINE,
    remedy: "call fetchJson<T>/fetchOk/fetchBlob from @/lib/apiClient instead",
  },
  {
    name: "dedupedAuthFetch(",
    regex: /\bdedupedAuthFetch\(/g,
    baseline: DEDUPEDAUTHFETCH_BASELINE,
    remedy: "call dedupedFetchJson<T> from @/lib/dedupedAuthFetch instead",
  },
  {
    name: ".json()",
    regex: /\.json\(\)/g,
    baseline: JSONPARSE_BASELINE,
    remedy: "fetchJson<T> parses the body; a hand parse hides the error envelope",
  },
  {
    name: "instanceof TokenExpiredError",
    regex: /instanceof TokenExpiredError/g,
    baseline: TOKENEXPIRED_BASELINE,
    remedy: "notifyError/apiErrorText render a token expiry centrally; keep a branch only where the UI must act (route, reset)",
  },
];

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

function isCommentLine(line: string): boolean {
  const trimmed = line.trimStart();
  return trimmed.startsWith("//") || trimmed.startsWith("*") || trimmed.startsWith("/*");
}

/** file (repo-relative, forward slashes) → code lines. */
function scannedSources(): Map<string, string[]> {
  const files: string[] = [];
  for (const dir of SCAN_DIRS) collectFiles(join(WEB_ROOT, dir), files);
  const out = new Map<string, string[]>();
  for (const file of files) {
    const rel = relative(WEB_ROOT, file).replace(/\\/g, "/");
    if (EXEMPT_FILES.has(rel)) continue;
    out.set(
      rel,
      readFileSync(file, "utf-8")
        .split("\n")
        .filter((l) => !isCommentLine(l)),
    );
  }
  return out;
}

function countPerFile(sources: Map<string, string[]>, regex: RegExp): Map<string, number> {
  const counts = new Map<string, number>();
  for (const [rel, lines] of sources) {
    let n = 0;
    for (const line of lines) n += line.match(regex)?.length ?? 0;
    if (n > 0) counts.set(rel, n);
  }
  return counts;
}

const sources = scannedSources();

describe("apiClient ratchet", () => {
  it("scans the app (plausibility floor)", () => {
    expect(sources.size).toBeGreaterThan(50);
  });

  for (const pattern of PATTERNS) {
    describe(pattern.name, () => {
      const counts = countPerFile(sources, pattern.regex);

      it("has no raw site above its baseline", () => {
        const violations: string[] = [];
        for (const [rel, n] of counts) {
          const allowed = pattern.baseline[rel] ?? 0;
          if (n > allowed) violations.push(`${rel}: ${n} (baseline ${allowed})`);
        }
        expect(
          violations,
          `New raw \`${pattern.name}\` site(s) — ${pattern.remedy}:\n  ${violations.join("\n  ")}`,
        ).toEqual([]);
      });

      it("has no stale baseline entry (the baseline states the true remaining debt)", () => {
        const stale: string[] = [];
        for (const [rel, allowed] of Object.entries(pattern.baseline)) {
          const n = counts.get(rel) ?? 0;
          if (n < allowed) stale.push(`${rel}: now ${n} (baseline ${allowed})`);
        }
        expect(
          stale,
          `Lower these entries in lib/__tests__/apiClient.guard.baseline.ts (${pattern.name}):\n  ${stale.join("\n  ")}`,
        ).toEqual([]);
      });
    });
  }

  it("never calls fetchJson without an explicit type argument", () => {
    const violations: string[] = [];
    for (const [rel, lines] of sources) {
      lines.forEach((line, i) => {
        if (/\b(?:fetchJson|dedupedFetchJson)\(/.test(line)) violations.push(`${rel}:${i + 1}`);
      });
    }
    expect(violations, "fetchJson<T>(…) — name the wire type (utils/wire-types.generated):\n  " + violations.join("\n  ")).toEqual([]);
  });

  it("documents every permanent exception against a file that is still in a baseline", () => {
    const inSomeBaseline = new Set(PATTERNS.flatMap((p) => Object.keys(p.baseline)));
    const rotten = Object.keys(PERMANENT).filter((rel) => !inSomeBaseline.has(rel));
    expect(rotten, "PERMANENT names a file with no baseline entry left — drop the reason").toEqual([]);
  });
});

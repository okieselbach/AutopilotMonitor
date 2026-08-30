/**
 * Runtime parity between hand-written TS mirrors and the C#-generated shared manifest.
 * The manifest is the single cross-language anchor: the backend reflection guard
 * (SharedManifestParityTests) pins JSON ↔ C#, these tests pin TS ↔ JSON.
 * Compile-time (key-level) checks live in utils/sharedManifestChecks.ts.
 */
import { describe, expect, it } from "vitest";
import { createRequire } from "node:module";
import fs from "node:fs";
import path from "node:path";
import { SHARED_MANIFEST } from "../shared-manifests.generated";
import { SEVERITY_INT } from "../sessionExportUtils";
import { isTerminalStatus } from "../sessionStatus";
import { OPERATORS, PRECONDITION_OPERATORS, SOURCES } from "@/app/analyze-rules/types";
import {
  ANNOTATION_LANES,
  ANNOTATION_VERDICTS,
} from "@/app/sessions/components/sessionAnnotationLogic";
import { V1_PHASE_NAMES, V2_PHASE_NAMES } from "@/app/sessions/utils/phaseConstants";
import { KNOWN_EVENT_TYPES } from "@/app/gather-rules/eventTypes";

const require = createRequire(import.meta.url);
const {
  buildGeneratedSource,
  buildWireTypesSource,
} = require("../../scripts/generate-shared-manifest-types.js");

const utilsDir = path.resolve(__dirname, "..");

describe("generated manifest modules freshness", () => {
  it("shared-manifests.generated.ts matches a fresh run of the codegen", () => {
    const json = fs.readFileSync(path.join(utilsDir, "shared-manifests.json"), "utf8");
    const committed = fs.readFileSync(
      path.join(utilsDir, "shared-manifests.generated.ts"),
      "utf8"
    );
    expect(committed.replace(/\r\n/g, "\n")).toBe(buildGeneratedSource(json));
  });

  it("wire-types.generated.ts matches a fresh run of the codegen", () => {
    const json = fs.readFileSync(path.join(utilsDir, "shared-manifests.json"), "utf8");
    const committed = fs.readFileSync(path.join(utilsDir, "wire-types.generated.ts"), "utf8");
    expect(committed.replace(/\r\n/g, "\n")).toBe(buildWireTypesSource(json));
  });
});

describe("analyze-rule builder catalogs", () => {
  it("SOURCES equals the backend's KnownSources", () => {
    expect([...SOURCES].sort()).toEqual([...SHARED_MANIFEST.analyzeRuleSources].sort());
  });

  it("OPERATORS equals the backend's KnownOperators", () => {
    expect([...OPERATORS].sort()).toEqual([...SHARED_MANIFEST.analyzeRuleOperators].sort());
  });

  it("PRECONDITION_OPERATORS is a subset of the backend's KnownOperators", () => {
    const known = new Set<string>(SHARED_MANIFEST.analyzeRuleOperators);
    for (const op of PRECONDITION_OPERATORS) expect(known).toContain(op);
  });
});

describe("annotation lanes and verdicts", () => {
  it("lanes match", () => {
    expect([...ANNOTATION_LANES]).toEqual([...SHARED_MANIFEST.annotationLanes]);
  });

  it("verdicts match", () => {
    expect([...ANNOTATION_VERDICTS]).toEqual([...SHARED_MANIFEST.annotationVerdicts]);
  });
});

describe("event severities", () => {
  it("SEVERITY_INT mirrors the C# EventSeverity enum exactly (names and ordinals)", () => {
    expect(SEVERITY_INT).toEqual(SHARED_MANIFEST.eventSeverities);
  });
});

describe("enrollment phases", () => {
  it("every numeric phase key used by the UI exists on the C# enum", () => {
    // Display STRINGS are deliberately surface-specific (V1 vs V2 wording); the
    // numeric ordinals are the wire contract.
    const enumValues = new Set<number>(Object.values(SHARED_MANIFEST.enrollmentPhases));
    for (const key of [...Object.keys(V1_PHASE_NAMES), ...Object.keys(V2_PHASE_NAMES)]) {
      expect(enumValues).toContain(Number(key));
    }
  });

  it("every C# enum ordinal has a UI name in both version maps", () => {
    for (const value of Object.values(SHARED_MANIFEST.enrollmentPhases)) {
      expect(V1_PHASE_NAMES[value], `V1 name for phase ${value}`).toBeTruthy();
      expect(V2_PHASE_NAMES[value], `V2 name for phase ${value}`).toBeTruthy();
    }
  });
});

describe("session statuses", () => {
  it("the terminal trio exists on the C# enum and is classified terminal", () => {
    for (const status of ["Succeeded", "Failed", "Incomplete"]) {
      expect(SHARED_MANIFEST.sessionStatuses).toContain(status);
      expect(isTerminalStatus(status)).toBe(true);
    }
  });

  it("non-terminal enum members are not classified terminal", () => {
    for (const status of SHARED_MANIFEST.sessionStatuses) {
      if (["Succeeded", "Failed", "Incomplete"].includes(status)) continue;
      expect(isTerminalStatus(status), `${status} must not be terminal`).toBe(false);
    }
  });
});

describe("signalR message names", () => {
  // The real enforcement is compile-time: SignalRContext.on/off type their event names
  // against this manifest section (lib/signalrMessages.ts), so a web subscription to a
  // name the backend never sends fails tsc. These tests pin the section's shape so a
  // regeneration that drops or duplicates names is caught even before tsc runs.
  it("exists with the full backend catalog and no duplicates", () => {
    const names = SHARED_MANIFEST.signalRMessages;
    expect(names.length).toBeGreaterThanOrEqual(13);
    expect(new Set(names).size).toBe(names.length);
    // Session-lifecycle names every dashboard hook depends on:
    for (const required of ["newSession", "newevents", "eventStream", "sessionDeleted"]) {
      expect(names).toContain(required);
    }
  });

  it("keeps the single legacy lowercase name and camelCase for the rest", () => {
    for (const name of SHARED_MANIFEST.signalRMessages) {
      if (name === "newevents") continue; // persisted wire name predating the convention
      expect(name).toMatch(/^[a-z]+([A-Z][a-z]*)*$/);
    }
  });
});

describe("gather-rule event-type autocomplete", () => {
  it("contains no event type the agent/backend does not know", () => {
    // Typo guard: every autocomplete entry must be a real Constants.EventTypes value.
    // (Completeness in the other direction is a deliberate curation question — the
    // catalog carries ~3x more types than the rule builder surfaces today.)
    const known = new Set<string>(SHARED_MANIFEST.eventTypes);
    for (const entry of KNOWN_EVENT_TYPES) {
      expect(known, `unknown event type '${entry.value}' in KNOWN_EVENT_TYPES`).toContain(
        entry.value
      );
    }
  });
});

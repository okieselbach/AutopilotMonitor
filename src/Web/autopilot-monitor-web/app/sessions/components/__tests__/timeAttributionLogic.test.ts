import { describe, expect, it } from "vitest";
import { SHARED_MANIFEST } from "@/utils/shared-manifests.generated";
import { osUpdateDetail } from "../timeAttributionLogic";

describe("osUpdateDetail", () => {
  it("names the outcome, the installed KBs and the restarts of an installed update", () => {
    expect(osUpdateDetail([
      { outcome: "installed", kbs: ["KB5124007", "KB5129195", "KB5054156"], notInstalledKbs: [], rebootCount: 2 },
    ])).toBe("installed · KB5124007, KB5129195, KB5054156 · 2 restarts");
  });

  it("names a failed update's staged KB as not installed", () => {
    // Field shape e7551b82: only the servicing stack was staged before the download failed.
    expect(osUpdateDetail([
      { outcome: "failed", kbs: [], notInstalledKbs: ["KB5124007"], rebootCount: 1 },
    ])).toBe("failed · not installed: KB5124007 · 1 restart");
  });

  it("names a skipped update", () => {
    expect(osUpdateDetail([{ outcome: "skipped", kbs: [], notInstalledKbs: [], rebootCount: 0 }])).toBe("skipped");
  });

  it("names no outcome for an unknown one or a row written before the outcome existed", () => {
    expect(osUpdateDetail([{ outcome: "unknown", kbs: ["KB5129195"], rebootCount: 0 }])).toBe("KB5129195");
    expect(osUpdateDetail([{ kbs: ["KB5129195"], rebootCount: 3 }])).toBe("KB5129195 · 3 restarts");
  });

  it("is empty without updates", () => {
    expect(osUpdateDetail([])).toBe("");
  });

  it("labels every outcome the backend can send", () => {
    // A new backend outcome must get its words here, not fall through as unnamed.
    for (const outcome of SHARED_MANIFEST.osUpdateOutcomes) {
      const detail = osUpdateDetail([{ outcome, kbs: [], notInstalledKbs: [], rebootCount: 0 }]);
      expect(detail).toBe(outcome === "unknown" ? "" : outcome);
    }
  });
});

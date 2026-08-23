import { describe, expect, it } from "vitest";
import { describeCondition, formatBuiltInSection } from "../builtInSectionDisplay";
import type { DiagnosticsBuiltInSection } from "@/types/diagnostics";

const section = (overrides: Partial<DiagnosticsBuiltInSection>): DiagnosticsBuiltInSection => ({
  id: "X",
  zipFolder: "X",
  sourceFolder: "C:\\X",
  patterns: ["*.log"],
  includeSubfolders: false,
  description: "x",
  condition: "Always",
  ...overrides,
});

describe("formatBuiltInSection", () => {
  it("renders a single pattern inline and needs no summary pill", () => {
    const d = formatBuiltInSection(section({ sourceFolder: "C:\\Windows\\Logs", patterns: ["realmjoin*.log"] }));
    expect(d.pathText).toBe("C:\\Windows\\Logs\\realmjoin*.log");
    expect(d.patternSummary).toBeNull();
    expect(d.patternTitle).toBe("realmjoin*.log");
  });

  it("collapses several patterns to a wildcard plus a count pill", () => {
    const patterns = ["*.log", "*.txt", "*.json", "*.jsonl", "*.etl", "*.evtx", "*.xml", "*.csv", "*.cab"];
    const d = formatBuiltInSection(section({ sourceFolder: "%ProgramData%\\AutopilotMonitor\\Logs", patterns }));
    expect(d.pathText).toBe("%ProgramData%\\AutopilotMonitor\\Logs\\*");
    expect(d.patternSummary).toBe("9 file types");
    expect(d.patternTitle).toBe(patterns.join(", "));
  });

  it("leaves the user-profile token untouched", () => {
    const d = formatBuiltInSection(section({
      sourceFolder: "%LOGGED_ON_USER_PROFILE%\\AppData\\Local\\RealmJoin",
      patterns: ["tray*.log"],
    }));
    expect(d.pathText).toBe("%LOGGED_ON_USER_PROFILE%\\AppData\\Local\\RealmJoin\\tray*.log");
  });
});

describe("describeCondition", () => {
  it("shows nothing for always-on sections", () => {
    expect(describeCondition("Always")).toBeNull();
    expect(describeCondition("Always", true)).toBeNull();
  });

  it("reports the tenant's persisted RealmJoin Watcher state when known", () => {
    expect(describeCondition("RealmJoinWatcher", true)).toMatchObject({ label: "RealmJoin Watcher on", state: true });
    expect(describeCondition("RealmJoinWatcher", false)).toMatchObject({ label: "RealmJoin Watcher off", state: false });
  });

  it("stays neutral without tenant context (Global-Admin view)", () => {
    const d = describeCondition("RealmJoinWatcher");
    expect(d?.label).toBe("RealmJoin Watcher only");
    expect(d?.state).toBeUndefined();
  });

  it("describes the Device Preparation scenario gate without an on/off state", () => {
    const d = describeCondition("DevicePreparation", true);
    expect(d?.label).toBe("Device Preparation only");
    expect(d?.state).toBeUndefined();
  });
});

import type { DiagnosticsBuiltInSection, DiagnosticsSectionCondition } from "@/types/diagnostics";

export interface BuiltInSectionDisplay {
  /** One-line path text: `<sourceFolder>\<pattern>` for single-pattern sections, `<sourceFolder>\*` otherwise. */
  pathText: string;
  /** `"N file types"` when the section collects several patterns; null for a single pattern. */
  patternSummary: string | null;
  /** All patterns, comma-separated — the tooltip behind the summary pill. */
  patternTitle: string;
}

/** Pure projection of a built-in section onto the compact one-line row. */
export function formatBuiltInSection(section: DiagnosticsBuiltInSection): BuiltInSectionDisplay {
  const patterns = section.patterns ?? [];
  const single = patterns.length === 1;
  return {
    pathText: `${section.sourceFolder}\\${single ? patterns[0] : "*"}`,
    patternSummary: single || patterns.length === 0 ? null : `${patterns.length} file types`,
    patternTitle: patterns.join(", "),
  };
}

export interface ConditionDisplay {
  label: string;
  title: string;
  /** true/false = known on/off state (tenant view); undefined = neutral (per-device scenario or admin view). */
  state?: boolean;
}

/**
 * Context pill for a section's collection gate. `realmJoinWatcherEnabled` is the tenant's
 * PERSISTED watcher toggle (undefined in the Global-Admin view, which has no tenant context).
 */
export function describeCondition(
  condition: DiagnosticsSectionCondition,
  realmJoinWatcherEnabled?: boolean,
): ConditionDisplay | null {
  switch (condition) {
    case "RealmJoinWatcher": {
      const known = realmJoinWatcherEnabled !== undefined;
      return {
        label: known ? `RealmJoin Watcher ${realmJoinWatcherEnabled ? "on" : "off"}` : "RealmJoin Watcher only",
        title: "Collected only when the RealmJoin Watcher is enabled for the tenant (Settings → Agent).",
        state: known ? realmJoinWatcherEnabled : undefined,
      };
    }
    case "DevicePreparation":
      return {
        label: "Device Preparation only",
        title: "Collected only on Windows Autopilot Device Preparation enrollments.",
      };
    default:
      return null;
  }
}

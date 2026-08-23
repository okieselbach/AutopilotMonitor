export interface DiagnosticsLogPath {
  path: string;
  description: string;
  isBuiltIn: boolean;
  includeSubfolders?: boolean;
}

/** Gate of a built-in section — mirrors Shared `DiagnosticsSectionCondition` (serialized as the enum NAME). */
export type DiagnosticsSectionCondition = "Always" | "RealmJoinWatcher" | "DevicePreparation";

/**
 * One built-in collection section of the diagnostics ZIP, as served by GET /api/diagnostics/paths.
 * Compiled into the agent (Shared `DiagnosticsBuiltInSections`) — read-only in the portal.
 */
export interface DiagnosticsBuiltInSection {
  id: string;
  zipFolder: string;
  /** Unexpanded: may contain %ProgramData% or %LOGGED_ON_USER_PROFILE%. */
  sourceFolder: string;
  patterns: string[];
  includeSubfolders: boolean;
  description: string;
  condition: DiagnosticsSectionCondition;
}

/** Wire shape of GET /api/diagnostics/paths. */
export interface DiagnosticsPathsCatalog {
  builtIn: DiagnosticsBuiltInSection[];
  globalPaths: DiagnosticsLogPath[];
}

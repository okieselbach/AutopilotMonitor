/**
 * Curated list of known agent event types for the Gather Rule "on_event" trigger autocomplete.
 *
 * This list is a TypeScript mirror of `AutopilotMonitor.Shared.Constants.EventTypes`.
 * When adding a new event type on the agent/backend side, add it here as well so tenant admins
 * can discover it in the rule builder.
 *
 * A unit test in lib/__tests__/eventTypes.test.ts verifies that the new stall-detection events
 * are present so refactoring the Shared constants cannot silently drop them.
 */

export interface EventTypeEntry {
  /** Canonical event type string as emitted by the agent. */
  value: string;
  /** Short human-readable label shown in the autocomplete list. */
  label: string;
  /** One-line description explaining when the agent emits this event. */
  description: string;
  /** Category used for grouping in the autocomplete. */
  category: "enrollment" | "app" | "esp" | "stall" | "script" | "diagnostics" | "misc";
}

export const KNOWN_EVENT_TYPES: EventTypeEntry[] = [
  // -------- Enrollment lifecycle --------
  { value: "enrollment_complete", label: "enrollment_complete", category: "enrollment",
    description: "Enrollment finished successfully (terminal)." },
  { value: "enrollment_failed", label: "enrollment_failed", category: "enrollment",
    description: "Enrollment failed (terminal)." },
  { value: "whiteglove_complete", label: "whiteglove_complete", category: "enrollment",
    description: "Pre-Provisioning Part 1 complete, session enters Pending waiting for user." },
  { value: "whiteglove_resumed", label: "whiteglove_resumed", category: "enrollment",
    description: "Pre-Provisioning Part 2 resumed after the user powered on the device." },
  { value: "desktop_arrived", label: "desktop_arrived", category: "enrollment",
    description: "User desktop detected (explorer.exe under real user)." },
  { value: "completion_check", label: "completion_check", category: "enrollment",
    description: "Throttled state-machine snapshot during completion evaluation." },
  { value: "agent_started", label: "agent_started", category: "enrollment",
    description: "Agent process started." },
  { value: "agent_shutdown", label: "agent_shutdown (V1)", category: "enrollment",
    description: "Agent process is shutting down (legacy V1 event type — V2 emits agent_shutting_down)." },
  { value: "agent_shutting_down", label: "agent_shutting_down (V2)", category: "enrollment",
    description: "Agent process is shutting down. V2 canonical event; reason field disambiguates (decision_terminal / max_lifetime / auth_failure / ctrl_c / process_exit / unhandled_exception)." },
  { value: "phase_transition", label: "phase_transition", category: "enrollment",
    description: "Enrollment phase transition detected." },

  // -------- Stall detection (Ebene 2) --------
  { value: "session_stalled", label: "session_stalled", category: "stall",
    description: "Agent reported 60+ minutes without progress. Fires once per session and triggers the backend Stalled status." },
  { value: "stall_probe_result", label: "stall_probe_result", category: "stall",
    description: "Stall probe found an anomaly (ModernDeployment error, DeploymentErrorCode≠0, IME EnforcementState Failed)." },
  { value: "stall_probe_check", label: "stall_probe_check", category: "stall",
    description: "Trace-level heartbeat from a stall probe — proves the logic ran (default: only the 15 min probe)." },

  // -------- ModernDeployment live capture (Ebene 1) --------
  { value: "modern_deployment_error", label: "modern_deployment_error", category: "esp",
    description: "Windows ModernDeployment-Diagnostics-Provider Critical/Error event forwarded live." },
  { value: "modern_deployment_warning", label: "modern_deployment_warning", category: "esp",
    description: "Windows ModernDeployment-Diagnostics-Provider Warning event forwarded live." },
  { value: "modern_deployment_log", label: "modern_deployment_log", category: "esp",
    description: "Windows ModernDeployment-Diagnostics-Provider informational event." },

  // -------- ESP signals --------
  { value: "esp_phase_changed", label: "esp_phase_changed", category: "esp",
    description: "ESP phase transition detected via IME log patterns." },
  { value: "esp_provisioning_status", label: "esp_provisioning_status", category: "esp",
    description: "ESP category status update from Windows Provisioning registry." },
  { value: "esp_state_change", label: "esp_state_change", category: "esp",
    description: "Generic ESP state change event." },
  { value: "esp_failure_advisory", label: "esp_failure_advisory", category: "esp",
    description: "ESP reported a subcategory failure but the device had already progressed to AccountSetup with ContinueAnyway enabled — non-terminal advisory, the agent continues monitoring." },

  // -------- App installs --------
  { value: "app_install_started", label: "app_install_started", category: "app",
    description: "An application installation has started." },
  { value: "app_install_completed", label: "app_install_completed", category: "app",
    description: "An application installation finished successfully." },
  { value: "app_install_failed", label: "app_install_failed", category: "app",
    description: "An application installation failed." },
  { value: "app_install_skipped", label: "app_install_skipped", category: "app",
    description: "An application installation was skipped." },
  { value: "app_download_started", label: "app_download_started", category: "app",
    description: "An application download has started." },
  { value: "download_progress", label: "download_progress", category: "app",
    description: "Download progress update (throttled)." },
  { value: "do_telemetry", label: "do_telemetry", category: "app",
    description: "Delivery Optimization peer/mode telemetry for an application download." },

  // -------- Scripts --------
  { value: "script_started", label: "script_started", category: "script",
    description: "Live indicator: IME has started executing a platform or health (remediation) script. The consolidated outcome arrives later via script_completed / script_failed (typically 30s-3min for health scripts)." },
  { value: "script_completed", label: "script_completed", category: "script",
    description: "A platform script completed, or one phase (detection / remediation / post-detection) of a health script completed. Health scripts emit up to three of these per policy run." },
  { value: "script_failed", label: "script_failed", category: "script",
    description: "A platform script crashed (exit code != 0), or the remediation phase of a health script crashed. Health-script detection / post-detection phases use exit code as a compliance verdict (non-zero = non-compliant) and emit script_completed even on non-zero exit; only a true script crash in those phases would surface here." },

  // -------- Diagnostics / misc --------
  { value: "error_detected", label: "error_detected", category: "diagnostics",
    description: "Generic error detected by the agent." },
  { value: "cert_validation", label: "cert_validation", category: "diagnostics",
    description: "Certificate validation result." },
  { value: "network_state_change", label: "network_state_change", category: "diagnostics",
    description: "Network state changed (online/offline/interface)." },
  { value: "vulnerability_report", label: "vulnerability_report", category: "diagnostics",
    description: "Aggregated vulnerability scan finding." },
  { value: "software_inventory_analysis", label: "software_inventory_analysis", category: "diagnostics",
    description: "Software inventory snapshot with high-confidence findings." },
  { value: "gather_result", label: "gather_result", category: "diagnostics",
    description: "Output of another gather rule (can be chained)." },
];

/** Lookup a single entry by its canonical value (case-insensitive). */
export function findEventType(value: string): EventTypeEntry | undefined {
  if (!value) return undefined;
  const lower = value.toLowerCase();
  return KNOWN_EVENT_TYPES.find((e) => e.value.toLowerCase() === lower);
}

/** Fuzzy filter for autocomplete — matches anywhere in value/label/description. */
export function filterEventTypes(query: string): EventTypeEntry[] {
  if (!query) return KNOWN_EVENT_TYPES;
  const lower = query.toLowerCase();
  return KNOWN_EVENT_TYPES.filter(
    (e) =>
      e.value.toLowerCase().includes(lower) ||
      e.label.toLowerCase().includes(lower) ||
      e.description.toLowerCase().includes(lower)
  );
}

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
  { value: "oobe_state_completed", label: "oobe_state_completed", category: "enrollment",
    description: "Windows OOBE state flipped InProgress→Completed (WinRT SystemSetupInfo, sampled on the desktop-arrival poll). One-shot and observational only; valuable as an owner-independent desktop corroboration in sessions where desktop_arrived never fires." },
  { value: "completion_check", label: "completion_check", category: "enrollment",
    description: "Throttled state-machine snapshot during completion evaluation." },
  { value: "completion_waiting", label: "completion_waiting", category: "enrollment",
    description: "Engine blocked or deferred a completion attempt; missingPrerequisites lists what it is still waiting on (deduped, emitted only when the set changes)." },
  { value: "agent_started", label: "agent_started", category: "enrollment",
    description: "Agent process started." },
  { value: "agent_shutdown", label: "agent_shutdown (V1)", category: "enrollment",
    description: "Agent process is shutting down (legacy V1 event type — V2 emits agent_shutting_down)." },
  { value: "agent_shutting_down", label: "agent_shutting_down (V2)", category: "enrollment",
    description: "Agent process is shutting down. V2 canonical event; reason field disambiguates (decision_terminal / max_lifetime / auth_failure / ctrl_c / process_exit / unhandled_exception)." },
  { value: "phase_transition", label: "phase_transition", category: "enrollment",
    description: "Enrollment phase transition detected." },
  { value: "keep_awake_engaged", label: "keep_awake_engaged", category: "enrollment",
    description: "Opt-in keep-awake hold engaged on entering the User-ESP (AccountSetup) phase — device is kept awake (system + display) so standby/sleep cannot stall app installs or account setup. Reboots are unaffected. Off by default per tenant." },
  { value: "keep_awake_released", label: "keep_awake_released", category: "enrollment",
    description: "Keep-awake hold released — reason is account_setup_complete (User-ESP finished), host_stop (session teardown), or safety_cap (the backstop timer elapsed without completion)." },

  // -------- Stall detection (Ebene 2) --------
  { value: "session_stalled", label: "session_stalled", category: "stall",
    description: "Agent reported 60+ minutes without progress. Fires once per session and triggers the backend Stalled status." },
  { value: "stall_probe_result", label: "stall_probe_result", category: "stall",
    description: "Stall probe found an anomaly (ModernDeployment error, DeploymentErrorCode≠0, IME EnforcementState Failed)." },
  { value: "stall_probe_check", label: "stall_probe_check", category: "stall",
    description: "Trace-level heartbeat from a stall probe — proves the logic ran (default: only the 15 min probe)." },
  { value: "esp_policy_provider_stalled", label: "esp_policy_provider_stalled", category: "stall",
    description: "The ESP's app-tracking wait (EnrollmentStatusTracking CSP) made no progress for 15+ minutes: a registered policy provider stayed incomplete (reason=provider_incomplete), or Setup/Apps providers exist without the expected Intune 'Sidecar' provider (reason=sidecar_provider_missing) — the co-management signature where only ConfigMgr is registered (even with TrackingPoliciesCreated=1) and the user ESP parks at 'Apps (Identifying)' with no OS timeout. One-shot Warning; payload carries reason, the full provider table, per-provider state, and sidecarRegistered." },
  { value: "agent_late_start", label: "agent_late_start", category: "stall",
    description: "Low observation coverage: the agent started long after device boot (≥10 min) AND lived only briefly (≤5 min) before a terminal outcome — it arrived after the enrollment had already decided, so its diagnosis is a post-mortem of the end-state, not a live observation. Carries bootToAgentStartSeconds, agentUptimeSeconds and outcome. Often paired with script_timeout_suspected (a script that hung ahead of the agent bootstrap)." },

  // -------- ModernDeployment live capture (Ebene 1) --------
  { value: "modern_deployment_error", label: "modern_deployment_error", category: "esp",
    description: "Windows ModernDeployment-Diagnostics-Provider Critical/Error event forwarded live." },
  { value: "modern_deployment_warning", label: "modern_deployment_warning", category: "esp",
    description: "Windows ModernDeployment-Diagnostics-Provider Warning event forwarded live." },
  { value: "modern_deployment_log", label: "modern_deployment_log", category: "esp",
    description: "Windows ModernDeployment-Diagnostics-Provider informational event." },

  // -------- Windows Update during OOBE --------
  { value: "windows_update_failed", label: "windows_update_failed", category: "esp",
    description: "A Windows quality/cumulative update FAILED to install during enrollment (WindowsUpdateClient EventID 20). Carries the decoded HRESULT (hresult + hresultSymbol), updateTitle and updateGuid. A cumulative update failing mid-OOBE is a known enrollment-breaker otherwise invisible in the Intune console." },
  { value: "windows_update_succeeded", label: "windows_update_succeeded", category: "esp",
    description: "A Windows quality/cumulative update installed during enrollment (WindowsUpdateClient EventID 19). Carries updateTitle and updateGuid." },
  { value: "windows_update_started", label: "windows_update_started", category: "esp",
    description: "Windows Update download/install started during enrollment (WindowsUpdateClient EventID 43/44). Debug context for the timeline." },
  { value: "windows_update_reboot_pending", label: "windows_update_reboot_pending", category: "esp",
    description: "Startup snapshot of the CBS RebootPending registry key. Present at agent start = a high-confidence corroborator that a (often pre-agent, OOBE-time) update landed and needs a reboot, which can delay/block completion. Gather rule GATHER-DEVICE-005 (on by default)." },
  { value: "windows_update_history", label: "windows_update_history", category: "esp",
    description: "Get-HotFix snapshot of recently installed updates (secondary corroboration). Gather rule GATHER-DEVICE-004 — opt-in (cmdlet cost; QFE-registered updates only)." },
  { value: "os_build_changed", label: "os_build_changed", category: "esp",
    description: "The OS build (CurrentBuild.UBR) differs across an agent restart — an update was installed during enrollment, no matter which servicing path it took. Deterministic corroboration for the Windows Update watcher (cannot be lost to timing, watermarks or unexpected logging channels). Carries previousBuild and currentBuild." },
  { value: "windows_update_channel_census", label: "windows_update_channel_census", category: "esp",
    description: "Blind-spot self-diagnosis: the OS build changed but zero targeted Windows Update events were captured. Carries an unfiltered EventID histogram of the WindowsUpdateClient and UpdateOrchestrator channels (wuClientCensus / updateOrchestratorCensus, 'id=count' format) — field evidence for which EventIDs to add to WindowsUpdateTargetedEventIds via remote config." },

  // -------- ESP signals --------
  { value: "esp_phase_changed", label: "esp_phase_changed", category: "esp",
    description: "ESP phase transition detected via IME log patterns." },
  { value: "esp_provisioning_status", label: "esp_provisioning_status", category: "esp",
    description: "ESP category status update from Windows Provisioning registry." },
  { value: "esp_state_change", label: "esp_state_change", category: "esp",
    description: "Generic ESP state change event." },
  { value: "esp_failure_advisory", label: "esp_failure_advisory", category: "esp",
    description: "ESP reported a subcategory failure but the device had already progressed to AccountSetup with ContinueAnyway enabled — non-terminal advisory, the agent continues monitoring." },
  { value: "esp_appx_failure_analysis", label: "esp_appx_failure_analysis", category: "esp",
    description: "AppX deployment log scan during the ESP failure settle window: suspected MSIX/Store package candidates behind an Apps-subcategory failure (assessment, not a confirmed root cause)." },
  { value: "esp_failure_retry_detected", label: "esp_failure_retry_detected", category: "esp",
    description: "After a terminally reported ESP failure, the failed subcategory left the failed state — consistent with the user pressing 'Try again' on the ESP failure page." },
  { value: "esp_failure_recovered", label: "esp_failure_recovered", category: "esp",
    description: "The ESP category behind a terminally reported failure later completed successfully — the earlier failure recovered (e.g. after a user retry)." },
  { value: "esp_failure_advisory_resolved", label: "esp_failure_advisory_resolved", category: "esp",
    description: "DecisionEngine: the category behind a defanged esp_failure_advisory resolved to success — the advisory no longer blocks completion." },

  // -------- App installs --------
  { value: "app_install_started", label: "app_install_started", category: "app",
    description: "An application installation has started." },
  { value: "app_install_completed", label: "app_install_completed", category: "app",
    description: "An application installation finished successfully." },
  { value: "app_install_failed", label: "app_install_failed", category: "app",
    description: "An application installation failed." },
  { value: "app_install_skipped", label: "app_install_skipped", category: "app",
    description: "An application installation was skipped." },
  { value: "app_install_starved", label: "app_install_starved", category: "app",
    description: "A required user-ESP app never started installing while the AccountSetup apps gate waited on it (one-shot per app)." },
  { value: "app_download_started", label: "app_download_started", category: "app",
    description: "An application download has started." },
  { value: "download_progress", label: "download_progress", category: "app",
    description: "Download progress update (throttled)." },
  { value: "do_telemetry", label: "do_telemetry", category: "app",
    description: "Delivery Optimization peer/mode telemetry for an application download." },
  { value: "office_install_started", label: "office_install_started", category: "app",
    description: "Microsoft 365 Apps (Office Click-to-Run) background install detected by the C2R detector." },
  { value: "office_install_progress", label: "office_install_progress", category: "app",
    description: "Office C2R install phase changed (Streaming/Finalizing) — emitted only on real state change, not periodically." },
  { value: "office_install_completed", label: "office_install_completed", category: "app",
    description: "Office C2R install completed (streaming finished, scenario cleared); carries version reached and duration." },
  { value: "office_install_failed", label: "office_install_failed", category: "app",
    description: "Office C2R install ended without reaching completion, or a failure code was observed." },
  { value: "office_preinstalled_detected", label: "office_preinstalled_detected", category: "app",
    description: "Office was already fully resident on disk at the first signal (OEM/consumer inbox Office running a background CLIENTUPDATE) — informational, not an enrollment install or failure." },

  // -------- Scripts --------
  { value: "script_started", label: "script_started", category: "script",
    description: "Live indicator: IME has started executing a platform or health (remediation) script. The consolidated outcome arrives later via script_completed / script_failed (typically 30s-3min for health scripts)." },
  { value: "script_completed", label: "script_completed", category: "script",
    description: "A platform script completed, or one phase (detection / remediation / post-detection) of a health script completed. Health scripts emit up to three of these per policy run." },
  { value: "script_failed", label: "script_failed", category: "script",
    description: "A platform script crashed (exit code != 0), or the remediation phase of a health script crashed. Health-script detection / post-detection phases use exit code as a compliance verdict (non-zero = non-compliant) and emit script_completed even on non-zero exit; only a true script crash in those phases would surface here." },
  { value: "script_timeout_suspected", label: "script_timeout_suspected", category: "script",
    description: "A platform script ran to the IME script-execution timeout (~30 min; threshold 25 min) and was marked Failed while the enrollment was in progress. IME runs platform scripts serially, so a hung script starves app installs and the Autopilot-Monitor bootstrap — the prime suspect behind a late agent start / pre-failed ESP. Advisory (one per policyId); carries durationSeconds, exitCode and espPhase." },
  { value: "historic_ime_replay_detected", label: "historic_ime_replay_detected", category: "diagnostics",
    description: "One-shot per agent run: the IME log contained replayed script and app activity from a previous enrollment (source lines more than 24 h stale, e.g. IME logs surviving a re-enrollment). The historic script_* and app_install_*/download_progress/do_telemetry events were suppressed for this session; carries earliestRejectedSourceTimestamp so the replayed window is datable." },

  // -------- Diagnostics / misc --------
  { value: "error_detected", label: "error_detected", category: "diagnostics",
    description: "Generic error detected by the agent." },
  { value: "cert_validation", label: "cert_validation", category: "diagnostics",
    description: "Certificate validation result." },
  { value: "network_state_change", label: "network_state_change", category: "diagnostics",
    description: "Network state changed (online/offline/interface)." },
  { value: "network_bandwidth_estimate", label: "network_bandwidth_estimate", category: "diagnostics",
    description: "Session-level internet-bandwidth estimate derived passively from the Delivery Optimization byte counters (no synthetic traffic). At most twice per session: an interim snapshot after DeviceSetup (snapshotTrigger=device_setup_end — survives account-phase starvation) and the authoritative final at enrollment end (collector_stop). p90/max Mbit/s for internet-path (HTTP + internet peers) and LAN sources (peers/Connected Cache) separately, plus bucket (<10 / 10-50 / 50-100 / 100-250 / 250+) and confidence." },
  { value: "vulnerability_report", label: "vulnerability_report", category: "diagnostics",
    description: "Aggregated vulnerability scan finding." },
  { value: "software_inventory_analysis", label: "software_inventory_analysis", category: "diagnostics",
    description: "Software inventory snapshot with high-confidence findings." },
  { value: "integrity_bypass_analysis", label: "integrity_bypass_analysis", category: "diagnostics",
    description: "Windows 11 install-time integrity-bypass audit (LabConfig TPM/SecureBoot/CPU/RAM/Disk bypass keys, MoSetup upgrade bypass, per-user PCHC eligibility, suspicious SetupComplete/ErrorHandler scripts)." },
  { value: "local_admin_analysis", label: "local_admin_analysis", category: "diagnostics",
    description: "Local administrator / user-profile audit detecting pre-enrollment admin account creation (an Autopilot bypass technique); emitted at startup and shutdown for delta detection." },
  { value: "autologon_analysis", label: "autologon_analysis", category: "diagnostics",
    description: "Winlogon AutoLogon facts (raw, Info-only): AutoAdminLogon/ForceAutoLogon, default user/domain, AutoLogon count, and presence-only of a plaintext DefaultPassword. AutoLogon-enabled alone is not graded (Windows' own ESP auto-logon looks identical on every normal enrollment); backend analyze-rules grade only a plaintext DefaultPassword on disk (ANALYZE-SEC-003) plus an optional kiosk allow-list template (ANALYZE-SEC-004). AutoLogon can be a legitimate kiosk or an enrollment/OOBE manipulation vector." },
  { value: "gather_result", label: "gather_result", category: "diagnostics",
    description: "Output of another gather rule (can be chained)." },
  { value: "provisioning_package_scan", label: "provisioning_package_scan", category: "diagnostics",
    description: "Single per-device provisioning-package (PPKG) scan at DeviceSetup-phase start. Carries an artifacts[] array — one entry per registry package, .ppkg file, or Recovery\\Customizations residue (each with a scalar identity) — plus .ppkg file metadata, registry package metadata, and best-effort content indicators. The PPKG analyze rules (ANALYZE-SEC-005/006) iterate the array against an allow-list regex. PPKGs can be legitimate bulk enrollment or a manipulation vector." },
  { value: "oobe_console_spawned", label: "oobe_console_spawned", category: "diagnostics",
    description: "LIVE detection of an interactive console opened during enrollment — the Shift+F10 OOBE bypass. Flags an interactive-session cmd.exe (SessionID != 0) with a bare, non-scripted command line (no /c or /k); confidence=high, or confidence=low when the command line was unreadable (instant-close). Warning severity. On by default, opt-out per tenant (EnableConsoleBypassDetection). Stopped once the real-user desktop arrives (Shift+F10 no longer possible). Best-effort, not parent-based: only consoles spawned after the agent started are observable (coverageComplete=false)." },
  { value: "console_prefetch_detected", label: "console_prefetch_detected", category: "diagnostics",
    description: "STARTUP-FORENSIC complement to oobe_console_spawned: a CMD.EXE-*.pf prefetch artifact whose last-run is after boot, covering the pre-agent OOBE window. Warning severity. Attribution is coarse — cmd.exe shares one prefetch file, so once ESP runs the timestamp cannot be attributed to Shift+F10 vs. a legitimate install-launched cmd (coverageComplete=false)." },
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

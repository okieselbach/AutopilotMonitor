/**
 * Source-of-truth for the static resource catalogs (event types, device
 * properties). Consumed by both `resources.ts` (MCP-protocol resources)
 * and the `get_resource` tool (workaround for stateless-HTTP MCP clients
 * whose resource discovery is broken).
 */

// Single source of truth for the model-facing event-type catalog. MUST stay in
// sync with C# `Constants.EventTypes` (Shared) — enforced by the vitest drift test
// in __tests__/event-types-drift.test.ts, which reads Constants.cs and asserts
// (all catalog values ∪ INTERNAL_EVENT_TYPES) === the C# const values. When the
// agent adds a new event type to Constants.EventTypes, add it to a group here too.
export const EVENT_TYPES_CATALOG = {
  phase_events: [
    'phase_transition',
    'esp_state_change',
    'esp_phase_changed',
    'esp_provisioning_status',
    'esp_ui_state',
    'esp_provisioning_raw',
    'esp_failure',
    'esp_failure_advisory',
    'esp_failure_settle_started',
    'esp_exiting',
    'esp_config_detected',
    'completion_check',
    'completion_waiting',
    'enrollment_complete',
    'enrollment_failed',
    'desktop_arrived',
    'desktop_detector_started',
    'desktop_detector_first_poll',
    'desktop_detector_no_candidate',
  ],
  whiteglove_events: [
    'whiteglove_complete',
    'whiteglove_started',
    'whiteglove_resumed',
    'whiteglove_part1_complete',
    'whiteglove_classification',
  ],
  hello_events: [
    'hello_policy_detected',
    'hello_policy_detection_mismatch',
    'hello_wait_timeout',
    'hello_completion_timeout',
    'hello_wizard_started',
    'hello_processing_started',
    'hello_processing_stopped',
    'hello_provisioning_completed',
    'hello_provisioning_failed',
    'hello_provisioning_blocked',
    'hello_pin_status',
    'hello_skipped',
    'waiting_for_hello',
  ],
  app_events: [
    'app_install_started',
    'app_install_completed',
    'app_install_failed',
    'app_download_started',
    'app_install_skipped',
    'app_install_starved',
    'download_progress',
    'do_telemetry',
    'all_apps_completed',
    'app_tracking_summary',
    'office_install_started',
    'office_install_progress',
    'office_install_completed',
    'office_install_failed',
  ],
  script_events: ['script_started', 'script_completed', 'script_failed'],
  network_events: [
    'network_state_change',
    'network_connectivity_check',
    'network_adapters',
    'network_interface_info',
    'dns_configuration',
    'proxy_configuration',
    'wifi_signal_info',
  ],
  device_info_events: [
    'os_info',
    'boot_time',
    'hardware_spec',
    'tpm_status',
    'secureboot_status',
    'bitlocker_status',
    'autopilot_profile',
    'enrollment_type_detected',
    'aad_join_status',
    'cert_validation',
    'device_location',
    'outbound_ip',
    'timezone_auto_set',
    'ntp_time_check',
    'power_state_check',
  ],
  identity_events: [
    'aad_placeholder_user_detected',
    'aad_user_joined_observed',
    'hybrid_login_pending',
  ],
  realmjoin_events: [
    'realmjoin_detected',
    'realmjoin_phase_changed',
    'realmjoin_resolved',
    'realmjoin_timeout',
    'realmjoin_package_started',
    'realmjoin_package_completed',
  ],
  deployment_events: [
    'modern_deployment_log',
    'modern_deployment_warning',
    'modern_deployment_error',
    'configmgr_client_detected',
  ],
  lifecycle_events: [
    'agent_started',
    'agent_shutting_down',
    'agent_shutdown',
    'agent_version_check',
    'ime_agent_version',
    'ime_user_session_completed',
    'ime_session_change',
    'ime_process_exited',
    'system_reboot_detected',
    'prior_run_died_with_state',
    'previous_crash_detected',
    'performance_collector_stopped',
    'agent_metrics_collector_stopped',
    'agent_unrestricted_mode_changed',
    'remote_config_fetch_failed',
    'agent_trace',
  ],
  health_events: [
    'stall_probe_check',
    'stall_probe_result',
    'session_stalled',
    'spool_pressure_detected',
    'collector_degraded',
    'state_quarantine_recovered',
    'telemetry_upload_poisoned',
    'telemetry_upload_blocked',
    'session_parked_without_deadline',
  ],
  metrics_events: ['performance_snapshot', 'agent_metrics_snapshot', 'ingress_backpressure'],
  security_events: [
    'integrity_bypass_analysis',
    'local_admin_analysis',
    'autologon_analysis',
    'security_warning',
    'security_audit',
    'provisioning_package_scan',
  ],
  vulnerability_events: ['software_inventory_analysis', 'vulnerability_report'],
  diagnostics_events: ['diagnostics_collecting', 'diagnostics_uploaded', 'diagnostics_upload_failed'],
  server_action_events: [
    'server_action_received',
    'server_action_executed',
    'server_action_failed',
    'admin_marked_session',
  ],
  gather_events: ['gather_result', 'gather_rules_collection_started', 'gather_rules_collection_completed'],
  termination_events: ['enrollment_summary_shown', 'reboot_triggered', 'session_timeout'],
  classification_events: ['enrollment_type_mismatch', 'decision_process_completion'],
  other: ['error_detected', 'log_entry', 'esp_resumed', 'esp_provisioning_settle_started'],
} as const;

/**
 * Event types that exist on the wire but are intentionally NOT advertised in the
 * public catalog (TEMP / internal). They ARE searchable (kept in ALL_EVENT_TYPES)
 * so historical recall works, but the model shouldn't treat them as a normal type.
 */
export const INTERNAL_EVENT_TYPES = ['shadow_discrepancy'] as const;

/**
 * Flat list of every known event type (public catalog + internal). Used by the
 * search tools for keyword→event-type candidate matching, where complete recall
 * matters more than hiding TEMP types.
 */
export const ALL_EVENT_TYPES: readonly string[] = [
  ...Object.values(EVENT_TYPES_CATALOG).flat(),
  ...INTERNAL_EVENT_TYPES,
];

/**
 * Event-type catalog as search documents for semantic candidate selection. Indexed
 * into a SearchProvider at startup so a query ("app stuck downloading") can map to the
 * closest event types ("download_progress", "do_telemetry") even without lexical overlap.
 * Text is the de-snaked type plus its group, which embeds far better than snake_case.
 */
export function buildEventTypeSearchDocs(): Array<{ id: string; text: string; metadata: { eventType: string; group: string } }> {
  const docs: Array<{ id: string; text: string; metadata: { eventType: string; group: string } }> = [];
  const deSnake = (s: string) => s.replace(/_/g, ' ');
  for (const [group, types] of Object.entries(EVENT_TYPES_CATALOG)) {
    const groupWords = deSnake(group).replace(/ events$/, '');
    for (const t of types) {
      docs.push({ id: t, text: `${deSnake(t)} (${groupWords})`, metadata: { eventType: t, group } });
    }
  }
  for (const t of INTERNAL_EVENT_TYPES) {
    docs.push({ id: t, text: deSnake(t), metadata: { eventType: t, group: 'internal' } });
  }
  return docs;
}

export const DEVICE_PROPERTIES_CATALOG = {
  _usage: {
    note: 'Use these keys in the deviceProperties parameter of search_sessions.',
    booleanFormat: 'Use "True" or "False" (case-insensitive)',
    numericOperators: 'Prefix value with >=, <=, >, or < for range filters (e.g. ">=8")',
    arraySearch: 'For array properties, value is matched as substring in any array element',
  },
  tpm_status: {
    'tpm_status.specVersion': { type: 'string', description: 'TPM specification version (e.g. "2.0", "1.2")' },
    'tpm_status.manufacturerName': { type: 'string', description: 'TPM manufacturer (e.g. "INTC", "IFX")' },
    'tpm_status.manufacturerVersion': { type: 'string', description: 'TPM firmware version' },
    'tpm_status.isActivated': { type: 'boolean', description: 'TPM activation status' },
    'tpm_status.isEnabled': { type: 'boolean', description: 'TPM enabled status' },
    'tpm_status.isOwned': { type: 'boolean', description: 'TPM ownership status' },
  },
  secureboot_status: {
    'secureboot_status.uefiSecureBootEnabled': { type: 'boolean', description: 'Secure Boot enabled' },
  },
  bitlocker_status: {
    'bitlocker_status.systemDriveProtected': { type: 'boolean', description: 'System drive (C:) BitLocker protection' },
    'bitlocker_status.volumes': { type: 'array', description: 'All BitLocker volumes — search by volume letter or protection status' },
  },
  autopilot_profile: {
    'autopilot_profile.autopilotModeLabel': { type: 'string', description: 'Deployment mode (e.g. "User Driven (0)", "Self-Deploying (1)")' },
    'autopilot_profile.domainJoinMethodLabel': { type: 'string', description: 'Join method (e.g. "Entra Join", "Hybrid Azure AD Join")' },
    'autopilot_profile.CloudAssignedEspEnabled': { type: 'boolean', description: 'ESP enabled in Autopilot profile' },
    'autopilot_profile.CloudAssignedDeviceName': { type: 'string', description: 'Cloud-assigned device name template' },
    'autopilot_profile.CloudAssignedLanguage': { type: 'string', description: 'Cloud-assigned language/locale' },
  },
  hardware_spec: {
    'hardware_spec.cpuName': { type: 'string', description: 'CPU model name (e.g. "Intel Core i7-1265U")' },
    'hardware_spec.cpuCores': { type: 'number', description: 'Number of physical CPU cores' },
    'hardware_spec.cpuLogicalProcessors': { type: 'number', description: 'Number of logical processors' },
    'hardware_spec.cpuArchitecture': { type: 'string', description: 'CPU/device architecture: "x86", "x64", "ARM", "ARM64". Use "ARM*" to match all ARM devices (ARM + ARM64), "x*" for all Intel/AMD.' },
    'hardware_spec.ramTotalGB': { type: 'number', description: 'Total RAM in GB. Use >=N for minimum (e.g. ">=8")' },
    'hardware_spec.ramSpeed': { type: 'string', description: 'RAM speed (e.g. "3200 MHz")' },
    'hardware_spec.ramType': { type: 'string', description: 'RAM type (e.g. "DDR4", "LPDDR5")' },
    'hardware_spec.diskCount': { type: 'number', description: 'Number of physical disks' },
    'hardware_spec.hasSSD': { type: 'boolean', description: 'Has SSD or NVMe storage (computed)' },
    'hardware_spec.disks': { type: 'array', description: 'Disk details — search for "NVMe", "SSD", disk model names' },
    'hardware_spec.systemDriveFreeGB': { type: 'number', description: 'Free space on C: in GB' },
    'hardware_spec.systemDriveTotalGB': { type: 'number', description: 'Total C: drive size in GB' },
    'hardware_spec.biosVersion': { type: 'string', description: 'BIOS/UEFI version string' },
    'hardware_spec.batteryPresent': { type: 'boolean', description: 'Device has a battery' },
    'hardware_spec.batteryHealthPercent': { type: 'number', description: 'Battery health percentage' },
    'hardware_spec.gpus': { type: 'array', description: 'GPU details — search for GPU model names' },
  },
  network_interface_info: {
    'network_interface_info.connectionType': { type: 'string', description: 'Connection type: "WiFi" or "Ethernet"' },
    'network_interface_info.linkSpeedMbps': { type: 'number', description: 'Link speed in Mbps' },
    'network_interface_info.adapterName': { type: 'string', description: 'Network adapter name' },
    'network_interface_info.macAddress': { type: 'string', description: 'MAC address of active adapter' },
  },
  aad_join_status: {
    'aad_join_status.joinType': { type: 'string', description: 'Azure AD join type' },
    'aad_join_status.tenantId': { type: 'string', description: 'Entra ID tenant' },
    'aad_join_status.deviceId': { type: 'string', description: 'Entra device object ID' },
  },
} as const;

// ── Filter-key validation ─────────────────────────────────────────────────
//
// Without these, an invalid eventType / deviceProperties key produces the SAME
// `count: 0` as a genuine miss, so "bad filter" is indistinguishable from "no
// matches". The search tools call these to reject a typo with a clear, correctable
// error instead of a silent empty result.

/** Set form of ALL_EVENT_TYPES for O(1) membership checks. */
const EVENT_TYPE_SET = new Set<string>(ALL_EVENT_TYPES);

/** True if `s` is a known event type (public catalog ∪ internal). */
export function isKnownEventType(s: string): boolean {
  return EVENT_TYPE_SET.has(s);
}

/**
 * Every catalogued deviceProperties key, flattened across event-type groups
 * (excluding the `_usage` doc block). This is a CURATED subset — not exhaustive —
 * because the backend reconstructs arbitrary `Props_*` columns. Use it for hints,
 * never for hard rejection of a full key.
 */
export const ALL_DEVICE_PROPERTY_KEYS: readonly string[] = Object.entries(DEVICE_PROPERTIES_CATALOG)
  .filter(([group]) => group !== '_usage')
  .flatMap(([, props]) => Object.keys(props as Record<string, unknown>));

/** Event-type prefix of a deviceProperties key ("tpm_status.specVersion" → "tpm_status"). */
export function eventTypePrefixOf(key: string): string {
  const dot = key.indexOf('.');
  return dot === -1 ? key : key.slice(0, dot);
}

/** Up to 5 catalogued event types that loosely resemble `input`, for "did you mean" hints. */
function suggestEventTypes(input: string): string[] {
  const lower = input.toLowerCase();
  const token = lower.split(/[_.]/)[0] ?? '';
  return ALL_EVENT_TYPES
    .filter((t) => t.includes(lower) || (token.length >= 3 && t.includes(token)))
    .slice(0, 5);
}

/**
 * Throws a descriptive Error if `eventType` is not a known type. The thrown
 * message routes through the tools' `toolError` handler so the model gets an
 * actionable correction instead of a misleading empty result.
 */
export function assertKnownEventType(eventType: string): void {
  if (isKnownEventType(eventType)) return;
  const suggestions = suggestEventTypes(eventType);
  const hint = suggestions.length > 0 ? ` Did you mean: ${suggestions.join(', ')}?` : '';
  throw new Error(
    `Unknown eventType "${eventType}" — it is not in the event_types catalog.${hint} ` +
    'Call get_resource(name="event_types") for the full list of valid event types.',
  );
}

/**
 * Throws if any deviceProperties key's event-type prefix is unknown (e.g. the
 * typo "tmp_status.x"). Keys whose prefix is valid but whose full name isn't
 * catalogued are allowed through — the catalog is a curated subset and the backend
 * accepts arbitrary `Props_*` columns, so hard-rejecting them would be a regression.
 */
export function assertKnownDevicePropertyKeys(keys: string[]): void {
  const bad = keys.filter((k) => !isKnownEventType(eventTypePrefixOf(k)));
  if (bad.length === 0) return;
  throw new Error(
    `Unknown deviceProperties key prefix(es): ${bad.join(', ')}. ` +
    'Each key is "eventType.propertyName"; the part before the first "." must be a known event type. ' +
    'Call get_resource(name="device_properties") for valid keys.',
  );
}

export type ResourceName = 'event_types' | 'device_properties';

export function getResourceContent(name: ResourceName): unknown {
  switch (name) {
    case 'event_types':
      return EVENT_TYPES_CATALOG;
    case 'device_properties':
      return DEVICE_PROPERTIES_CATALOG;
  }
}

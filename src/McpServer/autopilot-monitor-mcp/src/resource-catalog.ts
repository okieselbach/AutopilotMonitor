/**
 * Source-of-truth for the static resource catalogs (event types, device
 * properties). Consumed by both `resources.ts` (MCP-protocol resources)
 * and the `get_resource` tool (workaround for stateless-HTTP MCP clients
 * whose resource discovery is broken).
 */

export const EVENT_TYPES_CATALOG = {
  phase_events: [
    'phase_transition',
    'esp_state_change',
    'completion_check',
    'enrollment_complete',
    'enrollment_failed',
    'desktop_arrived',
  ],
  app_events: [
    'app_install_started',
    'app_install_completed',
    'app_install_failed',
    'app_download_started',
    'app_install_skipped',
  ],
  network_events: ['network_state_change', 'network_connectivity_check'],
  device_info_events: [
    'os_info',
    'hardware_spec',
    'tpm_status',
    'autopilot_profile',
    'secureboot_status',
    'bitlocker_status',
    'network_adapters',
    'network_interface_info',
    'aad_join_status',
    'enrollment_type_detected',
  ],
  error_events: ['error_detected'],
  vulnerability_events: ['software_inventory_analysis', 'vulnerability_report'],
  other: [
    'performance_snapshot',
    'log_entry',
    'gather_result',
    'script_started',
    'script_completed',
    'script_failed',
    'ime_agent_version',
  ],
} as const;

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

export type ResourceName = 'event_types' | 'device_properties';

export function getResourceContent(name: ResourceName): unknown {
  switch (name) {
    case 'event_types':
      return EVENT_TYPES_CATALOG;
    case 'device_properties':
      return DEVICE_PROPERTIES_CATALOG;
  }
}

/**
 * AUTO-GENERATED from rules/guardrails.json — DO NOT EDIT.
 * Run: node rules/scripts/combine.js
 */

// ---------------------------------------------------------------------------
// Categorized data (for documentation / UI display)
// ---------------------------------------------------------------------------

export interface GuardrailCategory {
  readonly category: string;
  readonly items: readonly string[];
}

export const REGISTRY_PREFIX_CATEGORIES: readonly GuardrailCategory[] = [
  {
    category: "MDM / Enrollment",
    items: [
      "SOFTWARE\\Microsoft\\Enrollments",
      "SOFTWARE\\Microsoft\\EnterpriseDesktopAppManagement",
      "SOFTWARE\\Microsoft\\Provisioning",
      "SOFTWARE\\Microsoft\\PolicyManager",
      "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MDM",
    ],
  },
  {
    category: "AAD / Entra Join",
    items: [
      "SOFTWARE\\Microsoft\\IdentityStore",
      "SYSTEM\\CurrentControlSet\\Control\\CloudDomainJoin",
    ],
  },
  {
    category: "Windows Update / WUfB",
    items: [
      "SOFTWARE\\Microsoft\\WindowsUpdate",
      "SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate",
    ],
  },
  {
    category: "BitLocker",
    items: [
      "SOFTWARE\\Microsoft\\BitLocker",
      "SYSTEM\\CurrentControlSet\\Control\\BitLockerStatus",
    ],
  },
  {
    category: "Network / Proxy",
    items: [
      "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Internet Settings",
      "SYSTEM\\CurrentControlSet\\Services\\Tcpip",
    ],
  },
  {
    category: "Autopilot / OOBE / Setup",
    items: [
      "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Setup",
      "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\OOBE",
      "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon",
    ],
  },
  {
    category: "TPM",
    items: [
      "SYSTEM\\CurrentControlSet\\Services\\TPM",
      "SOFTWARE\\Microsoft\\Tpm",
    ],
  },
  {
    category: "Secure Boot",
    items: [
      "SYSTEM\\CurrentControlSet\\Control\\SecureBoot",
    ],
  },
  {
    category: "Intune IME",
    items: [
      "SOFTWARE\\Microsoft\\IntuneManagementExtension",
    ],
  },
  {
    category: "Certificates (SCEP)",
    items: [
      "SOFTWARE\\Microsoft\\SystemCertificates",
      "SOFTWARE\\Policies\\Microsoft\\SystemCertificates",
    ],
  },
  {
    category: "Servicing",
    items: [
      "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Component Based Servicing",
    ],
  },
  {
    category: "RealmJoin",
    items: [
      "SYSTEM\\CurrentControlSet\\Services\\realmjoin",
      "SOFTWARE\\RealmJoin",
    ],
  },
];

export const COMMAND_CATEGORIES: readonly GuardrailCategory[] = [
  {
    category: "TPM and Security",
    items: [
      "Get-Tpm",
      "Get-SecureBootPolicy",
      "Get-SecureBootUEFI -Name SetupMode",
    ],
  },
  {
    category: "BitLocker",
    items: [
      "Get-BitLockerVolume -MountPoint C:",
    ],
  },
  {
    category: "Network",
    items: [
      "Get-NetAdapter | Select-Object Name, Status, InterfaceDescription, MacAddress, LinkSpeed",
      "Get-DnsClientServerAddress | Select-Object InterfaceAlias, ServerAddresses",
      "Get-NetIPConfiguration | Select-Object InterfaceAlias, IPv4Address, IPv4DefaultGateway, DNSServer",
      "netsh winhttp show proxy",
      "ipconfig /all",
    ],
  },
  {
    category: "Domain / Identity",
    items: [
      "nltest /dsgetdc:",
      "dsregcmd /status",
    ],
  },
  {
    category: "Certificate",
    items: [
      "certutil -store My",
      "$c = Get-ChildItem Cert:\\LocalMachine\\My; if ($c) { $c | Select-Object Subject, Thumbprint, Issuer, NotBefore, NotAfter, HasPrivateKey | ConvertTo-Json } else { '{\"CertificateCount\": 0}' }",
    ],
  },
  {
    category: "Windows Update",
    items: [
      "Get-HotFix | Select-Object -First 10 HotFixID, InstalledOn, Description",
    ],
  },
  {
    category: "Autopilot / Hardware Identity",
    items: [
      "try { $cs = Get-CimInstance -ClassName Win32_ComputerSystem; $bios = Get-CimInstance -ClassName Win32_BIOS; $hash = $null; try { $hash = (Get-CimInstance -Namespace root/cimv2/mdm/dmmap -ClassName MDM_DevDetail_Ext01 -Filter \"InstanceID='Ext' AND ParentID='./DevDetail'\" -ErrorAction Stop).DeviceHardwareData } catch { $hash = \"ERROR: $($_.Exception.Message)\" }; [pscustomobject]@{ Manufacturer = $cs.Manufacturer; Model = $cs.Model; SystemSKU = $cs.SystemSKUNumber; SerialNumber = $bios.SerialNumber; BiosVersion = $bios.SMBIOSBIOSVersion; HardwareHash = $hash } | ConvertTo-Json -Compress } catch { @{ error = $_.Exception.Message } | ConvertTo-Json -Compress }",
    ],
  },
];

export const EVENT_LOG_CHANNEL_CATEGORIES: readonly GuardrailCategory[] = [
  {
    category: "Core Windows logs",
    items: [
      "Application",
      "System",
      "Setup",
      "Microsoft-Windows-Kernel-Boot",
      "Microsoft-Windows-Diagnostics-Performance",
    ],
  },
  {
    category: "MDM / Enrollment",
    items: [
      "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider",
      "Microsoft-Windows-ModernDeployment-Diagnostics-Provider",
      "Microsoft-Windows-Provisioning-Diagnostics-Provider",
      "Microsoft-Windows-AAD",
      "Microsoft-Windows-User Device Registration",
    ],
  },
  {
    category: "ESP / Shell / Apps",
    items: [
      "Microsoft-Windows-Shell-Core",
      "Microsoft-Windows-AppXDeployment",
      "Microsoft-Windows-AppXDeploymentServer",
      "Microsoft-Windows-AppReadiness",
      "Microsoft-Windows-Store",
    ],
  },
  {
    category: "Security posture / Crypto",
    items: [
      "Microsoft-Windows-BitLocker-API",
      "Microsoft-Windows-BitLocker-DrivePreparationTool",
      "Microsoft-Windows-TPM-WMI",
      "Microsoft-Windows-CertificateServicesClient-Lifecycle-System",
    ],
  },
  {
    category: "Update / Servicing",
    items: [
      "Microsoft-Windows-WindowsUpdateClient",
    ],
  },
  {
    category: "Logon / Session",
    items: [
      "Microsoft-Windows-Winlogon",
      "Microsoft-Windows-User Profile Service",
      "Microsoft-Windows-GroupPolicy",
      "Microsoft-Windows-TaskScheduler",
    ],
  },
  {
    category: "Network",
    items: [
      "Microsoft-Windows-NetworkProfile",
      "Microsoft-Windows-Dhcp-Client",
      "Microsoft-Windows-DNS-Client",
      "Microsoft-Windows-NCSI",
      "Microsoft-Windows-WLAN-AutoConfig",
      "Microsoft-Windows-Time-Service",
    ],
  },
];

// ---------------------------------------------------------------------------
// Flat arrays (for validation logic)
// ---------------------------------------------------------------------------

export const ALLOWED_REGISTRY_PREFIXES: readonly string[] = [
  "SOFTWARE\\Microsoft\\Enrollments",
  "SOFTWARE\\Microsoft\\EnterpriseDesktopAppManagement",
  "SOFTWARE\\Microsoft\\Provisioning",
  "SOFTWARE\\Microsoft\\PolicyManager",
  "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MDM",
  "SOFTWARE\\Microsoft\\IdentityStore",
  "SYSTEM\\CurrentControlSet\\Control\\CloudDomainJoin",
  "SOFTWARE\\Microsoft\\WindowsUpdate",
  "SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate",
  "SOFTWARE\\Microsoft\\BitLocker",
  "SYSTEM\\CurrentControlSet\\Control\\BitLockerStatus",
  "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Internet Settings",
  "SYSTEM\\CurrentControlSet\\Services\\Tcpip",
  "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Setup",
  "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\OOBE",
  "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon",
  "SYSTEM\\CurrentControlSet\\Services\\TPM",
  "SOFTWARE\\Microsoft\\Tpm",
  "SYSTEM\\CurrentControlSet\\Control\\SecureBoot",
  "SOFTWARE\\Microsoft\\IntuneManagementExtension",
  "SOFTWARE\\Microsoft\\SystemCertificates",
  "SOFTWARE\\Policies\\Microsoft\\SystemCertificates",
  "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Component Based Servicing",
  "SYSTEM\\CurrentControlSet\\Services\\realmjoin",
  "SOFTWARE\\RealmJoin",
];

export const ALLOWED_FILE_PREFIXES: readonly string[] = [
  "C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs",
  "C:\\Windows\\CCM\\Logs",
  "C:\\Windows\\Logs",
  "C:\\Windows\\Panther",
  "C:\\Windows\\SetupDiag",
  "C:\\ProgramData\\Microsoft\\DiagnosticLogCSP",
  "C:\\Windows\\SoftwareDistribution\\ReportingEvents.log",
];

export const ALLOWED_WMI_QUERY_PREFIXES: readonly string[] = [
  "SELECT * FROM Win32_OperatingSystem",
  "SELECT * FROM Win32_ComputerSystem",
  "SELECT * FROM Win32_BIOS",
  "SELECT * FROM Win32_Processor",
  "SELECT * FROM Win32_BaseBoard",
  "SELECT * FROM Win32_Battery",
  "SELECT * FROM Win32_TPM",
  "SELECT * FROM Win32_NetworkAdapter",
  "SELECT * FROM Win32_NetworkAdapterConfiguration",
  "SELECT * FROM Win32_DiskDrive",
  "SELECT * FROM Win32_LogicalDisk",
  "SELECT * FROM SoftwareLicensingProduct",
];

export const ALLOWED_COMMANDS_LIST: readonly string[] = [
  "Get-Tpm",
  "Get-SecureBootPolicy",
  "Get-SecureBootUEFI -Name SetupMode",
  "Get-BitLockerVolume -MountPoint C:",
  "Get-NetAdapter | Select-Object Name, Status, InterfaceDescription, MacAddress, LinkSpeed",
  "Get-DnsClientServerAddress | Select-Object InterfaceAlias, ServerAddresses",
  "Get-NetIPConfiguration | Select-Object InterfaceAlias, IPv4Address, IPv4DefaultGateway, DNSServer",
  "netsh winhttp show proxy",
  "ipconfig /all",
  "nltest /dsgetdc:",
  "dsregcmd /status",
  "certutil -store My",
  "$c = Get-ChildItem Cert:\\LocalMachine\\My; if ($c) { $c | Select-Object Subject, Thumbprint, Issuer, NotBefore, NotAfter, HasPrivateKey | ConvertTo-Json } else { '{\"CertificateCount\": 0}' }",
  "Get-HotFix | Select-Object -First 10 HotFixID, InstalledOn, Description",
  "try { $cs = Get-CimInstance -ClassName Win32_ComputerSystem; $bios = Get-CimInstance -ClassName Win32_BIOS; $hash = $null; try { $hash = (Get-CimInstance -Namespace root/cimv2/mdm/dmmap -ClassName MDM_DevDetail_Ext01 -Filter \"InstanceID='Ext' AND ParentID='./DevDetail'\" -ErrorAction Stop).DeviceHardwareData } catch { $hash = \"ERROR: $($_.Exception.Message)\" }; [pscustomobject]@{ Manufacturer = $cs.Manufacturer; Model = $cs.Model; SystemSKU = $cs.SystemSKUNumber; SerialNumber = $bios.SerialNumber; BiosVersion = $bios.SMBIOSBIOSVersion; HardwareHash = $hash } | ConvertTo-Json -Compress } catch { @{ error = $_.Exception.Message } | ConvertTo-Json -Compress }",
];

export const ALLOWED_DIAGNOSTICS_PATH_PREFIXES: readonly string[] = [
  "C:\\ProgramData\\AutopilotMonitor",
  "C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs",
  "C:\\Windows\\Panther",
  "C:\\Windows\\Logs",
  "C:\\Windows\\SetupDiag",
  "C:\\Windows\\SoftwareDistribution\\ReportingEvents.log",
  "C:\\Windows\\System32\\winevt\\Logs",
  "C:\\Windows\\CCM\\Logs",
  "C:\\ProgramData\\Microsoft\\DiagnosticLogCSP",
  "C:\\ProgramData\\Microsoft\\Windows\\WER",
  "C:\\Windows\\Logs\\CBS",
  "C:\\Install\\Log",
];

export const BLOCKED_FILE_PREFIXES: readonly string[] = [
  "C:\\Users",
];

export const ALLOWED_EVENT_LOG_CHANNELS: readonly string[] = [
  "Application",
  "System",
  "Setup",
  "Microsoft-Windows-Kernel-Boot",
  "Microsoft-Windows-Diagnostics-Performance",
  "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider",
  "Microsoft-Windows-ModernDeployment-Diagnostics-Provider",
  "Microsoft-Windows-Provisioning-Diagnostics-Provider",
  "Microsoft-Windows-AAD",
  "Microsoft-Windows-User Device Registration",
  "Microsoft-Windows-Shell-Core",
  "Microsoft-Windows-AppXDeployment",
  "Microsoft-Windows-AppXDeploymentServer",
  "Microsoft-Windows-AppReadiness",
  "Microsoft-Windows-Store",
  "Microsoft-Windows-BitLocker-API",
  "Microsoft-Windows-BitLocker-DrivePreparationTool",
  "Microsoft-Windows-TPM-WMI",
  "Microsoft-Windows-CertificateServicesClient-Lifecycle-System",
  "Microsoft-Windows-WindowsUpdateClient",
  "Microsoft-Windows-Winlogon",
  "Microsoft-Windows-User Profile Service",
  "Microsoft-Windows-GroupPolicy",
  "Microsoft-Windows-TaskScheduler",
  "Microsoft-Windows-NetworkProfile",
  "Microsoft-Windows-Dhcp-Client",
  "Microsoft-Windows-DNS-Client",
  "Microsoft-Windows-NCSI",
  "Microsoft-Windows-WLAN-AutoConfig",
  "Microsoft-Windows-Time-Service",
];

export const BLOCKED_EVENT_LOG_CHANNELS: readonly string[] = [
  "Security",
  "Microsoft-Windows-PowerShell",
  "Windows PowerShell",
  "Microsoft-Windows-Sysmon",
];

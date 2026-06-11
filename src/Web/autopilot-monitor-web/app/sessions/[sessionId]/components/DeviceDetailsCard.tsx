"use client";

import { useState, useMemo } from "react";
import { EnrollmentEvent } from "@/types";
import OobeConfigModal from "./OobeConfigModal";
import { compareVersions, stripGitHashSuffix } from "@/utils/bootstrapVersion";

export default function DeviceDetailsCard({ events, latestAgentVersion }: { events: EnrollmentEvent[]; latestAgentVersion?: string | null }) {
  const [expanded, setExpanded] = useState(false);
  const [showIpv6, setShowIpv6] = useState<Record<number, boolean>>({});
  const [showOobeModal, setShowOobeModal] = useState(false);

  const getEventData = (eventType: string): Record<string, any> | null => {
    const matchingEvents = events.filter(e => e.eventType === eventType);
    if (matchingEvents.length === 0) return null;
    const latestEvent = matchingEvents[matchingEvents.length - 1];
    return latestEvent?.data ?? null;
  };

  const isIpv6 = (ip: string): boolean => {
    if (!ip || typeof ip !== 'string') return false;
    const colonCount = (ip.match(/:/g) || []).length;
    return colonCount >= 2;
  };

  const splitIpAddresses = (ipAddresses: string | string[]): { ipv4: string[]; ipv6: string[] } => {
    let ips: string[];
    if (Array.isArray(ipAddresses)) {
      ips = ipAddresses;
    } else if (typeof ipAddresses === 'string') {
      ips = ipAddresses.split(',').map(ip => ip.trim()).filter(ip => ip.length > 0);
    } else {
      ips = [];
    }

    const ipv4: string[] = [];
    const ipv6: string[] = [];

    for (const ip of ips) {
      if (typeof ip === 'string' && ip.trim()) {
        if (isIpv6(ip)) {
          ipv6.push(ip);
        } else {
          ipv4.push(ip);
        }
      }
    }

    return { ipv4, ipv6 };
  };

  const getBitLockerEncryptionMethodLabel = (value: unknown): string => {
    const method = value?.toString();
    const names: Record<string, string> = {
      "0": "None / Unknown",
      "1": "AES-128 mit Diffuser (legacy)",
      "2": "AES-256 mit Diffuser (legacy)",
      "3": "AES-128",
      "4": "AES-256",
      "5": "Hardware Encryption",
      "6": "XTS-AES 256",
      "7": "XTS-AES 128",
      "8": "Hardware Encryption (Full Disk)",
      "9": "Hardware Encryption (Data Only)",
    };

    if (!method) return "Unknown";
    return names[method] ?? `Unknown (${method})`;
  };

  const normalizeAutopilotProfile = (profile: Record<string, any> | null): Record<string, any> | null => {
    if (!profile) return null;

    const normalized = { ...profile };
    const policyJsonCache = normalized.PolicyJsonCache ?? normalized.policyJsonCache;

    if (typeof policyJsonCache === "string" && policyJsonCache.trim()) {
      try {
        const parsed = JSON.parse(policyJsonCache);
        if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) {
          Object.assign(normalized, parsed);
        }
      } catch {
        // Ignore malformed cache payload and keep original fields
      }
    }

    const aadServerData = normalized.CloudAssignedAadServerData;
    if (typeof aadServerData === "string" && aadServerData.trim()) {
      try {
        const parsed = JSON.parse(aadServerData);
        const zeroTouchConfig = parsed?.ZeroTouchConfig;
        if (!normalized.CloudAssignedTenantDomain && zeroTouchConfig?.CloudAssignedTenantDomain) {
          normalized.CloudAssignedTenantDomain = zeroTouchConfig.CloudAssignedTenantDomain;
        }
        if (normalized.CloudAssignedForcedEnrollment === undefined && zeroTouchConfig?.ForcedEnrollment !== undefined) {
          normalized.CloudAssignedForcedEnrollment = zeroTouchConfig.ForcedEnrollment;
        }
      } catch {
        // Ignore malformed nested JSON
      }
    }

    return normalized;
  };

  const hasValue = (value: unknown): boolean => value !== undefined && value !== null && `${value}` !== "";

  const agentStarted = getEventData("agent_started");
  const bootTime = getEventData("boot_time");

  const estimatedBootTime = (bootTime?.bootTimeUtc || bootTime?.bootTime)
    ? new Date(bootTime?.bootTimeUtc ?? bootTime?.bootTime)
    : null;

  const uptimeUntilEnrollment = useMemo(() => {
    if (!bootTime || events.length === 0) return null;
    const bootTimeStr = bootTime.bootTimeUtc ?? bootTime.bootTime;
    if (!bootTimeStr) return null;
    const bootTimeMs = new Date(bootTimeStr).getTime();
    if (isNaN(bootTimeMs)) return null;

    const firstEventMs = Math.min(...events.map(e => new Date(e.timestamp).getTime()));
    const diffMs = firstEventMs - bootTimeMs;

    if (diffMs >= 0) {
      const totalMinutes = Math.floor(diffMs / 60000);
      const hours = Math.floor(totalMinutes / 60);
      const minutes = totalMinutes % 60;
      return `${hours}h ${minutes}m`;
    }

    const uptimeMinutes = typeof bootTime.uptimeMinutes === 'number' ? bootTime.uptimeMinutes : null;
    if (uptimeMinutes !== null && uptimeMinutes >= 0) {
      const hours = Math.floor(uptimeMinutes / 60);
      const minutes = uptimeMinutes % 60;
      return `${hours}h ${minutes}m`;
    }

    return null;
  }, [bootTime, events]);

  const osInfo = getEventData("os_info");
  const networkAdapters = getEventData("network_adapters");
  const dnsConfig = getEventData("dns_configuration");
  const proxyConfig = getEventData("proxy_configuration");
  const networkInterfaceInfo = getEventData("network_interface_info");
  const wifiSignalInfo = getEventData("wifi_signal_info");
  const autopilotProfile = normalizeAutopilotProfile(getEventData("autopilot_profile"));
  const aadJoinStatus = getEventData("aad_join_status");
  const imeVersion = getEventData("ime_agent_version");
  const realmJoinInfo = getEventData("realmjoin_detected");
  const bitLockerStatus = getEventData("bitlocker_status");
  const secureBootStatus = getEventData("secureboot_status");
  const tpmStatus = getEventData("tpm_status");
  const deviceLocation = getEventData("device_location");
  const hwSpec = getEventData("hardware_spec");

  const hasData = agentStarted || bootTime || osInfo || networkAdapters || dnsConfig || proxyConfig || networkInterfaceInfo || wifiSignalInfo ||
                  autopilotProfile || aadJoinStatus || imeVersion || bitLockerStatus || secureBootStatus || deviceLocation || hwSpec;

  if (!hasData) return null;

  return (
    <div className="bg-white shadow rounded-lg p-6 mb-6">
      <button
        onClick={() => setExpanded(!expanded)}
        className="flex items-center justify-between w-full text-left"
      >
        <div className="flex items-center space-x-2">
          <svg className="w-5 h-5 text-gray-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 3v2m6-2v2M9 19v2m6-2v2M5 9H3m2 6H3m18-6h-2m2 6h-2M7 19h10a2 2 0 002-2V7a2 2 0 00-2-2H7a2 2 0 00-2 2v10a2 2 0 002 2zM9 9h6v6H9V9z" />
          </svg>
          <h2 className="text-xl font-semibold text-gray-900">Device Details</h2>
        </div>
        <svg className={`w-5 h-5 text-gray-400 transition-transform duration-200 ${expanded ? 'rotate-90' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
        </svg>
      </button>

      {expanded && (
        <div className="mt-4 flex flex-col md:flex-row gap-6">
          {/* Left Column: OS, Network, Security */}
          <div className="flex-1 flex flex-col gap-6">
            {/* OS Information */}
            {osInfo && (
              <DetailSection title="Operating System">
                {osInfo.osVersion && <DetailRow label="Version" value={osInfo.osVersion} />}
                {osInfo.displayVersion && <DetailRow label="Display Version" value={osInfo.displayVersion} />}
                {osInfo.currentBuild && osInfo.buildRevision && (
                  <DetailRow label="Build" value={`${osInfo.currentBuild}.${osInfo.buildRevision}`} />
                )}
                {osInfo.currentBuild && !osInfo.buildRevision && (
                  <DetailRow label="Build" value={osInfo.currentBuild} />
                )}
                {osInfo.edition && <DetailRow label="Edition" value={osInfo.edition} />}
                {osInfo.compositionEdition && <DetailRow label="Composition Edition" value={osInfo.compositionEdition} />}
                {osInfo.buildBranch && <DetailRow label="Build Branch" value={osInfo.buildBranch} />}
              </DetailSection>
            )}

            {/* Network */}
            {(networkAdapters || dnsConfig || proxyConfig || networkInterfaceInfo || wifiSignalInfo) && (
              <DetailSection title="Network">
                {networkAdapters && networkAdapters.adapters && (
                  (networkAdapters.adapters as unknown[]).map((adapter, i: number) => {
                    const a = adapter as Record<string, unknown>;
                    const { ipv4, ipv6 } = a.ipAddresses ? splitIpAddresses(a.ipAddresses as string) : { ipv4: [], ipv6: [] };
                    const hasIpv6 = ipv6.length > 0;
                    const isIpv6Shown = showIpv6[i] ?? false;

                    return (
                      <div key={i} className="mb-3 pb-3 border-b border-gray-100 last:border-b-0 last:mb-0 last:pb-0">
                        <div className="text-sm font-medium text-gray-700 mb-1">{(a.description || a.name || `Adapter ${i + 1}`) as string}</div>
                        {a.dhcpEnabled !== undefined && <DetailRow label="DHCP" value={a.dhcpEnabled ? "Enabled" : "Disabled"} />}
                        {a.macAddress && <DetailRow label="MAC" value={a.macAddress as string} />}
                        {ipv4.length > 0 && <DetailRow label="IPv4" value={ipv4.join(", ")} />}
                        {hasIpv6 && (
                          <div className="mt-1">
                            <button
                              onClick={() => setShowIpv6(prev => ({ ...prev, [i]: !isIpv6Shown }))}
                              className="text-xs text-blue-600 hover:text-blue-800 flex items-center space-x-1"
                            >
                              <svg className={`w-3 h-3 transition-transform duration-200 ${isIpv6Shown ? 'rotate-90' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.5} d="M9 5l7 7-7 7" />
                              </svg>
                              <span>IPv6 ({ipv6.length})</span>
                            </button>
                            {isIpv6Shown && (
                              <div className="mt-1 pl-4 text-xs text-gray-600 space-y-0.5">
                                {ipv6.map((ip, idx) => (
                                  <div key={idx} className="font-mono">{ip}</div>
                                ))}
                              </div>
                            )}
                          </div>
                        )}
                        {dnsConfig?.dnsEntries && Array.isArray(dnsConfig.dnsEntries) && (
                          (dnsConfig.dnsEntries as unknown[])
                            .map(e => e as Record<string, unknown>)
                            .filter(entry => entry.adapter === a.description || entry.adapter === a.name)
                            .map((entry, dnsIdx: number) => (
                              <DetailRow key={`dns-${dnsIdx}`} label="DNS" value={(entry.servers || "N/A") as string} />
                            ))
                        )}
                      </div>
                    );
                  })
                )}

                {networkInterfaceInfo && networkInterfaceInfo.status !== "no_active_interface" && (
                  <div className="mt-3 pt-3 border-t border-gray-200">
                    <div className="text-sm font-medium text-gray-700 mb-1">Active Interface</div>
                    <DetailRow label="Connection" value={networkInterfaceInfo.connectionType === "WiFi" ? "WiFi" : "Ethernet"} />
                    {networkInterfaceInfo.adapterDescription && <DetailRow label="Adapter" value={networkInterfaceInfo.adapterDescription} />}
                    {networkInterfaceInfo.linkSpeedMbps !== undefined && (
                      <DetailRow label="Link Speed" value={
                        networkInterfaceInfo.linkSpeedMbps >= 1000
                          ? `${(networkInterfaceInfo.linkSpeedMbps / 1000).toFixed(1)} Gbps`
                          : `${networkInterfaceInfo.linkSpeedMbps} Mbps`
                      } />
                    )}
                    {networkInterfaceInfo.gateways && <DetailRow label="Gateway" value={networkInterfaceInfo.gateways} />}
                  </div>
                )}

                {wifiSignalInfo && (
                  <div className="mt-3 pt-3 border-t border-gray-200">
                    <div className="text-sm font-medium text-gray-700 mb-1">WiFi</div>
                    {wifiSignalInfo.wifiSsid && <DetailRow label="SSID" value={wifiSignalInfo.wifiSsid} />}
                    {wifiSignalInfo.wifiSignalPercent !== undefined && (
                      <DetailRow label="Signal" value={`${wifiSignalInfo.wifiSignalPercent}%`} />
                    )}
                    {wifiSignalInfo.wifiRadioType && <DetailRow label="Radio" value={wifiSignalInfo.wifiRadioType} />}
                    {wifiSignalInfo.wifiChannel !== undefined && <DetailRow label="Channel" value={`${wifiSignalInfo.wifiChannel}`} />}
                  </div>
                )}

                {proxyConfig && (
                  <div className="mt-3 pt-3 border-t border-gray-200">
                    <div className="text-sm font-medium text-gray-700 mb-1">Proxy</div>
                    <DetailRow label="Type" value={proxyConfig.proxyType ?? proxyConfig.type ?? "Direct"} />
                    {proxyConfig.proxyServer && <DetailRow label="Server" value={proxyConfig.proxyServer} />}
                    {proxyConfig.autoConfigUrl && <DetailRow label="PAC URL" value={proxyConfig.autoConfigUrl} />}
                    {proxyConfig.winHttpProxy && <DetailRow label="WinHTTP" value={proxyConfig.winHttpProxy} />}
                  </div>
                )}
              </DetailSection>
            )}

            {/* Security */}
            {(bitLockerStatus || secureBootStatus || tpmStatus) && (
              <DetailSection title="Security">
                {secureBootStatus && (
                  <DetailRow label="SecureBoot" value={secureBootStatus.uefiSecureBootEnabled ? "Enabled" : "Disabled"} />
                )}
                {tpmStatus && (
                  <>
                    <DetailRow label="TPM" value={tpmStatus.available === false ? "Not Available" : `${tpmStatus.manufacturerName ?? "Unknown"} (${tpmStatus.manufacturerVersion ?? "?"})`} />
                    {tpmStatus.specVersion && (
                      <DetailRow label="TPM Spec Version" value={tpmStatus.specVersion} />
                    )}
                  </>
                )}
                {bitLockerStatus && (
                  <>
                    <DetailRow label="BitLocker" value={bitLockerStatus.systemDriveProtected ? "Protected" : "Not Protected"} />
                    {bitLockerStatus.volumes && Array.isArray(bitLockerStatus.volumes) && bitLockerStatus.volumes.length > 0 && (
                      <div className="mt-1 text-xs text-gray-500">
                        {(bitLockerStatus.volumes as unknown[]).map((vol, i: number) => {
                          const v = vol as Record<string, unknown>;
                          return (
                          <div key={i}>
                            {v.driveLetter as string} {v.protectionStatus === "1" ? "Protected" : "Not Protected"}
                            {v.encryptionMethod !== undefined && v.encryptionMethod !== null && v.encryptionMethod !== "" && (
                              ` (Method: ${getBitLockerEncryptionMethodLabel(v.encryptionMethod)})`
                            )}
                          </div>
                          );
                        })}
                      </div>
                    )}
                  </>
                )}
              </DetailSection>
            )}
          </div>

          {/* Right Column: System, Autopilot Profile, Hardware */}
          <div className="flex-1 flex flex-col gap-6">
            {/* System */}
            {(estimatedBootTime || agentStarted?.agentVersion || imeVersion || realmJoinInfo?.productVersion || aadJoinStatus?.joinType || deviceLocation?.country || deviceLocation?.Country || deviceLocation?.timezone || deviceLocation?.Timezone) && (
              <DetailSection title="System">
                {estimatedBootTime && (
                  <DetailRow label="Boot Time" value={estimatedBootTime.toLocaleString([], { dateStyle: "short", timeStyle: "medium" })} />
                )}
                {uptimeUntilEnrollment && <DetailRow label="Uptime until enrollment starts" value={uptimeUntilEnrollment} />}
                {agentStarted?.agentVersion && typeof agentStarted.agentVersion === 'string' && (() => {
                  const displayVersion = agentStarted.agentVersion.replace(/\+([0-9a-f]{7})[0-9a-f]+$/, '+$1');
                  const installedCleaned = stripGitHashSuffix(agentStarted.agentVersion);
                  const isOutdated = !!latestAgentVersion && compareVersions(installedCleaned, latestAgentVersion) < 0;
                  return (
                    <div className="flex justify-between text-xs py-0.5">
                      <span className="text-gray-500">Monitor Agent Version</span>
                      <span className="text-gray-900 font-mono ml-2 text-right break-all flex items-center justify-end gap-1" title={displayVersion}>
                        <span>{displayVersion}</span>
                        {isOutdated && (
                          <span
                            className="text-[10px] px-1.5 py-0.5 rounded-full font-medium bg-amber-100 text-amber-800 border border-amber-200"
                            title={`latest: v${latestAgentVersion}`}
                          >
                            outdated
                          </span>
                        )}
                      </span>
                    </div>
                  );
                })()}
                {imeVersion && <DetailRow label="IME Agent Version" value={imeVersion.version ?? imeVersion.agentVersion ?? "Unknown"} />}
                {realmJoinInfo?.productVersion && (
                  <DetailRow
                    label="RealmJoin Agent Version"
                    value={
                      realmJoinInfo.releaseChannel && realmJoinInfo.releaseChannel !== "release"
                        ? `${realmJoinInfo.productVersion} (${realmJoinInfo.releaseChannel})`
                        : realmJoinInfo.productVersion
                    }
                  />
                )}
                {(deviceLocation?.country || deviceLocation?.Country) && (
                  <DetailRow label="Country" value={deviceLocation.country ?? deviceLocation.Country} />
                )}
                {(deviceLocation?.timezone || deviceLocation?.Timezone) && (
                  <DetailRow label="Timezone" value={deviceLocation.timezone ?? deviceLocation.Timezone} />
                )}
              </DetailSection>
            )}

            {/* Autopilot Profile */}
            {autopilotProfile && (
              <DetailSection title="Autopilot Profile">
                {hasValue(autopilotProfile.CloudAssignedTenantDomain) && <DetailRow label="Tenant Domain" value={`${autopilotProfile.CloudAssignedTenantDomain}`} />}
                {hasValue(autopilotProfile.DeploymentProfileName) && <DetailRow label="Profile Name" value={`${autopilotProfile.DeploymentProfileName}`} />}
                {hasValue(autopilotProfile.CloudAssignedTenantId) && <DetailRow label="Tenant ID" value={`${autopilotProfile.CloudAssignedTenantId}`} />}
                {hasValue(autopilotProfile.PolicyDownloadDate) && <DetailRow label="Policy Downloaded" value={new Date(autopilotProfile.PolicyDownloadDate).toLocaleString()} />}
                {hasValue(autopilotProfile.CloudAssignedOobeConfig) && (
                  <>
                    <div className="flex items-center justify-between py-1">
                      <span className="text-xs text-gray-500 min-w-[120px]">OOBE Config</span>
                      <button
                        onClick={() => setShowOobeModal(true)}
                        className="text-xs text-right text-blue-600 hover:text-blue-800 hover:underline cursor-pointer font-mono transition-colors"
                        title="Click to decode bitmask"
                      >
                        {`${autopilotProfile.CloudAssignedOobeConfig}`}
                        <svg className="w-3 h-3 inline ml-1 -mt-0.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                        </svg>
                      </button>
                    </div>
                    <OobeConfigModal
                      show={showOobeModal}
                      value={Number(autopilotProfile.CloudAssignedOobeConfig)}
                      onClose={() => setShowOobeModal(false)}
                    />
                  </>
                )}
                {hasValue(autopilotProfile.ZtdRegistrationId) && <DetailRow label="ZTD Registration ID" value={`${autopilotProfile.ZtdRegistrationId}`} />}
                {hasValue(autopilotProfile.AadDeviceId) && <DetailRow label="AAD Device ID" value={`${autopilotProfile.AadDeviceId}`} />}
                {autopilotProfile.AutopilotMode !== undefined && (
                  <DetailRow label="Autopilot Mode" value={autopilotProfile.autopilotModeLabel ?? (
                    `${autopilotProfile.AutopilotMode}` === "0" ? "User Driven (0)" :
                    `${autopilotProfile.AutopilotMode}`
                  )} />
                )}
                {autopilotProfile.CloudAssignedDomainJoinMethod !== undefined && (
                  <DetailRow label="Domain Join Method" value={autopilotProfile.domainJoinMethodLabel ?? (
                    `${autopilotProfile.CloudAssignedDomainJoinMethod}` === "0" ? "Entra Join" :
                    `${autopilotProfile.CloudAssignedDomainJoinMethod}` === "1" ? "Hybrid Azure AD Join" :
                    `${autopilotProfile.CloudAssignedDomainJoinMethod}`
                  )} />
                )}
                {autopilotProfile.HybridJoinSkipDCConnectivityCheck !== undefined && (
                  <DetailRow label="Skip DC Connectivity Check" value={`${autopilotProfile.HybridJoinSkipDCConnectivityCheck}` === "1" ? "Yes" : "No"} />
                )}
                {autopilotProfile.CloudAssignedForcedEnrollment !== undefined && (
                  <DetailRow label="Forced Enrollment" value={`${autopilotProfile.CloudAssignedForcedEnrollment}` === "1" ? "Yes" : "No"} />
                )}
                {hasValue(autopilotProfile.AutopilotCreationDate) && <DetailRow label="Autopilot Created" value={new Date(autopilotProfile.AutopilotCreationDate).toLocaleString()} />}
                {hasValue(autopilotProfile.ProfileAvailable) && (
                  <DetailRow label="Profile Available" value={`${autopilotProfile.ProfileAvailable}` === "1" ? "Yes" : `${autopilotProfile.ProfileAvailable}`} />
                )}
              </DetailSection>
            )}

            {/* Hardware */}
            {hwSpec && (
              <DetailSection title="Hardware">
                {hwSpec.cpuName && <DetailRow label="CPU" value={String(hwSpec.cpuName)} />}
                {(hwSpec.cpuCores || hwSpec.cpuLogicalProcessors) && (
                  <DetailRow label="CPU Cores" value={`${hwSpec.cpuCores ?? '?'} cores / ${hwSpec.cpuLogicalProcessors ?? '?'} threads`} />
                )}
                {hwSpec.cpuMaxClockSpeedGHz && <DetailRow label="Max Clock" value={`${hwSpec.cpuMaxClockSpeedGHz} GHz`} />}
                {hwSpec.cpuArchitecture && <DetailRow label="Architecture" value={String(hwSpec.cpuArchitecture)} />}

                {hwSpec.ramTotalGB && (
                  <DetailRow label="RAM" value={`${hwSpec.ramTotalGB} GB${hwSpec.ramType ? ` ${hwSpec.ramType}` : ''}${hwSpec.ramSpeed ? ` @ ${hwSpec.ramSpeed} MHz` : ''}`} />
                )}
                {hwSpec.ramDimmCount !== undefined && (
                  <DetailRow label="DIMMs" value={`${hwSpec.ramDimmCount}${hwSpec.ramFormFactor ? ` (${hwSpec.ramFormFactor})` : ''}`} />
                )}

                {hwSpec.disks && Array.isArray(hwSpec.disks) && (hwSpec.disks as unknown[]).map((disk, i: number) => {
                  const d = disk as Record<string, unknown>;
                  return (
                  <DetailRow key={`disk-${i}`} label={hwSpec.disks.length > 1 ? `Disk ${i + 1}` : 'Disk'} value={`${d.model || 'Unknown'}${d.sizeGB ? ` (${d.sizeGB} GB)` : ''}${d.mediaType && d.mediaType !== 'Unknown' ? ` — ${d.mediaType}` : ''}`} />
                  );
                })}
                {hwSpec.systemDriveFreeGB !== undefined && hwSpec.systemDriveTotalGB !== undefined && (
                  <DetailRow label="C: Free Space" value={`${hwSpec.systemDriveFreeGB} / ${hwSpec.systemDriveTotalGB} GB`} />
                )}

                {hwSpec.biosVersion && <DetailRow label="BIOS" value={String(hwSpec.biosVersion)} />}
                {hwSpec.biosReleaseDate && <DetailRow label="BIOS Date" value={String(hwSpec.biosReleaseDate)} />}
                {hwSpec.smbiosVersion && <DetailRow label="SMBIOS" value={String(hwSpec.smbiosVersion)} />}

                {hwSpec.batteryPresent === true && (
                  <>
                    {hwSpec.batteryHealthPercent !== undefined && (
                      <DetailRow label="Battery Health" value={`${hwSpec.batteryHealthPercent}%`} />
                    )}
                    {hwSpec.batteryDesignCapacityMWh && hwSpec.batteryFullChargeCapacityMWh && (
                      <DetailRow label="Battery Capacity" value={`${hwSpec.batteryFullChargeCapacityMWh} / ${hwSpec.batteryDesignCapacityMWh} mWh`} />
                    )}
                    {hwSpec.batteryChargePercent !== undefined && (
                      <DetailRow label="Battery Charge" value={`${hwSpec.batteryChargePercent}%`} />
                    )}
                  </>
                )}
                {hwSpec.batteryPresent === false && <DetailRow label="Battery" value="Not present (Desktop)" />}

                {hwSpec.gpus && Array.isArray(hwSpec.gpus) && (hwSpec.gpus as unknown[]).map((gpu, i: number) => {
                  const g = gpu as Record<string, unknown>;
                  return (
                  <DetailRow key={`gpu-${i}`} label={hwSpec.gpus.length > 1 ? `GPU ${i + 1}` : 'GPU'} value={`${g.name || 'Unknown'}${g.adapterRAMGB ? ` (${g.adapterRAMGB} GB)` : ''}`} />
                  );
                })}
              </DetailSection>
            )}
          </div>
        </div>
      )}
    </div>
  );
}

function DetailSection({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="border border-gray-200 rounded-lg p-3">
      <h3 className="text-sm font-semibold text-gray-700 mb-2 border-b border-gray-100 pb-1">{title}</h3>
      <div>{children}</div>
    </div>
  );
}

function DetailRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex justify-between text-xs py-0.5">
      <span className="text-gray-500">{label}</span>
      <span className="text-gray-900 font-mono ml-2 text-right break-all" title={value}>{value}</span>
    </div>
  );
}

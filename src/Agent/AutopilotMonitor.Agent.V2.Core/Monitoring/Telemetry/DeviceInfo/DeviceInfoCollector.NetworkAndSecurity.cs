using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo
{
    /// <summary>
    /// Partial: Network, proxy, Autopilot profile, security (SecureBoot, BitLocker, TPM, AAD join),
    /// ESP configuration, WiFi signal, and helper methods.
    /// </summary>
    public partial class DeviceInfoCollector
    {
        /// <summary>
        /// True when AAD join status shows "Azure AD Joined" with a non-empty userEmail
        /// that is NOT a provisioning placeholder (see <see cref="IsFooUserDetected"/>).
        /// Used by EnrollmentTracker to distinguish user-driven from device-only deployments
        /// when SkipUserStatusPage=true (which admins commonly set for user-driven enrollments too).
        /// </summary>
        public bool HasAadJoinedUser { get; private set; }

        /// <summary>
        /// True when the AAD join userEmail matches a known transient provisioning-account
        /// pattern (foouser@*, autopilot@*). These accounts appear during Autopilot
        /// pre-provisioning (WhiteGlove) and are NOT a real AAD join — they are a soft
        /// positive WhiteGlove indicator and feed into <see cref="AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Completion.WhiteGloveClassifier"/>.
        /// </summary>
        public bool IsFooUserDetected { get; private set; }

        private void CollectNetworkAndDnsConfiguration()
        {
            try
            {
                var adapters = new List<Dictionary<string, object>>();
                var dnsServers = new List<Dictionary<string, object>>();

                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Description, IPAddress, IPSubnet, DefaultIPGateway, MACAddress, DHCPEnabled, DHCPServer, DNSServerSearchOrder " +
                    "FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        // Network adapter info
                        var adapter = new Dictionary<string, object>
                        {
                            { "description", obj["Description"]?.ToString() },
                            { "macAddress", obj["MACAddress"]?.ToString() },
                            { "dhcpEnabled", obj["DHCPEnabled"]?.ToString() },
                            { "dhcpServer", obj["DHCPServer"]?.ToString() }
                        };

                        var ipAddresses = obj["IPAddress"] as string[];
                        if (ipAddresses != null)
                            adapter["ipAddresses"] = string.Join(", ", ipAddresses);

                        var subnets = obj["IPSubnet"] as string[];
                        if (subnets != null)
                            adapter["subnets"] = string.Join(", ", subnets);

                        var gateways = obj["DefaultIPGateway"] as string[];
                        if (gateways != null)
                            adapter["gateways"] = string.Join(", ", gateways);

                        adapters.Add(adapter);

                        // DNS info (extracted from the same result set)
                        var servers = obj["DNSServerSearchOrder"] as string[];
                        if (servers != null && servers.Length > 0)
                        {
                            dnsServers.Add(new Dictionary<string, object>
                            {
                                { "adapter", obj["Description"]?.ToString() },
                                { "servers", string.Join(", ", servers) }
                            });
                        }
                    }
                }

                EmitDeviceInfoEvent(Constants.EventTypes.NetworkAdapters, "Network adapters configuration",
                    new Dictionary<string, object>
                    {
                        { "adapterCount", adapters.Count },
                        { "adapters", adapters }
                    });

                EmitDeviceInfoEvent(Constants.EventTypes.DnsConfiguration, "DNS server configuration",
                    new Dictionary<string, object>
                    {
                        { "dnsEntries", dnsServers }
                    });
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect network/DNS config: {ex.Message}");
            }
        }

        private void CollectProxyConfiguration()
        {
            try
            {
                var data = new Dictionary<string, object>();

                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (key != null)
                    {
                        data["proxyEnabled"] = key.GetValue("ProxyEnable")?.ToString() == "1";
                        data["proxyServer"] = key.GetValue("ProxyServer")?.ToString();
                        data["proxyOverride"] = key.GetValue("ProxyOverride")?.ToString();
                        data["autoConfigUrl"] = key.GetValue("AutoConfigURL")?.ToString();
                    }
                }

                // Also check WinHTTP proxy
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Connections"))
                {
                    data["winHttpProxyConfigured"] = key?.GetValue("WinHttpSettings") != null;
                }

                var proxyType = "Direct";
                if (data.ContainsKey("proxyEnabled") && (bool)data["proxyEnabled"])
                    proxyType = "Proxy";
                else if (data.ContainsKey("autoConfigUrl") && data["autoConfigUrl"] != null)
                    proxyType = "PAC";

                data["proxyType"] = proxyType;

                EmitDeviceInfoEvent(Constants.EventTypes.ProxyConfiguration, $"Proxy configuration: {proxyType}", data);
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect proxy config: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads the Autopilot profile from registry and detects enrollment type and Hybrid Join status.
        /// Returns enrollment type ("v1"/"v2") and whether the profile indicates Hybrid Azure AD Join.
        /// Note: AutopilotMode and DomainJoinMethod reflect profile capabilities (what is allowed),
        /// not the actual live enrollment state.
        /// </summary>
        private (string enrollmentType, bool isHybridJoin, int? autopilotMode) CollectAutopilotProfile()
        {
            var detectedType = "v1";
            var isHybridJoin = false;
            int? detectedAutopilotMode = null;

            try
            {
                var data = new Dictionary<string, object>();

                // Read JSON from AutopilotPolicyCache registry key (contains all Autopilot profile info)
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Provisioning\AutopilotPolicyCache"))
                {
                    if (key != null)
                    {
                        // The registry key contains individual values that together form the Autopilot profile
                        // Read all available values and add them to data dictionary
                        foreach (var valueName in key.GetValueNames())
                        {
                            var value = key.GetValue(valueName);
                            if (value != null)
                            {
                                // Store with original casing from registry
                                var valueAsString = value.ToString();
                                if (string.Equals(valueName, "PolicyJsonCache", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(valueName, "CloudAssignedAadServerData", StringComparison.OrdinalIgnoreCase))
                                {
                                    valueAsString = TryFormatJson(valueAsString);
                                }

                                data[valueName] = valueAsString;
                            }
                        }

                        // Extract key values for enrollment type detection
                        var deviceReg = data.ContainsKey("CloudAssignedDeviceRegistration")
                            ? data["CloudAssignedDeviceRegistration"]?.ToString()
                            : null;
                        var espEnabled = data.ContainsKey("CloudAssignedEspEnabled")
                            ? data["CloudAssignedEspEnabled"]?.ToString()
                            : null;

                        // Determine enrollment type: WDP if DeviceRegistration=2 or ESP explicitly disabled
                        if (deviceReg == "2" || espEnabled == "0")
                            detectedType = "v2";
                        else
                            detectedType = "v1";

                        // --- Interpret AutopilotMode (profile capability, not live state) ---
                        // 0 = User Driven, 1 = Self Deploying, 2 = Pre-Provisioning (White Glove)
                        var autopilotModeStr = data.ContainsKey("AutopilotMode")
                            ? data["AutopilotMode"]?.ToString()
                            : null;
                        if (autopilotModeStr != null && int.TryParse(autopilotModeStr, out var autopilotMode))
                        {
                            detectedAutopilotMode = autopilotMode;
                            // Only mode 0 is confirmed — modes 1, 2, 3+ are shown as raw integers
                            // until their meaning is verified across all Autopilot profile variants.
                            string modeLabel;
                            switch (autopilotMode)
                            {
                                case 0:  modeLabel = $"User Driven ({autopilotMode})"; break;
                                default: modeLabel = $"{autopilotMode}"; break;
                            }
                            data["autopilotModeLabel"] = modeLabel;
                            _logger.Info($"EnrollmentTracker: AutopilotMode={autopilotMode} ({modeLabel})");
                        }

                        // --- Interpret CloudAssignedDomainJoinMethod ---
                        // 0 = Entra Join (Azure AD Join), 1 = Hybrid Azure AD Join
                        // This value may be a top-level registry value OR embedded in PolicyJsonCache JSON.
                        var domainJoinMethodStr = data.ContainsKey("CloudAssignedDomainJoinMethod")
                            ? data["CloudAssignedDomainJoinMethod"]?.ToString()
                            : null;

                        // Fallback: extract from PolicyJsonCache if not a top-level registry value
                        if (domainJoinMethodStr == null && data.ContainsKey("PolicyJsonCache"))
                        {
                            try
                            {
                                var policyRaw = data["PolicyJsonCache"]?.ToString();
                                if (!string.IsNullOrWhiteSpace(policyRaw))
                                {
                                    using (var pDoc = System.Text.Json.JsonDocument.Parse(policyRaw))
                                    {
                                        if (pDoc.RootElement.TryGetProperty("CloudAssignedDomainJoinMethod", out var djmProp))
                                        {
                                            domainJoinMethodStr = djmProp.ToString();
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Trace($"EnrollmentTracker: Failed to parse PolicyJsonCache for DomainJoinMethod: {ex.Message}");
                            }
                        }

                        if (domainJoinMethodStr != null && int.TryParse(domainJoinMethodStr, out var domainJoinMethod))
                        {
                            string joinLabel;
                            switch (domainJoinMethod)
                            {
                                case 0:  joinLabel = "Entra Join"; break;
                                case 1:  joinLabel = "Hybrid Azure AD Join"; break;
                                default: joinLabel = $"Unknown ({domainJoinMethod})"; break;
                            }
                            data["domainJoinMethodLabel"] = joinLabel;

                            // Hybrid Join is reliably indicated by DomainJoinMethod == 1
                            isHybridJoin = domainJoinMethod == 1;
                        }

                        data["isHybridJoin"] = isHybridJoin;

                        if (isHybridJoin)
                        {
                            _logger.Info("EnrollmentTracker: Hybrid Azure AD Join profile detected");
                        }

                        _logger.Info($"EnrollmentTracker: Read {data.Count} values from AutopilotPolicyCache");
                    }
                    else
                    {
                        _logger.Warning("EnrollmentTracker: AutopilotPolicyCache registry key not found");
                    }
                }

                // Include enrollment type in autopilot_profile event data
                data["enrollmentType"] = detectedType;

                EmitDeviceInfoEvent(Constants.EventTypes.AutopilotProfile, "Autopilot profile configuration", data);

                // ProfileAvailable=0 means the ZTD service returned no profile for this device
                // during OOBE (not Autopilot-registered, profile assignment not propagated yet,
                // or device deleted from Autopilot). Such a device still enrolls via manual
                // Entra join + MDM auto-enrollment and would otherwise look like a regular
                // Autopilot session — surface the edge case explicitly.
                if (IsAutopilotProfileMissing(data))
                {
                    var missingData = new Dictionary<string, object>
                    {
                        { "profileAvailable", "0" },
                        { "likelyCauses", "not_registered_in_autopilot,profile_assignment_not_propagated,deleted_from_autopilot" }
                    };
                    if (TryExtractZeroTouchTenantDomain(data, out var ztdTenantDomain))
                    {
                        missingData["zeroTouchTenantDomain"] = ztdTenantDomain ?? "";
                        missingData["zeroTouchTenantDomainEmpty"] = string.IsNullOrEmpty(ztdTenantDomain);
                    }

                    EmitDeviceInfoEvent(Constants.EventTypes.AutopilotProfileMissing,
                        "No Autopilot profile cached on this device (ProfileAvailable=0) — the device was likely not Autopilot-registered when OOBE ran; this enrollment appears to be a manual Entra ID join, not an Autopilot deployment.",
                        missingData,
                        EventSeverity.Warning);

                    _logger.Warning("EnrollmentTracker: no Autopilot profile cached (ProfileAvailable=0) — non-Autopilot OOBE enrollment suspected");
                }

                // Emit dedicated enrollment_type_detected event for easy filtering. Routed through
                // EmitDeviceInfoEvent (same Severity/Source/Phase) so the restart dedup applies.
                EmitDeviceInfoEvent(Constants.EventTypes.EnrollmentTypeDetected,
                    detectedType == "v2"
                        ? "Enrollment type: Autopilot v2 (Windows Device Preparation)"
                        : "Enrollment type: Autopilot v1 (Classic ESP)",
                    new Dictionary<string, object>
                    {
                        { "enrollmentType", detectedType },
                        { "CloudAssignedDeviceRegistration", data.ContainsKey("CloudAssignedDeviceRegistration") ? data["CloudAssignedDeviceRegistration"] : "n/a" },
                        { "CloudAssignedEspEnabled", data.ContainsKey("CloudAssignedEspEnabled") ? data["CloudAssignedEspEnabled"] : "n/a" },
                        { "detectionRule", detectedType == "v2"
                            ? (data.ContainsKey("CloudAssignedDeviceRegistration") && data["CloudAssignedDeviceRegistration"]?.ToString() == "2"
                                ? "CloudAssignedDeviceRegistration=2"
                                : "CloudAssignedEspEnabled=0")
                            : "default (no v2 indicators)" }
                    });

                _logger.Info($"EnrollmentTracker: enrollment type detected: {detectedType}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect autopilot profile: {ex.Message}");
            }

            return (detectedType, isHybridJoin, detectedAutopilotMode);
        }

        /// <summary>
        /// True when the AutopilotPolicyCache explicitly recorded that no Autopilot profile
        /// is available (ProfileAvailable=0). Windows writes this when the ZTD service
        /// returned no profile for the device's hardware hash during OOBE. An absent
        /// ProfileAvailable value is NOT treated as missing (no evidence either way).
        /// </summary>
        internal static bool IsAutopilotProfileMissing(Dictionary<string, object> data)
        {
            return data != null
                && data.TryGetValue("ProfileAvailable", out var profileAvailable)
                && profileAvailable?.ToString() == "0";
        }

        /// <summary>
        /// Extracts ZeroTouchConfig.CloudAssignedTenantDomain from the cached profile data.
        /// The value lives in the CloudAssignedAadServerData JSON — either as a top-level
        /// registry value or embedded as a string property inside PolicyJsonCache.
        /// Returns false when it cannot be located (missing values, malformed JSON).
        /// </summary>
        internal static bool TryExtractZeroTouchTenantDomain(Dictionary<string, object> data, out string tenantDomain)
        {
            tenantDomain = null;
            if (data == null) return false;

            try
            {
                string serverDataJson = null;
                if (data.TryGetValue("CloudAssignedAadServerData", out var topLevel))
                {
                    serverDataJson = topLevel?.ToString();
                }
                else if (data.TryGetValue("PolicyJsonCache", out var policyCache))
                {
                    var policyRaw = policyCache?.ToString();
                    if (!string.IsNullOrWhiteSpace(policyRaw))
                    {
                        using (var pDoc = System.Text.Json.JsonDocument.Parse(policyRaw))
                        {
                            if (pDoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                                pDoc.RootElement.TryGetProperty("CloudAssignedAadServerData", out var serverDataProp) &&
                                serverDataProp.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                serverDataJson = serverDataProp.GetString();
                            }
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(serverDataJson)) return false;

                using (var doc = System.Text.Json.JsonDocument.Parse(serverDataJson))
                {
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("ZeroTouchConfig", out var ztc) &&
                        ztc.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        ztc.TryGetProperty("CloudAssignedTenantDomain", out var domainProp) &&
                        domainProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        tenantDomain = domainProp.GetString();
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                // Malformed cache JSON — treat as not extractable.
            }

            return false;
        }

        private void CollectSecureBootStatus()
        {
            try
            {
                var data = new Dictionary<string, object>();

                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
                {
                    if (key != null)
                    {
                        var value = key.GetValue("UEFISecureBootEnabled");
                        data["uefiSecureBootEnabled"] = value != null && Convert.ToInt32(value) == 1;
                    }
                    else
                    {
                        data["uefiSecureBootEnabled"] = false;
                        data["note"] = "SecureBoot registry key not found";
                    }
                }

                // UEFI CA 2023 certificate deployment status (expires June 2026)
                using (var servicingKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\Servicing"))
                {
                    if (servicingKey != null)
                    {
                        data["uefiCA2023Status"] = servicingKey.GetValue("UEFICA2023Status")?.ToString() ?? "unknown";
                        var capable = servicingKey.GetValue("WindowsUEFICA2023Capable");
                        if (capable != null)
                            data["windowsUefiCA2023Capable"] = Convert.ToInt32(capable);
                        var error = servicingKey.GetValue("UEFICA2023Error");
                        if (error != null && Convert.ToInt32(error) != 0)
                            data["uefiCA2023Error"] = Convert.ToInt32(error);
                        data["confidenceLevel"] = servicingKey.GetValue("ConfidenceLevel")?.ToString() ?? "unknown";
                    }
                    else
                    {
                        data["uefiCA2023Status"] = "notfound";
                    }
                }

                EmitDeviceInfoEvent(Constants.EventTypes.SecureBootStatus, $"SecureBoot: {data["uefiSecureBootEnabled"]}, CA2023: {data["uefiCA2023Status"]}", data);
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect SecureBoot status: {ex.Message}");
            }
        }

        private void CollectBitLockerStatus()
        {
            try
            {
                var data = new Dictionary<string, object>();
                var volumes = new List<Dictionary<string, object>>();

                using (var searcher = new ManagementObjectSearcher(
                    new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption"),
                    new ObjectQuery("SELECT DriveLetter, ProtectionStatus, ConversionStatus, EncryptionMethod FROM Win32_EncryptableVolume")))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        volumes.Add(new Dictionary<string, object>
                        {
                            { "driveLetter", obj["DriveLetter"]?.ToString() },
                            { "protectionStatus", obj["ProtectionStatus"]?.ToString() },
                            { "conversionStatus", obj["ConversionStatus"]?.ToString() },
                            { "encryptionMethod", obj["EncryptionMethod"]?.ToString() }
                        });
                    }
                }

                data["volumes"] = volumes;
                data["systemDriveProtected"] = volumes.Any(v =>
                    v["driveLetter"]?.ToString() == "C:" && v["protectionStatus"]?.ToString() == "1");

                EmitDeviceInfoEvent(Constants.EventTypes.BitLockerStatus, "BitLocker encryption status", data);
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect BitLocker status: {ex.Message}");
            }
        }

        private void CollectTpmStatus()
        {
            try
            {
                var data = new Dictionary<string, object>();

                using (var searcher = new ManagementObjectSearcher(
                    new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftTpm"),
                    new ObjectQuery("SELECT IsActivated_InitialValue, IsEnabled_InitialValue, IsOwned_InitialValue, ManufacturerIdTxt, ManufacturerVersion, SpecVersion FROM Win32_Tpm")))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        data["isActivated"] = obj["IsActivated_InitialValue"]?.ToString() == "True";
                        data["isEnabled"] = obj["IsEnabled_InitialValue"]?.ToString() == "True";
                        data["isOwned"] = obj["IsOwned_InitialValue"]?.ToString() == "True";
                        data["manufacturerName"] = obj["ManufacturerIdTxt"]?.ToString();
                        data["manufacturerVersion"] = obj["ManufacturerVersion"]?.ToString();

                        // SpecVersion is comma-separated: "2.0, 0, 1.59" → first element is the spec version
                        var specVersion = obj["SpecVersion"]?.ToString();
                        if (!string.IsNullOrEmpty(specVersion))
                        {
                            var parts = specVersion.Split(',');
                            data["specVersion"] = parts[0].Trim();
                        }
                    }
                }

                if (data.Count > 0)
                {
                    EmitDeviceInfoEvent(Constants.EventTypes.TpmStatus, $"TPM: {data["manufacturerName"]} v{data["manufacturerVersion"]}", data);
                }
                else
                {
                    data["available"] = false;
                    EmitDeviceInfoEvent(Constants.EventTypes.TpmStatus, "TPM: not available", data);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect TPM status: {ex.Message}");
            }
        }

        private void CollectAadJoinStatus()
        {
            try
            {
                var data = new Dictionary<string, object>();

                // Read AAD join info via shared helper. Placeholder users (foouser@, autopilot@)
                // are transient provisioning accounts and must NOT be treated as a real AAD join;
                // HasAadJoinedUser is only set for real user emails.
                if (AadJoinInfo.TryReadAadJoinedUser(out var userEmail, out var thumbprint, out var isPlaceholderUser))
                {
                    // Still need TenantId — open the subkey directly when we have a thumbprint.
                    if (!string.IsNullOrEmpty(thumbprint))
                    {
                        using (var joinInfoKey = Registry.LocalMachine.OpenSubKey(AadJoinInfo.JoinInfoRegistryPath))
                        using (var subKey = joinInfoKey?.OpenSubKey(thumbprint))
                        {
                            data["tenantId"] = subKey?.GetValue("TenantId")?.ToString();
                        }

                        data["userEmail"] = userEmail;
                        data["joinType"] = "Azure AD Joined";
                        data["thumbprint"] = thumbprint;

                        if (!string.IsNullOrWhiteSpace(userEmail))
                        {
                            if (isPlaceholderUser)
                            {
                                IsFooUserDetected = true;
                                data["isFooUser"] = true;
                                _logger.Info($"AAD join: placeholder user detected ({userEmail}) — pre-provisioning indicator, NOT a real AAD join");
                            }
                            else
                            {
                                HasAadJoinedUser = true;
                            }
                        }
                    }
                    else
                    {
                        data["joinType"] = "Not Joined";
                    }
                }
                else
                {
                    data["joinType"] = "Not Joined";
                }

                object joinTypeValue;
                var joinType = data.TryGetValue("joinType", out joinTypeValue) ? joinTypeValue?.ToString() ?? "Unknown" : "Unknown";
                EmitDeviceInfoEvent(Constants.EventTypes.AadJoinStatus, $"AAD join: {joinType}", data);
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect AAD join status: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads ESP skip configuration from the enrollment FirstSync registry key via the
        /// shared <see cref="EspSkipConfigurationProbe"/> (also used by
        /// <see cref="Monitoring.Enrollment.SystemSignals.EspAndHelloTracker"/> so the tracker's
        /// SkipUser guard and the reducer's <c>SkipUserEsp</c> fact stay in lockstep, plan §6 Fix 7/9).
        /// <para>
        /// Beyond the skip flags, the same <c>FirstSync</c> key carries the user-facing ESP
        /// error-handling toggles (<c>BlockInStatusPage</c> bitmask + <c>SyncFailureTimeout</c>).
        /// These are surfaced as additional fields on the <c>esp_config_detected</c> event so
        /// session-debug analysis can tell whether an enrollment that ends in
        /// <c>esp_terminal_failure</c> on the agent's side may still have let the user reach
        /// the desktop via "Continue anyway".
        /// </para>
        /// </summary>
        // Internal (not private) as a test seam: unlike CollectAll/CollectAtEnrollmentStart this
        // method touches no live system state once both probes are overridden, so tests can
        // drive the esp_config_detected event surface in isolation (InternalsVisibleTo).
        internal (bool? skipUserStatusPage, bool? skipDeviceStatusPage) CollectEspConfiguration()
        {
            bool? skipUser = null;
            bool? skipDevice = null;

            try
            {
                var snapshot = EspSkipConfigurationProbe.ReadFull(_logger);
                skipUser = snapshot.SkipUser;
                skipDevice = snapshot.SkipDevice;

                var data = new Dictionary<string, object>
                {
                    { "source", "registry_firstsync" }
                };
                if (skipUser.HasValue) data["skipUserStatusPage"] = skipUser.Value;
                if (skipDevice.HasValue) data["skipDeviceStatusPage"] = skipDevice.Value;
                if (snapshot.BlockInStatusPage.HasValue)
                {
                    data["blockInStatusPage"] = snapshot.BlockInStatusPage.Value;
                    data["allowReset"] = snapshot.AllowReset.Value;
                    data["allowTryAgain"] = snapshot.AllowTryAgain.Value;
                    data["allowContinueAnyway"] = snapshot.AllowContinueAnyway.Value;
                }
                if (snapshot.SyncFailureTimeoutMinutes.HasValue)
                    data["syncFailureTimeoutMinutes"] = snapshot.SyncFailureTimeoutMinutes.Value;

                // Session a4537c36: the ESP's own tracking lists (ESPTrackingInfo\Diagnostics)
                // tell which packages the ESP actually blocks on — vs. merely-required IME
                // assignments. Omit all keys when the Diagnostics key is absent (non-Autopilot
                // device / old build). The user-scoped S-<SID> subkeys usually appear only
                // after sign-in, so the user list is often empty at this early emission.
                var tracking = EspTrackingInfoProbe.Read(_logger);
                if (tracking.HasData)
                {
                    data["espTrackedWin32AppIds"] = tracking.Win32AppIds;
                    data["espTrackedUserWin32AppIds"] = tracking.UserWin32AppIds;
                    data["espTrackedMsiProductCodes"] = tracking.MsiProductCodes;
                    data["espTrackedModernAppPfns"] = tracking.ModernAppPfns;
                    data["espTrackedWin32Count"] = tracking.Win32Count;
                    data["espTrackedMsiCount"] = tracking.MsiCount;
                    data["espTrackedModernCount"] = tracking.ModernCount;
                }

                var summary = BuildSummary(snapshot, tracking);
                _logger.Info($"EnrollmentTracker: ESP configuration detected — {summary}");
                EmitDeviceInfoEvent(Constants.EventTypes.EspConfigDetected, $"ESP configuration: {summary}", data);
                PostEspConfigDetectedSignal(snapshot);
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect ESP configuration: {ex.Message}");
            }

            return (skipUser, skipDevice);
        }

        private static string BuildSummary(EspFirstSyncSnapshot snapshot, EspTrackingInfoSnapshot tracking = default)
        {
            var skipUserText = snapshot.SkipUser?.ToString() ?? "unknown";
            var skipDeviceText = snapshot.SkipDevice?.ToString() ?? "unknown";
            var basePart = $"SkipUser={skipUserText}, SkipDevice={skipDeviceText}";

            if (!snapshot.BlockInStatusPage.HasValue && !snapshot.SyncFailureTimeoutMinutes.HasValue && !tracking.HasData)
                return basePart;

            var sb = new System.Text.StringBuilder(basePart);
            if (snapshot.BlockInStatusPage.HasValue)
            {
                sb.Append(", BlockInStatusPage=").Append(snapshot.BlockInStatusPage.Value);
                sb.Append(" (Reset=").Append(snapshot.AllowReset.Value);
                sb.Append(", TryAgain=").Append(snapshot.AllowTryAgain.Value);
                sb.Append(", ContinueAnyway=").Append(snapshot.AllowContinueAnyway.Value).Append(")");
            }
            if (snapshot.SyncFailureTimeoutMinutes.HasValue)
                sb.Append(", SyncFailureTimeoutMin=").Append(snapshot.SyncFailureTimeoutMinutes.Value);
            if (tracking.HasData)
            {
                sb.Append(", TrackedApps(win32=").Append(tracking.Win32Count);
                sb.Append(", msi=").Append(tracking.MsiCount);
                sb.Append(", modern=").Append(tracking.ModernCount).Append(")");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Collects active network interface details (name, type, link speed, MAC) using
        /// System.Net.NetworkInformation — instant, no process spawn.
        /// If the active interface is WiFi, fires off a separate async task to collect
        /// WiFi signal info via netsh (fire-and-forget, never blocks).
        /// </summary>
        private void CollectActiveNetworkInterfaceInfo()
        {
            try
            {
                var activeNic = FindActiveNetworkInterface();

                if (activeNic == null)
                {
                    EmitDeviceInfoEvent(Constants.EventTypes.NetworkInterfaceInfo, "No active network interface found",
                        new Dictionary<string, object> { { "status", "no_active_interface" } });
                    return;
                }

                var data = new Dictionary<string, object>
                {
                    { "adapterName", activeNic.Name },
                    { "adapterDescription", activeNic.Description },
                    { "macAddress", activeNic.GetPhysicalAddress().ToString() },
                    { "interfaceType", activeNic.NetworkInterfaceType.ToString() },
                    { "linkSpeedMbps", activeNic.Speed / 1_000_000 },
                    { "interfaceId", activeNic.Id }
                };

                var isWifi = activeNic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                data["connectionType"] = isWifi ? "WiFi" : "Ethernet";

                var gateways = activeNic.GetIPProperties().GatewayAddresses;
                if (gateways.Count > 0)
                {
                    var gwList = new List<string>();
                    foreach (var gw in gateways)
                        gwList.Add(gw.Address.ToString());
                    data["gateways"] = string.Join(", ", gwList);
                }

                var message = $"Active interface: {activeNic.Description} ({data["connectionType"]}, {data["linkSpeedMbps"]} Mbps)";
                EmitDeviceInfoEvent(Constants.EventTypes.NetworkInterfaceInfo, message, data);

                // Fire-and-forget WiFi signal collection — separate event, never blocks
                if (isWifi)
                {
                    Task.Run(() => CollectWiFiSignalInfo());
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect active network interface info: {ex.Message}");
            }
        }

        /// <summary>
        /// Finds the active network interface: Up, not Loopback/Tunnel, has a non-0.0.0.0 gateway.
        /// </summary>
        private static NetworkInterface FindActiveNetworkInterface()
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                foreach (var nic in interfaces)
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                        continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    var ipProps = nic.GetIPProperties();
                    foreach (var gw in ipProps.GatewayAddresses)
                    {
                        if (gw.Address.ToString() != "0.0.0.0")
                            return nic;
                    }
                }
            }
            catch
            {
                // Caller handles null
            }
            return null;
        }

        /// <summary>
        /// Collects WiFi signal info via netsh wlan show interfaces and emits a separate
        /// wifi_signal_info event. Designed to run as fire-and-forget via Task.Run —
        /// never blocks other collection. VMs without WiFi service simply get no event.
        /// </summary>
        private void CollectWiFiSignalInfo()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = SystemPaths.Netsh,
                    Arguments = "wlan show interfaces",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                string output;
                using (var process = Process.Start(psi))
                {
                    output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);
                }

                if (string.IsNullOrEmpty(output))
                    return;

                var data = new Dictionary<string, object>();

                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    var colonIndex = trimmed.IndexOf(':');
                    if (colonIndex < 0) continue;

                    var key = trimmed.Substring(0, colonIndex).Trim();
                    var value = trimmed.Substring(colonIndex + 1).Trim();

                    if (key.Equals("SSID", StringComparison.OrdinalIgnoreCase) &&
                        !key.Equals("BSSID", StringComparison.OrdinalIgnoreCase))
                    {
                        data["wifiSsid"] = value;
                    }
                    else if (key.Equals("Signal", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(value.TrimEnd('%'), out var signal))
                            data["wifiSignalPercent"] = signal;
                    }
                    else if (key.Equals("Radio type", StringComparison.OrdinalIgnoreCase))
                    {
                        data["wifiRadioType"] = value;
                    }
                    else if (key.Equals("Channel", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(value, out var channel))
                            data["wifiChannel"] = channel;
                    }
                }

                if (data.Count > 0)
                {
                    var message = data.ContainsKey("wifiSsid")
                        ? $"WiFi: {data["wifiSsid"]}"
                        : "WiFi signal info";
                    if (data.ContainsKey("wifiSignalPercent"))
                        message += $", Signal: {data["wifiSignalPercent"]}%";
                    if (data.ContainsKey("wifiRadioType"))
                        message += $" ({data["wifiRadioType"]})";

                    EmitDeviceInfoEvent(Constants.EventTypes.WifiSignalInfo, message, data);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"WiFi signal info collection failed: {ex.Message}");
            }
        }

        private void EmitDeviceInfoEvent(string eventType, string message, Dictionary<string, object> data,
            EventSeverity severity = EventSeverity.Info)
        {
            if (!ShouldEmitDeviceInfoEvent(eventType, data)) return;

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = eventType,
                Severity = severity,
                Source = "EnrollmentTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = message,
                Data = data
            });

            // M4: commit the gate claim only after the emission went out (no-op for gate-exempt
            // event types — they never claimed).
            _startupGate?.MarkEmitted(eventType);
        }

        /// <summary>
        /// Restart dedup: device-info events are re-collected on every agent process start (one
        /// enrollment spans several reboots), but most payloads are static or change rarely — the
        /// <see cref="Persistence.StartupEventGate"/> suppresses an emission only when the payload
        /// is IDENTICAL to the last emitted one. A real change (e.g. aad_join_status flipping to
        /// Joined after a Hybrid-Join reboot, BitLocker conversion progressing) always re-emits.
        /// Collection itself — return values, HasAadJoinedUser, decision signals — never runs
        /// through this gate.
        /// </summary>
        internal bool ShouldEmitDeviceInfoEvent(string eventType, Dictionary<string, object> data)
        {
            if (_startupGate == null || GateExemptEventTypes.Contains(eventType)) return true;

            GateFingerprintExcludedFields.TryGetValue(eventType, out var excludedFields);
            var fingerprint = Persistence.StartupEventGate.ComputeFingerprint(data, excludedFields);
            if (_startupGate.ShouldEmit(eventType, fingerprint)) return true;

            _logger.Debug($"EnrollmentTracker: '{eventType}' suppressed — payload unchanged since last emission (restart dedup)");
            return false;
        }

        /// <summary>
        /// Plan §6 Fix 9 — post an <see cref="DecisionSignalKind.EspConfigDetected"/> decision
        /// signal alongside the user-facing <c>esp_config_detected</c> event so the reducer's
        /// <see cref="AutopilotMonitor.DecisionCore.State.DecisionState.SkipUserEsp"/> /
        /// <see cref="AutopilotMonitor.DecisionCore.State.DecisionState.SkipDeviceEsp"/> facts get
        /// populated. Fix 8's reducer guards then block premature <c>AwaitingHello</c> transitions
        /// on Classic (SkipUser=false) enrollments.
        /// <para>
        /// Deduplication lives in the reducer: <c>HandleEspConfigDetectedV1</c> uses per-fact
        /// set-once semantics, so repeat posts (e.g. <see cref="CollectAtEnrollmentStart"/> once
        /// wired, or a retry after FirstSync populates late) are safe and can fill in any fact
        /// that was still <c>null</c>. The collector deliberately does not carry its own
        /// fire-once flag — that would defeat the reducer's late-fill path.
        /// </para>
        /// </summary>
        private void PostEspConfigDetectedSignal(EspFirstSyncSnapshot snapshot)
        {
            if (_signalIngress == null || _clock == null) return;
            if (snapshot.SkipUser == null
                && snapshot.SkipDevice == null
                && snapshot.SyncFailureTimeoutMinutes == null
                && snapshot.AllowContinueAnyway == null)
            {
                return;
            }

            var payload = new Dictionary<string, string>(StringComparer.Ordinal);
            if (snapshot.SkipUser.HasValue)
                payload[SignalPayloadKeys.SkipUserEsp] = snapshot.SkipUser.Value ? "true" : "false";
            if (snapshot.SkipDevice.HasValue)
                payload[SignalPayloadKeys.SkipDeviceEsp] = snapshot.SkipDevice.Value ? "true" : "false";
            if (snapshot.SyncFailureTimeoutMinutes.HasValue && snapshot.SyncFailureTimeoutMinutes.Value > 0)
                payload[SignalPayloadKeys.EspSyncFailureTimeoutMinutes] = snapshot.SyncFailureTimeoutMinutes.Value
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (snapshot.AllowContinueAnyway.HasValue)
                payload[SignalPayloadKeys.EspAllowContinueAnyway] = snapshot.AllowContinueAnyway.Value ? "true" : "false";

            var derivationInputs = new Dictionary<string, string>(payload, StringComparer.Ordinal)
            {
                ["source"] = "registry_firstsync",
            };

            _signalIngress.Post(
                kind: DecisionSignalKind.EspConfigDetected,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: "DeviceInfoCollector",
                evidence: new Evidence(
                    kind: EvidenceKind.Raw,
                    identifier: "esp_config_detected",
                    summary: $"SkipUser={snapshot.SkipUser?.ToString() ?? "unknown"}, SkipDevice={snapshot.SkipDevice?.ToString() ?? "unknown"}",
                    derivationInputs: derivationInputs),
                payload: payload);
        }

        private static string TryFormatJson(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input ?? string.Empty;

            try
            {
                using (var doc = JsonDocument.Parse(input))
                {
                    return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });
                }
            }
            catch
            {
                return input;
            }
        }
    }
}


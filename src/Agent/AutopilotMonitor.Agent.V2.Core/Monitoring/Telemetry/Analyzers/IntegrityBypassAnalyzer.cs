using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers
{
    /// <summary>
    /// Detects Windows 11 "unsupported hardware" installations where the PC Health Check /
    /// Setup-time hardware gates were bypassed via registry flags, and flags suspicious
    /// post-OOBE setup scripts. Such devices may complete enrollment successfully yet lack
    /// TPM-backed features (WHfB, BitLocker TPM protector, Device Health Attestation,
    /// Credential Guard) — a silent quality risk not otherwise surfaced by enrollment_complete.
    ///
    /// Checks performed:
    ///   1. HKLM\SYSTEM\Setup\LabConfig\Bypass{TPM,SecureBoot,RAM,Storage,Disk,CPU}Check (install-time)
    ///   2. HKLM\SYSTEM\Setup\MoSetup\AllowUpgradesWithUnsupportedTPMOrCPU (in-place upgrade)
    ///   3. HKU\&lt;SID&gt;\SOFTWARE\Microsoft\PCHC\UpgradeEligibility (WU upgrade bypass, per-user)
    ///   4. %WINDIR%\Setup\Scripts\SetupComplete.cmd + ErrorHandler.cmd (suspicious post-OOBE hooks)
    ///
    /// Correlation:
    ///   Bypass flags are cross-checked against current TPM and SecureBoot state; a bypass
    ///   combined with a missing security feature is escalated to "critical".
    ///
    /// Runs only at startup: the inspected artefacts are pre-enrollment state (install-time
    /// registry keys, OEM/bypass scripts) that do not change during enrollment. Shutdown
    /// would just re-emit the same data — the analyzer treats AnalyzeAtShutdown as a no-op.
    /// </summary>
    public class IntegrityBypassAnalyzer : IAgentAnalyzer
    {
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;

        public string Name => "IntegrityBypassAnalyzer";

        // LabConfig bypass value names. Both BypassStorageCheck and BypassDiskCheck are observed
        // in the wild — scripts differ, both should be captured.
        private static readonly string[] LabConfigBypassValues = new[]
        {
            "BypassTPMCheck",
            "BypassSecureBootCheck",
            "BypassRAMCheck",
            "BypassStorageCheck",
            "BypassDiskCheck",
            "BypassCPUCheck"
        };

        // Registry base SIDs that are NOT real user profiles
        private static readonly HashSet<string> SystemSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".DEFAULT",
            "S-1-5-18", // LocalSystem
            "S-1-5-19", // LocalService
            "S-1-5-20"  // NetworkService
        };

        public IntegrityBypassAnalyzer(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void AnalyzeAtStartup()
        {
            _logger.Info($"{Name}: Running startup analysis");
            RunAnalysis("startup");
        }

        public void AnalyzeAtShutdown()
        {
            // Pre-enrollment state does not change during enrollment — skip the shutdown run.
            _logger.Debug($"{Name}: Shutdown no-op (bypass state is pre-enrollment, already captured at startup)");
        }

        // -----------------------------------------------------------------------
        // Core analysis
        // -----------------------------------------------------------------------

        private void RunAnalysis(string trigger)
        {
            try
            {
                var labConfig = CheckLabConfig();
                var moSetup = CheckMoSetupAllowUnsupported();
                var pchc = CheckPchcUpgradeEligibility();
                var setupScripts = CheckSetupScripts();

                var tpmPresent = CheckTpmPresentAndEnabled();
                var secureBootEnabled = CheckSecureBootEnabled();

                // --------- Severity aggregation ---------
                var severity = EventSeverity.Info;
                var findings = new List<string>();

                bool anyLabConfigBypass = labConfig.ActiveBypasses.Count > 0;
                bool labConfigKeyExists = labConfig.KeyExists;
                bool moSetupBypass = moSetup.Value == 1;
                bool pchcBypass = pchc.AnyUserWithFlag;
                bool scriptsPresent = setupScripts.Any(s => s.Exists);

                // Critical: bypass + corresponding feature missing
                if (labConfig.ActiveBypasses.Contains("BypassTPMCheck") && tpmPresent.HasValue && tpmPresent.Value == false)
                {
                    severity = Escalate(severity, EventSeverity.Critical);
                    findings.Add("tpm_bypass_with_missing_tpm");
                }
                if (labConfig.ActiveBypasses.Contains("BypassSecureBootCheck") && secureBootEnabled.HasValue && secureBootEnabled.Value == false)
                {
                    severity = Escalate(severity, EventSeverity.Critical);
                    findings.Add("secureboot_bypass_with_disabled_secureboot");
                }
                if (moSetupBypass && ((tpmPresent.HasValue && tpmPresent.Value == false) || (secureBootEnabled.HasValue && secureBootEnabled.Value == false)))
                {
                    severity = Escalate(severity, EventSeverity.Critical);
                    findings.Add("upgrade_bypass_with_missing_feature");
                }

                // Warning: bypass active but feature currently still OK, or scripts present, or PCHC flag
                if (anyLabConfigBypass && severity < EventSeverity.Warning)
                {
                    severity = Escalate(severity, EventSeverity.Warning);
                    findings.Add("labconfig_bypass_active");
                }
                if (moSetupBypass && severity < EventSeverity.Warning)
                {
                    severity = Escalate(severity, EventSeverity.Warning);
                    findings.Add("mosetup_upgrade_bypass_active");
                }
                if (pchcBypass)
                {
                    severity = Escalate(severity, EventSeverity.Warning);
                    findings.Add("pchc_upgrade_eligibility_forced");
                }
                if (scriptsPresent)
                {
                    severity = Escalate(severity, EventSeverity.Warning);
                    findings.Add("suspicious_setup_scripts_present");
                }

                // Info: key exists but nothing active
                if (severity == EventSeverity.Info)
                {
                    if (labConfigKeyExists)
                        findings.Add("labconfig_key_present_inactive");
                    else
                        findings.Add("no_bypass_indicators");
                }

                string findingLabel = findings.FirstOrDefault() ?? "no_bypass_indicators";
                string message = severity >= EventSeverity.Warning
                    ? $"{Name}: Integrity bypass indicators detected ({string.Join(",", findings)})"
                    : $"{Name}: No integrity bypass indicators";

                _logger.Info(
                    $"{Name}: severity={severity}, findings=[{string.Join(",", findings)}], " +
                    $"labConfigBypasses={labConfig.ActiveBypasses.Count}, " +
                    $"moSetupBypass={moSetupBypass}, pchcBypass={pchcBypass}, " +
                    $"scriptsPresent={scriptsPresent}, tpm={FormatNullableBool(tpmPresent)}, " +
                    $"secureBoot={FormatNullableBool(secureBootEnabled)}");

                var data = new Dictionary<string, object>
                {
                    { "severity", severity.ToString().ToLowerInvariant() },
                    { "finding", findingLabel },
                    { "findings", findings },
                    { "triggered_at", trigger },
                    { "enrollment_phase_at_check", EnrollmentPhase.Unknown.ToString() },
                    { "checks", new Dictionary<string, object>
                        {
                            { "lab_config", new Dictionary<string, object>
                                {
                                    { "key_exists", labConfig.KeyExists },
                                    { "values", labConfig.Values },
                                    { "active_bypasses", labConfig.ActiveBypasses }
                                }
                            },
                            { "mo_setup", new Dictionary<string, object>
                                {
                                    { "key_exists", moSetup.KeyExists },
                                    { "value", moSetup.Value },
                                    { "flagged", moSetupBypass }
                                }
                            },
                            { "pchc_upgrade_eligibility", new Dictionary<string, object>
                                {
                                    { "users_checked", pchc.UsersChecked },
                                    { "users_with_flag", pchc.UsersWithFlag },
                                    { "any_user_with_flag", pchc.AnyUserWithFlag }
                                }
                            },
                            { "setup_scripts", setupScripts.Select(s => new Dictionary<string, object>
                                {
                                    { "path", s.Path },
                                    { "exists", s.Exists },
                                    { "size_bytes", s.SizeBytes },
                                    { "last_modified_utc", s.LastModifiedUtc.HasValue
                                        ? s.LastModifiedUtc.Value.ToString("O")
                                        : null }
                                }).ToList()
                            },
                            { "correlation", new Dictionary<string, object>
                                {
                                    { "tpm_present_and_enabled", tpmPresent },
                                    { "secure_boot_enabled", secureBootEnabled }
                                }
                            }
                        }
                    }
                };

                _post.Emit(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = Constants.EventTypes.IntegrityBypassAnalysis,
                    Severity = severity,
                    Source = Name,
                    Phase = EnrollmentPhase.Unknown,
                    Message = message,
                    Data = data
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"{Name}: Analysis failed unexpectedly", ex);
            }
        }

        private static EventSeverity Escalate(EventSeverity current, EventSeverity candidate)
            => candidate > current ? candidate : current;

        private static string FormatNullableBool(bool? value)
            => value.HasValue ? value.Value.ToString() : "unknown";

        // -----------------------------------------------------------------------
        // Individual checks
        // -----------------------------------------------------------------------

        private LabConfigCheckResult CheckLabConfig()
        {
            var result = new LabConfigCheckResult();
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\Setup\LabConfig", writable: false))
                {
                    if (key == null)
                    {
                        _logger.Debug($"{Name}: HKLM\\SYSTEM\\Setup\\LabConfig not present");
                        return result;
                    }

                    result.KeyExists = true;
                    foreach (var valueName in LabConfigBypassValues)
                    {
                        var raw = key.GetValue(valueName);
                        int intValue = 0;
                        if (raw != null)
                        {
                            try { intValue = Convert.ToInt32(raw); }
                            catch (Exception ex) when (ex is InvalidCastException || ex is FormatException || ex is OverflowException)
                            {
                                _logger.Warning($"{Name}: LabConfig '{valueName}' has unparseable value (raw type={raw.GetType().Name}): {ex.Message}");
                                intValue = 0;
                            }
                        }
                        result.Values[valueName] = intValue;
                        if (intValue == 1)
                        {
                            result.ActiveBypasses.Add(valueName);
                            _logger.Debug($"{Name}: LabConfig {valueName}=1 (active bypass)");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"{Name}: Failed to read LabConfig registry: {ex.Message}");
            }
            return result;
        }

        private MoSetupCheckResult CheckMoSetupAllowUnsupported()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\Setup\MoSetup", writable: false))
                {
                    if (key == null)
                        return new MoSetupCheckResult { KeyExists = false, Value = 0 };

                    var raw = key.GetValue("AllowUpgradesWithUnsupportedTPMOrCPU");
                    if (raw == null)
                        return new MoSetupCheckResult { KeyExists = true, Value = 0 };

                    int intValue = 0;
                    try { intValue = Convert.ToInt32(raw); }
                    catch (Exception ex) when (ex is InvalidCastException || ex is FormatException || ex is OverflowException)
                    {
                        _logger.Warning($"{Name}: MoSetup 'AllowUpgradesWithUnsupportedTPMOrCPU' has unparseable value (raw type={raw.GetType().Name}): {ex.Message}");
                    }
                    return new MoSetupCheckResult { KeyExists = true, Value = intValue };
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"{Name}: Failed to read MoSetup registry: {ex.Message}");
                return new MoSetupCheckResult { KeyExists = false, Value = 0 };
            }
        }

        /// <summary>
        /// Enumerates HKEY_USERS and checks SOFTWARE\Microsoft\PCHC\UpgradeEligibility for each
        /// real user profile. Agent runs as SYSTEM — loaded hives include all interactive user
        /// profiles that Windows has mounted. SIDs for .DEFAULT and system accounts are skipped.
        /// Profiles whose hive is not currently loaded are not visible here; that is acceptable —
        /// the relevant user during enrollment is the primary logged-in user whose hive is loaded.
        /// </summary>
        private PchcCheckResult CheckPchcUpgradeEligibility()
        {
            var result = new PchcCheckResult();
            try
            {
                using (var users = Registry.Users)
                {
                    foreach (var sid in users.GetSubKeyNames())
                    {
                        if (string.IsNullOrEmpty(sid)) continue;
                        if (SystemSids.Contains(sid)) continue;
                        // Skip the Classes-hive companions
                        if (sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)) continue;

                        result.UsersChecked.Add(sid);

                        try
                        {
                            using (var pchcKey = users.OpenSubKey($@"{sid}\SOFTWARE\Microsoft\PCHC", writable: false))
                            {
                                if (pchcKey == null) continue;

                                var raw = pchcKey.GetValue("UpgradeEligibility");
                                if (raw == null) continue;

                                int intValue = 0;
                                try { intValue = Convert.ToInt32(raw); }
                                catch (Exception ex) when (ex is InvalidCastException || ex is FormatException || ex is OverflowException)
                                {
                                    _logger.Warning($"{Name}: PCHC UpgradeEligibility for SID {sid} has unparseable value (raw type={raw.GetType().Name}): {ex.Message}");
                                    continue;
                                }

                                if (intValue == 1)
                                {
                                    result.UsersWithFlag.Add(sid);
                                    _logger.Debug($"{Name}: PCHC UpgradeEligibility=1 for SID {sid}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug($"{Name}: Failed to read PCHC for SID {sid}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"{Name}: Failed to enumerate HKEY_USERS: {ex.Message}");
            }
            return result;
        }

        private List<SetupScriptCheckResult> CheckSetupScripts()
        {
            var results = new List<SetupScriptCheckResult>();
            var windir = Environment.GetEnvironmentVariable("WINDIR");
            if (string.IsNullOrEmpty(windir))
                windir = @"C:\Windows";

            var candidates = new[]
            {
                Path.Combine(windir, @"Setup\Scripts\SetupComplete.cmd"),
                Path.Combine(windir, @"Setup\Scripts\ErrorHandler.cmd")
            };

            foreach (var path in candidates)
            {
                var entry = new SetupScriptCheckResult { Path = path, Exists = false };
                try
                {
                    if (File.Exists(path))
                    {
                        entry.Exists = true;
                        var fi = new FileInfo(path);
                        entry.SizeBytes = fi.Length;
                        entry.LastModifiedUtc = fi.LastWriteTimeUtc;
                        _logger.Debug($"{Name}: Setup script present: {path} ({fi.Length} bytes)");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"{Name}: Failed to stat {path}: {ex.Message}");
                }
                results.Add(entry);
            }
            return results;
        }

        /// <summary>
        /// Returns true if a TPM is present and enabled, false if definitely absent/disabled,
        /// null if indeterminate (query failed). Uses Win32_Tpm in root\CIMV2\Security\MicrosoftTpm.
        /// </summary>
        private bool? CheckTpmPresentAndEnabled()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    @"root\CIMV2\Security\MicrosoftTpm",
                    "SELECT IsEnabled_InitialValue, IsActivated_InitialValue FROM Win32_Tpm"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject tpm in results)
                    {
                        using (tpm)
                        {
                            var isEnabled = tpm["IsEnabled_InitialValue"] != null
                                && Convert.ToBoolean(tpm["IsEnabled_InitialValue"]);
                            // At least one Tpm instance exists — return its enabled state
                            return isEnabled;
                        }
                    }
                }
                // No Win32_Tpm instance returned — no TPM present
                return false;
            }
            catch (Exception ex)
            {
                _logger.Debug($"{Name}: TPM WMI query failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns true if UEFI Secure Boot is enabled, false if disabled, null if not
        /// queryable (e.g. BIOS firmware without the registry key).
        /// </summary>
        private bool? CheckSecureBootEnabled()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", writable: false))
                {
                    if (key == null) return null;
                    var raw = key.GetValue("UEFISecureBootEnabled");
                    if (raw == null) return null;
                    return Convert.ToInt32(raw) == 1;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"{Name}: SecureBoot registry read failed: {ex.Message}");
                return null;
            }
        }

        // -----------------------------------------------------------------------
        // Private result types
        // -----------------------------------------------------------------------

        private class LabConfigCheckResult
        {
            public bool KeyExists { get; set; }
            public Dictionary<string, int> Values { get; set; } = new Dictionary<string, int>();
            public List<string> ActiveBypasses { get; set; } = new List<string>();
        }

        private class MoSetupCheckResult
        {
            public bool KeyExists { get; set; }
            public int Value { get; set; }
        }

        private class PchcCheckResult
        {
            public List<string> UsersChecked { get; set; } = new List<string>();
            public List<string> UsersWithFlag { get; set; } = new List<string>();
            public bool AnyUserWithFlag => UsersWithFlag.Count > 0;
        }

        private class SetupScriptCheckResult
        {
            public string Path { get; set; }
            public bool Exists { get; set; }
            public long SizeBytes { get; set; }
            public DateTime? LastModifiedUtc { get; set; }
        }
    }
}

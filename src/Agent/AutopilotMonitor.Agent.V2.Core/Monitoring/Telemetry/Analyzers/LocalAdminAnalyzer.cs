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
    /// Analyzes local administrator accounts and user profiles on the device
    /// to detect pre-enrollment admin account creation — a known Autopilot bypass technique.
    ///
    /// Checks performed:
    ///   1. BypassNRO registry flag (HKLM\...\OOBE\BypassNRO = 1)
    ///   2. Unexpected local user accounts (via WMI Win32_UserAccount)
    ///   3. Unexpected C:\Users profile directories
    ///
    /// Confidence scoring:
    ///   BypassNRO = 1                          → +20 (low indicator)
    ///   Unexpected local account found         → +40 (medium indicator)
    ///   Account + matching C:\Users profile    → +40 (high indicator, profile overlap)
    ///
    /// Emits a single "local_admin_analysis" event at startup and at shutdown,
    /// enabling delta detection between pre- and post-enrollment state.
    /// </summary>
    public class LocalAdminAnalyzer : IAgentAnalyzer
    {
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;
        private readonly List<string> _allowedAccounts;

        // Built-in accounts and profile folders always present on a freshly imaged Windows device.
        // "Public", "Default", "Default User", "All Users" are folders/junctions in C:\Users, not user accounts.
        // "defaultuser0" is a temporary OOBE/Autopilot system account created during enrollment.
        private static readonly List<string> BuiltInAllowedAccounts = new List<string>
        {
            "Administrator",
            "Guest",
            "DefaultAccount",
            "WDAGUtilityAccount",
            "defaultuser0",    // Temporary OOBE/Autopilot system account, present during enrollment
            "defaultuser1",    // Sometimes seen in OOBE, but not always present
            "defaultuser2",    // Sometimes seen in OOBE, but not always present
            "Public",          // Profile folder (not a user account)
            "Default",         // Default user profile template
            "Default User",    // Symlink to Default in some Windows versions
            "All Users"        // Junction pointing to C:\ProgramData, always present
        };

        public string Name => "LocalAdminAnalyzer";

        public LocalAdminAnalyzer(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            List<string> allowedAccounts = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId  = tenantId  ?? throw new ArgumentNullException(nameof(tenantId));
            _post      = post      ?? throw new ArgumentNullException(nameof(post));
            _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));

            // Tenant-supplied accounts are additive (union with built-ins, not replacement)
            _allowedAccounts = new List<string>(BuiltInAllowedAccounts);
            if (allowedAccounts != null)
            {
                foreach (var account in allowedAccounts)
                {
                    if (!string.IsNullOrWhiteSpace(account) &&
                        !_allowedAccounts.Any(a => string.Equals(a, account, StringComparison.OrdinalIgnoreCase)))
                    {
                        _allowedAccounts.Add(account);
                    }
                }
            }
        }

        public void AnalyzeAtStartup()
        {
            _logger.Info($"{Name}: Running startup analysis");
            RunAnalysis("startup", EnrollmentPhase.Unknown);
        }

        public void AnalyzeAtShutdown()
        {
            _logger.Info($"{Name}: Running shutdown analysis");

            // At shutdown a user has logged in — their profile folder and account are expected.
            // Detect logged-in users via explorer.exe owner (same technique as DesktopArrivalDetector)
            // and add them dynamically so they don't trigger false positives.
            var loggedInUsers = GetLoggedInUserNames();
            // Phase=Unknown: this is an analysis event, not a phase declaration.
            // Phase-tagged events are reserved for explicit phase transitions (esp_phase_changed etc.).
            RunAnalysis("shutdown", EnrollmentPhase.Unknown, loggedInUsers);
        }

        // -----------------------------------------------------------------------
        // Core analysis
        // -----------------------------------------------------------------------

        private void RunAnalysis(string trigger, EnrollmentPhase phase, List<string> dynamicAllowedUsers = null)
        {
            try
            {
                // Build effective allowed list: static + optional dynamic (logged-in users at shutdown)
                var effectiveAllowed = _allowedAccounts;
                if (dynamicAllowedUsers != null && dynamicAllowedUsers.Count > 0)
                {
                    effectiveAllowed = new List<string>(_allowedAccounts);
                    foreach (var user in dynamicAllowedUsers)
                    {
                        if (!effectiveAllowed.Any(a => string.Equals(a, user, StringComparison.OrdinalIgnoreCase)))
                        {
                            effectiveAllowed.Add(user);
                            _logger.Info($"{Name}: Dynamically allowing logged-in user: {user}");
                        }
                    }
                }

                var bypassNroResult  = CheckBypassNroRegistry();
                var accountsResult   = CheckLocalAdminAccounts(effectiveAllowed);
                var profilesResult   = CheckUserProfiles(effectiveAllowed);

                int confidenceScore = 0;

                if (bypassNroResult.Value == 1)
                    confidenceScore += 20;

                if (accountsResult.Unexpected.Count > 0)
                    confidenceScore += 40;

                // Profile overlap: unexpected account AND matching C:\Users folder
                bool profileOverlap = accountsResult.Unexpected.Any(a =>
                    profilesResult.Unexpected.Any(p =>
                        string.Equals(a, p, StringComparison.OrdinalIgnoreCase)));
                if (profileOverlap)
                    confidenceScore += 40;

                confidenceScore = Math.Min(confidenceScore, 100);

                EventSeverity severity;
                string findingLabel;

                if (confidenceScore == 0)
                {
                    severity     = EventSeverity.Info;
                    findingLabel = "no_unexpected_admins_detected";
                }
                else if (confidenceScore < 40)
                {
                    severity     = EventSeverity.Info;
                    findingLabel = "bypass_nro_flag_only";
                }
                else if (confidenceScore < 80)
                {
                    severity     = EventSeverity.Warning;
                    findingLabel = "unexpected_local_admins_detected";
                }
                else
                {
                    severity     = EventSeverity.Error;
                    findingLabel = "unexpected_local_admins_detected";
                }

                _logger.Info(
                    $"{Name}: confidence={confidenceScore}, finding={findingLabel}, " +
                    $"bypassNro={bypassNroResult.Value}, " +
                    $"unexpectedAccounts={accountsResult.Unexpected.Count}, " +
                    $"unexpectedProfiles={profilesResult.Unexpected.Count}");

                var data = new Dictionary<string, object>
                {
                    { "confidence_score",           confidenceScore },
                    { "severity",                   severity.ToString().ToLower() },
                    { "finding",                    findingLabel },
                    { "triggered_at",               trigger },
                    { "enrollment_phase_at_check",  phase.ToString() },
                    { "allowed_accounts",           effectiveAllowed },
                    { "dynamically_allowed_users",  dynamicAllowedUsers ?? new List<string>() },
                    { "checks", new Dictionary<string, object>
                        {
                            { "bypass_nro", new Dictionary<string, object>
                                {
                                    { "value",   bypassNroResult.Value },
                                    { "flagged", bypassNroResult.Value == 1 }
                                }
                            },
                            { "unexpected_accounts",  accountsResult.Unexpected },
                            { "unexpected_profiles",  profilesResult.Unexpected },
                            { "accounts_checked",     accountsResult.AllChecked },
                            { "profiles_found",       profilesResult.AllFound }
                        }
                    }
                };

                var message = confidenceScore == 0
                    ? $"{Name}: No unexpected local admins detected"
                    : $"{Name}: Unexpected admin activity detected (confidence={confidenceScore})";

                _post.Emit(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId  = _tenantId,
                    EventType = Constants.EventTypes.LocalAdminAnalysis,
                    Severity  = severity,
                    Source    = Name,
                    Phase     = phase,
                    Message   = message,
                    Data      = data
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"{Name}: Analysis failed unexpectedly", ex);
            }
        }

        // -----------------------------------------------------------------------
        // Individual checks
        // -----------------------------------------------------------------------

        private BypassNroCheckResult CheckBypassNroRegistry()
        {
            try
            {
                const string keyPath   = @"SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE";
                const string valueName = "BypassNRO";

                using (var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: false))
                {
                    if (key == null)
                    {
                        _logger.Debug($"{Name}: BypassNRO registry key not found");
                        return new BypassNroCheckResult { Value = 0, KeyExists = false };
                    }

                    var raw = key.GetValue(valueName);
                    if (raw == null)
                    {
                        _logger.Debug($"{Name}: BypassNRO value not present");
                        return new BypassNroCheckResult { Value = 0, KeyExists = true };
                    }

                    var intValue = Convert.ToInt32(raw);
                    _logger.Debug($"{Name}: BypassNRO = {intValue}");
                    return new BypassNroCheckResult { Value = intValue, KeyExists = true };
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"{Name}: Failed to read BypassNRO registry: {ex.Message}");
                return new BypassNroCheckResult { Value = 0, KeyExists = false };
            }
        }

        private LocalAccountCheckResult CheckLocalAdminAccounts(List<string> allowedAccounts)
        {
            var allChecked = new List<string>();
            var unexpected = new List<string>();

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, Disabled FROM Win32_UserAccount WHERE LocalAccount = True"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var name     = obj["Name"]?.ToString() ?? string.Empty;
                        var disabled = obj["Disabled"] != null && Convert.ToBoolean(obj["Disabled"]);

                        if (string.IsNullOrEmpty(name))
                            continue;

                        allChecked.Add(name);

                        // Skip disabled accounts — they cannot be used to log in
                        if (disabled)
                            continue;

                        if (!allowedAccounts.Any(a =>
                            string.Equals(a, name, StringComparison.OrdinalIgnoreCase)))
                        {
                            unexpected.Add(name);
                            _logger.Debug($"{Name}: Unexpected local account: {name}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"{Name}: Failed to enumerate local accounts via WMI: {ex.Message}");
            }

            return new LocalAccountCheckResult { AllChecked = allChecked, Unexpected = unexpected };
        }

        private UserProfileCheckResult CheckUserProfiles(List<string> allowedAccounts)
        {
            var allFound   = new List<string>();
            var unexpected = new List<string>();

            try
            {
                const string usersRoot = @"C:\Users";

                if (!Directory.Exists(usersRoot))
                {
                    _logger.Debug($"{Name}: C:\\Users does not exist");
                    return new UserProfileCheckResult { AllFound = allFound, Unexpected = unexpected };
                }

                var dirs = Directory.GetDirectories(usersRoot, "*", SearchOption.TopDirectoryOnly);
                foreach (var dir in dirs)
                {
                    var folderName = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(folderName))
                        continue;

                    allFound.Add(folderName);

                    if (!allowedAccounts.Any(a =>
                        string.Equals(a, folderName, StringComparison.OrdinalIgnoreCase)))
                    {
                        unexpected.Add(folderName);
                        _logger.Debug($"{Name}: Unexpected profile folder: {folderName}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"{Name}: Failed to enumerate user profiles: {ex.Message}");
            }

            return new UserProfileCheckResult { AllFound = allFound, Unexpected = unexpected };
        }

        /// <summary>
        /// Detects currently logged-in user(s) by finding explorer.exe processes owned by
        /// real (non-system) users. Returns the plain username part (without domain prefix).
        /// Uses the same approach as DesktopArrivalDetector.
        /// </summary>
        private List<string> GetLoggedInUserNames()
        {
            var users = new List<string>();

            try
            {
                var explorerProcesses = System.Diagnostics.Process.GetProcessesByName("explorer");
                foreach (var proc in explorerProcesses)
                {
                    try
                    {
                        // Session 0 = SYSTEM session, skip
                        if (proc.SessionId == 0)
                            continue;

                        string owner = GetExplorerOwner(proc.Id);
                        if (string.IsNullOrEmpty(owner))
                            continue;

                        // Extract username part (after backslash if DOMAIN\User format)
                        var userName = owner;
                        var bsIdx = owner.LastIndexOf('\\');
                        if (bsIdx >= 0 && bsIdx < owner.Length - 1)
                            userName = owner.Substring(bsIdx + 1);

                        // Skip system/service accounts
                        if (IsSystemAccount(userName))
                            continue;

                        if (!users.Any(u => string.Equals(u, userName, StringComparison.OrdinalIgnoreCase)))
                        {
                            users.Add(userName);
                            _logger.Debug($"{Name}: Detected logged-in user: {userName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug($"{Name}: Error checking explorer.exe PID {proc.Id}: {ex.Message}");
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"{Name}: Failed to detect logged-in users: {ex.Message}");
            }

            return users;
        }

        /// <summary>
        /// Gets the owner of a process via WMI Win32_Process.GetOwner.
        /// Returns "DOMAIN\User" or "User", or null on failure.
        /// </summary>
        private string GetExplorerOwner(int processId)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_Process WHERE ProcessId = {processId}"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var outParams = new object[2];
                        var result = (uint)obj.InvokeMethod("GetOwner", outParams);
                        if (result == 0)
                        {
                            var user   = outParams[0]?.ToString();
                            var domain = outParams[1]?.ToString();
                            return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"{Name}: WMI GetOwner failed for PID {processId}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Returns true for system/service accounts that are not real enrolled users.
        /// </summary>
        private static bool IsSystemAccount(string userName)
        {
            if (string.IsNullOrEmpty(userName))
                return true;

            var systemNames = new[] { "SYSTEM", "LOCAL SERVICE", "NETWORK SERVICE" };
            foreach (var sn in systemNames)
            {
                if (string.Equals(userName, sn, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // DefaultUser* pattern (OOBE system accounts)
            if (userName.StartsWith("DefaultUser", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        // -----------------------------------------------------------------------
        // Private result types
        // -----------------------------------------------------------------------

        private class BypassNroCheckResult
        {
            public int  Value     { get; set; }
            public bool KeyExists { get; set; }
        }

        private class LocalAccountCheckResult
        {
            public List<string> AllChecked { get; set; } = new List<string>();
            public List<string> Unexpected { get; set; } = new List<string>();
        }

        private class UserProfileCheckResult
        {
            public List<string> AllFound   { get; set; } = new List<string>();
            public List<string> Unexpected { get; set; } = new List<string>();
        }
    }
}

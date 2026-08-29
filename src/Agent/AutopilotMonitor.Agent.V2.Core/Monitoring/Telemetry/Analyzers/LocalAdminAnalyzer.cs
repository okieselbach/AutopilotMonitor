using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
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
    ///   2. Unexpected local user accounts (via WMI Win32_UserAccount) — disabled accounts
    ///      included: a backdoor created with /active:no and re-enabled after enrollment
    ///      is dormant at both scans and would otherwise never surface
    ///   3. Administrators-group membership (NetLocalGroupGetMembers on S-1-5-32-544) —
    ///      an unexpected account that also holds admin membership is the actual backdoor
    ///   4. Unexpected C:\Users profile directories
    ///
    /// Confidence scoring:
    ///   BypassNRO = 1                                  → +20 (low indicator)
    ///   Unexpected local account found                 → +40 (medium indicator)
    ///   Unexpected account is an Administrators member → +40 (high indicator)
    ///   Account + matching C:\Users profile            → +40 (high indicator, profile overlap)
    ///
    /// The enabled-state of the built-in Administrator (RID 500) is reported
    /// (<c>builtin_administrator_enabled</c>) but not scored: whether OOBE enables it
    /// transiently is not established, so the field first collects evidence.
    ///
    /// Emits a single "local_admin_analysis" event at startup and at shutdown,
    /// enabling delta detection between pre- and post-enrollment state.
    ///
    /// Tenant-supplied allowed accounts may contain glob wildcards (e.g. "adm-*");
    /// see <see cref="MatchesAllowedEntry"/>.
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

        private readonly Core.Persistence.StartupEventGate _startupGate;

        public LocalAdminAnalyzer(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            List<string> allowedAccounts = null,
            Core.Persistence.StartupEventGate startupGate = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId  = tenantId  ?? throw new ArgumentNullException(nameof(tenantId));
            _post      = post      ?? throw new ArgumentNullException(nameof(post));
            _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
            _startupGate = startupGate;

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

                // An unexpected account that is also a member of Administrators is the
                // actual backdoor (enabled or dormant) — high indicator on its own.
                if (accountsResult.UnexpectedAdminMembers.Count > 0)
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
                    $"unexpectedAdminMembers={accountsResult.UnexpectedAdminMembers.Count}, " +
                    $"adminGroupEnumerated={accountsResult.AdministratorsGroupEnumerated}, " +
                    $"builtinAdministratorEnabled={accountsResult.BuiltInAdministratorEnabled?.ToString() ?? "unknown"}, " +
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
                            { "unexpected_admin_members", accountsResult.UnexpectedAdminMembers },
                            { "unexpected_profiles",  profilesResult.Unexpected },
                            { "accounts_checked",     accountsResult.AllChecked },
                            { "account_details",      accountsResult.AccountDetails },
                            { "administrators_group", new Dictionary<string, object>
                                {
                                    { "enumerated", accountsResult.AdministratorsGroupEnumerated },
                                    { "error_code", accountsResult.AdministratorsGroupErrorCode },
                                    { "members",    accountsResult.AdministratorsGroupMembers }
                                }
                            },
                            { "builtin_administrator_enabled", accountsResult.BuiltInAdministratorEnabled },
                            { "profiles_found",       profilesResult.AllFound }
                        }
                    }
                };

                var message = confidenceScore == 0
                    ? $"{Name}: No unexpected local admins detected"
                    : $"{Name}: Unexpected admin activity detected (confidence={confidenceScore})";

                // Restart dedup — startup trigger only (the shutdown emission must always go out):
                // identical findings were already reported by a previous agent run of this
                // enrollment; a changed payload (new account, flipped BypassNRO) re-emits.
                if (trigger == "startup"
                    && _startupGate != null
                    && !_startupGate.ShouldEmit(
                        Constants.EventTypes.LocalAdminAnalysis,
                        Core.Persistence.StartupEventGate.ComputeFingerprint(data)))
                {
                    _logger.Debug($"{Name}: startup findings unchanged since last emission — event suppressed (restart dedup)");
                    return;
                }

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

                // M4: commit the gate claim only after the emission went out — committing first
                // let a crash in between suppress the findings for the rest of the enrollment.
                if (trigger == "startup")
                    _startupGate?.MarkEmitted(Constants.EventTypes.LocalAdminAnalysis);
            }
            catch (Exception ex)
            {
                _logger.Error($"{Name}: Analysis failed unexpectedly", ex);
            }
        }

        // -----------------------------------------------------------------------
        // Allowed-list matching
        // -----------------------------------------------------------------------

        /// <summary>
        /// Matches an account or profile-folder name against one allowed-list entry.
        /// Entries containing <c>*</c> or <c>?</c> are glob patterns (same semantics as the
        /// hardware whitelist: <c>*</c> = any run, <c>?</c> = one char, case-insensitive);
        /// all other entries match exactly. No ambiguity with literal names — <c>*</c> and
        /// <c>?</c> are invalid characters in Windows account names.
        /// Internal for direct unit-testing via InternalsVisibleTo.
        /// </summary>
        internal static bool MatchesAllowedEntry(string name, string entry)
        {
            if (entry.IndexOf('*') < 0 && entry.IndexOf('?') < 0)
                return string.Equals(entry, name, StringComparison.OrdinalIgnoreCase);

            var regexPattern = "^" + Regex.Escape(entry)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";

            try
            {
                // Timeout guards against pathological patterns (ReDoS)
                return Regex.IsMatch(name, regexPattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        private static bool IsAllowed(string name, List<string> allowedAccounts)
        {
            return allowedAccounts.Any(a => MatchesAllowedEntry(name, a));
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
            var accounts = new List<LocalAccountInfo>();

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, Disabled, SID FROM Win32_UserAccount WHERE LocalAccount = True"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var name     = obj["Name"]?.ToString() ?? string.Empty;
                        var disabled = obj["Disabled"] != null && Convert.ToBoolean(obj["Disabled"]);
                        var sid      = obj["SID"]?.ToString();

                        if (string.IsNullOrEmpty(name))
                            continue;

                        accounts.Add(new LocalAccountInfo { Name = name, Disabled = disabled, Sid = sid });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"{Name}: Failed to enumerate local accounts via WMI: {ex.Message}");
            }

            // Administrators-group membership is read from the SAM, independent of WMI and of
            // the enabled state — a dormant backdoor with admin rights is still a member.
            var group = LocalGroupNativeMethods.GetAdministratorsMembers();
            if (!group.Succeeded)
                _logger.Warning($"{Name}: Failed to enumerate members of '{group.GroupName}': NET_API_STATUS={group.ErrorCode}");

            var result = EvaluateAccounts(accounts, group.Members, group.Succeeded, Environment.MachineName, allowedAccounts);
            result.AdministratorsGroupErrorCode = group.ErrorCode;

            foreach (var name in result.Unexpected)
                _logger.Debug($"{Name}: Unexpected local account: {name}");
            foreach (var name in result.UnexpectedAdminMembers)
                _logger.Debug($"{Name}: Unexpected Administrators member: {name}");

            return result;
        }

        /// <summary>
        /// Pure evaluation of the account inventory against the allowed list — separated from
        /// the WMI / SAM reads for unit-testing (InternalsVisibleTo).
        ///
        /// Rules:
        ///   - every local account is checked, disabled ones included (state is reported);
        ///   - a local account is an Administrators member when its SID matches a group member,
        ///     or (SID unavailable) its name matches a member of the local machine domain;
        ///   - <c>UnexpectedAdminMembers</c> = unexpected accounts holding membership, plus local
        ///     members not on the allowed list that the WMI inventory did not return at all;
        ///   - members outside the local machine domain (Entra role SIDs, domain groups) are
        ///     listed for delta comparison but never flagged — they are expected on joined devices.
        /// </summary>
        internal static LocalAccountCheckResult EvaluateAccounts(
            IList<LocalAccountInfo> accounts,
            IList<LocalGroupMember> adminMembers,
            bool adminGroupEnumerated,
            string machineName,
            List<string> allowedAccounts)
        {
            var result = new LocalAccountCheckResult { AdministratorsGroupEnumerated = adminGroupEnumerated };

            var adminSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var adminLocalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in adminMembers ?? new List<LocalGroupMember>())
            {
                result.AdministratorsGroupMembers.Add(member.DomainAndName ?? string.Empty);
                if (!string.IsNullOrEmpty(member.Sid))
                    adminSids.Add(member.Sid);
                if (IsLocalMember(member, machineName))
                    adminLocalNames.Add(member.Name);
            }

            foreach (var account in accounts ?? new List<LocalAccountInfo>())
            {
                var isAdmin = (!string.IsNullOrEmpty(account.Sid) && adminSids.Contains(account.Sid))
                              || adminLocalNames.Contains(account.Name);

                result.AllChecked.Add(account.Name);
                result.AccountDetails.Add(new Dictionary<string, object>
                {
                    { "name",                  account.Name },
                    { "disabled",              account.Disabled },
                    { "administrators_member", isAdmin }
                });

                if (account.Sid != null && account.Sid.EndsWith("-500", StringComparison.Ordinal))
                    result.BuiltInAdministratorEnabled = !account.Disabled;

                if (IsAllowed(account.Name, allowedAccounts))
                    continue;

                result.Unexpected.Add(account.Name);
                if (isAdmin)
                    result.UnexpectedAdminMembers.Add(account.Name);
            }

            // Local members the WMI inventory did not surface (WMI failure, or an account the
            // provider does not list) — still an unexpected admin when not on the allowed list.
            foreach (var name in adminLocalNames)
            {
                if (IsAllowed(name, allowedAccounts))
                    continue;
                if (result.UnexpectedAdminMembers.Any(u => string.Equals(u, name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                result.UnexpectedAdminMembers.Add(name);
            }

            return result;
        }

        private static bool IsLocalMember(LocalGroupMember member, string machineName)
        {
            var domain = member.Domain;
            if (string.IsNullOrEmpty(domain))
                return false;   // unresolved SID (Entra role, deleted account) — not a local account
            return string.Equals(domain, machineName, StringComparison.OrdinalIgnoreCase);
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

                    if (!IsAllowed(folderName, allowedAccounts))
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

                        string owner = ProcessOwnerLookup.ResolveOwner(proc.Id, proc.SessionId);
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

        /// <summary>One local account as returned by Win32_UserAccount.</summary>
        internal sealed class LocalAccountInfo
        {
            public string Name     { get; set; }
            public bool   Disabled { get; set; }
            public string Sid      { get; set; }
        }

        internal sealed class LocalAccountCheckResult
        {
            /// <summary>All local account names (enabled and disabled).</summary>
            public List<string> AllChecked { get; set; } = new List<string>();

            /// <summary>Account names not on the allowed list, disabled ones included.</summary>
            public List<string> Unexpected { get; set; } = new List<string>();

            /// <summary>Unexpected accounts that are members of the Administrators group.</summary>
            public List<string> UnexpectedAdminMembers { get; set; } = new List<string>();

            /// <summary>Per-account state: name, disabled, administrators_member.</summary>
            public List<Dictionary<string, object>> AccountDetails { get; set; } = new List<Dictionary<string, object>>();

            /// <summary>Every Administrators member (DOMAIN\name or SID), local or not — for delta comparison.</summary>
            public List<string> AdministratorsGroupMembers { get; set; } = new List<string>();

            public bool AdministratorsGroupEnumerated { get; set; }
            public int  AdministratorsGroupErrorCode  { get; set; }

            /// <summary>Enabled state of the built-in Administrator (RID 500); null when not found.</summary>
            public bool? BuiltInAdministratorEnabled { get; set; }
        }

        private class UserProfileCheckResult
        {
            public List<string> AllFound   { get; set; } = new List<string>();
            public List<string> Unexpected { get; set; } = new List<string>();
        }
    }
}

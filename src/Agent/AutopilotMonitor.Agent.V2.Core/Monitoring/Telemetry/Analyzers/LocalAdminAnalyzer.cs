using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// Allowed list (<see cref="LocalAdminAllowList"/> built-ins plus the tenant's entries): an entry
    /// allows an account in any role, or — <c>name|user</c> — only while it is not an
    /// Administrators member. A restriction always wins over an entry that allows more. The four
    /// built-in accounts are decided by their well-known RID alone (500 any role; 501/503/504 never
    /// an administrator), so localized names (Gast, Administrateur) are recognised and no name entry
    /// can lift or impose a role on them. Profile folders have no role.
    ///
    /// At shutdown the logged-in users are allowed dynamically — as profile folders only, and only
    /// identities that are NOT local SAM accounts (Entra / domain users, whose profile folder is the
    /// only artifact they leave). A logged-in local account is exactly the delta this analyzer exists
    /// to catch and is never exempted by its own login (CWE-807: the login state is under the
    /// adversary's control); it is reported as <c>logged_in_local_accounts</c>.
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
        private readonly List<AllowedEntry> _allowedEntries;

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

            var ignored = new List<string>();
            _allowedEntries = BuildAllowedEntries(allowedAccounts, ignored);
            foreach (var entry in ignored)
                _logger.Warning($"{Name}: Allowed-list entry '{entry}' is not valid (expected 'name' or 'name|user') — ignored, it allows nothing");
        }

        public void AnalyzeAtStartup()
        {
            _logger.Debug($"{Name}: Running startup analysis");
            RunAnalysis("startup", EnrollmentPhase.Unknown);
        }

        public void AnalyzeAtShutdown()
        {
            _logger.Debug($"{Name}: Running shutdown analysis");

            // At shutdown a user has logged in — their profile folder is expected. Detect logged-in
            // users via explorer.exe owner (same technique as DesktopArrivalDetector); RunAnalysis
            // allows them dynamically unless the name is a local SAM account (see SplitLoggedInUsers).
            var loggedInUsers = GetLoggedInUserNames();
            // Phase=Unknown: this is an analysis event, not a phase declaration.
            // Phase-tagged events are reserved for explicit phase transitions (esp_phase_changed etc.).
            RunAnalysis("shutdown", EnrollmentPhase.Unknown, loggedInUsers);
        }

        // -----------------------------------------------------------------------
        // Core analysis
        // -----------------------------------------------------------------------

        private void RunAnalysis(string trigger, EnrollmentPhase phase, List<string> loggedInUsers = null)
        {
            try
            {
                // The SAM inventory is read first: it decides which logged-in users may be allowed
                // dynamically. A logged-in name that IS a local account is never exempted — its
                // login state is under the adversary's control, and a local account created after
                // the startup scan is precisely the delta this run exists to catch.
                var localAccounts = ReadLocalAccounts();
                var loggedIn      = SplitLoggedInUsers(loggedInUsers, localAccounts);

                foreach (var user in loggedIn.LocalAccounts)
                    _logger.Warning($"{Name}: Logged-in user '{user}' is a local account — not allowed dynamically");

                // Effective list: static entries + the profile folders of non-local logged-in users at
                // shutdown. Folder-only: a login never allows a local ACCOUNT — not even when the WMI
                // inventory failed and the split above could not recognise a local name.
                var effectiveEntries = _allowedEntries;
                if (loggedIn.Allowed.Count > 0)
                {
                    effectiveEntries = new List<AllowedEntry>(_allowedEntries);
                    foreach (var user in loggedIn.Allowed)
                    {
                        if (!effectiveEntries.Any(e => string.Equals(e.Pattern, user, StringComparison.OrdinalIgnoreCase)))
                        {
                            effectiveEntries.Add(new AllowedEntry(user, LocalAccountRole.Any, profileFolderOnly: true));
                            _logger.Info($"{Name}: Dynamically allowing profile folder of logged-in user: {user}");
                        }
                    }
                }

                var bypassNroResult  = CheckBypassNroRegistry();
                var accountsResult   = CheckLocalAdminAccounts(localAccounts, effectiveEntries);
                var profilesResult   = CheckUserProfiles(effectiveEntries);

                var confidenceScore = ComputeConfidenceScore(
                    bypassNroResult.Value == 1, accountsResult.Unexpected, accountsResult.UnexpectedAdminMembers, profilesResult.Unexpected);

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
                    $"roleViolations={accountsResult.RoleViolations.Count}, " +
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
                    // Account entries in canonical form ("Guest|user"); the folder-only entries are
                    // constant or listed in dynamically_allowed_users.
                    { "allowed_accounts",           _allowedEntries.Where(e => !e.ProfileFolderOnly).Select(e => e.Display).ToList() },
                    { "dynamically_allowed_users",  loggedIn.Allowed },
                    { "logged_in_local_accounts",   loggedIn.LocalAccounts },
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
                            { "role_violations",      accountsResult.RoleViolations },
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

        /// <summary>
        /// Confidence 0-100: BypassNRO +20, any unexpected account +40, any unexpected Administrators
        /// member +40 (the actual backdoor, enabled or dormant; a role violation is one), an unexpected
        /// account with a C:\Users folder of the same name +40. Pure; internal for unit-testing.
        /// </summary>
        internal static int ComputeConfidenceScore(
            bool bypassNro, IList<string> unexpectedAccounts, IList<string> unexpectedAdminMembers, IList<string> unexpectedProfiles)
        {
            var score = 0;
            if (bypassNro)
                score += 20;
            if (unexpectedAccounts.Count > 0)
                score += 40;
            if (unexpectedAdminMembers.Count > 0)
                score += 40;
            if (unexpectedAccounts.Any(a => unexpectedProfiles.Any(p => string.Equals(a, p, StringComparison.OrdinalIgnoreCase))))
                score += 40;
            return Math.Min(score, 100);
        }

        // -----------------------------------------------------------------------
        // Allowed-list matching
        // -----------------------------------------------------------------------

        /// <summary>
        /// Matches an account or profile-folder name against one allowed-list pattern.
        /// Patterns containing <c>*</c> or <c>?</c> are globs (same semantics as the
        /// hardware whitelist: <c>*</c> = any run, <c>?</c> = one char, case-insensitive);
        /// all other patterns match exactly. No ambiguity with literal names — <c>*</c> and
        /// <c>?</c> are invalid characters in Windows account names.
        /// Internal for direct unit-testing via InternalsVisibleTo.
        /// </summary>
        internal static bool MatchesAllowedEntry(string name, string pattern)
        {
            if (pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0)
                return string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);

            var regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";

            try
            {
                // Timeout guards against pathological patterns (ReDoS). CultureInvariant: under a
                // Turkish culture "i" and "I" are no case pair, so "WLapsAdmin*" missed "WLAPSADMIN01".
                return Regex.IsMatch(name, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100));
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        /// <summary>
        /// The effective static list: the built-in account-name and profile-folder entries of
        /// <see cref="LocalAdminAllowList.BuiltIns"/> (RID entries are decided by RID, see
        /// <see cref="Decide"/>) plus the tenant's entries. An entry that does not parse is added to
        /// <paramref name="ignored"/> and allows nothing. Pure; internal for unit-testing.
        /// </summary>
        internal static List<AllowedEntry> BuildAllowedEntries(IEnumerable<string> tenantEntries, List<string> ignored)
        {
            var entries = new List<AllowedEntry>();
            foreach (var builtIn in LocalAdminAllowList.BuiltIns)
            {
                if (builtIn.Match == LocalAdminBuiltInMatch.AccountName)
                    AddDistinct(entries, new AllowedEntry(builtIn.Name, builtIn.Role, profileFolderOnly: false));
                else if (builtIn.Match == LocalAdminBuiltInMatch.ProfileFolder)
                    AddDistinct(entries, new AllowedEntry(builtIn.Name, LocalAccountRole.Any, profileFolderOnly: true));
            }

            foreach (var raw in tenantEntries ?? Enumerable.Empty<string>())
            {
                if (LocalAdminAllowList.TryParseEntry(raw, out var pattern, out var role))
                    AddDistinct(entries, new AllowedEntry(pattern, role, profileFolderOnly: false));
                else if (!string.IsNullOrWhiteSpace(raw))
                    ignored.Add(raw);
            }

            return entries;
        }

        private static void AddDistinct(List<AllowedEntry> entries, AllowedEntry entry)
        {
            if (!entries.Any(e => e.ProfileFolderOnly == entry.ProfileFolderOnly
                                  && string.Equals(e.Display, entry.Display, StringComparison.OrdinalIgnoreCase)))
                entries.Add(entry);
        }

        /// <summary>
        /// Decides one local account. The built-in accounts are decided by their RID alone: 500 in any
        /// role, 501/503/504 never as an administrator — no name entry, built-in or tenant, lifts or
        /// imposes a role on them. Without a SID (a WMI corner case) the English name stands in for the
        /// RID. Every other account is allowed when an account entry matches it, unless it is an
        /// administrator and ANY matching entry is standard-user only: a restriction always wins.
        /// Pure; internal for unit-testing.
        /// </summary>
        internal static AccountDecision Decide(string name, string sid, bool isAdmin, IReadOnlyList<AllowedEntry> allowed)
        {
            var builtIn = FindBuiltInRidEntry(name, sid);
            if (builtIn != null)
                return isAdmin && builtIn.Role == LocalAccountRole.StandardUser ? AccountDecision.RoleViolation : AccountDecision.Allowed;

            var covered = false;
            var restricted = false;
            foreach (var entry in allowed)
            {
                if (entry.ProfileFolderOnly || !MatchesAllowedEntry(name, entry.Pattern))
                    continue;
                covered = true;
                if (entry.Role == LocalAccountRole.StandardUser)
                    restricted = true;
            }

            if (!covered)
                return AccountDecision.Unlisted;
            return isAdmin && restricted ? AccountDecision.RoleViolation : AccountDecision.Allowed;
        }

        private static LocalAdminBuiltInEntry FindBuiltInRidEntry(string name, string sid)
        {
            var rid = MachineAccountRid(sid);
            foreach (var builtIn in LocalAdminAllowList.BuiltIns)
            {
                if (builtIn.Match != LocalAdminBuiltInMatch.Rid)
                    continue;
                var matches = rid.HasValue
                    ? builtIn.Rid == rid.Value
                    : string.IsNullOrEmpty(sid) && string.Equals(builtIn.Name, name, StringComparison.OrdinalIgnoreCase);
                if (matches)
                    return builtIn;
            }
            return null;
        }

        /// <summary>
        /// RID (the last sub-authority) of a local-machine account SID (<c>S-1-5-21-…</c>; the WMI query
        /// and the member filter already restrict to local accounts), else null.
        /// Internal for direct unit-testing via InternalsVisibleTo.
        /// </summary>
        internal static int? MachineAccountRid(string sid)
        {
            if (string.IsNullOrEmpty(sid) || !sid.StartsWith("S-1-5-21-", StringComparison.Ordinal))
                return null;

            var dash = sid.LastIndexOf('-');
            return int.TryParse(sid.Substring(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var rid)
                ? rid
                : (int?)null;
        }

        /// <summary>A profile folder has no role: any entry of the same name allows it.</summary>
        private static bool IsAllowedFolder(string folderName, IReadOnlyList<AllowedEntry> allowed)
            => allowed.Any(e => MatchesAllowedEntry(folderName, e.Pattern));

        /// <summary>
        /// Splits the logged-in user names into those that may be allowed dynamically (not a
        /// local SAM account — Entra / domain identities whose only artifact is a profile
        /// folder) and those that are local accounts (never allowed; reported).
        /// Pure; internal for direct unit-testing via InternalsVisibleTo.
        /// </summary>
        internal static LoggedInUserSplit SplitLoggedInUsers(IList<string> loggedInUsers, IList<LocalAccountInfo> localAccounts)
        {
            var split = new LoggedInUserSplit();
            if (loggedInUsers == null)
                return split;

            var localNames = new HashSet<string>(
                (localAccounts ?? new List<LocalAccountInfo>()).Select(a => a.Name),
                StringComparer.OrdinalIgnoreCase);

            foreach (var user in loggedInUsers)
            {
                if (string.IsNullOrWhiteSpace(user))
                    continue;
                if (localNames.Contains(user))
                    split.LocalAccounts.Add(user);
                else
                    split.Allowed.Add(user);
            }

            return split;
        }

        /// <summary>
        /// Top-level entries of C:\Users that are not user profiles and are skipped entirely:
        /// junctions (<c>All Users</c>, <c>Default User</c> and their localized variants point
        /// at ProgramData / Default) and <c>$&lt;32 hex&gt;</c> folders. Neither can be the
        /// profile of a local account — a SAM account name is at most 20 characters — so they
        /// can never contribute to the profile-overlap score; listing them is pure noise.
        /// Internal for direct unit-testing via InternalsVisibleTo.
        /// </summary>
        internal static bool IsNonProfileFolder(string folderName, FileAttributes attributes)
        {
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return true;

            return folderName != null
                   && folderName.Length == 33
                   && folderName[0] == '$'
                   && folderName.Skip(1).All(Uri.IsHexDigit);
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

        /// <summary>Local account inventory via WMI; empty on failure (logged at Warning).</summary>
        private List<LocalAccountInfo> ReadLocalAccounts()
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

            return accounts;
        }

        private LocalAccountCheckResult CheckLocalAdminAccounts(List<LocalAccountInfo> accounts, IReadOnlyList<AllowedEntry> allowed)
        {
            // Administrators-group membership is read from the SAM, independent of WMI and of
            // the enabled state — a dormant backdoor with admin rights is still a member.
            var group = LocalGroupNativeMethods.GetAdministratorsMembers();
            if (!group.Succeeded)
                _logger.Warning($"{Name}: Failed to enumerate members of '{group.GroupName}': NET_API_STATUS={group.ErrorCode}");

            var result = EvaluateAccounts(accounts, group.Members, group.Succeeded, Environment.MachineName, allowed);
            result.AdministratorsGroupErrorCode = group.ErrorCode;

            foreach (var name in result.Unexpected)
                _logger.Debug($"{Name}: Unexpected local account: {name}");
            foreach (var name in result.UnexpectedAdminMembers)
                _logger.Debug($"{Name}: Unexpected Administrators member: {name}");
            foreach (var name in result.RoleViolations)
                _logger.Debug($"{Name}: Allowed only as a standard user but an Administrators member: {name}");

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
        ///   - each account is decided by <see cref="Decide"/>; an account no entry allows, or one
        ///     that breaks a standard-user entry, is unexpected (and an unexpected admin when it
        ///     holds membership; the latter also a role violation);
        ///   - local members the WMI inventory did not return at all are decided as administrators
        ///     and land in both unexpected lists, like any unknown administrator;
        ///   - with membership unknown (enumeration failed) nobody counts as an administrator, so a
        ///     standard-user entry acts like a plain one;
        ///   - members outside the local machine domain (Entra role SIDs, domain groups) are
        ///     listed for delta comparison but never flagged — they are expected on joined devices.
        /// </summary>
        internal static LocalAccountCheckResult EvaluateAccounts(
            IList<LocalAccountInfo> accounts,
            IList<LocalGroupMember> adminMembers,
            bool adminGroupEnumerated,
            string machineName,
            IReadOnlyList<AllowedEntry> allowed)
        {
            var result = new LocalAccountCheckResult { AdministratorsGroupEnumerated = adminGroupEnumerated };

            var adminSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var adminLocalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var adminLocalMembers = new List<LocalGroupMember>();
            foreach (var member in adminMembers ?? new List<LocalGroupMember>())
            {
                result.AdministratorsGroupMembers.Add(member.DomainAndName ?? string.Empty);
                if (!string.IsNullOrEmpty(member.Sid))
                    adminSids.Add(member.Sid);
                if (IsLocalMember(member, machineName))
                {
                    adminLocalNames.Add(member.Name);
                    adminLocalMembers.Add(member);
                }
            }

            var inventorySids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inventoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var account in accounts ?? new List<LocalAccountInfo>())
            {
                var isAdmin = (!string.IsNullOrEmpty(account.Sid) && adminSids.Contains(account.Sid))
                              || adminLocalNames.Contains(account.Name);
                var rid = MachineAccountRid(account.Sid);

                if (!string.IsNullOrEmpty(account.Sid))
                    inventorySids.Add(account.Sid);
                inventoryNames.Add(account.Name);

                result.AllChecked.Add(account.Name);
                result.AccountDetails.Add(new Dictionary<string, object>
                {
                    { "name",                  account.Name },
                    { "disabled",              account.Disabled },
                    { "administrators_member", isAdmin },
                    { "rid",                   rid }
                });

                if (rid == 500)
                    result.BuiltInAdministratorEnabled = !account.Disabled;

                Record(result, account.Name, Decide(account.Name, account.Sid, isAdmin, allowed), isAdmin);
            }

            // Local members the WMI inventory did not surface (WMI failure, or an account the
            // provider does not list): the SAM is authoritative for membership.
            foreach (var member in adminLocalMembers)
            {
                var returnedByWmi = !string.IsNullOrEmpty(member.Sid)
                    ? inventorySids.Contains(member.Sid)
                    : inventoryNames.Contains(member.Name);
                if (returnedByWmi)
                    continue;
                Record(result, member.Name, Decide(member.Name, member.Sid, isAdmin: true, allowed), isAdmin: true);
            }

            return result;
        }

        private static void Record(LocalAccountCheckResult result, string name, AccountDecision decision, bool isAdmin)
        {
            if (decision == AccountDecision.Allowed)
                return;
            AddOnce(result.Unexpected, name);
            if (isAdmin)
                AddOnce(result.UnexpectedAdminMembers, name);
            if (decision == AccountDecision.RoleViolation)
                AddOnce(result.RoleViolations, name);
        }

        private static void AddOnce(List<string> names, string name)
        {
            if (!names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                names.Add(name);
        }

        private static bool IsLocalMember(LocalGroupMember member, string machineName)
        {
            var domain = member.Domain;
            if (string.IsNullOrEmpty(domain))
                return false;   // unresolved SID (Entra role, deleted account) — not a local account
            return string.Equals(domain, machineName, StringComparison.OrdinalIgnoreCase);
        }

        private UserProfileCheckResult CheckUserProfiles(IReadOnlyList<AllowedEntry> allowed)
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

                    if (IsNonProfileFolder(folderName, new DirectoryInfo(dir).Attributes))
                    {
                        _logger.Debug($"{Name}: Skipping non-profile folder: {folderName}");
                        continue;
                    }

                    allFound.Add(folderName);

                    if (!IsAllowedFolder(folderName, allowed))
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

            /// <summary>Administrators members that an entry allows only as a standard user (subset of <see cref="UnexpectedAdminMembers"/>).</summary>
            public List<string> RoleViolations { get; set; } = new List<string>();

            /// <summary>Per-account state: name, disabled, administrators_member, rid.</summary>
            public List<Dictionary<string, object>> AccountDetails { get; set; } = new List<Dictionary<string, object>>();

            /// <summary>Every Administrators member (DOMAIN\name or SID), local or not — for delta comparison.</summary>
            public List<string> AdministratorsGroupMembers { get; set; } = new List<string>();

            public bool AdministratorsGroupEnumerated { get; set; }
            public int  AdministratorsGroupErrorCode  { get; set; }

            /// <summary>Enabled state of the built-in Administrator (RID 500); null when not found.</summary>
            public bool? BuiltInAdministratorEnabled { get; set; }
        }

        /// <summary>One effective allowed-list entry: a name or glob and what it may allow.</summary>
        internal sealed class AllowedEntry
        {
            public AllowedEntry(string pattern, LocalAccountRole role, bool profileFolderOnly)
            {
                Pattern = pattern;
                Role = role;
                ProfileFolderOnly = profileFolderOnly;
            }

            public string Pattern { get; }

            public LocalAccountRole Role { get; }

            /// <summary>Allows the C:\Users folder of this name, never a local account.</summary>
            public bool ProfileFolderOnly { get; }

            /// <summary>Canonical entry text as the event lists it ("Guest|user").</summary>
            public string Display => LocalAdminAllowList.Format(Pattern, Role);
        }

        internal enum AccountDecision
        {
            Allowed,

            /// <summary>No entry allows the account.</summary>
            Unlisted,

            /// <summary>An administrator that a standard-user entry (or a built-in RID) allows only as a standard user.</summary>
            RoleViolation,
        }

        /// <summary>Logged-in users at shutdown, split by whether the name is a local SAM account.</summary>
        internal sealed class LoggedInUserSplit
        {
            /// <summary>Not a local account (Entra / domain identity) — allowed dynamically.</summary>
            public List<string> Allowed { get; } = new List<string>();

            /// <summary>Local accounts that are logged in — never allowed, reported.</summary>
            public List<string> LocalAccounts { get; } = new List<string>();
        }

        private class UserProfileCheckResult
        {
            public List<string> AllFound   { get; set; } = new List<string>();
            public List<string> Unexpected { get; set; } = new List<string>();
        }
    }
}

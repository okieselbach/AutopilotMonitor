using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather
{
    /// <summary>
    /// Privacy and security guards for GatherRule collectors.
    ///
    /// All allowlists are loaded from the embedded guardrails.json resource
    /// (source of truth: rules/guardrails.json). They restrict data collection
    /// to enrollment-relevant information only. Rules targeting paths, queries,
    /// or commands outside these lists are blocked and reported as security_warning events.
    ///
    /// To allow additional paths: edit rules/guardrails.json and run
    /// node rules/scripts/combine.js to regenerate all derived files.
    ///
    /// Segment-bounded matching is used: the path must match the prefix exactly
    /// up to a path separator or end of string, preventing prefix spoofing.
    /// Registry paths are expected without the hive prefix (e.g. "SOFTWARE\\Microsoft\\..." not "HKLM\\...").
    /// File paths should be expanded (no environment variables).
    /// Commands must match exactly (trimmed) — no prefix matching for security.
    /// </summary>
    public static class GatherRuleGuards
    {
        // -----------------------------------------------------------------------
        // Loaded from embedded guardrails.json
        // -----------------------------------------------------------------------
        public static readonly IReadOnlyList<string> AllowedRegistryPrefixes;
        public static readonly IReadOnlyList<string> AllowedFilePrefixes;

        /// <summary>
        /// Folders under the signed-in user's profile that file collectors and
        /// admin-configured diagnostics paths may read, RELATIVE to the profile root
        /// (e.g. <c>AppData\Local\Vendor</c>). Resolved against the path
        /// %LOGGED_ON_USER_PROFILE% produced, at match time — the profile name is not
        /// known when the list is authored, which is why these entries cannot live in
        /// <see cref="AllowedFilePrefixes"/> (matched literally against the expanded path).
        /// </summary>
        public static readonly IReadOnlyList<string> AllowedUserProfileFilePrefixes;
        public static readonly IReadOnlyList<string> AllowedWmiQueryPrefixes;
        public static readonly IReadOnlyCollection<string> AllowedCommands;
        public static readonly IReadOnlyList<string> AllowedEventLogChannels;

        // Derived from AllowedWmiQueryPrefixes: entries of the canonical form
        // "SELECT * FROM <class>" contribute their class name; any other entry
        // (none today) stays on the legacy literal-prefix path.
        private static readonly HashSet<string> AllowedWmiClasses;
        private static readonly List<string> ResidualWmiPrefixes;

        // Canonical allowlist entry: exactly "SELECT * FROM <class>".
        private static readonly Regex CanonicalWmiEntryRegex = new Regex(
            @"^SELECT\s+\*\s+FROM\s+([A-Za-z_][A-Za-z0-9_]*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // WQL shape "SELECT <* | property-list> FROM <class>", class token bounded
        // by whitespace or end of string (a trailing WHERE clause is permitted,
        // "Win32_BIOSX" cannot pass as "Win32_BIOS").
        private static readonly Regex WmiQueryRegex = new Regex(
            @"^SELECT\s+(?:\*|[A-Za-z_][A-Za-z0-9_]*(?:\s*,\s*[A-Za-z_][A-Za-z0-9_]*)*)\s+FROM\s+([A-Za-z_][A-Za-z0-9_]*)(?=\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Hard blocks: never allowed, even in unrestricted mode. Internal (not private)
        // so GuardrailsParityTests can pin the guardrails.json display mirror to these
        // constants — enforcement stays code-only by design.
        internal static readonly string BlockedUsersPrefix = Path.GetFullPath(@"C:\Users");
        internal static readonly string[] AdditionalHardBlockedPathPrefixes = new[]
        {
            Path.GetFullPath(@"C:\Windows\System32\config"),  // SAM, SECURITY, SYSTEM hives
        };

        // The only subdirectories of a user profile the C:\Users hard block will ever
        // release (used with %LOGGED_ON_USER_PROFILE%). Single copy for both guards —
        // DiagnosticsPathGuards delegates here.
        internal static readonly string[] AllowedUserProfileSubdirs = new[]
        {
            @"AppData\Local",
            @"AppData\Roaming",
        };

        // Hard limit: maximum command length (even in unrestricted mode)
        internal const int MaxCommandLength = 2000;

        // Event log channels that are never readable, even in unrestricted mode.
        // Security carries the audit trail of user behaviour; the PowerShell channels
        // carry script-block logging, which routinely contains secrets in clear text.
        // Mirrored in rules/guardrails.json ("blockedEventLogChannels") for the portal
        // to display — enforcement lives here so a parse error cannot lift it.
        internal static readonly string[] HardBlockedEventLogChannels = new[]
        {
            "Security",
            "Microsoft-Windows-PowerShell",
            "Windows PowerShell",
            "Microsoft-Windows-Sysmon",
        };

        // Hard-blocked command patterns: never allowed, even in unrestricted mode.
        // These prevent privilege escalation, persistence, and data exfiltration.
        // Mirrored in rules/guardrails.json ("blockedCommandPatterns") for portal/MCP
        // pre-flight display — enforcement lives here so a parse error cannot lift it.
        internal static readonly string[] HardBlockedCommandPatterns = new[]
        {
            // Download / exfiltration
            "Invoke-WebRequest", "Invoke-RestMethod", "Start-BitsTransfer",
            "wget", "curl", "certutil -urlcache",
            // User / group manipulation
            "New-LocalUser", "Add-LocalGroupMember", "net user", "net localgroup",
            // Boot configuration
            "bcdedit", "bcdboot",
            // Persistence mechanisms
            "schtasks /create", "Register-ScheduledTask",
            // Destructive operations
            "Remove-Item -Recurse", "Format-Volume",
            // Execution policy bypass
            "Set-ExecutionPolicy",
        };

        static GatherRuleGuards()
        {
            try
            {
                var json = LoadEmbeddedGuardrails();
                if (json != null)
                {
                    var obj = JObject.Parse(json);

                    AllowedRegistryPrefixes = FlattenCategorized(obj, "registryPrefixes", "prefixes");
                    AllowedFilePrefixes = obj["filePrefixes"]?.ToObject<List<string>>() ?? new List<string>();
                    AllowedUserProfileFilePrefixes = LoadUserProfilePrefixes(obj);
                    AllowedWmiQueryPrefixes = obj["wmiQueryPrefixes"]?.ToObject<List<string>>() ?? new List<string>();

                    var commands = FlattenCategorized(obj, "allowedCommands", "commands");
                    AllowedCommands = new HashSet<string>(commands, StringComparer.OrdinalIgnoreCase);

                    AllowedEventLogChannels = FlattenCategorized(obj, "eventLogChannels", "channels");
                    DeriveWmiClassAllowlist(AllowedWmiQueryPrefixes, out AllowedWmiClasses, out ResidualWmiPrefixes);
                    return;
                }
            }
            catch
            {
                // Fallback to empty lists on parse error — agent will block everything
                // and emit security_warning events, making the issue visible.
            }

            AllowedRegistryPrefixes = new List<string>();
            AllowedFilePrefixes = new List<string>();
            AllowedUserProfileFilePrefixes = new List<string>();
            AllowedWmiQueryPrefixes = new List<string>();
            AllowedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AllowedEventLogChannels = new List<string>();
            AllowedWmiClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ResidualWmiPrefixes = new List<string>();
        }

        /// <summary>
        /// Reads <c>userProfileFilePrefixes</c> and drops every entry that is rooted, carries a
        /// traversal, or does not sit under one of <see cref="AllowedUserProfileSubdirs"/>.
        /// The C:\Users hard block releases nothing outside AppData, so such an entry could only
        /// ever be dead text — dropping it at load keeps the list honest instead of silently
        /// ineffective, and no configuration can widen the hard block's exception.
        /// </summary>
        private static List<string> LoadUserProfilePrefixes(JObject obj)
        {
            var raw = obj["userProfileFilePrefixes"]?.ToObject<List<string>>() ?? new List<string>();
            var result = new List<string>();

            foreach (var entry in raw)
            {
                if (string.IsNullOrWhiteSpace(entry))
                    continue;

                var trimmed = entry.Trim().TrimEnd('\\');
                if (Path.IsPathRooted(trimmed))
                    continue;
                if (trimmed.IndexOf("..", StringComparison.Ordinal) >= 0)
                    continue;

                foreach (var subdir in AllowedUserProfileSubdirs)
                {
                    if (trimmed.StartsWith(subdir, StringComparison.OrdinalIgnoreCase) &&
                        (trimmed.Length == subdir.Length || trimmed[subdir.Length] == '\\'))
                    {
                        result.Add(trimmed);
                        break;
                    }
                }
            }

            return result;
        }

        private static void DeriveWmiClassAllowlist(
            IReadOnlyList<string> entries,
            out HashSet<string> classes,
            out List<string> residual)
        {
            classes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            residual = new List<string>();

            foreach (var entry in entries)
            {
                var match = CanonicalWmiEntryRegex.Match(entry.Trim());
                if (match.Success)
                    classes.Add(match.Groups[1].Value);
                else
                    residual.Add(entry);
            }
        }

        private static string LoadEmbeddedGuardrails()
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(
                "AutopilotMonitor.Agent.V2.Core.Resources.guardrails.json"))
            {
                if (stream == null)
                    return null;
                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        /// <summary>
        /// Flattens a categorized array structure like
        /// [{ "category": "...", "prefixes": ["a","b"] }, ...] into ["a","b",...].
        /// </summary>
        private static List<string> FlattenCategorized(JObject root, string arrayProp, string itemsProp)
        {
            var result = new List<string>();
            var arr = root[arrayProp] as JArray;
            if (arr == null) return result;

            foreach (var group in arr)
            {
                var items = group[itemsProp] as JArray;
                if (items == null) continue;
                foreach (var item in items)
                {
                    var val = item.Value<string>();
                    if (!string.IsNullOrEmpty(val))
                        result.Add(val);
                }
            }
            return result;
        }

        // -----------------------------------------------------------------------
        // Guard methods
        // -----------------------------------------------------------------------

        /// <summary>
        /// Returns true if the registry subPath (hive stripped) matches an allowed prefix
        /// with segment-bounded matching (next char must be '\' or end of string).
        /// </summary>
        public static bool IsRegistryPathAllowed(string subPath)
            => IsRegistryPathAllowed(subPath, unrestrictedMode: false);

        /// <summary>
        /// Returns true if the registry subPath is allowed.
        /// When unrestrictedMode is true, all registry paths are allowed.
        /// </summary>
        public static bool IsRegistryPathAllowed(string subPath, bool unrestrictedMode)
        {
            if (string.IsNullOrEmpty(subPath))
                return false;

            if (unrestrictedMode)
                return true;

            return AllowedRegistryPrefixes.Any(prefix =>
                subPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (subPath.Length == prefix.Length || subPath[prefix.Length] == '\\'));
        }

        /// <summary>
        /// Returns true if the expanded file path matches an allowed prefix
        /// with segment-bounded matching and path normalization to prevent traversal.
        /// </summary>
        public static bool IsFilePathAllowed(string expandedPath)
            => IsFilePathAllowed(expandedPath, unrestrictedMode: false, userProfilePath: null);

        /// <summary>
        /// Returns true if the expanded file path is allowed.
        /// When unrestrictedMode is true, all paths are allowed except C:\Users (privacy protection).
        /// Path normalization and traversal protection always apply regardless of mode.
        /// </summary>
        public static bool IsFilePathAllowed(string expandedPath, bool unrestrictedMode)
            => IsFilePathAllowed(expandedPath, unrestrictedMode, userProfilePath: null);

        /// <summary>
        /// Returns true if the expanded file path is allowed.
        /// When userProfilePath is provided (from %LOGGED_ON_USER_PROFILE% token), paths under
        /// the user's AppData\Local and AppData\Roaming are additionally allowed.
        /// </summary>
        public static bool IsFilePathAllowed(string expandedPath, bool unrestrictedMode, string userProfilePath)
        {
            if (string.IsNullOrEmpty(expandedPath))
                return false;

            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(expandedPath);
            }
            catch
            {
                return false;
            }

            // C:\Users block always applies (even in unrestricted mode)
            // Exception: paths under <userProfilePath>\AppData\Local or AppData\Roaming
            // are allowed when the %LOGGED_ON_USER_PROFILE% token was used. The exception
            // only LIFTS the block — in restricted mode the allowlist below still decides.
            if (normalizedPath.StartsWith(BlockedUsersPrefix, StringComparison.OrdinalIgnoreCase) &&
                (normalizedPath.Length == BlockedUsersPrefix.Length ||
                 normalizedPath[BlockedUsersPrefix.Length] == Path.DirectorySeparatorChar))
            {
                if (!IsUserProfileSubpathAllowed(normalizedPath, userProfilePath))
                    return false;
            }

            // Additional hard-blocked paths (even in unrestricted mode)
            foreach (var blocked in AdditionalHardBlockedPathPrefixes)
            {
                if (normalizedPath.StartsWith(blocked, StringComparison.OrdinalIgnoreCase) &&
                    (normalizedPath.Length == blocked.Length ||
                     normalizedPath[blocked.Length] == Path.DirectorySeparatorChar))
                {
                    return false;
                }
            }

            if (unrestrictedMode)
                return true;

            if (AllowedFilePrefixes.Any(prefix =>
                normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (normalizedPath.Length == prefix.Length || normalizedPath[prefix.Length] == '\\')))
            {
                return true;
            }

            // Vendor folders under the signed-in user's profile. Only reachable when the rule
            // spelled %LOGGED_ON_USER_PROFILE% and the resolver found an interactive user —
            // a null profile denies, so a machine with nobody signed in cannot widen anything.
            return IsUserProfilePathOnAllowlist(normalizedPath, userProfilePath);
        }

        /// <summary>
        /// True when the normalized path sits under one of <see cref="AllowedUserProfileSubdirs"/>
        /// of the detected user profile. This is the C:\Users hard block's only exception; it
        /// releases the block and nothing else. A null profile denies.
        /// </summary>
        internal static bool IsUserProfileSubpathAllowed(string normalizedPath, string userProfilePath)
            => AllowedUserProfileSubdirs.Any(subdir =>
                   MatchesProfileRelativePrefix(normalizedPath, userProfilePath, subdir));

        /// <summary>
        /// True when the normalized path is admitted by an entry of
        /// <see cref="AllowedUserProfileFilePrefixes"/> under the detected user profile.
        /// </summary>
        internal static bool IsUserProfilePathOnAllowlist(string normalizedPath, string userProfilePath)
            => AllowedUserProfileFilePrefixes.Any(prefix =>
                   MatchesProfileRelativePrefix(normalizedPath, userProfilePath, prefix));

        /// <summary>
        /// Segment-bounded match of <paramref name="normalizedPath"/> against
        /// &lt;profile&gt;\&lt;relativePrefix&gt;. Both sides go through Path.GetFullPath, so a
        /// traversal spelled into either one cannot slip past the boundary check.
        /// </summary>
        private static bool MatchesProfileRelativePrefix(
            string normalizedPath, string userProfilePath, string relativePrefix)
        {
            if (string.IsNullOrEmpty(normalizedPath) || string.IsNullOrEmpty(userProfilePath))
                return false;

            try
            {
                var allowedPrefix = Path.GetFullPath(
                    Path.Combine(Path.GetFullPath(userProfilePath), relativePrefix));

                return normalizedPath.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase) &&
                       (normalizedPath.Length == allowedPrefix.Length ||
                        normalizedPath[allowedPrefix.Length] == Path.DirectorySeparatorChar);
            }
            catch
            {
                // Path normalization failure — deny.
                return false;
            }
        }

        /// <summary>
        /// Returns true if the WMI query (trimmed) is "SELECT &lt;* | property-list&gt; FROM &lt;class&gt;"
        /// (optionally followed by a WHERE clause) against a class on the allowlist.
        /// A property projection exposes a strict subset of the allowed "SELECT *".
        /// </summary>
        public static bool IsWmiQueryAllowed(string query)
            => IsWmiQueryAllowed(query, unrestrictedMode: false);

        /// <summary>
        /// Returns true if the WMI query is allowed.
        /// When unrestrictedMode is true, all WMI queries are allowed.
        /// </summary>
        public static bool IsWmiQueryAllowed(string query, bool unrestrictedMode)
        {
            if (string.IsNullOrEmpty(query))
                return false;

            if (unrestrictedMode)
                return true;

            var trimmed = query.Trim();

            var match = WmiQueryRegex.Match(trimmed);
            if (match.Success && AllowedWmiClasses.Contains(match.Groups[1].Value))
                return true;

            // Non-canonical allowlist entries keep the legacy literal-prefix match.
            return ResidualWmiPrefixes.Any(prefix =>
                trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (trimmed.Length == prefix.Length || char.IsWhiteSpace(trimmed[prefix.Length])));
        }

        /// <summary>
        /// Returns true if the event log channel matches an allowed entry with
        /// boundary matching (next char must be '/' or end of string), so that
        /// "Microsoft-Windows-AAD" admits "Microsoft-Windows-AAD/Operational"
        /// but not "Microsoft-Windows-AADSomethingElse".
        /// </summary>
        public static bool IsEventLogChannelAllowed(string channel)
            => IsEventLogChannelAllowed(channel, unrestrictedMode: false);

        /// <summary>
        /// Returns true if the event log channel is allowed.
        /// When unrestrictedMode is true, all channels are allowed except the
        /// hard-blocked ones (audit trail and script-block logging).
        /// </summary>
        public static bool IsEventLogChannelAllowed(string channel, bool unrestrictedMode)
        {
            if (string.IsNullOrEmpty(channel))
                return false;

            var trimmed = channel.Trim();

            // Hard blocks apply even in unrestricted mode
            foreach (var blocked in HardBlockedEventLogChannels)
            {
                if (MatchesChannel(trimmed, blocked))
                    return false;
            }

            if (unrestrictedMode)
                return true;

            return AllowedEventLogChannels.Any(allowed => MatchesChannel(trimmed, allowed));
        }

        private static bool MatchesChannel(string channel, string prefix)
            => channel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               (channel.Length == prefix.Length || channel[prefix.Length] == '/');

        /// <summary>
        /// Returns true if the command (trimmed) exactly matches an entry in the allowlist.
        /// Exact matching is intentional — no prefix spoofing possible.
        /// </summary>
        public static bool IsCommandAllowed(string command)
            => IsCommandAllowed(command, unrestrictedMode: false);

        /// <summary>
        /// Returns true if the command is allowed.
        /// When unrestrictedMode is true, all commands are allowed.
        /// </summary>
        public static bool IsCommandAllowed(string command, bool unrestrictedMode)
        {
            if (string.IsNullOrEmpty(command))
                return false;

            // Hard guards apply even in unrestricted mode
            if (command.Length > MaxCommandLength)
                return false;

            foreach (var blocked in HardBlockedCommandPatterns)
            {
                if (command.IndexOf(blocked, StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
            }

            if (unrestrictedMode)
                return true;

            return AllowedCommands.Contains(command.Trim());
        }
    }
}

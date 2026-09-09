using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather
{
    /// <summary>
    /// Privacy and security guards for configurable diagnostics log paths.
    ///
    /// Restricts user-defined diagnostics paths to directories that contain only
    /// enrollment- and device-management-relevant log files, preventing PII leakage.
    ///
    /// Rules:
    ///   - The path (after environment variable expansion and full-path normalization)
    ///     must start with one of the allowed prefixes.
    ///   - Segment-bounded matching: next character after the prefix must be '\' or end of string.
    ///   - Wildcards ('*', '?') are only permitted in the last path segment (filename part).
    ///   - Path traversal sequences are blocked via Path.GetFullPath normalization.
    /// </summary>
    public static class DiagnosticsPathGuards
    {
        // -----------------------------------------------------------------------
        // Allowed directory prefixes for diagnostics log paths
        // (expanded, absolute paths — no environment variables)
        // -----------------------------------------------------------------------
        public static readonly IReadOnlyList<string> AllowedDiagnosticsPathPrefixes = new[]
        {
            // Autopilot Monitor agent logs
            @"C:\ProgramData\AutopilotMonitor",

            // Intune Management Extension
            @"C:\ProgramData\Microsoft\IntuneManagementExtension\Logs",

            // Windows Setup & OOBE
            @"C:\Windows\Panther",

            // Windows general logs
            @"C:\Windows\Logs",

            // Windows Setup Diagnostics
            @"C:\Windows\SetupDiag",

            // Windows Update reporting
            @"C:\Windows\SoftwareDistribution\ReportingEvents.log",

            // Windows Event Log files
            @"C:\Windows\System32\winevt\Logs",

            // SCCM / ConfigMgr
            @"C:\Windows\CCM\Logs",

            // Microsoft Diagnostic Log CSP
            @"C:\ProgramData\Microsoft\DiagnosticLogCSP",

            // Windows Error Reporting
            @"C:\ProgramData\Microsoft\Windows\WER",

            // Windows Component-Based Servicing
            @"C:\Windows\Logs\CBS",

            // Custom install logs (e.g. app installers writing to C:\Install\Log\)
            @"C:\Install\Log",
        };

        // Hard blocks: never allowed, even in unrestricted mode
        private static readonly string BlockedUsersPrefix = Path.GetFullPath(@"C:\Users");
        private static readonly string[] AdditionalHardBlockedPrefixes = new[]
        {
            @"C:\Windows\System32\config",  // SAM, SECURITY, SYSTEM hives
        };

        // The allowed user-profile subdirectories and the vendor allowlist under them live in
        // GatherRuleGuards, which owns the embedded guardrails.json — one copy for both guards.

        /// <summary>
        /// Returns true if the given path is allowed for diagnostics collection.
        /// Expands environment variables and normalises to a full path before checking.
        /// Wildcards in the last path segment are supported.
        /// </summary>
        public static bool IsDiagnosticsPathAllowed(string rawPath)
            => IsDiagnosticsPathAllowed(rawPath, unrestrictedMode: false, userProfilePath: null);

        /// <summary>
        /// Returns true if the given path is allowed for diagnostics collection.
        /// When unrestrictedMode is true, all paths are allowed except C:\Users (privacy protection).
        /// Path normalization and traversal protection always apply regardless of mode.
        /// </summary>
        public static bool IsDiagnosticsPathAllowed(string rawPath, bool unrestrictedMode)
            => IsDiagnosticsPathAllowed(rawPath, unrestrictedMode, userProfilePath: null);

        /// <summary>
        /// Returns true if the given path is allowed for diagnostics collection.
        /// When userProfilePath is provided (from %LOGGED_ON_USER_PROFILE% token), paths under
        /// the user's AppData\Local and AppData\Roaming are additionally allowed.
        /// </summary>
        public static bool IsDiagnosticsPathAllowed(string rawPath, bool unrestrictedMode, string userProfilePath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return false;

            try
            {
                // Split off a trailing wildcard segment before normalization
                var expanded = Environment.ExpandEnvironmentVariables(rawPath);
                var fileName = Path.GetFileName(expanded);
                var hasWildcard = fileName.Contains('*') || fileName.Contains('?');

                // Normalize the directory part (or full path if no wildcard)
                string normalizedDir;
                if (hasWildcard)
                {
                    var dir = Path.GetDirectoryName(expanded);
                    if (string.IsNullOrEmpty(dir))
                        return false;
                    normalizedDir = Path.GetFullPath(dir);
                }
                else
                {
                    normalizedDir = Path.GetFullPath(expanded);
                }

                // Symlink / junction / reparse point detection — fail-closed
                if (IsSymlinkOrReparsePoint(normalizedDir))
                    return false;

                // C:\Users block always applies (even in unrestricted mode)
                // Exception: paths under <userProfilePath>\AppData\Local or AppData\Roaming
                // are allowed when the %LOGGED_ON_USER_PROFILE% token was used.
                if (normalizedDir.StartsWith(BlockedUsersPrefix, StringComparison.OrdinalIgnoreCase) &&
                    (normalizedDir.Length == BlockedUsersPrefix.Length ||
                     normalizedDir[BlockedUsersPrefix.Length] == Path.DirectorySeparatorChar))
                {
                    if (!GatherRuleGuards.IsUserProfileSubpathAllowed(normalizedDir, userProfilePath))
                        return false;
                }

                // Additional hard-blocked paths (even in unrestricted mode)
                foreach (var blocked in AdditionalHardBlockedPrefixes)
                {
                    var normalizedBlocked = Path.GetFullPath(blocked);
                    if (normalizedDir.StartsWith(normalizedBlocked, StringComparison.OrdinalIgnoreCase) &&
                        (normalizedDir.Length == normalizedBlocked.Length ||
                         normalizedDir[normalizedBlocked.Length] == Path.DirectorySeparatorChar))
                    {
                        return false;
                    }
                }

                // In unrestricted mode, everything except hard-blocked paths is allowed
                if (unrestrictedMode)
                    return true;

                foreach (var prefix in AllowedDiagnosticsPathPrefixes)
                {
                    var normalizedPrefix = Path.GetFullPath(prefix);
                    if (normalizedDir.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        // Segment-bounded: next char must be '\' or end-of-string
                        if (normalizedDir.Length == normalizedPrefix.Length ||
                            normalizedDir[normalizedPrefix.Length] == Path.DirectorySeparatorChar)
                        {
                            return true;
                        }
                    }
                }

                // Vendor folders under the signed-in user's profile — same allowlist the file
                // collectors use, since it answers the same question: which folders below a
                // profile carry enrollment-relevant logs rather than the user's own data.
                return GatherRuleGuards.IsUserProfilePathOnAllowlist(normalizedDir, userProfilePath);
            }
            catch
            {
                // Any path normalization failure → deny
                return false;
            }
        }

        /// <summary>
        /// Checks whether any component of the path (directories or file) is a symlink,
        /// junction, or other reparse point. Returns true if a reparse point is detected.
        /// Fail-closed: returns true on any error.
        /// </summary>
        private static bool IsSymlinkOrReparsePoint(string path)
        {
            try
            {
                // Walk up the directory tree checking each component
                var current = path;
                while (!string.IsNullOrEmpty(current))
                {
                    var dirInfo = new DirectoryInfo(current);
                    if (dirInfo.Exists && dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        return true;

                    var parent = Path.GetDirectoryName(current);
                    if (parent == current) // root reached
                        break;
                    current = parent;
                }

                // Check the target path itself (if it's a file)
                if (File.Exists(path))
                {
                    var fileInfo = new FileInfo(path);
                    if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        return true;
                }

                return false;
            }
            catch
            {
                // Fail-closed: treat errors as suspicious
                return true;
            }
        }
    }
}

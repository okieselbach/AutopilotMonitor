using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

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

                return IsNormalizedPathAllowed(normalizedDir, unrestrictedMode, userProfilePath);
            }
            catch
            {
                // Any path normalization failure → deny
                return false;
            }
        }

        /// <summary>
        /// What the package actually reads for one configured entry: every file matching
        /// <see cref="Pattern"/> in <see cref="Folder"/>, optionally below it.
        /// </summary>
        public sealed class CollectionTarget
        {
            public CollectionTarget(string folder, string pattern, bool recurse)
            {
                Folder = folder;
                Pattern = pattern;
                Recurse = recurse;
            }

            /// <summary>Full path without a trailing separator (drive roots keep theirs).</summary>
            public string Folder { get; }
            public string Pattern { get; }
            public bool Recurse { get; }
        }

        /// <summary>
        /// Resolves a configured entry to the folder and pattern that get enumerated, and
        /// validates exactly that. Returns false when the guard refuses the entry.
        ///
        /// <paramref name="expandedPath"/> must already have the %LOGGED_ON_USER_PROFILE% token
        /// resolved: validating the raw token would judge a different path than the one read.
        ///
        /// A wildcard in the last segment selects files in its parent folder. A trailing
        /// separator or an existing directory selects every file in that folder. Anything else
        /// names one file — validated as that file and never recursed, because its parent
        /// folder has not passed the guard. The filesystem decides folder-vs-file, never the
        /// spelling of the last segment.
        /// </summary>
        public static bool TryResolveCollectionTarget(
            string expandedPath,
            bool includeSubfolders,
            bool unrestrictedMode,
            string userProfilePath,
            out CollectionTarget target)
            => TryResolveCollectionTarget(
                expandedPath, includeSubfolders, unrestrictedMode, userProfilePath, Directory.Exists, out target);

        internal static bool TryResolveCollectionTarget(
            string expandedPath,
            bool includeSubfolders,
            bool unrestrictedMode,
            string userProfilePath,
            Func<string, bool> directoryExists,
            out CollectionTarget target)
        {
            target = null;
            if (string.IsNullOrWhiteSpace(expandedPath))
                return false;

            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(expandedPath.Trim());
                var lastSegment = Path.GetFileName(expanded);

                if (lastSegment.Contains('*') || lastSegment.Contains('?'))
                {
                    var parent = Path.GetDirectoryName(expanded);
                    if (string.IsNullOrEmpty(parent))
                        return false;
                    var folder = TrimTrailingSeparators(Path.GetFullPath(parent));
                    if (!IsDiagnosticsPathAllowed(folder, unrestrictedMode, userProfilePath))
                        return false;
                    target = new CollectionTarget(folder, lastSegment, includeSubfolders);
                    return true;
                }

                var full = TrimTrailingSeparators(Path.GetFullPath(expanded));
                if (!IsDiagnosticsPathAllowed(full, unrestrictedMode, userProfilePath))
                    return false;

                if (lastSegment.Length == 0 || directoryExists(full))
                {
                    target = new CollectionTarget(full, "*", includeSubfolders);
                    return true;
                }

                var fileFolder = Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(fileFolder))
                    return false;
                target = new CollectionTarget(fileFolder, Path.GetFileName(full), recurse: false);
                return true;
            }
            catch
            {
                target = null;
                return false;
            }
        }

        /// <summary>
        /// Guard for every subdirectory a recursive collection is about to enter. The start
        /// folder passed <see cref="IsDiagnosticsPathAllowed(string, bool, string)"/>, but a
        /// hard-blocked subtree can sit below an allowed folder (unrestricted mode, drive
        /// root). Lexical only: reparse points are skipped by the enumeration and refused
        /// again on the handle that is read.
        /// </summary>
        public static bool IsDirectoryEnterable(string directory, bool unrestrictedMode, string userProfilePath)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return false;

            try
            {
                return IsNormalizedPathAllowed(Path.GetFullPath(directory), unrestrictedMode, userProfilePath);
            }
            catch
            {
                return false;
            }
        }

        private static string TrimTrailingSeparators(string fullPath)
        {
            var root = Path.GetPathRoot(fullPath);
            var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return root != null && trimmed.Length < root.Length ? root : trimmed;
        }

        // Hard blocks, then (restricted mode) the allowlists — on an already normalized path.
        private static bool IsNormalizedPathAllowed(string normalizedDir, bool unrestrictedMode, string userProfilePath)
        {
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

            // Hard-blocked event logs (even in unrestricted mode). Only a path that names the
            // file is caught here; a wildcard or folder entry is judged per file by
            // DiagnosticsPackageService, which is the enforcement point.
            if (IsHardBlockedEventLogFile(normalizedDir))
                return false;

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

        // Windows archives a full channel as "Archive-<file name>-yyyy-MM-dd-HH-mm-ss-fff.evtx".
        private static readonly Regex ArchivedEventLogRegex = new Regex(
            @"^Archive-(.+)-\d{4}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2}-\d{3}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>
        /// True when the .evtx file name belongs to one of
        /// <see cref="GatherRuleGuards.HardBlockedEventLogChannels"/> — live file
        /// ("Microsoft-Windows-PowerShell%4Operational.evtx", where %4 encodes '/') or
        /// archived copy. Judges the NAME only; a channel may write to a file that does not
        /// follow the convention, so the packager also checks the channel it resolved.
        /// </summary>
        internal static bool IsHardBlockedEventLogFile(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            var name = Path.GetFileName(path);
            if (!name.EndsWith(".evtx", StringComparison.OrdinalIgnoreCase))
                return false;

            var stem = name.Substring(0, name.Length - ".evtx".Length);
            var archived = ArchivedEventLogRegex.Match(stem);
            if (archived.Success)
                stem = archived.Groups[1].Value;

            return GatherRuleGuards.IsHardBlockedEventLogChannel(stem.Replace("%4", "/"));
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

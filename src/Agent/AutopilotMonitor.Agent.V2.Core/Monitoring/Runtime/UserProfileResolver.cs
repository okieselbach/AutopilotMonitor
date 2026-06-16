using System;
using System.Diagnostics;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime
{
    /// <summary>
    /// Resolves the custom %LOGGED_ON_USER_PROFILE% token to the logged-on user's profile path.
    ///
    /// The agent runs as SYSTEM, so standard environment variables like %USERPROFILE% or
    /// %LOCALAPPDATA% resolve to the SYSTEM profile — not the logged-on user. This class
    /// detects the real user via explorer.exe ownership (WTS session query, WMI fallback —
    /// see <see cref="Interop.ProcessOwnerLookup"/>) and caches the result for the agent's lifetime.
    ///
    /// Usage in paths: %LOGGED_ON_USER_PROFILE%\AppData\Local\RealmJoin\Logs\*.log
    /// </summary>
    public static class UserProfileResolver
    {
        public const string Token = "%LOGGED_ON_USER_PROFILE%";

        private static readonly object Lock = new object();
        private static bool _resolved;
        private static string _userProfilePath; // e.g. C:\Users\JohnDoe — null if no user detected

        /// <summary>
        /// Returns the logged-on user's profile path (e.g. C:\Users\JohnDoe) or null
        /// if no interactive user session has been detected yet.
        /// Result is cached after first successful detection.
        /// </summary>
        public static string GetLoggedOnUserProfilePath()
        {
            if (_resolved)
                return _userProfilePath;

            lock (Lock)
            {
                if (_resolved)
                    return _userProfilePath;

                _userProfilePath = DetectLoggedOnUserProfile();
                _resolved = true;
                return _userProfilePath;
            }
        }

        /// <summary>
        /// Expands the custom %LOGGED_ON_USER_PROFILE% token, then delegates to
        /// Environment.ExpandEnvironmentVariables for standard tokens.
        /// Returns null if the path contains the token but no user is logged on.
        /// </summary>
        public static string ExpandCustomTokens(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath))
                return rawPath;

            if (rawPath.IndexOf(Token, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var profilePath = GetLoggedOnUserProfilePath();
                if (profilePath == null)
                    return null; // No user detected — caller should skip this path

                rawPath = ReplaceCaseInsensitive(rawPath, Token, profilePath);
            }

            return Environment.ExpandEnvironmentVariables(rawPath);
        }

        /// <summary>
        /// Returns true if the raw (unexpanded) path contains the custom user profile token.
        /// </summary>
        public static bool ContainsUserProfileToken(string rawPath)
        {
            return !string.IsNullOrEmpty(rawPath) &&
                   rawPath.IndexOf(Token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Resets the cached state. Only used for testing.
        /// </summary>
        internal static void Reset()
        {
            lock (Lock)
            {
                _resolved = false;
                _userProfilePath = null;
            }
        }

        /// <summary>
        /// Allows tests to inject a specific profile path without WMI.
        /// </summary>
        internal static void SetForTesting(string profilePath)
        {
            lock (Lock)
            {
                _userProfilePath = profilePath;
                _resolved = true;
            }
        }

        private static string DetectLoggedOnUserProfile()
        {
            try
            {
                var explorerProcesses = Process.GetProcessesByName("explorer");
                foreach (var proc in explorerProcesses)
                {
                    try
                    {
                        // Session 0 = SYSTEM session, skip
                        if (proc.SessionId == 0)
                            continue;

                        var userName = ProcessOwnerLookup.ResolveOwner(proc.Id, proc.SessionId);
                        if (userName == null)
                            continue;

                        if (DesktopArrivalDetector.IsExcludedUser(userName))
                            continue;

                        // Extract just the username part (after backslash if DOMAIN\User)
                        var backslashIndex = userName.LastIndexOf('\\');
                        if (backslashIndex >= 0 && backslashIndex < userName.Length - 1)
                            userName = userName.Substring(backslashIndex + 1);

                        var profilePath = Path.Combine(@"C:\Users", userName);
                        if (Directory.Exists(profilePath))
                            return profilePath;
                    }
                    catch
                    {
                        // Continue checking other explorer instances
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch
            {
                // WMI or process enumeration failure — no user detected
            }

            return null;
        }

        private static string ReplaceCaseInsensitive(string source, string oldValue, string newValue)
        {
            var index = source.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return source;
            return source.Substring(0, index) + newValue + source.Substring(index + oldValue.Length);
        }
    }
}

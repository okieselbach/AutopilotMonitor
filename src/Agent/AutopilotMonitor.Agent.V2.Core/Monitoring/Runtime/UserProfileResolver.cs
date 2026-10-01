using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
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
    /// see <see cref="Interop.ProcessOwnerLookup"/>) and caches the first user it finds for the
    /// agent's lifetime. "No user yet" is not cached: the agent usually starts before anyone has
    /// signed in, so detection is retried — at most every <see cref="RetryInterval"/>.
    ///
    /// Usage in paths: %LOGGED_ON_USER_PROFILE%\AppData\Local\RealmJoin\Logs\*.log
    /// </summary>
    public static class UserProfileResolver
    {
        // Spelled once, in Shared: the built-in diagnostics catalog uses the same token.
        public const string Token = Shared.Models.DiagnosticsBuiltInSections.UserProfileToken;

        /// <summary>Minimum time between two detections while no user has been found.</summary>
        internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

        private static readonly object Lock = new object();
        private static string _userProfilePath; // e.g. C:\Users\JohnDoe — null until a user is detected
        private static DateTime _lastFailedDetectionUtc = DateTime.MinValue;

        // Seams for tests; production uses the real detection and clock.
        private static Func<string> _detector = DetectLoggedOnUserProfile;
        private static Func<DateTime> _utcNow = () => DateTime.UtcNow;

        /// <summary>
        /// Returns the logged-on user's profile path (e.g. C:\Users\JohnDoe) or null
        /// if no interactive user session has been detected yet.
        /// Result is cached after first successful detection.
        /// </summary>
        public static string GetLoggedOnUserProfilePath()
        {
            var cached = Volatile.Read(ref _userProfilePath);
            if (cached != null)
                return cached;

            lock (Lock)
            {
                if (_userProfilePath != null)
                    return _userProfilePath;

                // A clock that stepped backwards (common during OOBE) never delays the retry.
                var now = _utcNow();
                var sinceLastFailure = now - _lastFailedDetectionUtc;
                if (_lastFailedDetectionUtc != DateTime.MinValue
                    && sinceLastFailure >= TimeSpan.Zero && sinceLastFailure < RetryInterval)
                    return null;

                var detected = _detector();
                if (detected != null)
                    Volatile.Write(ref _userProfilePath, detected);
                else
                    _lastFailedDetectionUtc = now;
                return detected;
            }
        }

        /// <summary>
        /// Expands the custom %LOGGED_ON_USER_PROFILE% token, then delegates to
        /// Environment.ExpandEnvironmentVariables for standard tokens.
        /// Returns null if the path contains the token but no user is logged on.
        /// </summary>
        public static string ExpandCustomTokens(string rawPath) =>
            ExpandCustomTokens(rawPath, ContainsUserProfileToken(rawPath) ? GetLoggedOnUserProfilePath() : null);

        /// <summary>
        /// Same, with the profile path the caller already resolved for its guard. Callers that
        /// guard the expanded path must use this overload: while detection is still retrying, a
        /// second lookup can find the user the first one missed, and the guard would then judge a
        /// C:\Users path without the profile that admits it.
        /// </summary>
        public static string ExpandCustomTokens(string rawPath, string userProfilePath)
        {
            if (string.IsNullOrEmpty(rawPath))
                return rawPath;

            if (rawPath.IndexOf(Token, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (userProfilePath == null)
                    return null; // No user detected — caller should skip this path

                rawPath = ReplaceCaseInsensitive(rawPath, Token, userProfilePath);
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
        /// Resets the cached state and the seams. Only used for testing.
        /// </summary>
        internal static void Reset()
        {
            lock (Lock)
            {
                _userProfilePath = null;
                _lastFailedDetectionUtc = DateTime.MinValue;
                _detector = DetectLoggedOnUserProfile;
                _utcNow = () => DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Allows tests to inject a specific profile path without WMI. Null pins "no user signed in"
        /// — detection keeps answering null instead of finding the developer's own session.
        /// </summary>
        internal static void SetForTesting(string profilePath)
        {
            SetDetectorForTesting(() => profilePath, () => DateTime.UtcNow);
        }

        /// <summary>Replaces detection and clock, clearing what was cached. Only used for testing.</summary>
        internal static void SetDetectorForTesting(Func<string> detector, Func<DateTime> utcNow)
        {
            lock (Lock)
            {
                _userProfilePath = null;
                _lastFailedDetectionUtc = DateTime.MinValue;
                _detector = detector ?? throw new ArgumentNullException(nameof(detector));
                _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
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

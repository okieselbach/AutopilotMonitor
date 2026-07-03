using System.Management;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Interop
{
    /// <summary>
    /// Resolves the owner ("DOMAIN\\User" or "User") of an interactive-session process —
    /// in practice always explorer.exe — via a WTS session query first, with WMI
    /// <c>Win32_Process.GetOwner</c> as the fallback.
    /// <para>
    /// WTS-primary (session 4d5a0b78 fix, 2026-06-11): <see cref="WtsNativeMethods.QuerySessionString"/>
    /// is a single fast kernel round-trip independent of the WinMgmt service, which fails
    /// persistently on some devices (GetOwner returned an error on every poll there). WMI
    /// remains the fallback so resolution never regresses below the old behaviour.
    /// </para>
    /// <para>
    /// <b>Constraint — interactive shell processes only.</b> The WTS path resolves the
    /// <i>session's</i> interactive user, which equals the process token owner only for the
    /// user's shell (explorer.exe). Callers MUST pass such a process together with its own
    /// <c>SessionId</c>. Do NOT reuse this for arbitrary PIDs (services, RunAs, scheduled
    /// tasks): WTS would return the console user, not the process owner.
    /// </para>
    /// </summary>
    internal static class ProcessOwnerLookup
    {
        /// <summary>
        /// WTS-primary, WMI-fallback owner resolution for an interactive-session process.
        /// Returns "DOMAIN\\User" / "User", or <c>null</c> when neither path resolves an owner.
        /// </summary>
        public static string ResolveOwner(int processId, int sessionId)
        {
            var owner = ViaWts(sessionId);
            if (!string.IsNullOrEmpty(owner))
                return owner;

            return ViaWmi(processId);
        }

        /// <summary>
        /// Primary path: <c>WTSQuerySessionInformation</c>(WTSUserName/WTSDomainName) for the
        /// process's session. Returns <c>null</c> when the session has no associated user or the
        /// API fails (an empty WTS result is coalesced to <c>null</c>).
        /// </summary>
        public static string ViaWts(int sessionId) => ViaWts(sessionId, out _);

        /// <summary>
        /// WTS primary path that also reports whether the API call actually FAILED via
        /// <paramref name="wtsErrored"/> (L15). A session that simply has no logged-on user yet —
        /// perfectly normal during OOBE polling — returns <c>null</c> with
        /// <paramref name="wtsErrored"/> false, so liveness counters no longer inflate the
        /// "error" rate with routine no-user reads.
        /// </summary>
        public static string ViaWts(int sessionId, out bool wtsErrored)
        {
            wtsErrored = false;
            try
            {
                var user = WtsNativeMethods.QuerySessionString(sessionId, WtsNativeMethods.WTSUserName);
                if (user == null)
                {
                    wtsErrored = true; // API failure (QuerySessionString returns null only then)
                    return null;
                }
                if (user.Length == 0)
                    return null;       // successful query, no user associated yet — not an error

                var domain = WtsNativeMethods.QuerySessionString(sessionId, WtsNativeMethods.WTSDomainName);
                return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
            }
            catch
            {
                wtsErrored = true;
                return null;
            }
        }

        /// <summary>
        /// Fallback path: WMI <c>Win32_Process.GetOwner</c>. Returns "DOMAIN\\User" / "User",
        /// or <c>null</c> on failure.
        /// </summary>
        public static string ViaWmi(int processId) => ViaWmi(processId, out _);

        /// <summary>
        /// WMI fallback that also reports whether the query failed via <paramref name="wmiErrored"/>
        /// (a non-zero <c>GetOwner</c> result or an exception) so liveness-instrumented callers can
        /// count the failure rate. A process that simply vanished between enumeration and query
        /// (zero rows) is NOT counted as an error.
        /// <para>
        /// Projects only <c>Handle</c> + <c>ProcessId</c>: <c>Handle</c> is the key property WMI
        /// needs for <c>GetOwner</c> method invocation, while still avoiding materializing the
        /// ~45 unused properties that <c>SELECT *</c> would.
        /// </para>
        /// </summary>
        public static string ViaWmi(int processId, out bool wmiErrored)
        {
            wmiErrored = false;
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    $"SELECT Handle, ProcessId FROM Win32_Process WHERE ProcessId = {processId}"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var outParams = new object[2];
                        var result = (uint)obj.InvokeMethod("GetOwner", outParams);
                        if (result == 0)
                        {
                            var user = outParams[0]?.ToString();
                            var domain = outParams[1]?.ToString();
                            return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
                        }

                        // Non-zero GetOwner result: PID just exited, or the owner ACL refused.
                        wmiErrored = true;
                    }
                }
            }
            catch
            {
                wmiErrored = true;
            }

            return null;
        }
    }
}

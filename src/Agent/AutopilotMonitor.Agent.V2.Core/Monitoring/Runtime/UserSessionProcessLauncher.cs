using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime
{
    /// <summary>
    /// Launches a process in the logged-in user's desktop session from a SYSTEM context.
    /// Uses WTSQueryUserToken + DuplicateTokenEx + SetTokenInformation(TokenSessionId)
    /// to create a session-stamped token, then CreateProcessAsUser to launch in the
    /// correct interactive session (not Session 0).
    /// </summary>
    internal static class UserSessionProcessLauncher
    {
        /// <summary>
        /// System/service account names that should NOT be considered real users.
        /// </summary>
        private static readonly string[] ExcludedUserNames =
        {
            "SYSTEM", "LOCAL SERVICE", "NETWORK SERVICE",
            "DefaultUser0", "DefaultUser1", "DefaultUser2", 
            "defaultuser0", "defaultuser1", "defaultuser2"
        };

        /// <summary>
        /// Launches the specified executable in the logged-in user's desktop session.
        /// Returns true if the process was launched successfully, false otherwise.
        /// This is fire-and-forget — the caller does not wait for the process to exit.
        /// If the process fails with 0x8007045A (DLL init failed), retries for up to maxRetrySeconds.
        /// </summary>
        public static bool LaunchInUserSession(string exePath, string arguments, AgentLogger logger, int maxRetrySeconds = 120)
        {
            IntPtr userToken = IntPtr.Zero;
            IntPtr dupToken = IntPtr.Zero;
            IntPtr environment = IntPtr.Zero;

            try
            {
                // Find explorer.exe in a non-zero session owned by a real user.
                var explorerProcess = FindExplorerProcess(logger);
                if (explorerProcess == null)
                {
                    logger.Warning("UserSessionProcessLauncher: No explorer.exe found in user session — cannot launch dialog");
                    return false;
                }

                uint sessionId;
                using (explorerProcess)
                {
                    sessionId = (uint)explorerProcess.SessionId;
                    logger.Info($"UserSessionProcessLauncher: Found explorer.exe PID {explorerProcess.Id} in session {sessionId}");
                }

                // Get the interactive session token via WTS.
                if (!ProcessNativeMethods.WTSQueryUserToken(sessionId, out userToken))
                {
                    var error = Marshal.GetLastWin32Error();
                    logger.Warning($"UserSessionProcessLauncher: WTSQueryUserToken failed for session {sessionId} (error {error})");
                    return false;
                }

                // Duplicate token and stamp the target session ID.
                // WTSQueryUserToken gives us the user's identity, but CreateProcessAsUser
                // needs TokenSessionId explicitly set to avoid Session 0 placement
                // (the calling service runs in Session 0; without stamping, the child
                // process inherits Session 0 even though the token belongs to the user).
                if (!ProcessNativeMethods.DuplicateTokenEx(userToken, ProcessNativeMethods.MAXIMUM_ALLOWED, IntPtr.Zero,
                    ProcessNativeMethods.SECURITY_IMPERSONATION, ProcessNativeMethods.TOKEN_PRIMARY, out dupToken))
                {
                    logger.Warning($"UserSessionProcessLauncher: DuplicateTokenEx failed (error {Marshal.GetLastWin32Error()})");
                    return false;
                }

                if (!ProcessNativeMethods.SetTokenInformation(dupToken, ProcessNativeMethods.TokenSessionId,
                    ref sessionId, (uint)Marshal.SizeOf(typeof(uint))))
                {
                    logger.Warning($"UserSessionProcessLauncher: SetTokenInformation(SessionId={sessionId}) failed (error {Marshal.GetLastWin32Error()})");
                    return false;
                }

                logger.Info($"UserSessionProcessLauncher: Token duplicated and stamped to session {sessionId}");

                // Create an environment block for the user (using session-stamped token)
                if (!ProcessNativeMethods.CreateEnvironmentBlock(out environment, dupToken, false))
                {
                    logger.Warning($"UserSessionProcessLauncher: CreateEnvironmentBlock failed (error {Marshal.GetLastWin32Error()})");
                    environment = IntPtr.Zero;
                }

                // Set up startup info — empty lpDesktop lets Windows select the correct
                // desktop for the session-stamped token.
                var si = new ProcessNativeMethods.STARTUPINFO();
                si.cb = Marshal.SizeOf(si);
                si.lpDesktop = "";
                si.dwFlags = ProcessNativeMethods.STARTF_USESHOWWINDOW | ProcessNativeMethods.STARTF_FORCEONFEEDBACK;
                si.wShowWindow = ProcessNativeMethods.SW_SHOW;

                uint creationFlags = 0;
                if (environment != IntPtr.Zero)
                    creationFlags |= ProcessNativeMethods.CREATE_UNICODE_ENVIRONMENT;

                var commandLine = $"\"{exePath}\" {arguments}";
                var workingDirectory = System.IO.Path.GetDirectoryName(exePath);

                // Retry loop with integrated CXH monitoring.
                // See WaitForCxhOrSleep for the CXH wake-up strategy.
                const int retryIntervalSeconds = 10;
                const string defaultDesktop = "";
                const string explicitDesktop = "WinSta0\\Default";
                const int defaultDesktopOnlyAttempts = 3;
                var retryStopwatch = System.Diagnostics.Stopwatch.StartNew();
                int attempt = 0;

                // One-time CXH lookup — track for the lifetime of the retry loop
                Process cxhProcess = null;
                if (maxRetrySeconds > 0)
                {
                    try
                    {
                        cxhProcess = FindProcessInSession("CloudExperienceHost", sessionId);
                        if (cxhProcess != null)
                            logger.Info($"UserSessionProcessLauncher: CloudExperienceHost PID {cxhProcess.Id} running " +
                                       $"in session {sessionId} — will monitor for exit during retries");
                    }
                    catch { }
                }

                try
                {
                while (true)
                {
                    attempt++;

                    // First N attempts use default desktop (""), then alternate with explicit desktop.
                    // Default works ~90% of the time; explicit fallback catches post-Hello desktop issues.
                    bool useExplicitDesktop = (attempt > defaultDesktopOnlyAttempts) && (attempt % 2 == 0);
                    si.lpDesktop = useExplicitDesktop ? explicitDesktop : defaultDesktop;

                    // CreateProcessAsUser with session-stamped token.
                    // The duplicated token has TokenSessionId set to the user's session,
                    // ensuring the process lands on the interactive desktop (not Session 0).
                    bool created;
                    ProcessNativeMethods.PROCESS_INFORMATION pi;

                    var procSa = new ProcessNativeMethods.SECURITY_ATTRIBUTES();
                    procSa.nLength = Marshal.SizeOf(procSa);
                    var threadSa = new ProcessNativeMethods.SECURITY_ATTRIBUTES();
                    threadSa.nLength = Marshal.SizeOf(threadSa);

                    created = ProcessNativeMethods.CreateProcessAsUser(
                        dupToken, null, commandLine,
                        ref procSa, ref threadSa, false,
                        creationFlags, environment, workingDirectory,
                        ref si, out pi);

                    if (!created)
                    {
                        logger.Warning($"UserSessionProcessLauncher: CreateProcessAsUser failed (error {Marshal.GetLastWin32Error()})");
                        return false;
                    }

                    ProcessNativeMethods.CloseHandle(pi.hThread);
                    logger.Info($"UserSessionProcessLauncher: Attempt {attempt} — created PID {pi.dwProcessId}" +
                               $" [{(useExplicitDesktop ? "explicit" : "default")} desktop], verifying startup...");

                    // Wait 3s to verify the process survives DLL init + CLR bootstrap.
                    var waitResult = ProcessNativeMethods.WaitForSingleObject(pi.hProcess, 3000);
                    if (waitResult == ProcessNativeMethods.WAIT_TIMEOUT)
                    {
                        // Process is still running after 3s — success
                        ProcessNativeMethods.CloseHandle(pi.hProcess);
                        logger.Info($"UserSessionProcessLauncher: Successfully launched PID {pi.dwProcessId} in user session" +
                                   $" [{(useExplicitDesktop ? "explicit" : "default")} desktop]" +
                                   (attempt > 1 ? $" (after {attempt} attempts, {retryStopwatch.Elapsed.TotalSeconds:F0}s)" : ""));
                        return true;
                    }

                    // Process exited within 3s — check why
                    ProcessNativeMethods.GetExitCodeProcess(pi.hProcess, out var exitCode);
                    ProcessNativeMethods.CloseHandle(pi.hProcess);

                    // 0x8007045A = ERROR_DLL_INIT_FAILED — profile or desktop issue.
                    // Retry until the desktop becomes accessible or we exceed the retry window.
                    if (exitCode == 0x8007045A && retryStopwatch.Elapsed.TotalSeconds < maxRetrySeconds)
                    {
                        // If CXH is tracked and still running, wait for its exit instead of blind sleep.
                        // On 24H2+/≤22H2: CXH exits → we wake up instantly.
                        // On 23H2: CXH never exits → WaitForExit times out → same as regular retry interval.
                        if (cxhProcess != null)
                        {
                            logger.Info($"UserSessionProcessLauncher: Attempt {attempt} — 0x8007045A, waiting for CXH exit (up to {retryIntervalSeconds}s)...");
                            if (cxhProcess.WaitForExit(retryIntervalSeconds * 1000))
                            {
                                logger.Info("UserSessionProcessLauncher: CloudExperienceHost exited — desktop should be available");
                                cxhProcess.Dispose();
                                cxhProcess = null;
                                System.Threading.Thread.Sleep(500); // brief delay for desktop init
                            }
                        }
                        else
                        {
                            logger.Info($"UserSessionProcessLauncher: Attempt {attempt} — 0x8007045A (desktop locked?) — retrying in {retryIntervalSeconds}s...");
                            System.Threading.Thread.Sleep(retryIntervalSeconds * 1000);
                        }
                        continue;
                    }

                    // Non-retryable error, or retry timeout exceeded
                    logger.Warning($"UserSessionProcessLauncher: Process PID {pi.dwProcessId} exited within 3s — " +
                                   $"exit code 0x{exitCode:X8}" +
                                   $" [{(useExplicitDesktop ? "explicit" : "default")} desktop]" +
                                   (attempt > 1 ? $" (gave up after {attempt} attempts, {retryStopwatch.Elapsed.TotalSeconds:F0}s)" : ""));
                    return false;
                }
                }
                finally
                {
                    cxhProcess?.Dispose();
                }
            }
            catch (Exception ex)
            {
                logger.Warning($"UserSessionProcessLauncher: Exception: {ex.Message}");
                return false;
            }
            finally
            {
                if (environment != IntPtr.Zero)
                    ProcessNativeMethods.DestroyEnvironmentBlock(environment);
                if (dupToken != IntPtr.Zero)
                    ProcessNativeMethods.CloseHandle(dupToken);
                if (userToken != IntPtr.Zero)
                    ProcessNativeMethods.CloseHandle(userToken);
            }
        }

        /// <summary>
        /// Finds a process by name running in a specific session.
        /// Returns the first matching Process handle, or null if none found.
        /// </summary>
        private static Process FindProcessInSession(string processName, uint sessionId)
        {
            try
            {
                var processes = Process.GetProcessesByName(processName);
                foreach (var proc in processes)
                {
                    try
                    {
                        if ((uint)proc.SessionId == sessionId)
                        {
                            foreach (var other in processes)
                            {
                                if (other.Id != proc.Id)
                                    try { other.Dispose(); } catch { }
                            }
                            return proc;
                        }
                        proc.Dispose();
                    }
                    catch
                    {
                        proc.Dispose();
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Finds an explorer.exe process running in a non-SYSTEM user session.
        /// Returns the Process handle, or null if none found.
        /// </summary>
        private static Process FindExplorerProcess(AgentLogger logger)
        {
            try
            {
                var explorerProcesses = Process.GetProcessesByName("explorer");
                foreach (var proc in explorerProcesses)
                {
                    try
                    {
                        if (proc.SessionId == 0)
                        {
                            proc.Dispose();
                            continue;
                        }

                        var owner = ProcessOwnerLookup.ResolveOwner(proc.Id, proc.SessionId);
                        if (owner == null || IsExcludedUser(owner))
                        {
                            proc.Dispose();
                            continue;
                        }

                        foreach (var other in explorerProcesses)
                        {
                            if (other.Id != proc.Id)
                            {
                                try { other.Dispose(); } catch { }
                            }
                        }
                        return proc;
                    }
                    catch
                    {
                        proc.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Debug($"UserSessionProcessLauncher: Error finding explorer.exe: {ex.Message}");
            }

            return null;
        }

        private static bool IsExcludedUser(string fullUserName)
        {
            if (string.IsNullOrEmpty(fullUserName))
                return true;

            var userName = fullUserName;
            var backslashIndex = fullUserName.LastIndexOf('\\');
            if (backslashIndex >= 0 && backslashIndex < fullUserName.Length - 1)
                userName = fullUserName.Substring(backslashIndex + 1);

            foreach (var excluded in ExcludedUserNames)
            {
                if (string.Equals(userName, excluded, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (userName.StartsWith("DefaultUser", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

    }
}

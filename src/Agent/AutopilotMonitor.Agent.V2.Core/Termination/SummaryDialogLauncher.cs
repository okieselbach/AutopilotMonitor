using System;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Termination
{
    /// <summary>
    /// Launches <c>AutopilotMonitor.SummaryDialog.exe</c> in the signed-in user's session
    /// with a freshly-written <c>final-status.json</c>. Plan §4.x M4.6.β.
    /// <para>
    /// Sequence (V1 parity, see legacy <c>EnrollmentCompletionHandler.LaunchEnrollmentSummaryDialog</c>):
    /// </para>
    /// <list type="number">
    ///   <item>Resolve the dialog exe next to the agent binary (<c>Assembly.Location</c>).</item>
    ///   <item>Write the final-status.json to the agent state directory.</item>
    ///   <item>Wipe + recreate <c>%ProgramData%\AutopilotMonitor-Summary\</c> (single flat dir,
    ///     V1 parity — no per-launch GUID subdirs that could leak across sessions).</item>
    ///   <item>Grant the signed-in user delete permission on that dir so the dialog's
    ///     self-cleanup step works (runs as user).</item>
    ///   <item>Copy exe + dependencies + final-status.json into the dir; the dialog deletes
    ///     itself via <c>--cleanup</c> after the user closes it / the timeout fires.</item>
    ///   <item>Invoke <see cref="UserSessionProcessLauncher.LaunchInUserSession"/> with the right args.</item>
    /// </list>
    /// <para>
    /// Fire-and-forget: the launcher does not wait for the dialog to exit. Returns <c>false</c>
    /// on any failure, never throws. Result is logged by the caller so a dialog failure never
    /// blocks the cleanup/shutdown path.
    /// </para>
    /// </summary>
    public static class SummaryDialogLauncher
    {
        internal const string DialogExeName = "AutopilotMonitor.SummaryDialog.exe";
        internal const string DialogConfigName = "AutopilotMonitor.SummaryDialog.exe.config";
        internal const string NewtonsoftDependency = "Newtonsoft.Json.dll";
        internal const string SummaryTempRootEnvVar = "AutopilotMonitor-Summary";
        internal const string FinalStatusFileName = "final-status.json";

        /// <summary>
        /// Writes <paramref name="status"/> to <paramref name="stateDirectory"/>/<c>final-status.json</c>
        /// and launches the dialog. When <see cref="AgentConfiguration.ShowEnrollmentSummary"/> is
        /// false, only the JSON is written (skipping the launch) — tests and backend-consumable
        /// audit trail benefit from always having the file on disk.
        /// </summary>
        /// <returns><c>true</c> when both write and launch (if enabled) succeeded.</returns>
        public static bool WriteAndLaunch(
            FinalStatus status,
            AgentConfiguration configuration,
            string stateDirectory,
            AgentLogger logger,
            string dialogExePathOverride = null)
        {
            if (status == null) throw new ArgumentNullException(nameof(status));
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (string.IsNullOrEmpty(stateDirectory)) throw new ArgumentException("stateDirectory required.", nameof(stateDirectory));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            // 1) Write final-status.json always (cheap, useful for audit even if dialog skipped).
            string statusPath;
            try
            {
                Directory.CreateDirectory(stateDirectory);
                statusPath = Path.Combine(stateDirectory, FinalStatusFileName);
                File.WriteAllText(statusPath, JsonConvert.SerializeObject(status, Formatting.Indented));
                logger.Info($"SummaryDialogLauncher: final-status.json written to {statusPath}.");
            }
            catch (Exception ex)
            {
                logger.Warning($"SummaryDialogLauncher: final-status.json write failed: {ex.Message}");
                return false;
            }

            if (!configuration.ShowEnrollmentSummary)
            {
                logger.Info("SummaryDialogLauncher: ShowEnrollmentSummary=false — skipping dialog launch.");
                return true;
            }

            // 2) Resolve dialog exe.
            var dialogExe = dialogExePathOverride;
            if (string.IsNullOrEmpty(dialogExe))
            {
                var agentDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                dialogExe = Path.Combine(agentDir, DialogExeName);
            }

            if (!File.Exists(dialogExe))
            {
                logger.Warning($"SummaryDialogLauncher: dialog exe not found at '{dialogExe}' — skipping launch.");
                return false;
            }

            // 3) Wipe + recreate %ProgramData%\AutopilotMonitor-Summary\ (V1 parity).
            // The dialog runs in the signed-in user's session — Path.GetTempPath() resolves to
            // C:\WINDOWS\SystemTemp\ when the agent runs as SYSTEM, where standard users have
            // neither Read nor Execute access, causing the dialog to fail to start with the
            // generic "This application could not be started" message. Single flat dir (no
            // per-launch GUID subdirs) matches V1's behaviour: only one summary dialog per
            // session, the previous run's leftovers (if --cleanup didn't fire) are wiped.
            string tempDir;
            string tempExe;
            try
            {
                tempDir = ResolveSummaryTempDir();
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
                catch (Exception ex) { logger.Debug($"SummaryDialogLauncher: pre-wipe of '{tempDir}' failed (continuing): {ex.Message}"); }
                Directory.CreateDirectory(tempDir);

                // Grant the signed-in user delete rights so the dialog's --cleanup self-removal
                // succeeds (the dialog runs as user, the directory was created by SYSTEM).
                GrantUserDeletePermission(tempDir, logger);

                tempExe = Path.Combine(tempDir, DialogExeName);
                File.Copy(dialogExe, tempExe, overwrite: true);

                var sourceDir = Path.GetDirectoryName(dialogExe) ?? string.Empty;
                TryCopy(Path.Combine(sourceDir, DialogConfigName), Path.Combine(tempDir, DialogConfigName));
                TryCopy(Path.Combine(sourceDir, NewtonsoftDependency), Path.Combine(tempDir, NewtonsoftDependency));

                var tempStatusFile = Path.Combine(tempDir, FinalStatusFileName);
                File.Copy(statusPath, tempStatusFile, overwrite: true);
            }
            catch (Exception ex)
            {
                logger.Warning($"SummaryDialogLauncher: temp dir setup failed: {ex.Message}");
                return false;
            }

            // 4) Compose args. Legacy contract: --status-file <path> --timeout <s> --cleanup [--branding-url <url>].
            var args = BuildDialogArguments(
                Path.Combine(tempDir, FinalStatusFileName),
                configuration.EnrollmentSummaryTimeoutSeconds,
                configuration.EnrollmentSummaryBrandingImageUrl,
                logger);

            // 5) Launch in user session with retry.
            var retrySeconds = configuration.EnrollmentSummaryLaunchRetrySeconds > 0
                ? configuration.EnrollmentSummaryLaunchRetrySeconds
                : 120;

            var launched = UserSessionProcessLauncher.LaunchInUserSession(tempExe, args, logger, retrySeconds);
            if (launched)
                logger.Info($"SummaryDialogLauncher: dialog launched (exe='{tempExe}', retrySeconds={retrySeconds}).");
            else
                logger.Warning("SummaryDialogLauncher: dialog launch failed or no user session available.");

            return launched;
        }

        /// <summary>
        /// Composes the dialog command line. The branding URL is tenant-supplied config and the
        /// dialog parses arguments last-occurrence-wins, so a value carrying a double quote could
        /// terminate its argument and override <c>--status-file</c>/<c>--timeout</c>/<c>--cleanup</c>
        /// (CWE-88). The backend rejects such values, but the agent is the last line of defence:
        /// only a plain absolute HTTPS URL without quotes, whitespace or control characters is
        /// ever placed on the command line — anything else is dropped (the dialog then simply
        /// shows no branding image).
        /// </summary>
        internal static string BuildDialogArguments(string statusFilePath, int timeoutSeconds, string brandingImageUrl, AgentLogger logger)
        {
            var args = $"--status-file \"{statusFilePath}\" --timeout {timeoutSeconds} --cleanup";
            if (string.IsNullOrWhiteSpace(brandingImageUrl))
                return args;

            if (IsSafeBrandingImageUrl(brandingImageUrl))
                return args + $" --branding-url \"{brandingImageUrl}\"";

            logger?.Warning("SummaryDialogLauncher: EnrollmentSummaryBrandingImageUrl is not a plain absolute HTTPS URL — ignoring it.");
            return args;
        }

        internal static bool IsSafeBrandingImageUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || url.Length > 2048)
                return false;

            foreach (var ch in url)
            {
                if (ch == '"' || ch == '\'' || char.IsWhiteSpace(ch) || char.IsControl(ch))
                    return false;
            }

            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && !string.IsNullOrEmpty(uri.Host);
        }

        private static void TryCopy(string source, string destination)
        {
            try { if (File.Exists(source)) File.Copy(source, destination, overwrite: true); }
            catch { /* best-effort — the dialog degrades gracefully on missing config/deps */ }
        }

        /// <summary>
        /// Resolves the dialog temp directory: <c>%ProgramData%\AutopilotMonitor-Summary\</c>.
        /// Standard users always have read+execute access under <c>%ProgramData%</c>, unlike
        /// <c>C:\WINDOWS\SystemTemp\</c> which is the path <see cref="Path.GetTempPath"/> resolves
        /// to when the agent runs as SYSTEM. V1-parity: single flat directory wiped on each
        /// launch — at most one summary dialog runs per session.
        /// </summary>
        internal static string ResolveSummaryTempDir() =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                SummaryTempRootEnvVar);

        /// <summary>
        /// Grants the BUILTIN\Users group full control on the dialog temp directory so the
        /// dialog (running in the user session) can delete itself via <c>--cleanup</c>. The
        /// directory is created by SYSTEM with default ACLs that deny user-side delete.
        /// Best-effort: failure logs at debug and continues — the dialog still launches; only
        /// self-cleanup may not run.
        /// </summary>
        private static void GrantUserDeletePermission(string directoryPath, AgentLogger logger)
        {
            try
            {
                var dirInfo = new DirectoryInfo(directoryPath);
                var security = dirInfo.GetAccessControl();
                var usersIdentity = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                security.AddAccessRule(new FileSystemAccessRule(
                    usersIdentity,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                dirInfo.SetAccessControl(security);
            }
            catch (Exception ex)
            {
                logger.Debug($"SummaryDialogLauncher: could not set ACL on summary folder: {ex.Message}");
            }
        }
    }
}

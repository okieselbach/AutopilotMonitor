using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Security;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Shared;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2
{
    /// <summary>
    /// <c>--install</c> mode: deploys the agent payload to the canonical install directory,
    /// persists bootstrap and await-enrollment config for the Scheduled Task, registers + starts
    /// the task, and writes the deployment registry marker. Plan §4.x M4.6.α.
    /// <para>
    /// Ported 1:1 from Legacy <c>Program.InstallMode.cs</c> — the OOBE bootstrap script and the
    /// Intune Platform Script contract expect exactly this sequence and these file locations.
    /// The only delta is the V2 exe name / build-output directory.
    /// </para>
    /// </summary>
    public static partial class Program
    {
        internal const string DeploymentRegistryKey = @"SOFTWARE\AutopilotMonitor";
        internal const string DeploymentRegistryValue = "Deployed";
        internal const string BootstrapConfigFileName = "bootstrap-config.json";
        internal const string AwaitEnrollmentConfigFileName = "await-enrollment.json";
        private const string InstalledAgentExeName = "AutopilotMonitor.Agent.V2.exe";

        internal static int RunInstallMode(string[] args)
        {
            var logDir = Environment.ExpandEnvironmentVariables(Constants.LogDirectory);
            var logger = new AgentLogger(logDir, AgentLogLevel.Debug);
            var consoleMode = args.Contains("--console") || Environment.UserInteractive;
            logger.EnableConsoleOutput = consoleMode;

            try
            {
                logger.Info("======================= Agent install mode (--install) =======================");

                EnsureAgentDirectories(logger);

                var sourceExePath = Assembly.GetExecutingAssembly().Location;
                var sourceDir = Path.GetDirectoryName(sourceExePath) ?? string.Empty;
                var targetAgentDir = Environment.ExpandEnvironmentVariables(
                    Path.Combine(Constants.AgentDataDirectory, DefaultAgentSubdirectory));
                var targetExePath = Path.Combine(targetAgentDir, InstalledAgentExeName);

                if (!string.Equals(
                        Path.GetFullPath(sourceDir).TrimEnd('\\'),
                        Path.GetFullPath(targetAgentDir).TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase))
                {
                    logger.Info($"Install mode called from '{sourceDir}'. Deploying payload to '{targetAgentDir}'.");
                    CopyDirectory(sourceDir, targetAgentDir, logger);
                }
                else
                {
                    logger.Info("Agent already running from target install directory; payload copy not required.");
                }

                // Persist bootstrap config if any of --bootstrap-token / --tenant-id /
                // --tenant-id-wait was provided. The Scheduled Task command line has no args —
                // the agent picks the persisted values up on the first post-install run.
                //
                // Read-merge-write: only fields explicitly set on this install invocation
                // overwrite the persisted file. This protects against a redeploy that passes
                // only --tenant-id-wait clobbering an existing BootstrapToken / TenantId, and
                // lets an admin opt out of the wait by passing --tenant-id-wait 0 explicitly
                // (which writes 0 over an old non-zero value).
                var bootstrapTokenArg = GetArgValue(args, "--bootstrap-token");
                var tenantIdArg = GetArgValue(args, "--tenant-id");
                var tenantIdWaitArg = GetArgValue(args, "--tenant-id-wait");

                bool bootstrapTokenGiven = !string.IsNullOrEmpty(bootstrapTokenArg);
                bool tenantIdGiven = !string.IsNullOrEmpty(tenantIdArg);
                bool tenantIdWaitGiven = !string.IsNullOrEmpty(tenantIdWaitArg);

                int tenantIdWaitSeconds = 0;
                if (tenantIdWaitGiven && !int.TryParse(tenantIdWaitArg, out tenantIdWaitSeconds))
                {
                    logger.Warning($"Install: --tenant-id-wait '{tenantIdWaitArg}' is not a valid integer — ignoring.");
                    tenantIdWaitGiven = false;
                    tenantIdWaitSeconds = 0;
                }

                if (bootstrapTokenGiven || tenantIdGiven || tenantIdWaitGiven)
                {
                    var bootstrapConfigPath = Path.Combine(
                        Environment.ExpandEnvironmentVariables(Constants.AgentDataDirectory),
                        BootstrapConfigFileName);

                    BootstrapConfigFile existing = null;
                    if (File.Exists(bootstrapConfigPath))
                    {
                        try
                        {
                            existing = JsonConvert.DeserializeObject<BootstrapConfigFile>(
                                File.ReadAllText(bootstrapConfigPath));
                        }
                        catch (Exception ex)
                        {
                            logger.Warning(
                                $"Existing bootstrap-config.json could not be parsed " +
                                $"({ex.GetType().Name}: {ex.Message}) — overwriting fresh.");
                        }
                    }

                    var merged = MergeBootstrapConfig(
                        existing,
                        bootstrapTokenArg, bootstrapTokenGiven,
                        tenantIdArg, tenantIdGiven,
                        tenantIdWaitSeconds, tenantIdWaitGiven);

                    File.WriteAllText(bootstrapConfigPath, JsonConvert.SerializeObject(merged));
                    logger.Info(
                        $"Bootstrap config persisted for Scheduled Task " +
                        $"(token={(string.IsNullOrEmpty(merged.BootstrapToken) ? "no" : "yes")}" +
                        $"{(bootstrapTokenGiven ? "*" : "")}, " +
                        $"tenantId={(string.IsNullOrEmpty(merged.TenantId) ? "no" : "yes")}" +
                        $"{(tenantIdGiven ? "*" : "")}, " +
                        $"tenantIdWait={merged.TenantIdWaitSeconds}s" +
                        $"{(tenantIdWaitGiven ? "*" : "")})." +
                        " Star = explicitly set on this --install invocation, others were preserved from prior config.");
                }

                // Persist await-enrollment config if requested.
                if (args.Contains("--await-enrollment"))
                {
                    var awaitConfigPath = Path.Combine(
                        Environment.ExpandEnvironmentVariables(Constants.AgentDataDirectory),
                        AwaitEnrollmentConfigFileName);
                    var awaitTimeoutArg = GetArgValue(args, "--await-enrollment-timeout");
                    var timeoutMinutes = 480;
                    if (!string.IsNullOrEmpty(awaitTimeoutArg) && int.TryParse(awaitTimeoutArg, out var parsed))
                        timeoutMinutes = parsed;

                    var awaitConfig = new AwaitEnrollmentConfigFile { TimeoutMinutes = timeoutMinutes };
                    File.WriteAllText(awaitConfigPath, JsonConvert.SerializeObject(awaitConfig));
                    logger.Info($"Await-enrollment config persisted for Scheduled Task (timeout: {timeoutMinutes}min).");
                }

                var taskName = Constants.ScheduledTaskName;

                logger.Info($"Registering Scheduled Task '{taskName}' for executable: {targetExePath}");

                // PR2: register the task from a hardened XML definition rather than the
                // schtasks /Create CLI. The CLI defaults inherit DisallowStartIfOnBatteries
                // = true and StopIfGoingOnBatteries = true, which queue the BootTrigger run
                // indefinitely on a laptop that boots on battery in OOBE / WinPE (observed
                // 2026-05-04, Event 325 'Launch request queued', task status = Queued).
                // The XML below disables both, sets StartWhenAvailable, and pins
                // ExecutionTimeLimit = PT0S so the long-running agent is never killed at
                // 72h (the schtasks CLI default).
                CreateScheduledTaskFromXml(taskName, targetExePath, logger);

                logger.Info($"Scheduled Task '{taskName}' created/updated successfully.");

                // PR1: launch the runtime directly via WMI Win32_Process.Create rather
                // than 'schtasks /Run'. Two reasons:
                //
                //   1. schtasks /Run reports SUCCESS as soon as the run is queued. The
                //      same battery / AV / OOBE-defer paths that motivate PR2 also queue
                //      a manual /Run — observed in WinPE 2026-05-04 with the task stuck
                //      in 'Queued' state (project_v2_install_runtime_handoff.md).
                //   2. WMI spawns the process from the WBEM service context, outside the
                //      install-mode process tree. A direct Process.Start would inherit
                //      whatever Job Object IntuneWindowsAgent / the bootstrap script
                //      sit in, and could be killed when those unwind. WMI gives us the
                //      same isolation Task Scheduler uses for /Run, without the queue.
                int runtimePid = StartRuntimeDetached(targetExePath, logger);
                logger.Info(
                    $"Runtime process launched (WMI-detached). PID={runtimePid}, exe={targetExePath}");

                TryWriteDeploymentMarker(logger);

                if (consoleMode)
                {
                    Console.WriteLine("Installation completed successfully.");
                    Console.WriteLine($"Task: {taskName}");
                    Console.WriteLine($"Executable: {targetExePath}");
                    Console.WriteLine($"Log: {logDir}");
                }

                return 0;
            }
            catch (Exception ex)
            {
                logger.Error("Install mode failed.", ex);
                if (consoleMode) Console.Error.WriteLine($"ERROR: {ex.Message}");
                return 1;
            }
        }

        private static void EnsureAgentDirectories(AgentLogger logger)
        {
            var basePath = Environment.ExpandEnvironmentVariables(Constants.AgentDataDirectory);
            var paths = new[]
            {
                basePath,
                Path.Combine(basePath, DefaultAgentSubdirectory),
                Environment.ExpandEnvironmentVariables(Constants.SpoolDirectory),
                Environment.ExpandEnvironmentVariables(Constants.LogDirectory),
            };

            foreach (var path in paths)
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                    logger.Info($"Created directory: {path}");
                }
                else
                {
                    logger.Debug($"Directory already exists: {path}");
                }
            }
        }

        private static int RunProcess(string fileName, string arguments, AgentLogger logger)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            logger.Info($"Executing: {fileName} {arguments}");

            // PR3-A3: prior `Process exit code: 0` line had no context — here we add the
            // process name + duration so log readers can correlate the line with the
            // command issued without scrolling back.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var process = Process.Start(psi))
            {
                if (process == null)
                    throw new InvalidOperationException($"Failed to start process: {fileName}");

                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                sw.Stop();

                if (!string.IsNullOrWhiteSpace(stdout)) logger.Info($"Process output: {stdout.Trim()}");
                if (!string.IsNullOrWhiteSpace(stderr)) logger.Warning($"Process error output: {stderr.Trim()}");

                var processName = System.IO.Path.GetFileName(fileName);
                logger.Info($"Process completed: {processName} -> exit={process.ExitCode}, durationMs={sw.ElapsedMilliseconds}");
                return process.ExitCode;
            }
        }

        private static void CopyDirectory(string sourceDir, string targetDir, AgentLogger logger)
        {
            if (!Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");

            Directory.CreateDirectory(targetDir);

            foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = dir.Substring(sourceDir.Length).TrimStart('\\');
                Directory.CreateDirectory(Path.Combine(targetDir, rel));
            }

            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(sourceDir.Length).TrimStart('\\');
                var dest = Path.Combine(targetDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? targetDir);

                try
                {
                    File.Copy(file, dest, overwrite: true);
                }
                catch (Exception ex)
                {
                    // Keep going on locked destinations — the Scheduled Task will pick up the
                    // latest available payload, and the runtime self-update path will retry later.
                    if (File.Exists(dest))
                    {
                        logger.Warning($"Could not overwrite '{dest}': {ex.Message}. Keeping existing file.");
                        continue;
                    }
                    throw;
                }
            }

            logger.Info($"Payload deployment completed: '{sourceDir}' -> '{targetDir}'");
        }

        /// <summary>
        /// Pure-function merge for <c>bootstrap-config.json</c>. Each <c>...Given</c> flag tells
        /// us whether the corresponding CLI arg was explicitly set on this <c>--install</c>
        /// invocation. When given, the new value wins (including explicit zero / empty) — this
        /// is what makes <c>--tenant-id-wait 0</c> a real opt-out. When not given, the prior
        /// persisted value is preserved — this is what stops a redeploy that only carries
        /// <c>--tenant-id-wait</c> from clobbering a previously-set BootstrapToken / TenantId.
        /// </summary>
        internal static BootstrapConfigFile MergeBootstrapConfig(
            BootstrapConfigFile existing,
            string bootstrapTokenArg, bool bootstrapTokenGiven,
            string tenantIdArg, bool tenantIdGiven,
            int tenantIdWaitSeconds, bool tenantIdWaitGiven)
        {
            return new BootstrapConfigFile
            {
                BootstrapToken = bootstrapTokenGiven ? bootstrapTokenArg : existing?.BootstrapToken,
                TenantId = tenantIdGiven ? tenantIdArg : existing?.TenantId,
                TenantIdWaitSeconds = tenantIdWaitGiven
                    ? tenantIdWaitSeconds
                    : (existing?.TenantIdWaitSeconds ?? 0),
            };
        }

        private static void TryWriteDeploymentMarker(AgentLogger logger)
        {
            try
            {
                using (var regKey = Registry.LocalMachine.CreateSubKey(DeploymentRegistryKey))
                {
                    regKey.SetValue(DeploymentRegistryValue, DateTime.UtcNow.ToString("O"));
                }
                logger.Info($"Deployment registry marker written (HKLM\\{DeploymentRegistryKey}\\{DeploymentRegistryValue}).");
            }
            catch (Exception ex)
            {
                logger.Warning($"Failed to write deployment registry marker: {ex.Message}");
            }
        }

        /// <summary>
        /// PR2: registers the BootTrigger Scheduled Task from a hardened XML definition
        /// rather than via <c>schtasks /Create /TR ... /SC ONSTART /RU SYSTEM /RL HIGHEST</c>.
        /// The CLI form inherits XML schema defaults that queue the task indefinitely on a
        /// laptop booting on battery (Event 325 'Launch request queued', observed
        /// 2026-05-04 in WinPE) and kill long-running tasks at 72 h. This XML disables both
        /// behaviours and makes the BootTrigger run robust across the OOBE → first-login
        /// power transitions.
        /// </summary>
        private static void CreateScheduledTaskFromXml(string taskName, string exePath, AgentLogger logger)
        {
            var taskXml = BuildScheduledTaskXml(exePath);
            var tempXmlPath = Path.Combine(
                Path.GetTempPath(),
                $"AutopilotMonitor-Task-{Guid.NewGuid():N}.xml");

            try
            {
                // schtasks /XML expects UTF-16 LE with BOM (matches Task Scheduler exports).
                File.WriteAllText(tempXmlPath, taskXml, Encoding.Unicode);

                var createExitCode = RunProcess(
                    SystemPaths.Schtasks,
                    $"/Create /TN \"{taskName}\" /XML \"{tempXmlPath}\" /F",
                    logger);

                if (createExitCode != 0)
                    throw new InvalidOperationException(
                        $"Failed to create/update Scheduled Task '{taskName}' from XML " +
                        $"(exit code {createExitCode}, xml='{tempXmlPath}').");
            }
            finally
            {
                try
                {
                    if (File.Exists(tempXmlPath)) File.Delete(tempXmlPath);
                }
                catch (Exception ex)
                {
                    logger.Warning($"Failed to delete temp task XML '{tempXmlPath}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Pure builder for the Task Scheduler 1.2 XML used by
        /// <see cref="CreateScheduledTaskFromXml"/>. Kept <c>internal static</c> so unit
        /// tests can pin the hardened settings — disabling these silently in a future
        /// refactor would re-introduce the OOBE battery-queue failure mode.
        /// </summary>
        internal static string BuildScheduledTaskXml(string exePath)
        {
            if (string.IsNullOrEmpty(exePath))
                throw new ArgumentException("exePath must be set.", nameof(exePath));

            var escapedExe = SecurityElement.Escape(exePath);

            return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Autopilot Monitor enrollment observability agent.</Description>
    <Author>AutopilotMonitor</Author>
  </RegistrationInfo>
  <Triggers>
    <BootTrigger>
      <Enabled>true</Enabled>
    </BootTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{escapedExe}</Command>
    </Exec>
  </Actions>
</Task>";
        }

        /// <summary>
        /// PR1: launches the runtime out-of-band of the install-mode process tree via
        /// WMI <c>Win32_Process.Create</c>. The WBEM service spawns the new process from
        /// its own service context, giving the runtime the same Job-Object isolation that
        /// <c>schtasks /Run</c> would (the Task Scheduler service is the parent there) —
        /// without paying the scheduler's queue defer (battery / AV / OOBE).
        /// </summary>
        private static int StartRuntimeDetached(string exePath, AgentLogger logger)
        {
            using (var processClass = new ManagementClass("Win32_Process"))
            using (var inParams = processClass.GetMethodParameters("Create"))
            {
                inParams["CommandLine"] = $"\"{exePath}\"";
                inParams["CurrentDirectory"] = Path.GetDirectoryName(exePath) ?? string.Empty;

                using (var outParams = processClass.InvokeMethod("Create", inParams, null))
                {
                    if (outParams == null)
                        throw new InvalidOperationException(
                            $"WMI Win32_Process.Create returned no out-parameters for '{exePath}'.");

                    var returnValue = Convert.ToUInt32(outParams["ReturnValue"]);
                    if (returnValue != 0u)
                    {
                        // WMI return codes: 0=ok, 2=access denied, 3=insufficient privilege,
                        // 8=unknown failure, 9=path not found, 21=invalid parameter, 22=invalid name
                        // (https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/create-method-in-class-win32-process).
                        throw new InvalidOperationException(
                            $"WMI Win32_Process.Create failed for '{exePath}': returnValue={returnValue}.");
                    }

                    var pid = Convert.ToInt32(outParams["ProcessId"]);
                    if (pid <= 0)
                        throw new InvalidOperationException(
                            $"WMI Win32_Process.Create reported success but no ProcessId for '{exePath}'.");

                    return pid;
                }
            }
        }
    }
}

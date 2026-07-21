using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather.Collectors
{
    public class CommandCollector : IGatherRuleCollector
    {
        public string CollectorType => "command_allowlisted";

        private const int CommandTimeoutMs = 30000;

        /// <summary>
        /// Returns what the reader captured, or an empty string if it did not finish
        /// (a killed process can leave the read faulted or still pending).
        /// </summary>
        private static string DrainOrEmpty(System.Threading.Tasks.Task<string> readTask)
        {
            try
            {
                return readTask.Wait(2000) ? readTask.Result : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public Dictionary<string, object> Execute(GatherRule rule, GatherRuleContext context)
        {
            var data = new Dictionary<string, object>();
            var command = rule.Target;

            if (string.IsNullOrEmpty(command))
                return data;

            // SECURITY: Check command against allowlist
            if (!GatherRuleGuards.IsCommandAllowed(command, context.UnrestrictedMode))
            {
                context.Logger.Warning($"SECURITY: Command blocked by allowlist: {command} (Rule: {rule.RuleId})");
                data["blocked"] = true;
                data["reason"] = "Command not on allowlist";
                data["command"] = command;

                context.OnEventCollected(new EnrollmentEvent
                {
                    SessionId = context.SessionId,
                    TenantId = context.TenantId,
                    Timestamp = DateTime.UtcNow,
                    EventType = Constants.EventTypes.SecurityWarning,
                    Severity = EventSeverity.Warning,
                    Source = "GatherRuleExecutor",
                    Message = $"Blocked command not on allowlist: {command} (Rule: {rule.RuleId})",
                    Data = data
                });

                return data;
            }

            try
            {
                var isPowerShell = !command.StartsWith("netsh", StringComparison.OrdinalIgnoreCase) &&
                                   !command.StartsWith("ipconfig", StringComparison.OrdinalIgnoreCase) &&
                                   !command.StartsWith("nltest", StringComparison.OrdinalIgnoreCase) &&
                                   !command.StartsWith("certutil", StringComparison.OrdinalIgnoreCase) &&
                                   !command.StartsWith("dsregcmd", StringComparison.OrdinalIgnoreCase);

                ProcessStartInfo psi;
                if (isPowerShell)
                {
                    // Use -EncodedCommand with Base64-encoded UTF-16LE to prevent command injection
                    var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
                    psi = new ProcessStartInfo
                    {
                        FileName = SystemPaths.PowerShell,
                        Arguments = $"-NoProfile -ExecutionPolicy RemoteSigned -EncodedCommand {encodedCommand}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                }
                else
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = SystemPaths.Cmd,
                        Arguments = $"/c {command}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                }

                using (var process = Process.Start(psi))
                {
                    // Read asynchronously: a synchronous ReadToEnd() blocks until the pipe
                    // closes, which only happens when the process exits — the WaitForExit
                    // timeout below would never be reached and a hung command would pin
                    // this worker forever.
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();

                    var exited = process.WaitForExit(CommandTimeoutMs);
                    if (!exited)
                    {
                        context.Logger.Warning(
                            $"Command timed out after {CommandTimeoutMs / 1000}s, killing: {command} (Rule: {rule.RuleId})");
                        try { process.Kill(); } catch { /* already gone */ }
                        // Give the readers a moment to drain once the pipes close.
                        try { process.WaitForExit(5000); } catch { /* best effort */ }
                        data["timed_out"] = true;
                    }

                    data["command"] = command;
                    data["exit_code"] = exited ? process.ExitCode : -1;

                    var output = DrainOrEmpty(outputTask);
                    var error = DrainOrEmpty(errorTask);

                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        data["output"] = output.Length > 32000 ? output.Substring(0, 32000) + "... (truncated)" : output;
                    }

                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        data["error_output"] = error.Length > 8000 ? error.Substring(0, 8000) + "... (truncated)" : error;
                    }
                }
            }
            catch (Exception ex)
            {
                data["error"] = ex.Message;
            }

            return data;
        }
    }
}

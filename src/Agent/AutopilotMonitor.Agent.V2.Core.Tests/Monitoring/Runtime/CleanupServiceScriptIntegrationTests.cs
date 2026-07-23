using System;
using System.Diagnostics;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Security;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Runtime
{
    /// <summary>
    /// Executes the generated cleanup script against REAL scratch trees with a real
    /// powershell.exe — end-to-end replays of the 2026-07-22 incident mechanics that the
    /// string pins in <see cref="CleanupServiceScriptTests"/> cannot prove:
    /// <list type="bullet">
    ///   <item>a locked tree must abort WITHOUT deleting anything (old script force-deleted
    ///     marker + state around the locks and orphaned the binaries),</item>
    ///   <item>a straggler agent process (the incident's second instance) must be found by
    ///     NAME, killed, and the cleanup must then complete — simulated via function
    ///     shadowing, NEVER by launching a renamed executable (EDR isolation, see the test),</item>
    ///   <item>concurrent cleanup scripts must be serialized by the lock file.</item>
    /// </list>
    /// Timings are dialed down via the test-only BuildCleanupScript parameters so each case
    /// runs in seconds. Every test uses GUID-unique process/task/tree names so nothing real
    /// (a dev-box agent, a real Scheduled Task) can ever be touched.
    /// </summary>
    [Collection("SerialThreading")]
    public class CleanupServiceScriptIntegrationTests : IDisposable
    {
        private readonly string _root;
        private readonly string _basePath;
        private readonly string _taskName = $"AmTest-NoSuchTask-{Guid.NewGuid():N}";
        private readonly string _fakeProcessName = $"AmTestAgent{Guid.NewGuid().ToString("N").Substring(0, 8)}";

        public CleanupServiceScriptIntegrationTests()
        {
            _root = Path.Combine(Path.GetTempPath(), $"am-cleanup-it-{Guid.NewGuid():N}");
            _basePath = Path.Combine(_root, "AutopilotMonitor");
            Directory.CreateDirectory(Path.Combine(_basePath, "Agent"));
            Directory.CreateDirectory(Path.Combine(_basePath, "State"));
            Directory.CreateDirectory(Path.Combine(_basePath, "Logs"));
            File.WriteAllText(Path.Combine(_basePath, "Agent", "fake.exe"), "x");
            File.WriteAllText(Path.Combine(_basePath, "State", "enrollment-complete.marker"), "x");
            File.WriteAllText(Path.Combine(_basePath, "Logs", "agent.log"), "x");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private static string LockPath => Path.Combine(Path.GetTempPath(), "AutopilotMonitor-Cleanup.lock");

        private string BuildScript(bool keepLogs = false, int processWaitSeconds = 1)
            => CleanupService.BuildCleanupScript(
                agentProcessName: _fakeProcessName,
                agentBasePath: _basePath,
                keepLogs: keepLogs,
                scheduledTaskName: _taskName,
                rebootOnComplete: false,
                initialDelaySeconds: 0,
                processWaitSeconds: processWaitSeconds,
                renameRetries: 2,
                renameRetryDelaySeconds: 1);

        /// <summary>Runs the script synchronously in a real powershell.exe. Returns the script path.</summary>
        private string RunScript(string script)
        {
            var scriptPath = Path.Combine(_root, $"cleanup-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(scriptPath, script);
            using (var proc = Process.Start(new ProcessStartInfo
            {
                FileName = SystemPaths.PowerShell,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            }))
            {
                Assert.True(proc.WaitForExit(90_000), "cleanup script did not finish within 90s");
            }
            return scriptPath;
        }

        [Fact]
        public void Happy_path_deletes_tree_and_removes_lock_and_script()
        {
            var scriptPath = RunScript(BuildScript());

            Assert.False(Directory.Exists(_basePath), "tree should be fully deleted");
            Assert.Empty(Directory.GetFileSystemEntries(_root, "*.del-*"));
            Assert.False(File.Exists(scriptPath), "script should self-delete");
            Assert.False(File.Exists(LockPath), "cleanup lock should be released");
        }

        [Fact]
        public void Locked_tree_aborts_completely_marker_and_files_survive()
        {
            // The incident regression: with a file locked inside the tree the OLD script
            // deleted everything else (incl. the enrollment-complete marker) and left the
            // binaries orphaned with the task already gone. The hardened script must treat
            // the failed probe-rename as "someone still holds this" and walk away entirely.
            using (File.Open(Path.Combine(_basePath, "Agent", "fake.exe"),
                       FileMode.Open, FileAccess.Read, FileShare.None))
            {
                RunScript(BuildScript());
            }

            Assert.True(File.Exists(Path.Combine(_basePath, "Agent", "fake.exe")), "binaries must stay in place");
            Assert.True(File.Exists(Path.Combine(_basePath, "State", "enrollment-complete.marker")),
                "marker must survive so the next boot retries cleanup");
            Assert.Empty(Directory.GetFileSystemEntries(_root, "*.del-*"));
            Assert.False(File.Exists(LockPath));
        }

        [Fact]
        public void Straggler_agent_process_is_waited_on_and_force_killed_via_name_backstop()
        {
            // Incident replay of the second-instance mechanics WITHOUT spawning a process:
            // an earlier version of this test copied powershell.exe under the fake agent name
            // and launched it — textbook MITRE T1036.003 ("Rename Legitimate Utilities"),
            // which Defender for Endpoint answered with a machine ISOLATION on the dev box
            // (see tasks/lessons.md). Instead, a harness script shadows Get-Process /
            // Stop-Process with functions (PowerShell resolves functions before cmdlets, so
            // the dot-sourced cleanup script binds to the stubs): Get-Process reports a live
            // agent-named straggler on every probe; Stop-Process records the kill. This pins
            // the same contract — the script polls by NAME until the deadline, then
            // force-kills by NAME before deleting — with zero EDR-visible behavior.
            var probeLog = Path.Combine(_root, "probe.log");
            var cleanupScriptPath = Path.Combine(_root, $"cleanup-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(cleanupScriptPath, BuildScript(processWaitSeconds: 2));

            var harness = @"
$probeLog = 'LOG_PATH'
$global:AmProbeCount = 0
function Get-Process {
    [CmdletBinding()]
    param([string[]]$Name)
    if ($Name -contains 'FAKE_NAME') {
        $global:AmProbeCount++
        Add-Content -Path $probeLog -Value ('probe ' + $global:AmProbeCount)
        return [pscustomobject]@{ Name = 'FAKE_NAME'; Id = 424242 }
    }
    Microsoft.PowerShell.Management\Get-Process @PSBoundParameters
}
function Stop-Process {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline = $true)]$InputObject, [switch]$Force)
    process { Add-Content -Path $probeLog -Value ('kill ' + $InputObject.Id + ' force=' + $Force) }
}
. 'SCRIPT_PATH'
"
                .Replace("LOG_PATH", probeLog)
                .Replace("FAKE_NAME", _fakeProcessName)
                .Replace("SCRIPT_PATH", cleanupScriptPath);

            RunScript(harness);

            var log = File.ReadAllLines(probeLog);
            Assert.Contains("kill 424242 force=True", log);
            Assert.True(Array.FindAll(log, l => l.StartsWith("probe")).Length >= 2,
                "script must poll by name in the wait loop AND re-probe for the kill backstop");
            Assert.False(Directory.Exists(_basePath), "after the kill the tree must be fully deleted");
            Assert.Empty(Directory.GetFileSystemEntries(_root, "*.del-*"));
            Assert.False(File.Exists(LockPath));
        }

        [Fact]
        public void Fresh_foreign_lock_backs_off_without_touching_the_tree()
        {
            File.WriteAllText(LockPath, "held by another cleanup");
            try
            {
                var scriptPath = RunScript(BuildScript());

                Assert.True(File.Exists(Path.Combine(_basePath, "Agent", "fake.exe")),
                    "loser script must not touch the tree — the lock holder owns the deletion");
                Assert.True(File.Exists(LockPath), "foreign lock must not be deleted by the loser");
                Assert.False(File.Exists(scriptPath), "loser still self-deletes its script");
            }
            finally
            {
                try { File.Delete(LockPath); } catch { }
            }
        }

        [Fact]
        public void Stale_foreign_lock_is_taken_over_and_cleanup_proceeds()
        {
            File.WriteAllText(LockPath, "crashed cleanup from a previous boot");
            File.SetLastWriteTime(LockPath, DateTime.Now.AddMinutes(-15));

            RunScript(BuildScript());

            Assert.False(Directory.Exists(_basePath), "stale lock must be taken over, cleanup must run");
            Assert.False(File.Exists(LockPath));
        }

        [Fact]
        public void KeepLogs_variant_deletes_everything_except_logs()
        {
            RunScript(BuildScript(keepLogs: true));

            Assert.True(File.Exists(Path.Combine(_basePath, "Logs", "agent.log")), "Logs must survive");
            Assert.False(Directory.Exists(Path.Combine(_basePath, "Agent")));
            Assert.False(Directory.Exists(Path.Combine(_basePath, "State")));
            Assert.Empty(Directory.GetFileSystemEntries(_basePath, "*.del-*"));
        }
    }
}

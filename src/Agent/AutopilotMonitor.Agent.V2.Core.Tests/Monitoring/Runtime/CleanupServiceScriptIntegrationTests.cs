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
    ///     NAME, killed, and the cleanup must then complete,</item>
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
        public void Straggler_process_holding_the_tree_is_killed_and_cleanup_completes()
        {
            // Full incident replay: a SECOND agent-named process (here: a renamed
            // powershell.exe, matching how only the process NAME identifies agents) holds a
            // file in the tree open. The old script waited on the launcher PID only and ran
            // its force-delete against the live locks. The hardened script must find the
            // straggler by name, kill it after the wait deadline, and then delete everything.
            var fakeExe = Path.Combine(_root, _fakeProcessName + ".exe");
            File.Copy(SystemPaths.PowerShell, fakeExe);

            var lockTarget = Path.Combine(_basePath, "Agent", "fake.exe").Replace("'", "''");
            Process straggler = null;
            try
            {
                straggler = Process.Start(new ProcessStartInfo
                {
                    FileName = fakeExe,
                    Arguments = "-NoProfile -Command \"$f = [IO.File]::Open('" + lockTarget +
                                "', 'Open', 'Read', 'None'); Start-Sleep -Seconds 120\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

                // Give the straggler time to acquire its file handle before cleanup starts.
                var acquireDeadline = DateTime.UtcNow.AddSeconds(20);
                var acquired = false;
                while (!acquired && DateTime.UtcNow < acquireDeadline)
                {
                    try
                    {
                        using (File.Open(Path.Combine(_basePath, "Agent", "fake.exe"),
                                   FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                        }
                    }
                    catch (IOException)
                    {
                        acquired = true; // share violation ⇒ straggler holds the file
                    }
                    if (!acquired) System.Threading.Thread.Sleep(200);
                }
                Assert.True(acquired, "straggler never acquired its file lock");

                RunScript(BuildScript(processWaitSeconds: 2));

                Assert.True(straggler.HasExited, "straggler must be force-killed by the name-based backstop");
                Assert.False(Directory.Exists(_basePath), "after the kill the tree must be fully deleted");
                Assert.Empty(Directory.GetFileSystemEntries(_root, "*.del-*"));
            }
            finally
            {
                try { if (straggler != null && !straggler.HasExited) straggler.Kill(); } catch { }
                try { straggler?.Dispose(); } catch { }
            }
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

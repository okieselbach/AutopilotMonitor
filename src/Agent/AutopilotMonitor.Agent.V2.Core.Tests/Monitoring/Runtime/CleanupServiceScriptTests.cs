using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Runtime
{
    /// <summary>
    /// Pins the LIFE-F7 hardening invariants of the self-destruct cleanup script
    /// (incident 2026-07-22: a second agent instance held exe + loaded DLLs while the
    /// old script — waiting on the launcher PID only — force-deleted around the locks
    /// AFTER removing the Scheduled Task, orphaning the binaries with no retry path).
    /// These are string-level pins on <see cref="CleanupService.BuildCleanupScript"/>:
    /// weakening any of them silently re-introduces the orphaned-binaries failure mode.
    /// </summary>
    public class CleanupServiceScriptTests
    {
        private const string ProcessName = "AutopilotMonitor.Agent";
        private const string BasePath = @"C:\ProgramData\AutopilotMonitor";
        private const string TaskName = "AutopilotMonitorAgent";

        private static string Script(bool keepLogs = false, bool reboot = false)
            => CleanupService.BuildCleanupScript(ProcessName, BasePath, keepLogs, TaskName, reboot);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Waits_by_process_name_not_launcher_pid(bool keepLogs)
        {
            var script = Script(keepLogs);

            // LIFE-F7 #2: name-based wait covers marker-retry / self-update / manual instances
            // and is immune to PID reuse. Wait-Process -Id saw exactly one PID and nothing else.
            Assert.Contains($"Get-Process -Name '{ProcessName}'", script);
            Assert.DoesNotContain("Wait-Process", script);

            // Backstop: stragglers are force-killed — their images are deleted right after.
            Assert.Contains("Stop-Process -Force", script);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Never_deletes_the_original_path(bool keepLogs)
        {
            var script = Script(keepLogs);

            // LIFE-F7 #3: deletion happens exclusively on successfully renamed targets
            // ($entry.Renamed). A Remove-Item against the original tree is what deleted
            // marker + state around locked binaries in the incident.
            Assert.DoesNotContain($"Remove-Item -Path '{BasePath}'", script);
            Assert.Contains("Remove-Item -Path $entry.Renamed", script);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Task_removal_is_gated_on_probe_and_deletion_on_task_gone(bool keepLogs)
        {
            var script = Script(keepLogs);

            // Probe-rename first; only a successful probe reaches task removal, and only a
            // removed task reaches deletion. Both failure branches rename BACK so the next
            // boot retries with task + marker + files intact.
            Assert.Contains("if ($probeOk)", script);
            Assert.Contains("if ($taskGone)", script);
            Assert.Contains("Rename-Item -Path $entry.Renamed -NewName $entry.Original", script);

            // LIFE-F6 task-removal loop + module-independent fallback stay intact.
            Assert.Contains("Unregister-ScheduledTask -TaskName $taskName", script);
            Assert.Contains("schtasks.exe /Delete /TN $taskName /F", script);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Concurrent_cleanup_scripts_are_serialized_by_lock_file(bool keepLogs)
        {
            var script = Script(keepLogs);

            // LIFE-F7 #1: CreateNew is the atomic single-flight gate; the loser exits without
            // touching the tree. Stale locks (>10 min) are taken over.
            Assert.Contains("AutopilotMonitor-Cleanup.lock", script);
            Assert.Contains("[System.IO.FileMode]::CreateNew", script);
            Assert.Contains("$lockAge.TotalMinutes -gt 10", script);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Rename_targets_carry_a_guid_suffix(bool keepLogs)
        {
            // A fixed '.del' target collides with leftovers of earlier attempts and with a
            // concurrent script's rename — the GUID makes every attempt's target unique.
            Assert.Contains(".del-' + [Guid]::NewGuid().ToString('N')", Script(keepLogs));
        }

        [Fact]
        public void KeepLogs_variant_excludes_logs_directory()
        {
            var script = Script(keepLogs: true);
            Assert.Contains("-Exclude 'Logs'", script);
            Assert.DoesNotContain($"Rename-Item -Path '{BasePath}' -NewName", script);
        }

        [Fact]
        public void Full_variant_renames_the_whole_tree()
        {
            var script = Script(keepLogs: false);
            Assert.Contains($"Rename-Item -Path '{BasePath}' -NewName $renamedPath", script);
            Assert.DoesNotContain("-Exclude 'Logs'", script);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Reboot_flag_controls_shutdown_invocation(bool reboot)
        {
            var script = Script(keepLogs: false, reboot: reboot);
            if (reboot)
                Assert.Contains("shutdown.exe /r /t 10", script);
            else
                Assert.DoesNotContain("shutdown.exe", script);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Script_and_lock_are_released_in_finally(bool keepLogs)
        {
            var script = Script(keepLogs);
            Assert.Contains("finally", script);
            Assert.Contains("$lockStream.Dispose()", script);
            Assert.Contains("Remove-Item -Path $scriptPath -Force", script);
        }
    }
}

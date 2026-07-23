using System;
using System.Diagnostics;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Security;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime
{
    /// <summary>
    /// Handles agent cleanup on enrollment completion:
    /// removes the Scheduled Task and deletes all agent files (self-destruct).
    /// </summary>
    public class CleanupService
    {
        private readonly AgentConfiguration _configuration;
        private readonly AgentLogger _logger;

        public CleanupService(AgentConfiguration configuration, AgentLogger logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Executes full self-destruct: removes scheduled task and deletes all files
        /// </summary>
        public virtual void ExecuteSelfDestruct()
        {
            try
            {
                _logger.Info($"Executing FULL SELF-DESTRUCT (Scheduled Task + Files{(_configuration.RebootOnComplete ? " + Reboot" : "")})");

                var currentProcess = Process.GetCurrentProcess();
                var agentBasePath = Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "AutopilotMonitor"
                ));

                var cleanupScript = BuildCleanupScript(
                    agentProcessName: currentProcess.ProcessName,
                    agentBasePath: agentBasePath,
                    keepLogs: _configuration.KeepLogFile,
                    scheduledTaskName: _configuration.ScheduledTaskName,
                    rebootOnComplete: _configuration.RebootOnComplete);

                // Write cleanup script to temp location (outside of agent folder)
                var tempScriptPath = Path.Combine(Path.GetTempPath(), $"AutopilotMonitor-Cleanup-{Guid.NewGuid()}.ps1");
                File.WriteAllText(tempScriptPath, cleanupScript);
                _logger.Info($"Cleanup script written to {tempScriptPath}");

                // Change CWD to temp so this process no longer holds a reference into
                // the AutopilotMonitor folder tree - Windows won't allow renaming a
                // directory that any process has as its current working directory.
                try { Directory.SetCurrentDirectory(Path.GetTempPath()); } catch { }

                // Launch via cmd /c start so the powershell process is created outside the
                // current Job Object (Scheduled Task job). cmd's 'start' command always
                // creates a new process group that breaks job inheritance, even under SYSTEM.
                var psi = new ProcessStartInfo
                {
                    FileName = SystemPaths.Cmd,
                    // Absolute path to powershell.exe is passed to `start` to prevent PATH hijacking
                    // (the `start` builtin does its own PATH resolution for the target command).
                    Arguments = $"/c start \"\" /b \"{SystemPaths.PowerShell}\" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{tempScriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetTempPath()
                };

                Process.Start(psi);
                _logger.Info("Cleanup script launched. Agent will now exit.");
            }
            catch (Exception ex)
            {
                _logger.Error("Error executing self-destruct", ex);
            }
        }

        /// <summary>
        /// Builds the self-deleting cleanup PowerShell script. Extracted (internal static, pure)
        /// so tests can pin the hardened invariants without launching processes.
        /// <para>
        /// LIFE-F7 hardening (incident 2026-07-22 — exe + loaded DLLs orphaned forever):
        /// </para>
        /// <list type="number">
        ///   <item><b>Single-flight lock</b> — the enrollment-complete marker is written BEFORE
        ///     cleanup launches, so any agent start during an in-flight cleanup spawns a SECOND
        ///     cleanup script. Two unsynchronized scripts interleave their rename/delete phases
        ///     and leave partial trees behind. A CreateNew lock file in the SYSTEM temp dir
        ///     serializes them; a stale lock (&gt;10 min) is taken over.</item>
        ///   <item><b>Wait by process NAME, not launcher PID</b> — marker-retry instances,
        ///     self-update restarts and manual runs all load the same images but have PIDs the
        ///     old <c>Wait-Process -Id</c> never saw (and an already-freed PID can be reused by
        ///     an unrelated boot-time process). After the deadline any straggler is force-killed:
        ///     its images are about to be deleted anyway.</item>
        ///   <item><b>Probe-rename BEFORE task removal, never delete the original path</b> — the
        ///     rename is the atomic lock probe. If it fails, something still holds the tree: abort
        ///     with task + files + marker intact so the next boot retries (file-side symmetry to
        ///     the LIFE-F6 task-side rule). If the task cannot be removed afterwards, rename BACK
        ///     and abort for the same reason. Only a fully renamed tree with the task gone is
        ///     deleted — the old <c>Remove-Item</c>-on-original fallback deleted marker + state
        ///     around locked binaries AFTER the task was gone, converting a transient lock into a
        ///     permanently orphaned exe.</item>
        /// </list>
        /// </summary>
        /// <remarks>
        /// The timing parameters exist ONLY so integration tests can replay the incident
        /// scenarios (locked tree, straggler process) in seconds instead of minutes —
        /// production call sites always use the defaults.
        /// </remarks>
        internal static string BuildCleanupScript(
            string agentProcessName,
            string agentBasePath,
            bool keepLogs,
            string scheduledTaskName,
            bool rebootOnComplete,
            int initialDelaySeconds = 2,
            int processWaitSeconds = 60,
            int renameRetries = 10,
            int renameRetryDelaySeconds = 2)
        {
            return $@"
$scriptPath = $MyInvocation.MyCommand.Path

# Single-flight lock (LIFE-F7 #1): serialize concurrent cleanup scripts. CreateNew is
# atomic; the loser backs off and lets the winner own the deletion.
$lockPath = Join-Path ([System.IO.Path]::GetTempPath()) 'AutopilotMonitor-Cleanup.lock'
$lockStream = $null
try {{
    if (Test-Path $lockPath) {{
        $lockAge = (Get-Date) - (Get-Item $lockPath -ErrorAction Stop).LastWriteTime
        if ($lockAge.TotalMinutes -gt 10) {{ Remove-Item -Path $lockPath -Force -ErrorAction SilentlyContinue }}
    }}
    $lockStream = [System.IO.File]::Open($lockPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
}} catch {{
    Remove-Item -Path $scriptPath -Force -ErrorAction SilentlyContinue
    exit 0
}}

try {{
    # Wait for EVERY agent process to exit, by NAME (LIFE-F7 #2) — covers marker-retry
    # instances, self-update restarts and manual runs, and is immune to PID reuse.
    Start-Sleep -Seconds {initialDelaySeconds}
    $deadline = (Get-Date).AddSeconds({processWaitSeconds})
    while ((Get-Date) -lt $deadline) {{
        if (-not (Get-Process -Name '{agentProcessName}' -ErrorAction SilentlyContinue)) {{ break }}
        Start-Sleep -Seconds 2
    }}
    # Backstop: force-kill stragglers — their images are about to be deleted anyway.
    Get-Process -Name '{agentProcessName}' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1

{(keepLogs ? $@"
    # Probe-rename all children except Logs (LIFE-F7 #3). Any failure ⇒ rename back and
    # abort with the Scheduled Task intact so the next boot retries cleanly.
    $renamed = @()
    $probeOk = $true
    foreach ($item in @(Get-ChildItem -Path '{agentBasePath}' -Exclude 'Logs' -ErrorAction SilentlyContinue)) {{
        $dest = $item.FullName + '.del-' + [Guid]::NewGuid().ToString('N')
        $itemOk = $false
        for ($i = 1; $i -le {renameRetries}; $i++) {{
            try {{
                Rename-Item -Path $item.FullName -NewName $dest -Force -ErrorAction Stop
                $itemOk = $true
                break
            }} catch {{
                Start-Sleep -Seconds {renameRetryDelaySeconds}
            }}
        }}
        if ($itemOk) {{ $renamed += @{{ Original = $item.FullName; Renamed = $dest }} }}
        else {{ $probeOk = $false; break }}
    }}
" : $@"
    # Probe-rename the whole tree (LIFE-F7 #3). Failure ⇒ abort with task + files + marker
    # intact so the next boot retries cleanly. GUID suffix avoids collisions with leftovers
    # of earlier attempts.
    $renamedPath = '{agentBasePath}.del-' + [Guid]::NewGuid().ToString('N')
    $probeOk = $false
    for ($i = 1; $i -le {renameRetries}; $i++) {{
        try {{
            Rename-Item -Path '{agentBasePath}' -NewName $renamedPath -Force -ErrorAction Stop
            $probeOk = $true
            break
        }} catch {{
            Start-Sleep -Seconds {renameRetryDelaySeconds}
        }}
    }}
    $renamed = @(@{{ Original = '{agentBasePath}'; Renamed = $renamedPath }})
")}
    if ($probeOk) {{
        # Remove Scheduled Task — verify removal BEFORE deleting files (LIFE-F6). An orphan
        # task firing against a deleted exe spams Task Scheduler errors every boot forever
        # and, once the files (incl. the enrollment-complete marker) are gone, has no
        # cleanup-retry path.
        $taskName = '{scheduledTaskName}'
        $taskGone = $false
        for ($i = 1; $i -le 5; $i++) {{
            try {{ Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue }} catch {{ }}
            try {{ Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue }} catch {{ }}
            $still = $null
            try {{ $still = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue }} catch {{ }}
            if (-not $still) {{ $taskGone = $true; break }}
            Start-Sleep -Seconds 1
        }}
        if (-not $taskGone) {{
            # Fallback: schtasks.exe /Delete does not depend on the ScheduledTasks PowerShell module.
            try {{ & schtasks.exe /Delete /TN $taskName /F 2>$null | Out-Null }} catch {{ }}
            try {{ $taskGone = -not (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) }} catch {{ }}
        }}

        if ($taskGone) {{
            # Task gone + tree renamed ⇒ safe to delete. Never touches the original path.
            foreach ($entry in $renamed) {{
                Remove-Item -Path $entry.Renamed -Recurse -Force -ErrorAction SilentlyContinue
            }}
        }} else {{
            # Task survived ⇒ rename back and leave everything for the next-boot retry
            # (the marker survives, so bootstrap re-runs cleanup).
            foreach ($entry in $renamed) {{
                try {{ Rename-Item -Path $entry.Renamed -NewName $entry.Original -Force -ErrorAction SilentlyContinue }} catch {{ }}
            }}
        }}
    }} else {{
        # Probe failed ⇒ something still holds the tree. Rename back whatever succeeded and
        # abort WITHOUT deleting anything — task + marker stay, next boot retries.
        foreach ($entry in $renamed) {{
            try {{ Rename-Item -Path $entry.Renamed -NewName $entry.Original -Force -ErrorAction SilentlyContinue }} catch {{ }}
        }}
    }}
{(rebootOnComplete ? @"
    # shutdown.exe (NOT Restart-Computer): Restart-Computer has no -Comment parameter and -Delay is
    # only honoured with -Wait, so the previous invocation failed parameter binding and the device
    # silently never rebooted. shutdown.exe /r /t 10 mirrors the standalone reboot path and reboots
    # after a 10 s delay (lets this script finish + the agent exit). Review LIFE-F2.
    shutdown.exe /r /t 10 /c 'Autopilot enrollment completed - Autopilot Monitor is rebooting'
" : "")}
}} finally {{
    if ($lockStream) {{ $lockStream.Dispose() }}
    Remove-Item -Path $lockPath -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $scriptPath -Force -ErrorAction SilentlyContinue
}}
";
        }

    }
}

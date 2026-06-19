#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using AutopilotMonitor.Agent.V2.Core.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Security
{
    /// <summary>
    /// Live watcher for a SYSTEM console opened during enrollment — the classic <b>Shift+F10</b>
    /// OOBE bypass, where <c>winlogon.exe</c> spawns a <c>cmd.exe</c> running as SYSTEM. The
    /// discriminator is not the keystroke (un-hookable across sessions from session 0) but the
    /// <b>process signature</b>: a watched console process whose <i>parent</i> is <c>winlogon.exe</c>.
    /// That parent cleanly separates the bypass console from ordinary install-launched cmd (parented
    /// to IME / a script host), keeping the false-positive rate near zero.
    /// <para>
    /// Mirrors <see cref="Office.OfficeProcessWatcher"/>: a true ETW-backed push via WMI
    /// <c>Win32_ProcessStartTrace</c> (no idle polling) plus a one-shot startup probe over
    /// <c>Win32_Process</c> so a console still open when the agent boots (realistic during OOBE) is
    /// still caught. Fail-soft — if the WMI watcher cannot start it is logged and the startup probe
    /// still covers the already-open case; no polling fallback by design.
    /// </para>
    /// <para>
    /// <b>Coverage</b> is best-effort: the agent installs during ESP, so a Shift+F10 pressed earlier
    /// in OOBE (before the agent exists) is invisible here — that pre-agent window is covered, coarsely,
    /// by the <see cref="Analyzers.ConsolePrefetchScanner"/>. The emitted event documents this gap.
    /// </para>
    /// </summary>
    public sealed class ConsoleBypassWatcher : IDisposable
    {
        /// <summary>
        /// Console executables treated as a bypass console when parented to winlogon. v1 watches only
        /// <c>cmd.exe</c> (the exact Shift+F10 spawn); the list is the single seam to broaden later
        /// (e.g. <c>powershell.exe</c>) without touching the watcher logic.
        /// </summary>
        internal static readonly string[] WatchedProcessNames = { "cmd.exe" };

        internal const string ParentProcessName = "winlogon.exe";
        private const string WinlogonNoExt = "winlogon";

        private readonly AgentLogger _logger;
        private readonly Func<int, string?> _parentNameResolver;
        private readonly object _lock = new object();
        private readonly HashSet<int> _seen = new HashSet<int>();

        private ManagementEventWatcher? _startWatcher;
        private bool _started;
        private bool _disposed;

        /// <summary>
        /// Raised once per process when a watched console spawns with <c>winlogon.exe</c> as its
        /// parent. The host translates this into a Warning <c>oobe_console_spawned</c> event.
        /// </summary>
        public event EventHandler<ConsoleSpawnInfo>? BypassConsoleDetected;

        public ConsoleBypassWatcher(AgentLogger logger, Func<int, string?>? parentNameResolver = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _parentNameResolver = parentNameResolver ?? DefaultResolveParentName;
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_started || _disposed) return;
                _started = true;
            }

            // 1) Startup probe — a console already open before the watcher armed (Shift+F10 pressed
            //    while the agent was installing). Win32_Process exposes the parent PID + session
            //    directly, so the same classification path is reused.
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, ParentProcessId, SessionId FROM Win32_Process WHERE Name = 'cmd.exe'");
                using var results = searcher.Get();
                foreach (var mo in results)
                {
                    using (mo)
                    {
                        HandleStart(
                            ToInt(mo["ProcessId"]),
                            ToInt(mo["ParentProcessId"]),
                            ToInt(mo["SessionId"]),
                            "startup_probe");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"[ConsoleBypassWatcher] startup probe failed: {ex.Message}");
            }

            // 2) ETW-backed push on console creation — no polling.
            try
            {
                var query = new WqlEventQuery(
                    "SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName = 'cmd.exe'");
                _startWatcher = new ManagementEventWatcher(query);
                _startWatcher.EventArrived += OnProcessStartTrace;
                _startWatcher.Start();
                _logger.Info("[ConsoleBypassWatcher] watching Win32_ProcessStartTrace for cmd.exe (parent winlogon.exe)");
            }
            catch (Exception ex)
            {
                _logger.Warning($"[ConsoleBypassWatcher] could not start WMI process-start watcher " +
                    $"({ex.GetType().Name}: {ex.Message}); relying on the startup probe only.");
                _startWatcher = null;
            }
        }

        private void OnProcessStartTrace(object sender, EventArrivedEventArgs e)
        {
            try
            {
                var pid = ToInt(e.NewEvent?["ProcessID"]);
                var parentPid = ToInt(e.NewEvent?["ParentProcessID"]);
                var sessionId = ToInt(e.NewEvent?["SessionID"]);
                HandleStart(pid, parentPid, sessionId, "process_start_trace");
            }
            catch (Exception ex)
            {
                _logger.Debug($"[ConsoleBypassWatcher] start-trace handler error: {ex.Message}");
            }
        }

        /// <summary>
        /// Classification core (internal for tests): resolves the parent name and raises
        /// <see cref="BypassConsoleDetected"/> exactly once per PID when the parent is winlogon.
        /// A non-winlogon parent (ordinary install-launched cmd) is ignored.
        /// </summary>
        internal void HandleStart(int pid, int parentPid, int sessionId, string detectedVia)
        {
            if (pid <= 0 || parentPid <= 0) return;

            lock (_lock) { if (_disposed) return; }

            string? parentName;
            try { parentName = _parentNameResolver(parentPid); }
            catch (Exception ex)
            {
                // Parent already gone / inaccessible — cannot confirm winlogon, so stay conservative
                // and do not flag (avoids a false positive). winlogon never exits during enrollment.
                // Deliberately NOT marked seen: the PID may be reused later by a real winlogon-parented
                // console, which must still be detected.
                _logger.Debug($"[ConsoleBypassWatcher] parent resolve failed for pid={pid} parent={parentPid}: {ex.Message}");
                return;
            }

            if (string.IsNullOrEmpty(parentName) ||
                !parentName!.Equals(WinlogonNoExt, StringComparison.OrdinalIgnoreCase))
            {
                // Not a Shift+F10 SYSTEM console. Do NOT mark the PID seen — an ordinary cmd that
                // exits could see its PID reused by a later genuine winlogon-parented console, which
                // must not be suppressed by this non-match.
                return;
            }

            // Confirmed bypass console — dedup ONLY here, so the startup probe and the live trace
            // observing the same console collapse to a single detection, while a non-winlogon start
            // with the same PID never pre-empts a real one.
            lock (_lock)
            {
                if (_disposed) return;
                if (!_seen.Add(pid)) return;
            }

            var info = new ConsoleSpawnInfo(
                processName: "cmd.exe",
                processId: pid,
                parentProcessName: ParentProcessName,
                parentProcessId: parentPid,
                sessionId: sessionId,
                detectedVia: detectedVia);

            _logger.Warning($"[ConsoleBypassWatcher] SYSTEM console detected: cmd.exe (PID={pid}) " +
                $"parent winlogon.exe (PID={parentPid}), session={sessionId}, via={detectedVia} — possible Shift+F10");

            try { BypassConsoleDetected?.Invoke(this, info); }
            catch (Exception ex) { _logger.Warning($"[ConsoleBypassWatcher] handler threw: {ex.Message}"); }
        }

        private static string? DefaultResolveParentName(int parentPid)
        {
            using var proc = Process.GetProcessById(parentPid);
            return proc.ProcessName; // "winlogon" (no extension)
        }

        private static int ToInt(object? value)
        {
            if (value == null) return 0;
            try { return Convert.ToInt32(value); }
            catch { return 0; }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
            }

            if (_startWatcher != null)
            {
                try { _startWatcher.EventArrived -= OnProcessStartTrace; } catch { }
                try { _startWatcher.Stop(); } catch { }
                try { _startWatcher.Dispose(); } catch { }
                _startWatcher = null;
            }
        }
    }

    /// <summary>Immutable detail record for a detected bypass console spawn.</summary>
    public sealed class ConsoleSpawnInfo
    {
        public ConsoleSpawnInfo(string processName, int processId, string parentProcessName,
            int parentProcessId, int sessionId, string detectedVia)
        {
            ProcessName = processName;
            ProcessId = processId;
            ParentProcessName = parentProcessName;
            ParentProcessId = parentProcessId;
            SessionId = sessionId;
            DetectedVia = detectedVia;
        }

        public string ProcessName { get; }
        public int ProcessId { get; }
        public string ParentProcessName { get; }
        public int ParentProcessId { get; }
        public int SessionId { get; }
        public string DetectedVia { get; }
    }
}

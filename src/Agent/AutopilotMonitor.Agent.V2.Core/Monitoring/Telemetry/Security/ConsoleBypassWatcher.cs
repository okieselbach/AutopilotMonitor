#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Security
{
    /// <summary>
    /// Live watcher for an interactive console opened during enrollment — the classic <b>Shift+F10</b>
    /// OOBE bypass (a <c>cmd.exe</c> a human can type into, opened before the real desktop exists).
    /// <para>
    /// <b>Discriminator (no parent dependency).</b> Field experience (session f240a555, Win11 24H2)
    /// showed the Shift+F10 cmd is NOT a child of a long-lived <c>winlogon.exe</c> — its parent is a
    /// short-lived launcher that exits within milliseconds, so resolving the parent name races and the
    /// parent is not winlogon anyway. Two signals that do NOT depend on the parent:
    /// <list type="bullet">
    ///   <item><b><see cref="ManagementEventWatcher"/> trace gives <c>SessionID</c> race-free</b> — an
    ///     interactive console runs in an interactive session (≠ 0); Intune/IME install <c>cmd /c</c>
    ///     run as SYSTEM in session 0. So <c>SessionID == 0</c> is dropped without even probing.</item>
    ///   <item><b>The console's own command line</b> (queried for the just-started PID): a bare
    ///     <c>cmd.exe</c> with no <c>/c</c>/<c>/k</c> script argument is an interactive shell; a
    ///     <c>cmd /c "..."</c> is a scripted invocation (install/script) and is ignored.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>A process id is not an identity.</b> WMI delivers the start trace in batches about once a
    /// second, so the probe runs up to a second after the start. By then a scripted <c>cmd /c</c> has
    /// usually exited, and Windows may have given its id to another process. A start whose process is
    /// gone, or whose id now belongs to a different process (other image name, session 0, other
    /// parent), is not a console someone types into: it is counted in
    /// <see cref="ConsoleScriptActivityTally"/> and reported as one summary, never as a console.
    /// The comparison uses no clock on purpose — trace time and process start time need not follow
    /// the same clock corrections during OOBE, and a wrong tolerance would count real consoles away.
    /// </para>
    /// <para>
    /// <b>Lifecycle.</b> The host stops this watcher once the real-user desktop has arrived (wired in
    /// the composition root): from that point Shift+F10 is no longer possible, so any later cmd is an
    /// ordinary user action — stopping avoids false positives over the agent's remaining lifetime.
    /// The pre-agent OOBE window is covered separately by <see cref="Analyzers.ConsolePrefetchScanner"/>.
    /// </para>
    /// </summary>
    public sealed class ConsoleBypassWatcher : IDisposable
    {
        /// <summary>
        /// Console executables watched. v1 watches only <c>cmd.exe</c> (the exact Shift+F10 spawn); the
        /// list is the single seam to broaden later (e.g. <c>powershell.exe</c>) — both the WQL trace
        /// filter and the startup probe are built from it (L20).
        /// </summary>
        internal static readonly string[] WatchedProcessNames = { "cmd.exe" };

        // cmd.exe honors only the FIRST of /c or /k; everything after that switch is the command
        // payload. /c turns the shell into a one-shot scripted invocation (install/script) — ignored.
        // /k runs its command and then STAYS interactive (L12) — unlike /c this leaves a shell a
        // human can type into (e.g. a technician-planted `cmd /k whoami`), so it is surfaced as a
        // low-confidence interactive console instead of being silently ignored. One ordered scan over
        // the ARGUMENTS (executable path stripped first) finds the first switch token, so combined
        // switches like /d/q/c are caught and a `/c` inside a /k payload (`cmd /k rem /c`) can no
        // longer demote an interactive shell to a scripted one.
        private static readonly Regex FirstRunSwitchRegex =
            new Regex(@"/(?<sw>[ck])\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private enum RunSwitch { None, RunAndExit, RunAndStay }

        /// <summary>The first <c>/c</c> or <c>/k</c> switch in the argument tail — cmd.exe precedence.</summary>
        private static RunSwitch FirstRunSwitch(string arguments)
        {
            var m = FirstRunSwitchRegex.Match(arguments);
            if (!m.Success) return RunSwitch.None;
            return char.ToLowerInvariant(m.Groups["sw"].Value[0]) == 'c' ? RunSwitch.RunAndExit : RunSwitch.RunAndStay;
        }

        private readonly AgentLogger _logger;
        private readonly Func<int, string, ProcessProbe?> _processProbe;
        private readonly Func<int, bool> _isAlive;
        private readonly Func<int, string?> _parentName;
        private readonly Func<DateTime> _utcNow;
        private readonly ConsoleScriptActivityTally _tally = new ConsoleScriptActivityTally();
        private readonly object _lock = new object();
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        private ManagementEventWatcher? _startWatcher;
        private bool _started;
        private bool _disposed;

        /// <summary>
        /// Raised once per process when a watched console is classified as an interactive console
        /// (high confidence) or as one that stays open or cannot be classified (low confidence). The
        /// host translates this into a Warning <c>oobe_console_spawned</c> event.
        /// </summary>
        public event EventHandler<ConsoleSpawnInfo>? BypassConsoleDetected;

        /// <summary>
        /// Raised when a short-lived start is counted into an empty tally. The host opens its
        /// reporting window on it and collects the content with <see cref="TakeScriptActivity"/>.
        /// </summary>
        internal event EventHandler? ScriptActivityStarted;

        /// <summary>
        /// Raised when the WMI <c>Win32_ProcessStartTrace</c> watcher fails to arm — the LIVE detector
        /// is dead and only the one-shot startup probe ran. The host surfaces this as a
        /// <c>collector_degraded</c> event so the backend can tell "no console seen" from "detector
        /// never started".
        /// </summary>
        public event EventHandler<Exception>? WatcherArmFailed;

        /// <param name="processProbe">Reads a live process by id; the second argument is the image
        /// name the trace announced. Returns null when the process has exited or the id belongs to
        /// another program. Defaults to a process-list check followed by one WMI row.</param>
        /// <param name="isAlive">Whether a process id still runs. Defaults to the process list.</param>
        /// <param name="parentName">Image file name of a process, or null when it cannot be read.</param>
        /// <param name="utcNow">Clock for the timestamps of counted starts.</param>
        public ConsoleBypassWatcher(
            AgentLogger logger,
            Func<int, string, ProcessProbe?>? processProbe = null,
            Func<int, bool>? isAlive = null,
            Func<int, string?>? parentName = null,
            Func<DateTime>? utcNow = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _processProbe = processProbe ?? DefaultProbe;
            _isAlive = isAlive ?? IsRunning;
            _parentName = parentName ?? ImageFileName;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_started || _disposed) return;
                _started = true;
            }

            // L13: both the trace arm and the startup probe are synchronous WMI operations, and
            // WinMgmt can hang during OOBE (documented failure mode for GetOwner in this repo).
            // Running them inline stalled the orchestrator's sequential host-start loop and
            // delayed every host after this one — so they run on a background thread. Exceptions
            // are contained in StartCore; a disposal racing the background start is handled by
            // the _disposed re-checks inside.
            Task.Run(() => StartCore());
        }

        private void StartCore()
        {
            // 1) ETW-backed push on console creation — armed FIRST so a console spawned during the
            //    startup snapshot below is still caught by the live trace. The _seen set dedups the
            //    overlap (a console that starts mid-arming is seen by both). Arming after the snapshot
            //    would leave a blind gap for a fast cmd that starts between snapshot and arm.
            try
            {
                var nameFilter = string.Join(" OR ",
                    Array.ConvertAll(WatchedProcessNames, n => $"ProcessName = '{n}'"));
                var query = new WqlEventQuery(
                    $"SELECT * FROM Win32_ProcessStartTrace WHERE {nameFilter}");
                var watcher = new ManagementEventWatcher(query);
                watcher.EventArrived += OnProcessStartTrace;

                lock (_lock)
                {
                    if (_disposed)
                    {
                        // Dispose won the race against the background start — don't arm.
                        try { watcher.EventArrived -= OnProcessStartTrace; } catch { }
                        try { watcher.Dispose(); } catch { }
                        return;
                    }
                    _startWatcher = watcher;
                }

                watcher.Start();

                // Re-check after arming: Dispose() may have run between the lock release above and
                // Start() — it then tore down a not-yet-armed watcher and this thread armed it right
                // back, leaving a WMI subscription no code path could ever reach again. If disposal
                // won that race, tear the watcher down here.
                bool disposedDuringArm;
                lock (_lock)
                {
                    disposedDuringArm = _disposed;
                    if (disposedDuringArm) _startWatcher = null;
                }
                if (disposedDuringArm)
                {
                    TearDownStartTrace(watcher);
                    return;
                }

                _logger.Info($"[ConsoleBypassWatcher] watching Win32_ProcessStartTrace for " +
                    $"{string.Join(", ", WatchedProcessNames)} (interactive-session + bare command line)");
            }
            catch (Exception ex)
            {
                _logger.Warning($"[ConsoleBypassWatcher] could not start WMI process-start watcher " +
                    $"({ex.GetType().Name}: {ex.Message}); relying on the startup probe only.");
                lock (_lock) { _startWatcher = null; }
                // Surface the dead live detector so the backend can distinguish it from a clean run.
                var handler = WatcherArmFailed;
                try { handler?.Invoke(this, ex); }
                catch (Exception handlerEx)
                {
                    // Never throw from Start.
                    _logger.Warning($"[ConsoleBypassWatcher] WatcherArmFailed handler threw: {handlerEx.Message}");
                }
            }

            // 2) Startup probe — catch a console already open before the watcher armed. One query
            //    carries everything the classification needs; Handle is selected because the owner
            //    is a WMI method call on the row. Runs AFTER the arm so there is no gap; overlap
            //    with the live trace is deduped by _seen.
            try
            {
                var nameFilter = string.Join(" OR ",
                    Array.ConvertAll(WatchedProcessNames, n => $"Name = '{n}'"));
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Handle, Name, ProcessId, ParentProcessId, SessionId, CommandLine, CreationDate " +
                    $"FROM Win32_Process WHERE {nameFilter}");
                using var results = searcher.Get();
                foreach (ManagementObject mo in results)
                {
                    using (mo)
                    {
                        var sessionId = ToInt(mo["SessionId"]);
                        var commandLine = mo["CommandLine"]?.ToString();
                        // The owner costs a method call: only for a row that will be reported.
                        if (Classify(sessionId, commandLine) == ConsoleVerdict.Ignore) continue;

                        var name = mo["Name"]?.ToString() ?? WatchedProcessNames[0];
                        var parentPid = ToInt(mo["ParentProcessId"]);
                        HandleProbed(
                            ToInt(mo["ProcessId"]), parentPid, sessionId, ProcessOwnerLookup.OwnerSid(mo), "startup_probe", name,
                            new ProcessProbe(name, sessionId, parentPid, commandLine, mo["CreationDate"]?.ToString()));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"[ConsoleBypassWatcher] startup probe failed: {ex.Message}");
            }
        }

        private void OnProcessStartTrace(object sender, EventArrivedEventArgs e)
        {
            try
            {
                var pid = ToInt(e.NewEvent?["ProcessID"]);
                var parentPid = ToInt(e.NewEvent?["ParentProcessID"]);
                var sessionId = ToInt(e.NewEvent?["SessionID"]);
                var ownerSid = TryReadSid(e.NewEvent?["Sid"]); // race-free owner from the trace
                var processName = e.NewEvent?["ProcessName"]?.ToString() ?? WatchedProcessNames[0];
                HandleStart(pid, parentPid, sessionId, ownerSid, "process_start_trace", processName);
            }
            catch (Exception ex)
            {
                _logger.Debug($"[ConsoleBypassWatcher] start-trace handler error: {ex.Message}");
            }
        }

        /// <summary>
        /// One traced start (internal for tests). Gates race-free on <paramref name="sessionId"/>,
        /// then probes the process: a start that is gone or whose id belongs to another process is
        /// counted, everything else goes through <see cref="HandleProbed"/>.
        /// </summary>
        internal void HandleStart(int pid, int parentPid, int sessionId, string? ownerSid, string detectedVia, string? processName = null)
        {
            if (pid <= 0) return;
            lock (_lock) { if (_disposed) return; }

            // Race-free gate: an interactive console is never in the service session. This drops the
            // bulk of legitimate IME/Intune install cmd (SYSTEM, session 0) without even probing.
            if (sessionId == 0) return;

            var name = processName ?? WatchedProcessNames[0];
            var probe = _processProbe(pid, name);
            if (probe == null || !IsTracedProcess(probe, name, parentPid))
            {
                CountShortLived(name, pid, parentPid, sessionId, ownerSid);
                return;
            }

            HandleProbed(pid, parentPid, sessionId, ownerSid, detectedVia, name, probe);
        }

        /// <summary>
        /// Classification of a process that was read while it ran (internal for tests). Raises
        /// <see cref="BypassConsoleDetected"/> once per process for an interactive console (bare
        /// command line → high confidence), one that stays open after its command (<c>/k</c> → low)
        /// or a running one whose command line WMI does not return (low). A scripted <c>cmd /c</c>
        /// is ignored.
        /// </summary>
        internal void HandleProbed(int pid, int parentPid, int sessionId, string? ownerSid, string detectedVia, string processName, ProcessProbe probe)
        {
            lock (_lock) { if (_disposed) return; }

            // A row without a command line is only a console nobody could classify while the
            // process still runs; one that ended in the meantime is a short-lived start.
            if (probe.CommandLine == null && !_isAlive(pid))
            {
                CountShortLived(processName, pid, parentPid, sessionId, ownerSid);
                return;
            }

            var verdict = Classify(sessionId, probe.CommandLine);
            if (verdict == ConsoleVerdict.Ignore) return; // scripted invocation (cmd /c ...)

            // Dedup only on a flagged hit, so the startup probe + live trace observing the same console
            // collapse to one detection, and an ignored start never pre-empts a later real one. The
            // creation time is part of the key: a later console may be given an id seen before.
            lock (_lock)
            {
                if (_disposed) return;
                if (!_seen.Add(pid + "|" + probe.CreationDate)) return;
            }

            bool high = verdict == ConsoleVerdict.InteractiveConsole;
            string classification;
            switch (verdict)
            {
                case ConsoleVerdict.InteractiveConsole: classification = "interactive_console"; break;
                case ConsoleVerdict.InteractiveWithCommand: classification = "interactive_console_with_command"; break;
                default: classification = "interactive_session_unclassified"; break;
            }
            var info = new ConsoleSpawnInfo(
                processName: processName,
                processId: pid,
                parentProcessId: parentPid,
                sessionId: sessionId,
                owner: ownerSid,
                commandLine: probe.CommandLine,
                confidence: high ? "high" : "low",
                classification: classification,
                detectedVia: detectedVia,
                parentProcessName: SafeParentName(parentPid));

            _logger.Warning($"[ConsoleBypassWatcher] interactive console detected: {info.ProcessName} (PID={pid}) " +
                $"session={sessionId}, confidence={info.Confidence}, via={detectedVia} — possible Shift+F10");

            try { BypassConsoleDetected?.Invoke(this, info); }
            catch (Exception ex) { _logger.Warning($"[ConsoleBypassWatcher] handler threw: {ex.Message}"); }
        }

        /// <summary>
        /// The counted short-lived starts since the last call, or null when there were none. The
        /// host calls it when its window ends and when it stops.
        /// </summary>
        internal ConsoleScriptActivity? TakeScriptActivity() => _tally.Take();

        private void CountShortLived(string processName, int pid, int parentPid, int sessionId, string? ownerSid)
        {
            var opened = _tally.Add(processName, pid, parentPid, sessionId, ownerSid, _utcNow(), SafeParentName);
            _logger.Trace($"[ConsoleBypassWatcher] short-lived {processName} counted (PID={pid}, parent={parentPid}, session={sessionId})");
            if (!opened) return;

            try { ScriptActivityStarted?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.Debug($"[ConsoleBypassWatcher] script-activity handler threw: {ex.Message}"); }
        }

        private string? SafeParentName(int parentPid)
        {
            if (parentPid <= 0) return null;
            try { return _parentName(parentPid); }
            catch (Exception) { return null; }
        }

        internal enum ConsoleVerdict { Ignore, InteractiveConsole, UnclassifiedInteractive, InteractiveWithCommand }

        /// <summary>
        /// Pure classification (internal for tests): session-0 → Ignore; a scripted <c>cmd /c</c> →
        /// Ignore; <c>cmd /k</c> (runs its command, then STAYS interactive — L12) →
        /// InteractiveWithCommand (low); a bare command line → InteractiveConsole (high); a running
        /// process without a readable command line in an interactive session →
        /// UnclassifiedInteractive (low).
        /// </summary>
        internal static ConsoleVerdict Classify(int sessionId, string? commandLine)
        {
            if (sessionId == 0) return ConsoleVerdict.Ignore;
            if (commandLine == null) return ConsoleVerdict.UnclassifiedInteractive;
            switch (FirstRunSwitch(StripExecutable(commandLine)))
            {
                case RunSwitch.RunAndExit: return ConsoleVerdict.Ignore;
                case RunSwitch.RunAndStay: return ConsoleVerdict.InteractiveWithCommand;
                default: return ConsoleVerdict.InteractiveConsole;
            }
        }

        /// <summary>
        /// Whether a probed row is still the process the trace announced (internal for tests): same
        /// image name, not the service session, and the same parent where both sides know it.
        /// </summary>
        internal static bool IsTracedProcess(ProcessProbe probe, string processName, int parentPid)
        {
            if (!string.Equals(probe.Name, processName, StringComparison.OrdinalIgnoreCase)) return false;
            if (probe.SessionId == 0) return false;
            return parentPid <= 0 || probe.ParentProcessId <= 0 || probe.ParentProcessId == parentPid;
        }

        /// <summary>Returns the command line with the leading executable token (quoted or
        /// whitespace-delimited) removed, so switch detection only inspects the arguments.</summary>
        internal static string StripExecutable(string commandLine)
        {
            var s = commandLine.TrimStart();
            if (s.Length == 0) return string.Empty;
            if (s[0] == '"')
            {
                int end = s.IndexOf('"', 1);
                return end >= 0 ? s.Substring(end + 1) : string.Empty;
            }
            int space = s.IndexOf(' ');
            return space >= 0 ? s.Substring(space + 1) : string.Empty;
        }

        private static ProcessProbe? DefaultProbe(int pid, string processName)
        {
            // The process list first: a WMI query takes a few hundred milliseconds, and in a burst
            // almost every traced process has already exited. Queried one after the other on the
            // delivery thread they would pile up, and a real console closed during the backlog
            // would be counted as a script.
            if (!IsRunningAs(pid, processName)) return null;

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT Name, SessionId, ParentProcessId, CommandLine, CreationDate FROM Win32_Process WHERE ProcessId = {pid}");
                using var results = searcher.Get();
                foreach (ManagementObject mo in results)
                {
                    using (mo)
                    {
                        return new ProcessProbe(
                            mo["Name"]?.ToString(),
                            ToInt(mo["SessionId"]),
                            ToInt(mo["ParentProcessId"]),
                            mo["CommandLine"]?.ToString(),
                            mo["CreationDate"]?.ToString());
                    }
                }
            }
            catch (Exception)
            {
                // Process gone or query failed — the same as exited.
            }
            return null;
        }

        private static bool IsRunningAs(int pid, string processName)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return string.Equals(process.ProcessName, Path.GetFileNameWithoutExtension(processName), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false; // GetProcessById throws for an id that no longer runs
            }
        }

        private static bool IsRunning(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string? ImageFileName(int pid)
        {
            var path = ProcessImagePath.Query(pid);
            return string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);
        }

        private static string? TryReadSid(object? sidValue)
        {
            try
            {
                if (sidValue is byte[] bytes && bytes.Length > 0)
                    return new SecurityIdentifier(bytes, 0).Value;
            }
            catch { /* best-effort enrichment */ }
            return null;
        }

        private static int ToInt(object? value)
        {
            if (value == null) return 0;
            try { return Convert.ToInt32(value); }
            catch { return 0; }
        }

        private void TearDownStartTrace(ManagementEventWatcher watcher)
        {
            try { watcher.EventArrived -= OnProcessStartTrace; } catch { }
            try { watcher.Stop(); } catch { }
            try { watcher.Dispose(); } catch { }
        }

        public void Dispose()
        {
            ManagementEventWatcher? startWatcher;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                startWatcher = _startWatcher;
                _startWatcher = null;
            }

            if (startWatcher != null)
            {
                // Cancelling a WMI subscription can take tens of seconds on a busy WinMgmt. The caller
                // is the agent's stop path, so it never waits for it.
                var armed = startWatcher;
                Task.Run(() => TearDownStartTrace(armed));
            }
        }
    }

    /// <summary>
    /// What was read about a running process: the facts that tell whether it is still the process
    /// a start trace announced, and its command line. Any text field may be null.
    /// </summary>
    public sealed class ProcessProbe
    {
        public ProcessProbe(string? name, int sessionId, int parentProcessId, string? commandLine, string? creationDate)
        {
            Name = name;
            SessionId = sessionId;
            ParentProcessId = parentProcessId;
            CommandLine = commandLine;
            CreationDate = creationDate;
        }

        public string? Name { get; }
        public int SessionId { get; }
        public int ParentProcessId { get; }
        public string? CommandLine { get; }
        /// <summary>The raw WMI creation time; only compared for equality.</summary>
        public string? CreationDate { get; }
    }

    /// <summary>Immutable detail record for a detected interactive console spawn.</summary>
    public sealed class ConsoleSpawnInfo
    {
        public ConsoleSpawnInfo(string processName, int processId, int parentProcessId, int sessionId,
            string? owner, string? commandLine, string confidence, string classification, string detectedVia,
            string? parentProcessName)
        {
            ProcessName = processName;
            ProcessId = processId;
            ParentProcessId = parentProcessId;
            SessionId = sessionId;
            Owner = owner;
            CommandLine = commandLine;
            Confidence = confidence;
            Classification = classification;
            DetectedVia = detectedVia;
            ParentProcessName = parentProcessName;
        }

        public string ProcessName { get; }
        public int ProcessId { get; }
        public int ParentProcessId { get; }
        public int SessionId { get; }
        /// <summary>Owner SID, or null when it could not be read.</summary>
        public string? Owner { get; }
        public string? CommandLine { get; }
        /// <summary>"high" (bare interactive shell) or "low" (stays open after a command, or not classifiable).</summary>
        public string Confidence { get; }
        public string Classification { get; }
        public string DetectedVia { get; }
        /// <summary>
        /// Image name of the parent while it still ran, else null. The launcher behind Shift+F10
        /// exits within milliseconds, so null is the expected value for that case.
        /// </summary>
        public string? ParentProcessName { get; }
    }
}

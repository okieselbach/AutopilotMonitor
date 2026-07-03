#nullable enable
using System;
using System.Collections.Generic;
using System.Management;
using System.Security.Principal;
using System.Text.RegularExpressions;
using AutopilotMonitor.Agent.V2.Core.Logging;

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
    /// <b>Instant-close fallback.</b> Reading the command line still races if the process exits before
    /// the probe. In that case (<c>SessionID ≠ 0</c> but command line unreadable) the console is still
    /// surfaced, tagged <c>confidence:"low"</c> — better than missing it. A truly race-free capture of
    /// the command line would require the ETW Kernel-Process provider (deferred).
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

        // /c turns the shell into a one-shot scripted invocation (install/script) — ignored. Matched
        // as a token anywhere in the ARGUMENTS (the executable path is stripped first) so combined
        // switches like /d/q/c are caught too.
        private static readonly Regex RunAndExitSwitchRegex =
            new Regex(@"/c\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // /k runs its command and then STAYS interactive (L12) — unlike /c this leaves a shell a
        // human can type into (e.g. a technician-planted `cmd /k whoami`), so it is surfaced as a
        // low-confidence interactive console instead of being silently ignored.
        private static readonly Regex RunAndStaySwitchRegex =
            new Regex(@"/k\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly AgentLogger _logger;
        private readonly Func<int, ProcessProbe?> _processProbe;
        private readonly object _lock = new object();
        private readonly HashSet<int> _seen = new HashSet<int>();

        private ManagementEventWatcher? _startWatcher;
        private bool _started;
        private bool _disposed;

        /// <summary>
        /// Raised once per process when a watched console is classified as an interactive console
        /// (high confidence) or an unclassifiable interactive-session console (low confidence). The
        /// host translates this into a Warning <c>oobe_console_spawned</c> event.
        /// </summary>
        public event EventHandler<ConsoleSpawnInfo>? BypassConsoleDetected;

        /// <summary>
        /// Raised when the WMI <c>Win32_ProcessStartTrace</c> watcher fails to arm — the LIVE detector
        /// is dead and only the one-shot startup probe ran. The host surfaces this as a
        /// <c>collector_degraded</c> event so the backend can tell "no console seen" from "detector
        /// never started".
        /// </summary>
        public event EventHandler<Exception>? WatcherArmFailed;

        /// <param name="processProbe">Resolves the command line + owner for a live PID; returns null
        /// when the process has already exited (the instant-close race). Defaults to a WMI query.</param>
        public ConsoleBypassWatcher(AgentLogger logger, Func<int, ProcessProbe?>? processProbe = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _processProbe = processProbe ?? DefaultProbe;
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
            System.Threading.Tasks.Task.Run(() => StartCore());
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
                try { handler?.Invoke(this, ex); } catch { /* never throw from Start */ }
            }

            // 2) Startup probe — catch a console already open before the watcher armed. Win32_Process
            //    exposes SessionId directly, so the same classification path is reused. Runs AFTER the
            //    arm so there is no gap; overlap with the live trace is deduped by _seen.
            try
            {
                var nameFilter = string.Join(" OR ",
                    Array.ConvertAll(WatchedProcessNames, n => $"Name = '{n}'"));
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT Name, ProcessId, ParentProcessId, SessionId FROM Win32_Process WHERE {nameFilter}");
                using var results = searcher.Get();
                foreach (var mo in results)
                {
                    using (mo)
                    {
                        HandleStart(
                            ToInt(mo["ProcessId"]),
                            ToInt(mo["ParentProcessId"]),
                            ToInt(mo["SessionId"]),
                            ownerSidHint: null,
                            detectedVia: "startup_probe",
                            processName: mo["Name"]?.ToString() ?? WatchedProcessNames[0]);
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
        /// Classification core (internal for tests). Gates race-free on <paramref name="sessionId"/>,
        /// then probes the live process for its command line; raises <see cref="BypassConsoleDetected"/>
        /// once per PID for an interactive console (bare command line → high confidence) or an
        /// unreadable interactive-session console (instant-close → low confidence). Scripted
        /// invocations (<c>cmd /c</c>) and session-0 (service) cmd are ignored.
        /// </summary>
        internal void HandleStart(int pid, int parentPid, int sessionId, string? ownerSidHint, string detectedVia, string? processName = null)
        {
            if (pid <= 0) return;
            lock (_lock) { if (_disposed) return; }

            // Race-free gate: an interactive console is never in the service session. This drops the
            // bulk of legitimate IME/Intune install cmd (SYSTEM, session 0) without even probing.
            if (sessionId == 0) return;

            var probe = _processProbe(pid); // null when the process already exited (instant-close)
            var verdict = Classify(sessionId, probe?.CommandLine);
            if (verdict == ConsoleVerdict.Ignore) return; // scripted invocation (cmd /c ...)

            // Dedup only on a flagged hit, so the startup probe + live trace observing the same console
            // collapse to one detection, and an ignored start never pre-empts a later real one.
            lock (_lock)
            {
                if (_disposed) return;
                if (!_seen.Add(pid)) return;
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
                processName: processName ?? WatchedProcessNames[0],
                processId: pid,
                parentProcessId: parentPid,
                sessionId: sessionId,
                owner: probe?.Owner ?? ownerSidHint,
                commandLine: probe?.CommandLine,
                confidence: high ? "high" : "low",
                classification: classification,
                detectedVia: detectedVia);

            _logger.Warning($"[ConsoleBypassWatcher] interactive console detected: {info.ProcessName} (PID={pid}) " +
                $"session={sessionId}, confidence={info.Confidence}, via={detectedVia} — possible Shift+F10");

            try { BypassConsoleDetected?.Invoke(this, info); }
            catch (Exception ex) { _logger.Warning($"[ConsoleBypassWatcher] handler threw: {ex.Message}"); }
        }

        internal enum ConsoleVerdict { Ignore, InteractiveConsole, UnclassifiedInteractive, InteractiveWithCommand }

        /// <summary>
        /// Pure classification (internal for tests): session-0 → Ignore; a scripted <c>cmd /c</c> →
        /// Ignore; <c>cmd /k</c> (runs its command, then STAYS interactive — L12) →
        /// InteractiveWithCommand (low); a bare command line → InteractiveConsole (high); an
        /// unreadable command line in an interactive session → UnclassifiedInteractive (low, the
        /// instant-close fallback).
        /// </summary>
        internal static ConsoleVerdict Classify(int sessionId, string? commandLine)
        {
            if (sessionId == 0) return ConsoleVerdict.Ignore;
            if (commandLine == null) return ConsoleVerdict.UnclassifiedInteractive;
            var arguments = StripExecutable(commandLine);
            if (RunAndExitSwitchRegex.IsMatch(arguments)) return ConsoleVerdict.Ignore;
            if (RunAndStaySwitchRegex.IsMatch(arguments)) return ConsoleVerdict.InteractiveWithCommand;
            return ConsoleVerdict.InteractiveConsole;
        }

        /// <summary>True when the command line carries a <c>/c</c> run-and-exit switch (a scripted
        /// invocation), false for a bare interactive shell or a <c>/k</c> run-and-stay shell. The
        /// executable token is stripped first so a path can never spoof a switch, and switches may
        /// be combined (e.g. <c>cmd /d/q/c exit 9</c>).</summary>
        internal static bool HasScriptArgument(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return false;
            return RunAndExitSwitchRegex.IsMatch(StripExecutable(commandLine));
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

        private static ProcessProbe? DefaultProbe(int pid)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
                using var results = searcher.Get();
                foreach (ManagementObject mo in results)
                {
                    using (mo)
                    {
                        var commandLine = mo["CommandLine"]?.ToString();
                        return new ProcessProbe(commandLine, TryGetOwner(mo));
                    }
                }
            }
            catch (Exception)
            {
                // Process gone or query failed — treated as the instant-close fallback (low confidence).
            }
            return null;
        }

        private static string? TryGetOwner(ManagementObject process)
        {
            try
            {
                var outParams = process.InvokeMethod("GetOwner", null, null);
                if (outParams != null && Convert.ToInt32(outParams["ReturnValue"]) == 0)
                {
                    var domain = outParams["Domain"]?.ToString();
                    var user = outParams["User"]?.ToString();
                    if (!string.IsNullOrEmpty(user))
                        return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
                }
            }
            catch { /* best-effort enrichment */ }
            return null;
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

    /// <summary>Command line + owner for a probed PID; either may be null (best-effort).</summary>
    public sealed class ProcessProbe
    {
        public ProcessProbe(string? commandLine, string? owner)
        {
            CommandLine = commandLine;
            Owner = owner;
        }

        public string? CommandLine { get; }
        public string? Owner { get; }
    }

    /// <summary>Immutable detail record for a detected interactive console spawn.</summary>
    public sealed class ConsoleSpawnInfo
    {
        public ConsoleSpawnInfo(string processName, int processId, int parentProcessId, int sessionId,
            string? owner, string? commandLine, string confidence, string classification, string detectedVia)
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
        }

        public string ProcessName { get; }
        public int ProcessId { get; }
        public int ParentProcessId { get; }
        public int SessionId { get; }
        public string? Owner { get; }
        public string? CommandLine { get; }
        /// <summary>"high" (bare interactive shell) or "low" (interactive session, command line unreadable).</summary>
        public string Confidence { get; }
        public string Classification { get; }
        public string DetectedVia { get; }
    }
}

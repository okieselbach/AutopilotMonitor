using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// Watches for IntuneManagementExtension.exe process exit.
    /// Discovers the process (retry every 5s), then attaches Process.Exited —
    /// no continuous polling while attached. Emits ime_process_exited when the process terminates,
    /// then re-arms discovery so an IME restart is attached again and a later crash is reported too.
    /// <para>
    /// Identity is established by <see cref="ImeProcessIdentity"/> (session 0 + image under the IME
    /// install root), not by process name alone: a standard user can run any binary renamed to
    /// IntuneManagementExtension.exe during AccountSetup, and attaching to it would forge a crash
    /// Warning and mute the real signal (CWE-807). Untrusted name matches are logged once per PID
    /// and never attached.
    /// </para>
    /// </summary>
    public class ImeProcessWatcher : IDisposable
    {
        private const string ImeProcessName = "IntuneManagementExtension";
        private const int DiscoveryIntervalSeconds = 5;

        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;
        private readonly IReadOnlyList<string> _trustedRoots;
        private readonly HashSet<int> _rejectedPids = new HashSet<int>();

        private Timer _discoveryTimer;
        private Process _imeProcess;
        private bool _disposed;
        private readonly object _lock = new object();

        /// <summary>Test seam: enumerates the name-matched processes (default: Process.GetProcessesByName).</summary>
        internal Func<Process[]> ProcessSource { get; set; } = () => Process.GetProcessesByName(ImeProcessName);

        /// <summary>Test seam: reads a candidate's identity facts (default: <see cref="ImeProcessIdentity.Probe"/>).</summary>
        internal Func<Process, ImeProcessCandidate> IdentityProbe { get; set; } = ImeProcessIdentity.Probe;

        public ImeProcessWatcher(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            IReadOnlyList<string> trustedRoots = null)
        {
            _sessionId = sessionId;
            _tenantId = tenantId;
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _trustedRoots = trustedRoots ?? ImeProcessIdentity.DefaultTrustedRoots();
        }

        /// <summary>PID of the attached process, or null while discovering.</summary>
        internal int? AttachedProcessId
        {
            get { lock (_lock) { return _imeProcess?.Id; } }
        }

        public void Start()
        {
            _logger.Info($"ImeProcessWatcher: starting (discovery every {DiscoveryIntervalSeconds}s until {ImeProcessName}.exe is found; trusted roots: {string.Join("; ", _trustedRoots)})");
            _discoveryTimer = new Timer(TryAttach, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(DiscoveryIntervalSeconds));
        }

        /// <summary>Runs one discovery pass synchronously (tests).</summary>
        internal void TryAttachNow() => TryAttach(null);

        /// <summary>
        /// Tries to find and attach to the real IntuneManagementExtension.exe.
        /// Stops the discovery timer once attached — switches to pure event-driven from there.
        /// </summary>
        private void TryAttach(object state)
        {
            lock (_lock)
            {
                if (_disposed || _imeProcess != null)
                    return;

                Process[] candidates = null;
                Process chosen = null;
                try
                {
                    candidates = ProcessSource() ?? Array.Empty<Process>();
                    if (candidates.Length == 0)
                        return;

                    var facts = new List<ImeProcessCandidate>(candidates.Length);
                    foreach (var p in candidates)
                    {
                        var c = IdentityProbe(p);
                        if (ImeProcessIdentity.IsTrusted(c, _trustedRoots))
                        {
                            facts.Add(c);
                        }
                        else if (c.Pid > 0 && _rejectedPids.Add(c.Pid))
                        {
                            // One line per PID: a user-session process carrying the IME name is either
                            // a spoof attempt or a stray tool — either way not the service, never attached.
                            _logger.Warning($"ImeProcessWatcher: ignoring untrusted {ImeProcessName}.exe candidate (PID={c.Pid}, session={c.SessionId}, image={c.ImagePath ?? "<unresolved>"})");
                        }
                    }

                    var preferred = ImeProcessIdentity.SelectPreferred(facts, _trustedRoots);
                    if (preferred == null)
                        return;

                    foreach (var p in candidates)
                    {
                        int pid = -1;
                        try { pid = p.Id; } catch { }
                        if (chosen == null && pid == preferred.Value.Pid)
                            chosen = p;
                        else
                            p.Dispose();
                    }
                    if (chosen == null)
                        return;

                    chosen.EnableRaisingEvents = true;
                    chosen.Exited += OnImeExited;
                    _imeProcess = chosen;

                    // Suspend discovery timer — now event-driven
                    _discoveryTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                    _logger.Info($"ImeProcessWatcher: attached to {ImeProcessName}.exe (PID={chosen.Id}, session={preferred.Value.SessionId}, image={preferred.Value.ImagePath})");

                    // The process may have exited between the probe and the subscription — Exited
                    // fires for an already-exited process only if EnableRaisingEvents was set before
                    // exit; check explicitly so a race does not leave the watcher attached to a corpse.
                    if (chosen.HasExited)
                        ThreadPool.QueueUserWorkItem(_ => OnImeExited(chosen, EventArgs.Empty));
                }
                catch (Exception ex)
                {
                    _logger.Debug($"ImeProcessWatcher: could not attach to {ImeProcessName}.exe: {ex.Message}");

                    if (_imeProcess == null && candidates != null)
                        foreach (var p in candidates)
                            try { p.Dispose(); } catch { }
                }
            }
        }

        private void OnImeExited(object sender, EventArgs e)
        {
            if (!(sender is Process proc))
                return;

            // Detach + re-arm discovery. Only the currently attached process counts — a late
            // Exited from a process already replaced or disposed is ignored (no duplicate event).
            lock (_lock)
            {
                if (_disposed || !ReferenceEquals(proc, _imeProcess))
                    return;
                try { proc.Exited -= OnImeExited; } catch { }
                _imeProcess = null;
                _discoveryTimer?.Change(TimeSpan.FromSeconds(DiscoveryIntervalSeconds), TimeSpan.FromSeconds(DiscoveryIntervalSeconds));
            }

            int pid = -1;
            int exitCode = -1;
            double uptimeSeconds = 0;

            try { pid = proc.Id; } catch { }
            try { exitCode = proc.ExitCode; } catch { }
            try { uptimeSeconds = (proc.ExitTime - proc.StartTime).TotalSeconds; } catch { }
            try { proc.Dispose(); } catch { }

            _logger.Info($"ImeProcessWatcher: {ImeProcessName}.exe exited (PID={pid}, exit={exitCode}, uptime={uptimeSeconds:F0}s) — discovery re-armed");

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                Timestamp = DateTime.UtcNow,
                EventType = Constants.EventTypes.ImeProcessExited,
                Severity = EventSeverity.Warning,
                Source = "ImeProcessWatcher",
                Phase = EnrollmentPhase.Unknown,
                Message = $"{ImeProcessName}.exe exited (PID={pid}, exit code={exitCode})",
                Data = new Dictionary<string, object>
                {
                    { "processName", ImeProcessName },
                    { "pid", pid },
                    { "exitCode", exitCode },
                    { "imeUptimeSeconds", Math.Round(uptimeSeconds, 0) }
                }
            });
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                _discoveryTimer?.Dispose();
                _discoveryTimer = null;

                if (_imeProcess != null)
                {
                    try { _imeProcess.Exited -= OnImeExited; } catch { }
                    _imeProcess.Dispose();
                    _imeProcess = null;
                }
            }
        }
    }
}

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
    /// Discovers the process once (with retry every 5s), then attaches Process.Exited —
    /// no continuous polling after the process is found.
    /// Emits ime_process_exited when the process terminates.
    /// </summary>
    public class ImeProcessWatcher : IDisposable
    {
        private const string ImeProcessName = "IntuneManagementExtension";
        private const int DiscoveryIntervalSeconds = 5;

        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;

        private Timer _discoveryTimer;
        private Process _imeProcess;
        private bool _disposed;
        private readonly object _lock = new object();

        public ImeProcessWatcher(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger)
        {
            _sessionId = sessionId;
            _tenantId = tenantId;
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Start()
        {
            _logger.Info($"ImeProcessWatcher: starting (discovery every {DiscoveryIntervalSeconds}s until {ImeProcessName}.exe is found)");
            _discoveryTimer = new Timer(TryAttach, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(DiscoveryIntervalSeconds));
        }

        /// <summary>
        /// Tries to find and attach to IntuneManagementExtension.exe.
        /// Stops the discovery timer once attached — switches to pure event-driven from there.
        /// </summary>
        private void TryAttach(object state)
        {
            lock (_lock)
            {
                if (_disposed || _imeProcess != null)
                    return;

                Process[] candidates = null;
                try
                {
                    candidates = Process.GetProcessesByName(ImeProcessName);
                    if (candidates.Length == 0)
                        return;

                    var proc = candidates[0];
                    for (int i = 1; i < candidates.Length; i++)
                        candidates[i].Dispose();

                    proc.EnableRaisingEvents = true;
                    proc.Exited += OnImeExited;
                    _imeProcess = proc;

                    // Suspend discovery timer — now event-driven
                    _discoveryTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                    _logger.Info($"ImeProcessWatcher: attached to {ImeProcessName}.exe (PID={proc.Id})");
                }
                catch (Exception ex)
                {
                    _logger.Debug($"ImeProcessWatcher: could not attach to {ImeProcessName}.exe: {ex.Message}");

                    // Dispose any leftover handles from the failed attempt
                    if (candidates != null)
                        foreach (var p in candidates)
                            try { p.Dispose(); } catch { }
                }
            }
        }

        private void OnImeExited(object sender, EventArgs e)
        {
            if (!(sender is Process proc))
                return;

            int pid = -1;
            int exitCode = -1;
            double uptimeSeconds = 0;

            try { pid = proc.Id; } catch { }
            try { exitCode = proc.ExitCode; } catch { }
            try { uptimeSeconds = (proc.ExitTime - proc.StartTime).TotalSeconds; } catch { }

            _logger.Info($"ImeProcessWatcher: {ImeProcessName}.exe exited (PID={pid}, exit={exitCode}, uptime={uptimeSeconds:F0}s)");

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

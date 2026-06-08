#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office
{
    /// <summary>
    /// Event-driven watcher for the Office Click-to-Run worker process (<c>OfficeC2RClient.exe</c>),
    /// the process C2R spawns for an active install/update operation (NOT the always-running
    /// <c>OfficeClickToRun.exe</c> service). Drives the <see cref="OfficeInstallDetector"/> without any
    /// idle polling:
    /// <list type="bullet">
    ///   <item><b>Start</b> — a true ETW-backed push via WMI <c>Win32_ProcessStartTrace</c>
    ///     (<see cref="ManagementEventWatcher"/>), plus a one-shot startup probe so an install already
    ///     in flight when the agent boots (realistic during OOBE) is still caught.</item>
    ///   <item><b>Stop</b> — <see cref="Process.Exited"/> on each tracked worker (clean OS event, same
    ///     pattern as <c>ImeProcessWatcher</c>).</item>
    /// </list>
    /// <para>
    /// <see cref="Started"/> fires once when the worker count transitions 0→1; <see cref="Stopped"/>
    /// once when it transitions back to 0 (multiple short-lived workers during one install collapse to
    /// a single active window). Fail-soft: if the WMI watcher cannot start, it is logged and the
    /// startup probe still covers the already-running case — no polling fallback by design.
    /// </para>
    /// </summary>
    public sealed class OfficeProcessWatcher : IDisposable
    {
        internal const string WorkerProcessName = "OfficeC2RClient.exe";
        private const string WorkerProcessNameNoExt = "OfficeC2RClient";

        private readonly AgentLogger _logger;
        private readonly int _settleSeconds;
        private readonly object _lock = new object();
        private readonly Dictionary<int, Process> _workers = new Dictionary<int, Process>();

        private ManagementEventWatcher? _startWatcher;
        private Timer? _settleTimer;
        private bool _started;
        private bool _disposed;
        private bool _active; // "an install window is open" — stays true across short worker gaps (settle)

        /// <summary>Raised once when the first Office worker appears (install window opens).</summary>
        public event EventHandler? Started;

        /// <summary>
        /// Raised once when the install window closes — i.e. no worker has run for the settle window.
        /// C2R spawns several short-lived OfficeC2RClient.exe processes (with small gaps) during one
        /// install, so the last-worker-exit is debounced to avoid a premature terminal decision.
        /// </summary>
        public event EventHandler? Stopped;

        /// <param name="settleSeconds">Grace period after the last worker exits before the window is
        /// declared closed; a new worker within it keeps the window open. &lt;=0 closes immediately.</param>
        public OfficeProcessWatcher(AgentLogger logger, int settleSeconds = 10)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settleSeconds = settleSeconds;
        }

        /// <summary>True while at least one Office worker is being tracked.</summary>
        public bool IsActive
        {
            get { lock (_lock) { return _active; } }
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_started || _disposed) return;
                _started = true;
            }

            // 1) Startup probe — catch a worker already running before the watcher armed.
            try
            {
                foreach (var proc in Process.GetProcessesByName(WorkerProcessNameNoExt))
                {
                    TrackWorker(proc);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"[OfficeProcessWatcher] startup probe failed: {ex.Message}");
            }

            // 2) ETW-backed push on worker creation — no polling.
            try
            {
                var query = new WqlEventQuery(
                    $"SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName = '{WorkerProcessName}'");
                _startWatcher = new ManagementEventWatcher(query);
                _startWatcher.EventArrived += OnProcessStartTrace;
                _startWatcher.Start();
                _logger.Info($"[OfficeProcessWatcher] watching Win32_ProcessStartTrace for {WorkerProcessName}");
            }
            catch (Exception ex)
            {
                // No polling fallback by design — the startup probe already covers the in-flight case.
                _logger.Warning($"[OfficeProcessWatcher] could not start WMI process-start watcher " +
                    $"({ex.GetType().Name}: {ex.Message}); relying on the startup probe only.");
                _startWatcher = null;
            }
        }

        private void OnProcessStartTrace(object sender, EventArrivedEventArgs e)
        {
            try
            {
                var pidObj = e.NewEvent?["ProcessID"];
                if (pidObj == null) return;
                var pid = Convert.ToInt32(pidObj);

                Process? proc = null;
                try { proc = Process.GetProcessById(pid); }
                catch { /* already gone — ignore */ }
                if (proc != null) TrackWorker(proc);
            }
            catch (Exception ex)
            {
                _logger.Debug($"[OfficeProcessWatcher] start-trace handler error: {ex.Message}");
            }
        }

        private void TrackWorker(Process proc)
        {
            bool raiseStarted = false;
            try
            {
                lock (_lock)
                {
                    if (_disposed) { try { proc.Dispose(); } catch { } return; }
                    if (_workers.ContainsKey(proc.Id)) { try { proc.Dispose(); } catch { } return; }

                    try
                    {
                        proc.EnableRaisingEvents = true;
                        proc.Exited += OnWorkerExited;
                    }
                    catch
                    {
                        // Process exited between discovery and Exited-hookup — drop it.
                        try { proc.Dispose(); } catch { }
                        return;
                    }

                    _workers[proc.Id] = proc;
                    // A worker is back — cancel any pending settle (the window must stay open).
                    CancelSettleTimer();
                    if (!_active)
                    {
                        _active = true;
                        raiseStarted = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"[OfficeProcessWatcher] TrackWorker error: {ex.Message}");
                return;
            }

            if (raiseStarted)
            {
                _logger.Info($"[OfficeProcessWatcher] Office worker started (PID={SafePid(proc)}) — install active");
                RaiseStarted();
            }
        }

        private void OnWorkerExited(object? sender, EventArgs e)
        {
            if (!(sender is Process proc)) return;
            bool raiseStoppedNow = false;

            lock (_lock)
            {
                if (_workers.Remove(SafePid(proc)))
                {
                    try { proc.Exited -= OnWorkerExited; } catch { }
                    try { proc.Dispose(); } catch { }
                }
                if (_active && _workers.Count == 0)
                {
                    if (_settleSeconds <= 0)
                    {
                        _active = false;
                        raiseStoppedNow = true;
                    }
                    else
                    {
                        // Debounce: C2R may start another short-lived worker after a small gap. Only
                        // close the window if none reappears within the settle period.
                        _logger.Debug($"[OfficeProcessWatcher] last worker exited — settling {_settleSeconds}s before closing the install window");
                        StartSettleTimer();
                    }
                }
            }

            if (raiseStoppedNow)
            {
                _logger.Info("[OfficeProcessWatcher] last Office worker exited — install window closed");
                RaiseStopped();
            }
        }

        // Settle-window helpers — all callers hold _lock except the timer callback, which re-locks.
        private void StartSettleTimer()
        {
            _settleTimer?.Dispose();
            _settleTimer = new Timer(OnSettleElapsed, null, TimeSpan.FromSeconds(_settleSeconds), Timeout.InfiniteTimeSpan);
        }

        private void CancelSettleTimer()
        {
            _settleTimer?.Dispose();
            _settleTimer = null;
        }

        private void OnSettleElapsed(object? state)
        {
            bool raiseStopped = false;
            lock (_lock)
            {
                if (_disposed) return;
                _settleTimer?.Dispose();
                _settleTimer = null;
                // A worker may have reappeared during settle (which cancels the timer); re-check.
                if (_active && _workers.Count == 0)
                {
                    _active = false;
                    raiseStopped = true;
                }
            }

            if (raiseStopped)
            {
                _logger.Info($"[OfficeProcessWatcher] no Office worker for {_settleSeconds}s — install window closed");
                RaiseStopped();
            }
        }

        private void RaiseStarted()
        {
            try { Started?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.Warning($"[OfficeProcessWatcher] Started handler threw: {ex.Message}"); }
        }

        private void RaiseStopped()
        {
            try { Stopped?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.Warning($"[OfficeProcessWatcher] Stopped handler threw: {ex.Message}"); }
        }

        private static int SafePid(Process proc)
        {
            try { return proc.Id; } catch { return -1; }
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

            lock (_lock)
            {
                CancelSettleTimer();
                foreach (var kv in _workers)
                {
                    try { kv.Value.Exited -= OnWorkerExited; } catch { }
                    try { kv.Value.Dispose(); } catch { }
                }
                _workers.Clear();
            }
        }
    }
}

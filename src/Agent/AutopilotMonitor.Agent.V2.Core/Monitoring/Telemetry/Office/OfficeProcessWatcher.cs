#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
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
    ///     (<see cref="ManagementEventWatcher"/>), plus a startup probe so an install already
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
    /// <para>
    /// <b>Locking</b>: the lock guards this watcher's own state. No event is raised and no
    /// <see cref="Process"/> is disposed while it is held: .NET holds its own lock on a
    /// <see cref="Process"/> for as long as it delivers <see cref="Process.Exited"/>, that handler
    /// takes this lock, and <see cref="Process.Dispose()"/> waits for the delivery to finish.
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

        /// <summary>Test seam: enumerates the running workers (default: Process.GetProcessesByName).</summary>
        internal Func<Process[]> WorkerSource { get; set; } = () => Process.GetProcessesByName(WorkerProcessNameNoExt);

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

        public void Start() => Start(armWmi: true);

        /// <summary>Test seam: <paramref name="armWmi"/> = false leaves out the WMI subscription.</summary>
        internal void Start(bool armWmi)
        {
            lock (_lock)
            {
                if (_started || _disposed) return;
                _started = true;
            }

            // 1) Startup probe — catch a worker already running before the subscription exists. It
            //    stays on the caller's thread: it needs no WMI, so a worker that is already running is
            //    reported even when the subscription below never completes.
            ProbeRunningWorkers();

            // 2) ETW-backed push on worker creation — no polling. WinMgmt can hang during OOBE, so the
            //    subscription never runs on the caller's thread (the orchestrator starts its hosts one
            //    after another on it).
            if (armWmi) Task.Run(() => ArmStartTrace());
        }

        private void ProbeRunningWorkers()
        {
            try
            {
                foreach (var proc in WorkerSource())
                {
                    TrackWorker(proc);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"[OfficeProcessWatcher] startup probe failed: {ex.Message}");
            }
        }

        private void ArmStartTrace()
        {
            ManagementEventWatcher? watcher = null;
            try
            {
                watcher = new ManagementEventWatcher(new WqlEventQuery(
                    $"SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName = '{WorkerProcessName}'"));
                watcher.EventArrived += OnProcessStartTrace;
                watcher.Start();
            }
            catch (Exception ex)
            {
                // No polling fallback by design — the startup probe already covers the in-flight case.
                _logger.Warning($"[OfficeProcessWatcher] could not start WMI process-start watcher " +
                    $"({ex.GetType().Name}: {ex.Message}); relying on the startup probe only.");
                if (watcher != null) TearDownStartTrace(watcher);
                return;
            }

            // Published only once it is armed, so Dispose never meets a half-started subscription; one
            // that completes after Dispose is taken down right here.
            bool keep;
            lock (_lock)
            {
                keep = !_disposed;
                if (keep) _startWatcher = watcher;
            }
            if (!keep)
            {
                TearDownStartTrace(watcher);
                return;
            }

            _logger.Info($"[OfficeProcessWatcher] watching Win32_ProcessStartTrace for {WorkerProcessName}");

            // A worker may have started between the probe in Start and the subscription.
            ProbeRunningWorkers();
        }

        private void TearDownStartTrace(ManagementEventWatcher watcher)
        {
            try { watcher.EventArrived -= OnProcessStartTrace; } catch { }
            try { watcher.Stop(); } catch { }
            try { watcher.Dispose(); } catch { }
        }

        private void OnProcessStartTrace(object sender, EventArrivedEventArgs e)
        {
            try
            {
                var pidObj = e.NewEvent?["ProcessID"];
                if (pidObj == null) return;
                TrackTracedProcess(Convert.ToInt32(pidObj), WorkerProcessNameNoExt);
            }
            catch (Exception ex)
            {
                _logger.Debug($"[OfficeProcessWatcher] start-trace handler error: {ex.Message}");
            }
        }

        /// <summary>
        /// Tracks the process a start trace announced (internal for tests). WMI delivers the trace
        /// with a delay, so the id may already belong to another process: only a process that still
        /// carries the worker's name is tracked. A foreign process would hold the install window
        /// open until it exits.
        /// </summary>
        internal void TrackTracedProcess(int pid, string processNameNoExt)
        {
            Process? proc = null;
            try { proc = Process.GetProcessById(pid); }
            catch { /* already gone — ignore */ }
            if (proc == null) return;

            bool isWorker;
            try { isWorker = string.Equals(proc.ProcessName, processNameNoExt, StringComparison.OrdinalIgnoreCase); }
            catch { isWorker = false; /* exited between the lookup and the name read */ }

            if (isWorker)
            {
                TrackWorker(proc);
                return;
            }

            try { proc.Dispose(); } catch { }
        }

        private void TrackWorker(Process proc)
        {
            bool tracked = false;
            bool raiseStarted = false;
            try
            {
                lock (_lock)
                {
                    if (!_disposed && !_workers.ContainsKey(proc.Id))
                    {
                        try
                        {
                            proc.EnableRaisingEvents = true;
                            proc.Exited += OnWorkerExited;
                            tracked = true;
                        }
                        catch
                        {
                            // Process exited between discovery and Exited-hookup — drop it.
                        }

                        if (tracked)
                        {
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
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"[OfficeProcessWatcher] TrackWorker error: {ex.Message}");
            }

            if (!tracked)
            {
                // Disposed, already tracked under another handle, or gone before the hookup.
                try { proc.Dispose(); } catch { }
                return;
            }

            if (raiseStarted)
            {
                _logger.Info($"[OfficeProcessWatcher] Office worker started (PID={SafePid(proc)}) — install active");
                RaiseStarted();
            }

            // .NET raises Exited once per Process object. A worker that exited before the hookup
            // above was subscribed would stay tracked forever and the window would never close.
            if (HasExitedSafe(proc))
                ThreadPool.QueueUserWorkItem(_ => OnWorkerExited(proc, EventArgs.Empty));
        }

        private void OnWorkerExited(object? sender, EventArgs e)
        {
            if (!(sender is Process proc)) return;
            bool raiseStoppedNow = false;

            lock (_lock)
            {
                // Only the call that still finds this worker acts: after Dispose nothing is tracked,
                // and TrackWorker's own re-check can deliver the same exit a second time.
                if (_disposed || !_workers.TryGetValue(SafePid(proc), out var tracked) || !ReferenceEquals(tracked, proc))
                    return;
                _workers.Remove(SafePid(proc));

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
                        _logger.Trace($"[OfficeProcessWatcher] last worker exited — settling {_settleSeconds}s before closing the install window");
                        StartSettleTimer();
                    }
                }
            }

            try { proc.Exited -= OnWorkerExited; } catch { }
            try { proc.Dispose(); } catch { }

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

        private static bool HasExitedSafe(Process proc)
        {
            try { return proc.HasExited; } catch { return false; }
        }

        public void Dispose()
        {
            ManagementEventWatcher? startWatcher;
            Process[] workers;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                startWatcher = _startWatcher;
                _startWatcher = null;
                CancelSettleTimer();
                workers = new Process[_workers.Count];
                _workers.Values.CopyTo(workers, 0);
                _workers.Clear();
            }

            if (startWatcher != null)
            {
                // Cancelling a WMI subscription can take tens of seconds on a busy WinMgmt. The caller
                // is the agent's stop path, so it never waits for it.
                var armed = startWatcher;
                Task.Run(() => TearDownStartTrace(armed));
            }

            foreach (var worker in workers)
            {
                try { worker.Exited -= OnWorkerExited; } catch { }
                try { worker.Dispose(); } catch { }
            }
        }
    }
}

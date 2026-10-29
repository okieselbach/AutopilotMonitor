using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry
{
    /// <summary>
    /// Abstract base for interval-based event collectors.
    /// Handles Timer lifecycle (Start/Stop/Dispose) so subclasses only implement Collect().
    /// <para>
    /// Single-rail refactor (plan §5.4): subclasses emit through <see cref="InformationalEventPost"/>
    /// instead of a raw <c>Action&lt;EnrollmentEvent&gt;</c> callback. Every collector-produced event
    /// flows Signal-Ingress → Reducer → EmitEventTimelineEntry effect → EventTimelineEmitter, with
    /// no direct <c>TelemetryEventEmitter.Emit</c> bypass.
    /// </para>
    /// </summary>
    public abstract class CollectorBase : IDisposable
    {
        protected readonly string SessionId;
        protected readonly string TenantId;
        protected readonly InformationalEventPost Post;
        protected readonly AgentLogger Logger;

        private readonly int _intervalSeconds;
        private Timer _timer;
        private bool _disposed;
        // 0 = idle, 1 = a Collect call is in flight. System.Threading.Timer is documented to
        // fire callbacks on different ThreadPool threads when the previous callback exceeds
        // the period — observed in production on stall-recovery (network reinit makes the
        // PerformanceCollector's NetworkInterface.GetAllNetworkInterfaces() call slow enough
        // to overlap the next 30s tick), producing two parallel Collect calls that race on
        // the collector's shared baseline state and emit duplicate snapshots. Using
        // Interlocked here keeps the sample interval honest and prevents ghost samples.
        private int _collecting;

        protected CollectorBase(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            int intervalSeconds)
        {
            SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            TenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            Post = post ?? throw new ArgumentNullException(nameof(post));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _intervalSeconds = intervalSeconds;
        }

        /// <summary>Starts the collection interval timer.</summary>
        public void Start()
        {
            if (_disposed) return;
            OnBeforeStart();
            var interval = TimeSpan.FromSeconds(_intervalSeconds);
            _timer = new Timer(_ => CollectSafe(), null, GetInitialDelay(), interval);
            Logger.Info($"[{GetType().Name}] Started (interval={_intervalSeconds}s)");
        }

        /// <summary>Stops the collection timer without disposing the collector.</summary>
        public void Stop()
        {
            // Hosts call Stop and then Dispose, which stops again; only a running timer logs.
            var wasRunning = _timer != null;
            _timer?.Dispose();
            _timer = null;
            if (wasRunning) Logger.Info($"[{GetType().Name}] Stopped");
            OnAfterStop();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        /// <summary>Override to perform collection. Called on each timer tick.</summary>
        protected abstract void Collect();

        /// <summary>
        /// Override to return the initial delay before the first tick.
        /// Default: one full interval (same as the repeat interval).
        /// </summary>
        protected virtual TimeSpan GetInitialDelay() => TimeSpan.FromSeconds(_intervalSeconds);

        /// <summary>Optional hook called before the timer starts (e.g. to initialise counters).</summary>
        protected virtual void OnBeforeStart() { }

        /// <summary>Optional hook called after the timer stops (e.g. to dispose counters).</summary>
        protected virtual void OnAfterStop() { }

        /// <summary>Pauses the collection timer without stopping/disposing the collector.</summary>
        protected void PauseTimer()
        {
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Resumes the collection timer after a pause.</summary>
        protected void ResumeTimer()
        {
            if (_disposed || _timer == null) return;
            var interval = TimeSpan.FromSeconds(_intervalSeconds);
            _timer.Change(TimeSpan.Zero, interval);
        }

        /// <summary>
        /// Reentrancy-guarded entry point invoked by <see cref="_timer"/>. <c>internal</c> so the
        /// V2 test assembly (via <c>InternalsVisibleTo</c>) can drive the guard directly without
        /// having to spin up a real timer + wait on real wall-clock time.
        /// </summary>
        internal void CollectSafe()
        {
            // Skip the tick if a previous Collect is still running. The Timer keeps firing in
            // the background, so the next eligible tick will pick up where this one declined —
            // the sample cadence stays aligned with the configured interval rather than the
            // duration of any single slow Collect.
            if (Interlocked.CompareExchange(ref _collecting, 1, 0) != 0)
            {
                Logger.Trace($"[{GetType().Name}] tick skipped — previous Collect still running");
                return;
            }

            try { Collect(); }
            catch (Exception ex) { Logger.Error($"[{GetType().Name}] Collection error: {ex.Message}", ex); }
            finally { Interlocked.Exchange(ref _collecting, 0); }
        }
    }
}

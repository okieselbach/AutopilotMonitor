#nullable enable
using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Kernel host owning the <see cref="RealmJoinWatcher"/> + <see cref="RealmJoinWatcherAdapter"/>.
    /// Always instantiated; the watcher's lazy-attach + dedup semantics make the host a no-op
    /// on devices where RealmJoin is not installed. The internal kill-switch
    /// <see cref="RealmJoinTrackingEnabled"/> short-circuits <see cref="Start"/> if a future
    /// regression needs a single-line off-toggle without re-deploying.
    /// </summary>
    internal sealed class RealmJoinHost : ICollectorHost
    {
        public string Name => "RealmJoinWatcher";

        /// <summary>
        /// Compile-time master kill-switch — set to <c>false</c> to disable the entire RJ
        /// tracking pipeline without changes elsewhere. This is now a build-time backstop in
        /// addition to the per-tenant remote toggle (AnalyzerConfiguration.EnableRealmJoinWatcher,
        /// default off): DefaultComponentFactory only creates this host when both are true.
        /// </summary>
        internal const bool RealmJoinTrackingEnabled = true;

        private readonly AgentLogger _logger;
        private readonly RealmJoinWatcher _watcher;
        private readonly RealmJoinWatcherAdapter _adapter;
        private int _disposed;

        public RealmJoinHost(AgentLogger logger, ISignalIngressSink ingress, IClock clock)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _watcher = new RealmJoinWatcher(logger);
            _adapter = new RealmJoinWatcherAdapter(_watcher, ingress, clock);
        }

#pragma warning disable CS0162 // unreachable-code warnings from the compile-time kill-switch
        public void Start()
        {
            if (!RealmJoinTrackingEnabled)
            {
                _logger.Info("RealmJoinHost: tracking disabled at compile time — skipping start");
                return;
            }
            _watcher.Start();
        }

        public void Stop()
        {
            if (!RealmJoinTrackingEnabled) return;
            _watcher.Stop();
        }

        /// <summary>
        /// Attach the HKU watcher for the active real user. Called by the composition root
        /// when <see cref="DesktopArrivalDetector"/> resolves a real user owner and
        /// <see cref="Monitoring.Runtime.UserSidResolver"/> produces the SID. No-op when the
        /// SID is empty or the kill-switch is off.
        /// </summary>
        public void ArmHkuWatcher(string sid)
        {
            if (!RealmJoinTrackingEnabled) return;
            _watcher.ArmHku(sid);
        }
#pragma warning restore CS0162

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _adapter.Dispose(); } catch { }
            try { _watcher.Dispose(); } catch { }
        }

        // Test seam — exposes the watcher / adapter for unit tests to drive deterministic emits.
        internal RealmJoinWatcher WatcherForTest => _watcher;
        internal RealmJoinWatcherAdapter AdapterForTest => _adapter;
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Hosts the <see cref="ImeRegistryAppStateObserver"/> (registry second pillar, audit
    /// 2026-08-17): one recursive <see cref="RegistryWatcher"/> on the IME root — all three
    /// observed surfaces (Win32Apps, EspTrackingWin32Apps, SideCarPolicies\StatusServiceReports)
    /// live under it — debounced into snapshot-and-diff ticks, plus a 60-s periodic fallback
    /// tick. The fallback both evaluates the reconciliation settle-delay when the registry goes
    /// quiet AND keeps the pillar alive if the watcher fails to arm (poll-only degraded mode,
    /// announced once via <c>collector_degraded</c>). Always-on observability host, no config
    /// gate — precedent: <see cref="EspPolicyProviderStallHost"/>.
    /// </summary>
    internal sealed class ImeRegistryAppStateHost : ICollectorHost
    {
        public string Name => "ImeRegistryAppState";

        private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan PeriodicInterval = TimeSpan.FromSeconds(60);

        private readonly ImeRegistryAppStateObserver _observer;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger? _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;

        private RegistryWatcher? _watcher;
        private Timer? _debounceTimer;
        private Timer? _periodicTimer;
        private int _stopped;

        public ImeRegistryAppStateHost(
            string sessionId,
            string tenantId,
            AgentLogger? logger,
            ISignalIngressSink ingress,
            IClock clock,
            Func<IReadOnlyList<AppPackageState>>? trackerStateProbe)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _sessionId = sessionId;
            _tenantId = tenantId;
            _logger = logger;
            _post = new InformationalEventPost(ingress, clock);
            _observer = new ImeRegistryAppStateObserver(_post, logger, clock, trackerStateProbe);
        }

        public void Start()
        {
            // Baseline immediately: pre-existing registry state must be captured silently
            // BEFORE the first change edge, or stale entries would replay as fresh events.
            _observer.Tick("baseline");

            try
            {
                _watcher = new RegistryWatcher(
                    RegistryHive.LocalMachine,
                    ImeRegistryAppStateObserver.ImeRootKeyPath,
                    watchSubtree: true,
                    view: RegistryView.Registry64,
                    filter: RegistryNativeMethods.RegChangeNotifyFilter.Name
                          | RegistryNativeMethods.RegChangeNotifyFilter.LastSet
                          | RegistryNativeMethods.RegChangeNotifyFilter.ThreadAgnostic,
                    trace: msg => _logger?.Debug($"ImeRegistryAppState watcher: {msg}"));
                _watcher.Changed += (_, __) => ScheduleDebouncedTick();
                _watcher.Error += (_, ex) =>
                {
                    _logger?.Warning($"ImeRegistryAppState watcher error (poll-only fallback continues): {ex.Message}");
                };
                _watcher.Start();
            }
            catch (Exception ex)
            {
                // Watcher arm failure degrades to poll-only (periodic tick below) — announce once.
                _logger?.Warning($"ImeRegistryAppState: watcher arm failed, running poll-only: {ex.Message}");
                CollectorDegradationReporter.Report(_post, _sessionId, _tenantId, Name, "watcher_arm_failed", ex);
                _watcher?.Dispose();
                _watcher = null;
            }

            _periodicTimer = new Timer(_ => SafeTick("periodic"), null, PeriodicInterval, PeriodicInterval);
            _logger?.Info($"ImeRegistryAppStateHost started (watcher={(_watcher != null ? "armed" : "poll-only")})");
        }

        private void ScheduleDebouncedTick()
        {
            if (Volatile.Read(ref _stopped) != 0) return;
            // One trailing-edge tick per change burst; RegistryWatcher coalesces anyway and the
            // observer snapshot-diffs, so collapsing bursts loses nothing.
            var timer = _debounceTimer;
            if (timer == null)
            {
                var created = new Timer(_ => SafeTick("registry_change"), null, DebounceDelay, Timeout.InfiniteTimeSpan);
                if (Interlocked.CompareExchange(ref _debounceTimer, created, null) != null)
                    created.Dispose();
                else
                    return;
                timer = _debounceTimer;
            }
            try { timer?.Change(DebounceDelay, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { /* stopping */ }
        }

        private void SafeTick(string reason)
        {
            if (Volatile.Read(ref _stopped) != 0) return;
            try
            {
                _observer.Tick(reason);
            }
            catch (Exception ex)
            {
                // Observer.Tick is itself fail-soft; this is the last-resort belt.
                _logger?.Debug($"ImeRegistryAppState tick failed: {ex.Message}");
            }
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

            // Never Stop() the watcher from inside its own callback — we're outside here,
            // but RequestStop first keeps the contract obvious.
            try { _watcher?.RequestStop(); } catch { /* fail-soft */ }
            try { _watcher?.Dispose(); } catch { /* fail-soft */ }
            _watcher = null;

            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _periodicTimer?.Dispose();
            _periodicTimer = null;
        }

        public void Dispose() => Stop();
    }
}

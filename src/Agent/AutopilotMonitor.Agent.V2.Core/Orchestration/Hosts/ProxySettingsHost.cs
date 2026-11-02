#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Transport;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Tells <see cref="ProxyObserver"/> the moment proxy settings change, so the log has the exact
    /// time. A watcher that fails costs only that time: the observer compares again on every failed
    /// upload.
    /// </summary>
    internal sealed class ProxySettingsHost : ICollectorHost
    {
        private const string Source = "ProxySettingsHost";
        private const int DebounceMs = 1000;

        // The whole subtree: "Connections" does not exist yet in an account nothing has written to.
        // HKCU of a SYSTEM process is HKU\S-1-5-18.
        private const string AccountSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        // Holds WinHttpSettings, which netsh winhttp writes.
        private const string MachineConnectionsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Connections";

        private const RegistryNativeMethods.RegChangeNotifyFilter Filter =
            RegistryNativeMethods.RegChangeNotifyFilter.Name |
            RegistryNativeMethods.RegChangeNotifyFilter.LastSet;

        private readonly ProxyObserver _observer;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;
        private readonly List<RegistryWatcher> _watchers = new List<RegistryWatcher>();
        private readonly Timer _debounce;
        private int _started;
        private int _stopped;

        public string Name => "ProxySettingsWatcher";

        public ProxySettingsHost(
            ProxyObserver observer,
            ISignalIngressSink ingress,
            IClock clock,
            AgentLogger logger)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _observer = observer ?? throw new ArgumentNullException(nameof(observer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _post = new InformationalEventPost(ingress, clock);
            _debounce = new Timer(_ => OnDebounceElapsed(), null, Timeout.Infinite, Timeout.Infinite);

            AddWatcher(RegistryHive.CurrentUser, AccountSettingsKey, watchSubtree: true, "account settings");
            AddWatcher(RegistryHive.LocalMachine, MachineConnectionsKey, watchSubtree: false, "WinHTTP settings");
        }

        private void AddWatcher(RegistryHive hive, string subKey, bool watchSubtree, string label)
        {
            var watcher = new RegistryWatcher(hive, subKey, watchSubtree, RegistryView.Registry64, Filter);
            watcher.Changed += (_, _) => _debounce.Change(DebounceMs, Timeout.Infinite);
            watcher.Error += (_, ex) => _logger.Warning($"ProxySettingsHost: {label} watcher error - {ex.GetType().Name}: {ex.Message}");
            _watchers.Add(watcher);
        }

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;

            _observer.SettingsChanged += OnSettingsChanged;
            foreach (var watcher in _watchers)
            {
                try { watcher.Start(); }
                catch (Exception ex) { _logger.Warning($"ProxySettingsHost: a watcher failed to start: {ex.Message}"); }
            }

            // Whatever moved between the baseline and the start of the watchers.
            OnDebounceElapsed();
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 1) return;

            DisposeWatchers();
            _observer.SettingsChanged -= OnSettingsChanged;
            if (Volatile.Read(ref _started) == 1) _observer.LogOpenFailures();
        }

        public void Dispose()
        {
            Stop();
            _debounce.Dispose();
        }

        private void DisposeWatchers()
        {
            foreach (var watcher in _watchers)
            {
                try { watcher.Dispose(); }
                catch (Exception ex) { _logger.Debug($"ProxySettingsHost: disposing a watcher threw: {ex.Message}"); }
            }
            _watchers.Clear();
        }

        private void OnDebounceElapsed()
        {
            _logger.Trace("ProxySettingsHost: checking the proxy settings.");
            try { _observer.CheckForChange(); }
            catch (Exception ex) { _logger.Warning($"ProxySettingsHost: checking the proxy settings failed: {ex.Message}"); }
        }

        /// <summary>
        /// Waits in the spool if uploads are down, so the timeline explains the gap once they work
        /// again. The portal shows the newest event of this type.
        /// </summary>
        private void OnSettingsChanged(ProxySettings settings, string what, string route)
        {
            var data = settings.ToEventData();
            data["trigger"] = "changed";
            data["change"] = what;
            data["route"] = route;

            _post.Emit(
                eventType: Constants.EventTypes.ProxyConfiguration,
                source: Source,
                message: $"Proxy settings changed: {what}",
                severity: EventSeverity.Warning,
                typedPayload: data);
        }
    }
}

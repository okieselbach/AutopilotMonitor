using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Production <see cref="IRegistryChangeSignal"/> wired to two
    /// <see cref="RegistryWatcher"/> instances:
    /// <list type="bullet">
    /// <item><description><c>HKLM\SOFTWARE\Microsoft\Enrollments</c> (recursive) — catches
    /// new enrollment GUID sub-keys and <c>AADTenantID</c> writes inside them.</description></item>
    /// <item><description><c>HKLM\SYSTEM\CurrentControlSet\Control\CloudDomainJoin</c>
    /// (recursive, watching the parent because <c>TenantInfo</c> / <c>JoinInfo</c>
    /// might not exist yet at start-up) — catches AAD-join completion.</description></item>
    /// </list>
    /// Both watchers fan their <c>Changed</c> events into a single <see cref="Changed"/>
    /// edge so the awaiter doesn't have to know which path moved.
    /// </summary>
    public sealed class CompositeRegistryChangeSignal : IRegistryChangeSignal
    {
        private const RegistryNativeMethods.RegChangeNotifyFilter Filter =
            RegistryNativeMethods.RegChangeNotifyFilter.Name |
            RegistryNativeMethods.RegChangeNotifyFilter.LastSet |
            RegistryNativeMethods.RegChangeNotifyFilter.ThreadAgnostic;

        private readonly List<RegistryWatcher> _watchers = new List<RegistryWatcher>();
        private readonly AgentLogger _logger;
        private bool _started;
        private bool _disposed;

        public event EventHandler Changed;

        /// <summary>
        /// Constructs the production signal — two watchers on the registry locations the
        /// <see cref="TenantIdResolver"/> probes. Use the protected constructor in tests
        /// when you don't want real Win32 watchers.
        /// </summary>
        public CompositeRegistryChangeSignal(AgentLogger logger)
        {
            _logger = logger;
            _watchers.Add(BuildWatcher(
                RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Enrollments",
                "Enrollments"));
            _watchers.Add(BuildWatcher(
                RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\CloudDomainJoin",
                "CloudDomainJoin"));
        }

        private RegistryWatcher BuildWatcher(RegistryHive hive, string subKey, string label)
        {
            var watcher = new RegistryWatcher(
                hive: hive,
                subKey: subKey,
                watchSubtree: true,
                view: RegistryView.Registry64,
                filter: Filter,
                trace: null);

            watcher.Changed += (sender, args) =>
            {
                _logger?.Trace($"CompositeRegistryChangeSignal: {label} change detected.");
                Changed?.Invoke(this, EventArgs.Empty);
            };
            watcher.Error += (sender, ex) =>
            {
                _logger?.Warning($"CompositeRegistryChangeSignal: {label} watcher error — {ex.GetType().Name}: {ex.Message}");
            };
            return watcher;
        }

        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CompositeRegistryChangeSignal));
            if (_started) return;
            _started = true;

            foreach (var w in _watchers)
            {
                try
                {
                    w.Start();
                }
                catch (Exception ex)
                {
                    // RegOpenKeyEx can fail if a watched root happens to not exist on this
                    // device variant. Log + keep going — the other watcher (and the
                    // belt-and-suspenders re-probe in TenantIdAwaiter) cover the gap.
                    _logger?.Warning($"CompositeRegistryChangeSignal: failed to start a watcher — {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var w in _watchers)
            {
                try { w.Dispose(); } catch { /* never throw from Dispose */ }
            }
            _watchers.Clear();
        }
    }
}

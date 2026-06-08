#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office
{
    /// <summary>
    /// Push-based registry-change notifier built on <c>RegNotifyChangeKeyValue</c> (async). Signals
    /// <see cref="Changed"/> whenever a value is set or a subkey is added/removed anywhere under the
    /// target key (watch-subtree) — used by the <see cref="OfficeInstallDetector"/> to react to C2R
    /// <c>Configuration</c> value changes (VersionToReport / StreamingFinished) and <c>Scenario</c>
    /// subkey churn without polling.
    /// <para>
    /// <b>Bootstrap fallback</b>: on a clean first install the target key (e.g.
    /// <c>…\Office\ClickToRun</c>) may not exist yet when the C2R worker starts. Instead of giving up,
    /// the watcher arms a <em>cheap</em> watch on the nearest existing ancestor — <b>non-recursive</b>
    /// (<c>bWatchSubtree=FALSE</c>) and <b>name-only</b> (<c>REG_NOTIFY_CHANGE_NAME</c>) — so it fires
    /// ONLY when a direct child key is created/deleted there (rare; it ignores the noisy value churn in
    /// the subtree below). When the target finally appears it promotes to the full target watch and
    /// raises one <see cref="Changed"/>. This avoids both "no signal until process end" and the high
    /// event volume that a recursive watch on a broad parent (e.g. <c>…\Microsoft</c>) would cause.
    /// </para>
    /// One-shot per OS contract, so it re-arms after each fire. Fail-soft: any failure is logged and
    /// the watcher stays quiet (the detector still gets the process Started/Stopped boundary events).
    /// </summary>
    public sealed class RegistryChangeWatcher : IDisposable
    {
        [Flags]
        private enum RegNotifyFilter : uint
        {
            ChangeName = 0x00000001,      // subkey added/removed (ClickToRun appearing; Scenario\* churn)
            ChangeLastSet = 0x00000004,   // value set (Configuration: VersionToReport / StreamingFinished)
        }

        private const RegNotifyFilter PrimaryFilter = RegNotifyFilter.ChangeName | RegNotifyFilter.ChangeLastSet;
        private const RegNotifyFilter BootstrapFilter = RegNotifyFilter.ChangeName; // cheap: direct-child create/delete only

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern int RegNotifyChangeKeyValue(
            SafeRegistryHandle hKey,
            [MarshalAs(UnmanagedType.Bool)] bool bWatchSubtree,
            RegNotifyFilter dwNotifyFilter,
            SafeWaitHandle hEvent,
            [MarshalAs(UnmanagedType.Bool)] bool fAsynchronous);

        private enum WatchMode { None, Primary, Bootstrap }

        private readonly string _targetSubKey;
        private readonly AgentLogger _logger;
        private readonly object _lock = new object();

        private RegistryKey? _baseKey;
        private RegistryKey? _watchedKey;
        private WatchMode _mode;
        private AutoResetEvent? _signal;
        private RegisteredWaitHandle? _registeredWait;
        private bool _started;
        private bool _disposed;

        /// <summary>Raised (on a ThreadPool thread) when the target subtree meaningfully changes.</summary>
        public event EventHandler? Changed;

        /// <param name="targetSubKey">HKLM-relative key, e.g. <c>SOFTWARE\Microsoft\Office\ClickToRun</c>.</param>
        public RegistryChangeWatcher(string targetSubKey, AgentLogger logger)
        {
            _targetSubKey = targetSubKey ?? throw new ArgumentNullException(nameof(targetSubKey));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_started || _disposed) return;
                _started = true;
                try
                {
                    // Forced Registry64 view — consistent with the C2R reads (AnyCPU net48 may otherwise
                    // resolve the stale WOW6432Node mirror).
                    _baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    _signal = new AutoResetEvent(false);
                    ArmBestEffort(raiseOnPromote: false);
                }
                catch (Exception ex)
                {
                    _logger.Warning($"[RegistryChangeWatcher] failed to start on HKLM\\{_targetSubKey}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Arms the best available watch: the target key (full, subtree) if it exists, otherwise a cheap
        /// name-only watch on the nearest existing ancestor until the target appears. Must hold <see cref="_lock"/>.
        /// </summary>
        private void ArmBestEffort(bool raiseOnPromote)
        {
            if (_disposed || _baseKey == null) return;

            CloseWatchedKey();

            var target = TryOpen(_targetSubKey);
            if (target != null)
            {
                _watchedKey = target;
                _mode = WatchMode.Primary;
                if (!ArmNotify(subtree: true, PrimaryFilter)) return;
                if (raiseOnPromote) RaiseChanged();
                return;
            }

            // Target absent — watch the nearest existing ancestor (non-recursive, name-only = cheap).
            var ancestor = OpenNearestExistingAncestor(out var ancestorPath);
            if (ancestor == null)
            {
                _logger.Debug($"[RegistryChangeWatcher] no existing ancestor for HKLM\\{_targetSubKey}; not arming");
                return;
            }
            _watchedKey = ancestor;
            _mode = WatchMode.Bootstrap;
            _logger.Debug($"[RegistryChangeWatcher] target HKLM\\{_targetSubKey} absent — bootstrap-watching HKLM\\{ancestorPath} (name-only)");
            if (!ArmNotify(subtree: false, BootstrapFilter)) return;

            // TOCTOU: the target may have been created between TryOpen and arming the ancestor watch.
            using (var recheck = TryOpen(_targetSubKey))
            {
                if (recheck != null) ArmBestEffort(raiseOnPromote: true);
            }
        }

        private bool ArmNotify(bool subtree, RegNotifyFilter filter)
        {
            if (_disposed || _watchedKey == null || _signal == null) return false;
            try
            {
                int rc = RegNotifyChangeKeyValue(_watchedKey.Handle, subtree, filter, _signal.SafeWaitHandle, fAsynchronous: true);
                if (rc != 0)
                {
                    _logger.Warning($"[RegistryChangeWatcher] RegNotifyChangeKeyValue failed rc={rc} (HKLM\\{_targetSubKey})");
                    return false;
                }
                UnregisterWait();
                _registeredWait = ThreadPool.RegisterWaitForSingleObject(
                    _signal, OnSignalled, state: null, millisecondsTimeOutInterval: Timeout.Infinite, executeOnlyOnce: true);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warning($"[RegistryChangeWatcher] arm failed (HKLM\\{_targetSubKey}): {ex.Message}");
                return false;
            }
        }

        private void OnSignalled(object? state, bool timedOut)
        {
            lock (_lock)
            {
                UnregisterWait();
                if (_disposed || timedOut) return;

                if (_mode == WatchMode.Primary)
                {
                    RaiseChanged();
                    ArmNotify(subtree: true, PrimaryFilter); // re-arm on the same target key
                }
                else
                {
                    // A direct child changed under the ancestor — promote to the target watch if it now
                    // exists (raises Changed on promotion), else keep the cheap bootstrap watch.
                    ArmBestEffort(raiseOnPromote: true);
                }
            }
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private RegistryKey? TryOpen(string subKey)
        {
            try { return _baseKey?.OpenSubKey(subKey, writable: false); }
            catch { return null; }
        }

        /// <summary>Walks up the target path and opens the first ancestor that exists.</summary>
        private RegistryKey? OpenNearestExistingAncestor(out string ancestorPath)
        {
            ancestorPath = string.Empty;
            var path = _targetSubKey;
            while (true)
            {
                var idx = path.LastIndexOf('\\');
                if (idx <= 0) return null;
                path = path.Substring(0, idx);
                var key = TryOpen(path);
                if (key != null) { ancestorPath = path; return key; }
            }
        }

        private void RaiseChanged()
        {
            var handler = Changed;
            if (handler == null) return;
            try { handler(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.Warning($"[RegistryChangeWatcher] Changed handler threw: {ex.Message}"); }
        }

        private void UnregisterWait()
        {
            try { _registeredWait?.Unregister(null); } catch { }
            _registeredWait = null;
        }

        private void CloseWatchedKey()
        {
            try { _watchedKey?.Dispose(); } catch { }
            _watchedKey = null;
            _mode = WatchMode.None;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                UnregisterWait();
                try { _signal?.Set(); } catch { } // release any pending wait
                try { _signal?.Dispose(); } catch { }
                _signal = null;
                CloseWatchedKey();
                try { _baseKey?.Dispose(); } catch { }
                _baseKey = null;
            }
        }
    }
}

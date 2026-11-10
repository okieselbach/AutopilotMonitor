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
    /// <para>
    /// <b>Locking</b>: the lock guards this watcher's own state and is never held while
    /// <see cref="Changed"/> is raised. A handler may dispose the watcher or take locks of its own,
    /// and <see cref="Dispose"/> never waits for a handler. Raising under the lock deadlocks against
    /// an owner that disposes this watcher under a lock its handler also takes.
    /// </para>
    /// </summary>
    public sealed class RegistryChangeWatcher : IDisposable
    {
        [Flags]
        private enum RegNotifyFilter : uint
        {
            ChangeName = 0x00000001,      // subkey added/removed (ClickToRun appearing; Scenario\* churn)
            ChangeLastSet = 0x00000004,   // value set (Configuration: VersionToReport / StreamingFinished)
            // The registration must outlive the thread that issued it: re-arming happens on rotating
            // ThreadPool threads, and without this flag Windows signals the event when such a thread
            // ends — reported here as a change that never happened.
            ThreadAgnostic = 0x10000000,
        }

        private const RegNotifyFilter PrimaryFilter =
            RegNotifyFilter.ChangeName | RegNotifyFilter.ChangeLastSet | RegNotifyFilter.ThreadAgnostic;
        // Cheap: direct-child create/delete only.
        private const RegNotifyFilter BootstrapFilter = RegNotifyFilter.ChangeName | RegNotifyFilter.ThreadAgnostic;

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern int RegNotifyChangeKeyValue(
            SafeRegistryHandle hKey,
            [MarshalAs(UnmanagedType.Bool)] bool bWatchSubtree,
            RegNotifyFilter dwNotifyFilter,
            SafeWaitHandle hEvent,
            [MarshalAs(UnmanagedType.Bool)] bool fAsynchronous);

        private enum WatchMode { None, Primary, Bootstrap }

        private readonly RegistryHive _hive;
        private readonly string _hiveLabel;
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
            : this(RegistryHive.LocalMachine, targetSubKey, logger)
        {
        }

        /// <summary>Test seam: production always watches HKLM; tests use a hive they may write to.</summary>
        internal RegistryChangeWatcher(RegistryHive hive, string targetSubKey, AgentLogger logger)
        {
            _hive = hive;
            _hiveLabel = hive == RegistryHive.LocalMachine ? "HKLM" : hive.ToString();
            _targetSubKey = targetSubKey ?? throw new ArgumentNullException(nameof(targetSubKey));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>The filter the full target watch is armed with (tests).</summary>
        internal uint EffectivePrimaryFilter => (uint)PrimaryFilter;

        public void Start()
        {
            bool raise = false;
            lock (_lock)
            {
                if (_started || _disposed) return;
                _started = true;
                try
                {
                    // Forced Registry64 view — consistent with the C2R reads (AnyCPU net48 may otherwise
                    // resolve the stale WOW6432Node mirror).
                    _baseKey = RegistryKey.OpenBaseKey(_hive, RegistryView.Registry64);
                    _signal = new AutoResetEvent(false);
                    raise = ArmBestEffort(targetIsNews: false);
                }
                catch (Exception ex)
                {
                    _logger.Warning($"[RegistryChangeWatcher] failed to start on {_hiveLabel}\\{_targetSubKey}: {ex.Message}");
                }
            }

            if (raise) RaiseChanged();
        }

        /// <summary>
        /// Arms the best available watch: the target key (full, subtree) if it exists, otherwise a cheap
        /// name-only watch on the nearest existing ancestor until the target appears. Must hold <see cref="_lock"/>.
        /// Returns true when the target key appeared and the caller has to raise <see cref="Changed"/>
        /// for it — after releasing the lock.
        /// </summary>
        /// <param name="targetIsNews">False when finding the target right away is the expected start
        /// state; true when the watch was waiting for it.</param>
        private bool ArmBestEffort(bool targetIsNews)
        {
            if (_disposed || _baseKey == null) return false;

            CloseWatchedKey();

            var target = TryOpen(_targetSubKey);
            if (target != null)
            {
                _watchedKey = target;
                _mode = WatchMode.Primary;
                return ArmNotify(subtree: true, PrimaryFilter) && targetIsNews;
            }

            // Target absent — watch the nearest existing ancestor (non-recursive, name-only = cheap).
            var ancestor = OpenNearestExistingAncestor(out var ancestorPath);
            if (ancestor == null)
            {
                _logger.Debug($"[RegistryChangeWatcher] no existing ancestor for {_hiveLabel}\\{_targetSubKey}; not arming");
                return false;
            }
            _watchedKey = ancestor;
            _mode = WatchMode.Bootstrap;
            _logger.Debug($"[RegistryChangeWatcher] target {_hiveLabel}\\{_targetSubKey} absent — bootstrap-watching {_hiveLabel}\\{ancestorPath} (name-only)", repeatKey: "RegistryChangeWatcher.bootstrap:" + _targetSubKey);
            if (!ArmNotify(subtree: false, BootstrapFilter)) return false;

            // TOCTOU: the target may have been created between TryOpen and arming the ancestor watch.
            using (var recheck = TryOpen(_targetSubKey))
            {
                return recheck != null && ArmBestEffort(targetIsNews: true);
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
                    _logger.Warning($"[RegistryChangeWatcher] RegNotifyChangeKeyValue failed rc={rc} ({_hiveLabel}\\{_targetSubKey})");
                    return false;
                }
                UnregisterWait();
                _registeredWait = ThreadPool.RegisterWaitForSingleObject(
                    _signal, OnSignalled, state: null, millisecondsTimeOutInterval: Timeout.Infinite, executeOnlyOnce: true);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warning($"[RegistryChangeWatcher] arm failed ({_hiveLabel}\\{_targetSubKey}): {ex.Message}");
                return false;
            }
        }

        private void OnSignalled(object? state, bool timedOut)
        {
            bool raise;
            bool rearmAfterHandler;
            lock (_lock)
            {
                UnregisterWait();
                if (_disposed || timedOut) return;

                rearmAfterHandler = _mode == WatchMode.Primary;
                // Bootstrap: a direct child changed under the ancestor — promote to the target watch if
                // it now exists (then Changed is due), else keep the cheap bootstrap watch.
                raise = rearmAfterHandler || ArmBestEffort(targetIsNews: true);
            }

            if (raise) RaiseChanged();

            if (!rearmAfterHandler) return;
            lock (_lock)
            {
                // Re-arm on the same target key, and only now: no wait was registered while the handler
                // ran, so handlers never overlap, and a change that arrived meanwhile completes this
                // registration at once.
                ArmNotify(subtree: true, PrimaryFilter);
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

        // Never called with _lock held.
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

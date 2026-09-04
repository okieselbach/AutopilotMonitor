using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    public sealed class AadPlaceholderUserDetectedEventArgs : EventArgs
    {
        public string UserEmail { get; }
        public string Thumbprint { get; }
        public AadPlaceholderUserDetectedEventArgs(string userEmail, string thumbprint)
        {
            UserEmail = userEmail;
            Thumbprint = thumbprint;
        }
    }

    public sealed class AadUserJoinedEventArgs : EventArgs
    {
        public string UserEmail { get; }
        public string Thumbprint { get; }
        public AadUserJoinedEventArgs(string userEmail, string thumbprint)
        {
            UserEmail = userEmail;
            Thumbprint = thumbprint;
        }
    }

    /// <summary>
    /// Watches <c>HKLM\SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo</c> for a
    /// late AAD user join. Uses <see cref="RegistryWatcher"/> (RegNotifyChangeKeyValue) for
    /// instant, low-overhead change detection — no polling.
    ///
    /// Two distinct events are raised:
    /// <list type="bullet">
    /// <item><description><see cref="PlaceholderUserDetected"/> — first time a transient
    ///   provisioning account (<c>foouser@*</c>, <c>autopilot@*</c>) appears. Fire-once.
    ///   The watcher keeps running afterwards, as the placeholder is expected to be
    ///   replaced by a real user.</description></item>
    /// <item><description><see cref="AadUserJoined"/> — first time a REAL user e-mail
    ///   (non-placeholder) appears. Fire-once. The watcher auto-stops afterwards.</description></item>
    /// </list>
    ///
    /// Retry semantics: the <c>JoinInfo</c> root key may not exist yet when the agent
    /// starts — it is created during AAD provisioning. A retry timer polls for its
    /// appearance every 2 seconds until it exists, then attaches the registry watcher.
    /// </summary>
    internal sealed class AadJoinWatcher : IDisposable
    {
        internal const int RetryIntervalSeconds = 2;
        internal const int DebounceMilliseconds = 500;

        private readonly AgentLogger _logger;
        private readonly object _stateLock = new object();

        private RegistryWatcher _watcher;
        private Timer _retryTimer;
        private Timer _debounceTimer;

        private bool _placeholderFired;
        private bool _realUserFired;
        private bool _disposed;

        public event EventHandler<AadPlaceholderUserDetectedEventArgs> PlaceholderUserDetected;
        public event EventHandler<AadUserJoinedEventArgs> AadUserJoined;

        /// <summary>
        /// <c>true</c> while a provisioning placeholder (foouser@/autopilot@) is the JoinInfo
        /// user and no real user has replaced it. On a Hybrid join this stays true through the
        /// whole user sign-in — the AD sign-in never touches JoinInfo — so consumers report it
        /// as a fact, never as sign-in evidence (2026-09-04).
        /// </summary>
        internal bool PlaceholderObservedWithoutRealUser
        {
            get { lock (_stateLock) return _placeholderFired && !_realUserFired; }
        }

        public AadJoinWatcher(AgentLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AadJoinWatcher));

            // Initial synchronous read. If a real AAD user is already present (typical after
            // DeviceInfoCollector ran at agent startup), fire AadUserJoined once and do NOT
            // attach the registry watcher — there is nothing left to watch for.
            CheckJoinInfo();
            bool terminalFromStartup;
            lock (_stateLock) { terminalFromStartup = _realUserFired; }
            if (terminalFromStartup)
            {
                _logger.Info("AadJoinWatcher: real AAD user already present at startup — skipping RegistryWatcher attach");
                return;
            }

            if (!TryStartWatcher())
            {
                _logger.Info("AadJoinWatcher: JoinInfo key not yet present — retrying every 2s");
                _retryTimer = new Timer(
                    _ =>
                    {
                        if (TryStartWatcher())
                        {
                            _retryTimer?.Dispose();
                            _retryTimer = null;
                        }
                    },
                    null,
                    TimeSpan.FromSeconds(RetryIntervalSeconds),
                    TimeSpan.FromSeconds(RetryIntervalSeconds));
            }
        }

        public void Stop(string reason = "watcher_stopped")
        {
            try
            {
                lock (_stateLock)
                {
                    _retryTimer?.Dispose();
                    _retryTimer = null;

                    _debounceTimer?.Dispose();
                    _debounceTimer = null;

                    if (_watcher != null)
                    {
                        _watcher.Dispose();
                        _watcher = null;
                        _logger.Info($"AadJoinWatcher: stopped ({reason})");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("AadJoinWatcher: error during Stop", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop("disposed");
        }

        private bool TryStartWatcher()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(AadJoinInfo.JoinInfoRegistryPath))
                {
                    if (key == null) return false;
                }

                lock (_stateLock)
                {
                    if (_watcher != null) return true; // Already running

                    _debounceTimer = new Timer(
                        _ => CheckJoinInfo(),
                        null,
                        Timeout.Infinite,
                        Timeout.Infinite);

                    _watcher = new RegistryWatcher(
                        RegistryHive.LocalMachine,
                        AadJoinInfo.JoinInfoRegistryPath,
                        watchSubtree: true,
                        filter: RegistryNativeMethods.RegChangeNotifyFilter.LastSet
                              | RegistryNativeMethods.RegChangeNotifyFilter.Name,
                        trace: msg => _logger.Trace($"AadJoinWatcher(reg): {msg}"));

                    _watcher.Changed += (s, e) =>
                    {
                        _logger.Trace("AadJoinWatcher: registry changed — debouncing");
                        _debounceTimer?.Change(DebounceMilliseconds, Timeout.Infinite);
                    };
                    _watcher.Error += (s, ex) => _logger.Warning($"AadJoinWatcher: watcher error: {ex.Message}");

                    _watcher.Start();
                    _logger.Info("AadJoinWatcher: RegistryWatcher attached to JoinInfo");
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.Warning($"AadJoinWatcher: failed to start watcher: {ex.Message}");
                return false;
            }
        }

        private void CheckJoinInfo()
        {
            try
            {
                // Already reached terminal state? Stop any further processing.
                lock (_stateLock)
                {
                    if (_realUserFired) return;
                }

                if (!AadJoinInfo.TryReadAadJoinedUser(out var userEmail, out var thumbprint, out var isPlaceholderUser))
                {
                    // Key gone or unreadable — keep watching, may come back
                    return;
                }

                if (string.IsNullOrWhiteSpace(userEmail))
                {
                    // Sub-key exists but no UserEmail value yet — nothing to act on
                    return;
                }

                bool firePlaceholder = false;
                bool fireRealUser = false;
                lock (_stateLock)
                {
                    if (isPlaceholderUser && !_placeholderFired)
                    {
                        _placeholderFired = true;
                        firePlaceholder = true;
                    }
                    else if (!isPlaceholderUser && !_realUserFired)
                    {
                        _realUserFired = true;
                        fireRealUser = true;
                    }
                }

                if (firePlaceholder)
                {
                    _logger.Info($"AadJoinWatcher: placeholder user detected ({userEmail}) — pre-provisioning indicator");
                    try { PlaceholderUserDetected?.Invoke(this, new AadPlaceholderUserDetectedEventArgs(userEmail, thumbprint)); }
                    catch (Exception ex) { _logger.Error("AadJoinWatcher: PlaceholderUserDetected handler threw", ex); }
                }

                if (fireRealUser)
                {
                    _logger.Info($"AadJoinWatcher: real AAD user joined ({userEmail}) — firing AadUserJoined and stopping watcher");
                    try { AadUserJoined?.Invoke(this, new AadUserJoinedEventArgs(userEmail, thumbprint)); }
                    catch (Exception ex) { _logger.Error("AadJoinWatcher: AadUserJoined handler threw", ex); }

                    // Terminal — stop the watcher. Run on a thread-pool thread to avoid
                    // callback-in-callback lock edge cases with the RegistryWatcher worker.
                    ThreadPool.QueueUserWorkItem(_ => Stop("real_user_joined"));
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"AadJoinWatcher: CheckJoinInfo threw: {ex.Message}");
            }
        }
    }
}

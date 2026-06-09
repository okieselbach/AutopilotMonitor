using System;
using System.ComponentModel;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime
{
    /// <summary>
    /// Watches a registry key for changes using RegNotifyChangeKeyValue.
    /// Raises Changed whenever the watched key/subtree changes.
    ///
    /// Notes:
    /// - Notification is for the key scope, not one individual value.
    /// - To track a specific value, read/compare that value in the Changed handler.
    /// - Backed by a <see cref="ThreadPool"/> wait (<see cref="ThreadPool.RegisterWaitForSingleObject"/>)
    ///   on the async-signalled change event — NOT a dedicated background thread. This is the same
    ///   lightweight pattern used by <c>Monitoring/Telemetry/Office/RegistryChangeWatcher</c>; it
    ///   removes one OS thread (~1 MB committed stack) per watcher. The public contract — the ctor,
    ///   <see cref="Changed"/>/<see cref="Error"/> events, <see cref="Start"/>, <see cref="RequestStop"/>,
    ///   <see cref="Stop"/> (which rethrows a captured background failure) and <see cref="Dispose"/> —
    ///   is preserved verbatim.
    /// </summary>
    /// <remarks>
    /// Threading contract: <see cref="Changed"/> is raised on a ThreadPool thread, OUTSIDE the
    /// internal lock, so a handler may freely call <see cref="RequestStop"/> (the documented way to
    /// stop from inside a handler). RegNotifyChangeKeyValue is single-shot, so the watcher re-arms
    /// after each notification — there is, by design, a tiny window between the signal and the
    /// re-arm during which an intermediate change is collapsed into the next notification; callers
    /// always re-read current state in their handler, so no edge is lost in practice.
    /// </remarks>
    internal sealed class RegistryWatcher : IDisposable
    {
        private readonly UIntPtr _hive;
        private readonly string _subKey;
        private readonly bool _watchSubtree;
        private readonly RegistryView _view;
        private readonly RegistryNativeMethods.RegChangeNotifyFilter _filter;
        private readonly Action<string> _trace;

        private readonly object _sync = new object();

        private SafeRegistryHandle _keyHandle;
        private AutoResetEvent _signal;
        private RegisteredWaitHandle _registeredWait;
        private bool _running;
        private bool _stopped;
        private Exception _backgroundException;
        private bool _disposed;

        public event EventHandler Changed;
        public event EventHandler<Exception> Error;

        /// <param name="trace">Optional trace callback for diagnostic logging (called on arm / signal / re-arm).</param>
        public RegistryWatcher(
            RegistryHive hive,
            string subKey,
            bool watchSubtree = false,
            RegistryView view = RegistryView.Default,
            RegistryNativeMethods.RegChangeNotifyFilter filter =
                RegistryNativeMethods.RegChangeNotifyFilter.LastSet |
                RegistryNativeMethods.RegChangeNotifyFilter.ThreadAgnostic,
            Action<string> trace = null)
        {
            _hive = HiveToPointer(hive);
            _subKey = subKey ?? throw new ArgumentNullException(nameof(subKey));
            _watchSubtree = watchSubtree;
            _view = view;
            _filter = filter;
            _trace = trace;
        }

        public bool IsRunning
        {
            get
            {
                lock (_sync)
                {
                    return _running;
                }
            }
        }

        public void Start()
        {
            ThrowIfDisposed();

            Exception openError;
            lock (_sync)
            {
                if (_running)
                    throw new InvalidOperationException("Watcher is already running.");

                _running = true;
                _stopped = false;
                _backgroundException = null;
                _signal = new AutoResetEvent(false);

                // Open the key + arm the first async notification synchronously, so by the time
                // Start() returns the kernel is already tracking changes on our handle (callers
                // and tests rely on the watch being live shortly after Start). A RegOpenKeyEx /
                // RegNotifyChangeKeyValue failure does NOT throw from Start() — to preserve the
                // dedicated-thread contract it is captured, surfaced via Error, and rethrown on
                // Stop(). Every production caller wraps Start() in try/catch and swallows the
                // Stop() rethrow, so this keeps their behaviour identical.
                openError = OpenKeyAndArm();
                if (openError != null)
                    _backgroundException = openError;
            }

            if (openError != null)
                RaiseError(openError);
        }

        /// <summary>
        /// Requests stop without blocking. Safe to call from Changed handlers (it does not dispose
        /// the underlying handles — that happens in <see cref="Stop"/>/<see cref="Dispose"/> — it
        /// only guarantees no further Changed is raised).
        /// </summary>
        public void RequestStop()
        {
            lock (_sync)
            {
                if (!_running) return;
                _stopped = true;
                UnregisterWait();
                try { _signal?.Set(); } catch { /* releasing a pending wait must not throw */ }
            }
        }

        /// <summary>
        /// Stops the watcher and releases its handles. Rethrows a captured background failure
        /// (other than cancellation) as an <see cref="InvalidOperationException"/>, preserving the
        /// original dedicated-thread contract. Avoid calling from inside Changed; use
        /// <see cref="RequestStop"/> there.
        /// </summary>
        public void Stop()
        {
            Exception backgroundException;

            lock (_sync)
            {
                if (!_running)
                    return;

                _running = false;
                _stopped = true;

                UnregisterWait();

                try { _signal?.Set(); } catch { /* release any pending wait */ }
                try { _signal?.Dispose(); } catch { /* dispose must not throw */ }
                _signal = null;

                try { _keyHandle?.Dispose(); } catch { /* dispose must not throw */ }
                _keyHandle = null;

                backgroundException = _backgroundException;
                _backgroundException = null;
            }

            _trace?.Invoke("Stopped (cancellation requested)");

            if (backgroundException != null &&
                !(backgroundException is OperationCanceledException))
            {
                throw new InvalidOperationException(
                    "Registry watcher stopped because the background notification failed.",
                    backgroundException);
            }
        }

        /// <summary>
        /// Opens the watched key and arms the first async notification. Must hold <see cref="_sync"/>.
        /// Returns the failure exception (so the caller can capture/surface it without throwing) or
        /// <c>null</c> on success.
        /// </summary>
        private Exception OpenKeyAndArm()
        {
            int samDesired = RegistryNativeMethods.GetSamDesired(_view);

            _trace?.Invoke($"RegOpenKeyEx '{_subKey}' (view: {_view}, sam: 0x{samDesired:X})");

            int openResult = RegistryNativeMethods.RegOpenKeyEx(
                _hive,
                _subKey,
                0,
                samDesired,
                out SafeRegistryHandle keyHandle);

            if (openResult != 0)
            {
                return new Win32Exception(
                    openResult,
                    $"RegOpenKeyEx failed for '{_subKey}' (view: {_view}).");
            }

            _keyHandle = keyHandle;
            _trace?.Invoke($"RegOpenKeyEx succeeded — handle valid: {!keyHandle.IsInvalid}");

            return ArmNotify();
        }

        /// <summary>
        /// Arms one async RegNotifyChangeKeyValue and registers a ThreadPool wait on the change
        /// event. Single-shot — re-armed after each notification by <see cref="OnSignalled"/>.
        /// Must hold <see cref="_sync"/>. Returns the failure exception or <c>null</c> on success.
        /// </summary>
        private Exception ArmNotify()
        {
            if (_disposed || _stopped || _keyHandle == null || _signal == null)
                return null;

            _trace?.Invoke($"RegNotifyChangeKeyValue (subtree={_watchSubtree}, filter=0x{(uint)_filter:X})");

            int notifyResult = RegistryNativeMethods.RegNotifyChangeKeyValue(
                _keyHandle,
                _watchSubtree,
                _filter,
                _signal.SafeWaitHandle,
                true);

            if (notifyResult != 0)
            {
                return new Win32Exception(
                    notifyResult,
                    $"RegNotifyChangeKeyValue failed for '{_subKey}'.");
            }

            UnregisterWait();
            _registeredWait = ThreadPool.RegisterWaitForSingleObject(
                _signal,
                OnSignalled,
                state: null,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: true);

            _trace?.Invoke("Armed — waiting for change notification (ThreadPool)");
            return null;
        }

        private void OnSignalled(object state, bool timedOut)
        {
            // Drop the (now-consumed, executeOnlyOnce) registration reference.
            lock (_sync)
            {
                _registeredWait = null;
            }

            bool proceed;
            lock (_sync)
            {
                proceed = _running && !_stopped && !_disposed && !timedOut;
            }

            if (!proceed)
            {
                _trace?.Invoke("Signalled after stop/dispose — ignoring");
                return;
            }

            _trace?.Invoke("Change notification received — invoking Changed handler");

            // Raise Changed OUTSIDE the lock so a handler may call RequestStop() (or post work to
            // another thread) without risk of a lock-ordering surprise. Handler exceptions are
            // routed to Error, never allowed to escape the ThreadPool callback.
            try
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                RaiseError(ex);
            }

            // Re-arm for the next change (RegNotifyChangeKeyValue is single-shot).
            Exception armError = null;
            lock (_sync)
            {
                if (!_running || _stopped || _disposed)
                {
                    _trace?.Invoke("Stop requested during Changed — not re-arming");
                    return;
                }

                armError = ArmNotify();
                if (armError != null)
                    _backgroundException = armError;
            }

            if (armError != null)
                RaiseError(armError);
        }

        private void RaiseError(Exception ex)
        {
            try
            {
                Error?.Invoke(this, ex);
            }
            catch
            {
                // Never let an Error-handler exception escape the ThreadPool callback / Start.
            }
        }

        /// <summary>Unregisters the pending ThreadPool wait without blocking. Must hold <see cref="_sync"/>.</summary>
        private void UnregisterWait()
        {
            // Unregister(null) is non-blocking: it does NOT wait for an in-flight callback, so it is
            // safe to call from within OnSignalled itself (mirrors Office RegistryChangeWatcher).
            try { _registeredWait?.Unregister(null); } catch { /* best-effort */ }
            _registeredWait = null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            try
            {
                Stop();
            }
            catch
            {
                // Dispose must not throw.
            }

            lock (_sync)
            {
                _disposed = true;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RegistryWatcher));
        }

        private static UIntPtr HiveToPointer(RegistryHive hive)
        {
            switch (hive)
            {
                case RegistryHive.ClassesRoot:
                    return (UIntPtr)0x80000000u;
                case RegistryHive.CurrentUser:
                    return (UIntPtr)0x80000001u;
                case RegistryHive.LocalMachine:
                    return (UIntPtr)0x80000002u;
                case RegistryHive.Users:
                    return (UIntPtr)0x80000003u;
                case RegistryHive.CurrentConfig:
                    return (UIntPtr)0x80000005u;
                default:
                    throw new ArgumentOutOfRangeException(nameof(hive));
            }
        }
    }
}

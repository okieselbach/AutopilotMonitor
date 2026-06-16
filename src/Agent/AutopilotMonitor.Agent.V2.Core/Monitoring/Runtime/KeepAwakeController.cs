#nullable enable
using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime
{
    /// <summary>
    /// Thin abstraction over the Win32 execution-state API so the keep-awake hold can be unit
    /// tested without touching real OS power policy. <see cref="PreventSleep"/> and
    /// <see cref="AllowSleep"/> are always invoked on the same dedicated thread owned by
    /// <see cref="KeepAwakeController"/> (the continuous execution-state requirement is bound to
    /// the calling thread's lifetime — see <see cref="PowerNativeMethods"/>).
    /// </summary>
    internal interface IKeepAwakeApi
    {
        /// <summary>Engage a continuous system + display keep-awake on the calling thread.</summary>
        bool PreventSleep();

        /// <summary>Clear the keep-awake on the calling thread.</summary>
        bool AllowSleep();
    }

    /// <summary>Production <see cref="IKeepAwakeApi"/> backed by <see cref="PowerNativeMethods"/>.</summary>
    internal sealed class Win32KeepAwakeApi : IKeepAwakeApi
    {
        public bool PreventSleep() => PowerNativeMethods.PreventSleep();
        public bool AllowSleep() => PowerNativeMethods.AllowSleep();
    }

    /// <summary>
    /// Holds a system + display keep-awake for the duration of the User-ESP (AccountSetup) phase
    /// so idle standby/sleep cannot stall app installs or account provisioning. Reboots are
    /// unaffected (the execution state only resets idle timers).
    /// <para>
    /// The hold is owned by a dedicated background thread: it calls
    /// <see cref="IKeepAwakeApi.PreventSleep"/>, parks on a release signal, then calls
    /// <see cref="IKeepAwakeApi.AllowSleep"/> and exits. A continuous requirement is released
    /// automatically when its owning thread (or the whole process) exits, so there is no way to
    /// leak a permanent "never sleep" state even if the agent crashes.
    /// </para>
    /// <para>Both <see cref="Engage"/> and <see cref="Release"/> are idempotent and thread-safe.</para>
    /// </summary>
    internal sealed class KeepAwakeController : IDisposable
    {
        private static readonly TimeSpan DefaultEngageReadyTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ReleaseJoinTimeout = TimeSpan.FromSeconds(5);

        private readonly IKeepAwakeApi _api;
        private readonly AgentLogger _logger;
        private readonly TimeSpan _engageReadyTimeout;
        private readonly object _sync = new object();

        private Thread? _holdThread;
        private ManualResetEventSlim? _releaseSignal;
        private bool _engaged;
        private volatile bool _lastPreventResult;
        private int _disposed;

        /// <param name="engageReadyTimeout">
        /// Test seam: how long <see cref="Engage"/> waits for the hold thread to confirm it applied
        /// the execution state before returning unconfirmed. Defaults to 5s in production.
        /// </param>
        public KeepAwakeController(AgentLogger logger, IKeepAwakeApi? api = null, TimeSpan? engageReadyTimeout = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _api = api ?? new Win32KeepAwakeApi();
            _engageReadyTimeout = engageReadyTimeout ?? DefaultEngageReadyTimeout;
        }

        public bool IsEngaged
        {
            get { lock (_sync) { return _engaged; } }
        }

        /// <summary>
        /// Engage the keep-awake hold. No-op (returns <c>false</c>) if already engaged or disposed.
        /// Otherwise returns whether the OS accepted the request (logged either way).
        /// </summary>
        public bool Engage()
        {
            ManualResetEventSlim ready;
            lock (_sync)
            {
                if (Volatile.Read(ref _disposed) == 1 || _engaged) return false;

                ready = new ManualResetEventSlim(false);
                var releaseLocal = new ManualResetEventSlim(false);
                _releaseSignal = releaseLocal;
                _holdThread = new Thread(() => HoldLoop(ready, releaseLocal))
                {
                    IsBackground = true,
                    Name = "AutopilotMonitor.KeepAwake",
                };
                _engaged = true;
                _holdThread.Start();
            }

            // Wait (off the lock) for the hold thread to apply the execution state so the caller
            // can report the real OS outcome. The PreventSleep syscall is sub-millisecond; the
            // timeout only guards against a pathological thread-start stall.
            var confirmed = ready.Wait(_engageReadyTimeout);
            if (confirmed)
            {
                // Wait returned true => HoldLoop has finished with `ready` (it does not touch it
                // after Set()), so disposal here cannot race the background thread.
                ready.Dispose();
            }
            else
            {
                // Pathological: the hold thread hasn't reached PreventSleep within the timeout
                // (e.g. ThreadPool/scheduler starvation under OOBE boot load). Do NOT dispose
                // `ready` — HoldLoop may still call ready.Set() afterwards. On net48
                // ManualResetEventSlim.Set() after Dispose() happens to be a benign no-op, but
                // disposing an object another thread is about to touch is fragile (and would throw
                // for wait primitives that reject post-dispose), so we simply leave `ready` for the
                // GC. The hold still applies once the thread is scheduled.
                _logger.Warning("KeepAwakeController: engage not confirmed within timeout — hold will apply once the background thread is scheduled.");
            }

            var ok = confirmed && _lastPreventResult;
            _logger.Info($"KeepAwakeController: keep-awake engaged (confirmed={confirmed}, osAccepted={ok}).");
            return ok;
        }

        /// <summary>
        /// Release the keep-awake hold. No-op (returns <c>false</c>) if not currently engaged.
        /// Blocks until the hold thread has cleared the execution state (bounded join).
        /// </summary>
        public bool Release()
        {
            Thread? thread;
            ManualResetEventSlim? signal;
            lock (_sync)
            {
                if (!_engaged) return false;
                _engaged = false;
                thread = _holdThread;
                signal = _releaseSignal;
                _holdThread = null;
                _releaseSignal = null;
            }

            try { signal?.Set(); } catch { }
            try { thread?.Join(ReleaseJoinTimeout); } catch { }
            try { signal?.Dispose(); } catch { }

            _logger.Info("KeepAwakeController: keep-awake released.");
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Release();
        }

        private void HoldLoop(ManualResetEventSlim ready, ManualResetEventSlim releaseSignal)
        {
            bool ok = false;
            try { ok = _api.PreventSleep(); }
            catch (Exception ex) { _logger.Error("KeepAwakeController: PreventSleep threw.", ex); }
            _lastPreventResult = ok;
            ready.Set();

            try { releaseSignal.Wait(); }
            catch { /* disposed during shutdown — fall through to clear the state */ }

            try { _api.AllowSleep(); }
            catch (Exception ex) { _logger.Error("KeepAwakeController: AllowSleep threw.", ex); }
        }
    }
}

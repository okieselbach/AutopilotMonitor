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
    /// Hybrid-identity host: the JoinInfo watcher plus the two single-shot detectors that
    /// describe the user side of a Hybrid Azure AD Join — sign-in overdue
    /// (<see cref="HybridLoginPendingDetector"/>) and Entra user affinity after the sign-in
    /// (<see cref="EntraUserAffinityDetector"/>). The composition root feeds it the
    /// DesktopArrivalDetector's real-user observation and the ImeLogTracker's raw token lines.
    /// </summary>
    internal sealed class AadJoinHost : ICollectorHost
    {
        public string Name => "AadJoinWatcher";

        private readonly AadJoinWatcher _watcher;
        private readonly AadJoinWatcherAdapter _adapter;
        private readonly HybridLoginPendingDetector _hybridLoginPendingDetector;
        private readonly EntraUserAffinityDetector _userAffinityDetector;
        private int _disposed;

        public AadJoinHost(
            AgentLogger logger,
            ISignalIngressSink ingress,
            IClock clock,
            Action? onRealUserJoined = null,
            Func<bool>? isHybridJoinProbe = null)
        {
            _watcher = new AadJoinWatcher(logger);
            _adapter = new AadJoinWatcherAdapter(_watcher, ingress, clock, onRealUserJoined: onRealUserJoined);
            var post = new InformationalEventPost(ingress, clock);
            _hybridLoginPendingDetector = new HybridLoginPendingDetector(
                watcher: _watcher,
                post: post,
                logger: logger);
            _userAffinityDetector = new EntraUserAffinityDetector(
                post: post,
                logger: logger,
                isHybridJoinProbe: isHybridJoinProbe ?? (() => false),
                placeholderActiveProbe: () => _watcher.PlaceholderObservedWithoutRealUser,
                utcNow: () => clock.UtcNow);
        }

        public void Start() => _watcher.Start();
        public void Stop() => _watcher.Stop();

        /// <summary>
        /// Composition-root entry point for the Hybrid User-Driven sign-in-overdue warning
        /// (2026-05-01, semantics 2026-09-04). Only call when all prerequisites hold: a) the
        /// prior agent process was killed by an OS reboot (<c>previousExitType=reboot_kill</c>),
        /// b) the Autopilot profile is Hybrid AAD Join (<c>isHybridJoin=true</c>), c) the device
        /// is not a WhiteGlove device (neither resuming Part 2 nor carrying a Part-1 archive).
        /// The detector itself enforces single-shot semantics and both cancel paths —
        /// repeated calls or late calls after a desktop / real user are safe no-ops.
        /// </summary>
        public void ArmHybridLoginPendingDetector() => _hybridLoginPendingDetector.Arm();

        /// <summary>
        /// The DesktopArrivalDetector resolved explorer.exe under a real user: cancels the
        /// sign-in-overdue timer and arms the user-affinity timer (Hybrid devices only).
        /// </summary>
        public void NotifyRealUserDesktop()
        {
            try { _hybridLoginPendingDetector.NotifyRealUserDesktop(); }
            catch (Exception ex) { LogCallbackFailure("HybridLoginPendingDetector.NotifyRealUserDesktop", ex); }
            try { _userAffinityDetector.NotifyRealUserDesktop(); }
            catch (Exception ex) { LogCallbackFailure("EntraUserAffinityDetector.NotifyRealUserDesktop", ex); }
        }

        /// <summary>ImeLogTracker observed an IME-TOKEN-SUCCESS line (raw, every line).</summary>
        public void NotifyUserTokenAcquired(DateTime? lineUtc)
        {
            try { _userAffinityDetector.NotifyUserTokenAcquired(lineUtc); }
            catch (Exception ex) { LogCallbackFailure("EntraUserAffinityDetector.NotifyUserTokenAcquired", ex); }
        }

        /// <summary>ImeLogTracker observed an IME-TOKEN-FAILURE line (raw, every line).</summary>
        public void NotifyTokenFailureLine(string? errorCode, DateTime? lineUtc)
        {
            try { _userAffinityDetector.NotifyTokenFailureLine(errorCode, lineUtc); }
            catch (Exception ex) { LogCallbackFailure("EntraUserAffinityDetector.NotifyTokenFailureLine", ex); }
        }

        // Test seams — expose the detectors for unit tests in the V2.Core.Tests project.
        internal HybridLoginPendingDetector HybridLoginPendingDetectorForTest => _hybridLoginPendingDetector;
        internal EntraUserAffinityDetector UserAffinityDetectorForTest => _userAffinityDetector;

        private static void LogCallbackFailure(string what, Exception ex)
        {
            // Host callbacks are best-effort — a detector fault must never break the caller
            // (DesktopArrivalDetector poll / ImeLogTracker drain). Swallowed on purpose; the
            // detectors log their own state transitions.
            _ = what; _ = ex;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _userAffinityDetector.Dispose(); } catch { }
            try { _hybridLoginPendingDetector.Dispose(); } catch { }
            try { _adapter.Dispose(); } catch { }
            try { _watcher.Dispose(); } catch { }
        }
    }
}

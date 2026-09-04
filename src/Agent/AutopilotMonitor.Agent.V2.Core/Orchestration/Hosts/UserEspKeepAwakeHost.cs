#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Opt-in host (per-tenant <c>AnalyzerConfiguration.KeepAwakeDuringUserEsp</c>, default off)
    /// that keeps the device awake for the duration of the User-ESP (AccountSetup) phase so idle
    /// standby/sleep cannot stall app installs or account provisioning. Reboots are unaffected —
    /// the hold only resets the system + display idle timers.
    /// <para>
    /// It observes the central signal rail (<see cref="SignalIngress.SignalPosted"/>, the same
    /// hook <c>GatherRuleExecutorHost</c> uses for phase_change triggers):
    /// <list type="bullet">
    ///   <item><b>Engage</b> when a signal reports the ESP phase as <c>AccountSetup</c>.</item>
    ///   <item><b>Release</b> on <see cref="DecisionSignalKind.EspExiting"/> once the AccountSetup
    ///     registry shows progress (the real user-ESP page exit — see below), on
    ///     <see cref="Stop"/> (session teardown), or on the safety-cap timer (a backstop so a
    ///     device is never held awake for the agent's full max-lifetime).</item>
    /// </list>
    /// The hold itself is owned by <see cref="KeepAwakeController"/>; the OS also auto-clears it
    /// on process exit / reboot, so it can never leak.
    /// </para>
    /// <para>
    /// <b>Why the ESP exit and not <see cref="DecisionSignalKind.AccountSetupProvisioningComplete"/></b>
    /// (backlog q8n, 2026-09-04): that completion signal is, fleet-wide, almost always the
    /// user-apps-settled synthesis (Windows no longer writes <c>categorySucceeded</c>), and the
    /// synthesis builds on the Device→Account handoff 62407 — so it fired a median 3 min before
    /// the user-ESP page actually closed in 77 % of keep-awake sessions, and a device on battery
    /// dropped into Modern Standby with the page still up (session a2256107). The page itself is
    /// the truth: a Shell-Core 62407 that arrives after the AccountSetup registry left
    /// <c>notStarted</c> is the user-ESP page exit (progress precedes it by ≥ 1 min across the
    /// fleet), whereas the handoff exit always precedes any progress. Desktop arrival is NOT a
    /// substitute — explorer.exe already runs under the real user while the page is still up.
    /// Sessions that only ever see the handoff exit (about one in five) keep the hold until the
    /// safety cap; that is the advertised behaviour ("awake during User-ESP") erring long, never
    /// early. The completion guards in <c>EspAndHelloTracker</c> deliberately stay on presence
    /// semantics — see <c>ProvisioningStatusTracker.HasAccountSetupActivity</c>.
    /// </para>
    /// <para>
    /// <b>Safety-cap sizing</b>: rather than a fixed product assumption, the cap is derived at
    /// engage time from the tenant's own ESP policy — the Intune
    /// <c>SyncFailureTimeout</c> ("show error when installation takes longer than X minutes",
    /// read via <see cref="EspSkipConfigurationProbe"/>) plus
    /// <see cref="EspCapMarginMinutes"/>. A legitimate User-ESP cannot run longer than ESP itself
    /// tolerates before it errors / offers "continue anyway", so <c>SyncFailureTimeout + margin</c>
    /// always outlives a real run while still bounding a hung one. Reading at engage time (rather
    /// than host construction) means the ESP policy is already present in the registry. When the
    /// value is unavailable (ESP skipped, WDP v2 with no ESP, or unreadable) the cap falls back to
    /// <see cref="DefaultFallbackCapMinutes"/>. The resolved cap and its source are reported on the
    /// <c>keep_awake_engaged</c> event for fleet-wide observability.
    /// </para>
    /// </summary>
    internal sealed class UserEspKeepAwakeHost : ICollectorHost
    {
        public string Name => "UserEspKeepAwake";

        internal const string Source = "UserEspKeepAwake";

        /// <summary>
        /// Fallback cap (minutes) when the ESP <c>SyncFailureTimeout</c> is unavailable. Equals the
        /// Intune ESP default timeout (60) + <see cref="EspCapMarginMinutes"/>.
        /// </summary>
        internal const int DefaultFallbackCapMinutes = 90;

        /// <summary>
        /// Added to the ESP <c>SyncFailureTimeout</c> to cover the window after ESP errors out: the
        /// "continue anyway" → desktop settling and the completion-signal propagation that follow.
        /// </summary>
        internal const int EspCapMarginMinutes = 30;

        // Release reasons (carried on the keep_awake_released event payload). "account_setup_complete"
        // (release on the synthesized completion signal) was retired with q8n — see the class doc.
        internal const string ReasonUserEspExited = "user_esp_exited";
        internal const string ReasonHostStop = "host_stop";
        internal const string ReasonSafetyCap = "safety_cap";

        // Cap-source labels (carried on the keep_awake_engaged event payload).
        internal const string CapSourceEspTimeout = "esp_timeout";
        internal const string CapSourceDefault = "default";
        internal const string CapSourceOverride = "override";

        private static readonly string AccountSetupPhaseName = EnrollmentPhase.AccountSetup.ToString();

        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly AgentLogger _logger;
        private readonly InformationalEventPost _post;
        private readonly KeepAwakeController _controller;
        private readonly SignalIngress? _observableIngress;
        private readonly int _fallbackCapMinutes;
        private readonly TimeSpan? _safetyCapOverride;
        private readonly Func<int?> _espTimeoutProvider;
        private readonly Func<bool> _accountSetupProgressProbe;

        private enum HoldState { Idle, Engaging, Engaged, Released }

        private readonly object _gate = new object();
        private Action<DecisionSignalKind, IReadOnlyDictionary<string, string>?>? _signalPostedHandler;
        private Timer? _safetyCapTimer;
        private HoldState _state = HoldState.Idle;  // guarded by _gate
        private int _activeCapMinutes;              // guarded by _gate — for the cap-elapsed log line
        private int _disposed;

        public UserEspKeepAwakeHost(
            string sessionId,
            string tenantId,
            ISignalIngressSink ingress,
            IClock clock,
            AgentLogger logger,
            int fallbackCapMinutes = DefaultFallbackCapMinutes,
            KeepAwakeController? controller = null,
            TimeSpan? safetyCapOverride = null,
            Func<int?>? espTimeoutProvider = null,
            Func<bool>? accountSetupProgressProbe = null)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _sessionId = sessionId ?? string.Empty;
            _tenantId = tenantId ?? string.Empty;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _post = new InformationalEventPost(ingress, clock, logger);
            _controller = controller ?? new KeepAwakeController(logger);
            _observableIngress = ingress as SignalIngress;
            _fallbackCapMinutes = fallbackCapMinutes > 0 ? fallbackCapMinutes : DefaultFallbackCapMinutes;
            // safetyCapOverride is a test seam for a fixed (e.g. sub-second) cap.
            _safetyCapOverride = safetyCapOverride;
            // The ESP SyncFailureTimeout is read at engage time (the ESP policy is present by then).
            _espTimeoutProvider = espTimeoutProvider
                ?? (() => EspSkipConfigurationProbe.ReadFull(_logger).SyncFailureTimeoutMinutes);
            // Without a probe no exit can be told apart from the handoff exit — the hold then
            // lasts until the safety cap (erring long, never early). Production always wires the
            // EspAndHelloHost probe (DefaultComponentFactory).
            _accountSetupProgressProbe = accountSetupProgressProbe ?? (() => false);
        }

        /// <summary>
        /// Pure cap-resolution policy (testable in isolation): derive the safety cap from the ESP
        /// <c>SyncFailureTimeout</c> when present (timeout + <see cref="EspCapMarginMinutes"/>),
        /// otherwise the fallback. A non-positive ESP value is treated as "unavailable".
        /// </summary>
        internal static (int capMinutes, string capSource) ResolveSafetyCapMinutes(
            int? espSyncFailureTimeoutMinutes,
            int fallbackCapMinutes)
        {
            if (espSyncFailureTimeoutMinutes.HasValue && espSyncFailureTimeoutMinutes.Value > 0)
                return (espSyncFailureTimeoutMinutes.Value + EspCapMarginMinutes, CapSourceEspTimeout);
            return (fallbackCapMinutes, CapSourceDefault);
        }

        public void Start()
        {
            if (_observableIngress == null)
            {
                // Non-SignalIngress sink (test fakes) — no phase stream to observe; degrade off.
                _logger.Info("UserEspKeepAwakeHost: ingress is not observable — keep-awake disabled this run.");
                return;
            }

            if (_signalPostedHandler == null)
            {
                _signalPostedHandler = OnSignalPosted;
                _observableIngress.SignalPosted += _signalPostedHandler;
            }
            _logger.Info($"UserEspKeepAwakeHost: started (cap derived from ESP SyncFailureTimeout at engage, fallback={_fallbackCapMinutes} min).");
        }

        /// <summary>
        /// Translates posted signals into engage/release. Must be fast and must not throw (the
        /// ingress swallows handler exceptions, but we still keep this off the hot path): the
        /// actual engage/release work — which briefly blocks on the keep-awake thread — is
        /// dispatched to the ThreadPool.
        /// </summary>
        private void OnSignalPosted(DecisionSignalKind kind, IReadOnlyDictionary<string, string>? payload)
        {
            // Fast, allocation-free triage on the posting thread; the blocking engage/release work
            // (which briefly waits on the keep-awake thread) is dispatched to the ThreadPool.
            try
            {
                if (kind == DecisionSignalKind.EspExiting)
                {
                    // Only the user-ESP page exit releases: the Device→Account handoff 62407 arrives
                    // before any AccountSetup subcategory has left notStarted, the real exit after.
                    // Cheap to skip while idle; the probe is a locked dictionary scan.
                    if (IsEngagedOrEngaging() && ProbeAccountSetupProgress())
                        ThreadPool.QueueUserWorkItem(_ => SafeRelease(ReasonUserEspExited, EventSeverity.Info));
                    return;
                }

                if (payload != null
                    && payload.TryGetValue(SignalPayloadKeys.EspPhase, out var phaseName)
                    && string.Equals(phaseName, AccountSetupPhaseName, StringComparison.OrdinalIgnoreCase))
                {
                    ThreadPool.QueueUserWorkItem(_ => SafeEngage());
                }
            }
            catch (Exception ex)
            {
                _logger.Verbose($"UserEspKeepAwakeHost: signal dispatch failed: {ex.Message}");
            }
        }

        private void SafeEngage()
        {
            try { Engage(); }
            catch (Exception ex) { _logger.Error("UserEspKeepAwakeHost: engage failed.", ex); }
        }

        private bool IsEngagedOrEngaging()
        {
            lock (_gate) { return _state == HoldState.Engaging || _state == HoldState.Engaged; }
        }

        private bool ProbeAccountSetupProgress()
        {
            try { return _accountSetupProgressProbe(); }
            catch (Exception ex)
            {
                _logger.Debug($"UserEspKeepAwakeHost: AccountSetup progress probe threw: {ex.Message}");
                return false;
            }
        }

        private void SafeRelease(string reason, EventSeverity severity)
        {
            try { Release(reason, severity, emit: true); }
            catch (Exception ex) { _logger.Error("UserEspKeepAwakeHost: release failed.", ex); }
        }

        /// <summary>Engage the hold exactly once (Idle → Engaging → Engaged). No-op from any other state.</summary>
        private void Engage()
        {
            int capMinutes;
            string capSource;
            TimeSpan cap;
            lock (_gate)
            {
                if (_state != HoldState.Idle) return;
                // L16 (delta review 2026-07-02): claim the transition, but run the controller's
                // Engage OUTSIDE the lock — it can block up to ~5 s on the keep-awake thread's
                // ready-wait, and holding _gate across that stalled a concurrent Stop→Release.
                _state = HoldState.Engaging;

                // Size the cap now (AccountSetup has started, so the ESP policy is in the registry).
                if (_safetyCapOverride.HasValue)
                {
                    cap = _safetyCapOverride.Value;
                    capMinutes = (int)Math.Round(cap.TotalMinutes);
                    capSource = CapSourceOverride;
                }
                else
                {
                    (capMinutes, capSource) = ResolveSafetyCapMinutes(SafeReadEspTimeoutMinutes(), _fallbackCapMinutes);
                    cap = TimeSpan.FromMinutes(capMinutes);
                }
            }

            var osAccepted = _controller.Engage(); // blocking — deliberately outside _gate (L16)

            lock (_gate)
            {
                if (_state != HoldState.Engaging)
                {
                    // A Release (stop / completion) won the race while the controller was
                    // engaging. The state is final — undo the OS hold we just acquired.
                    try { _controller.Release(); }
                    catch (Exception ex) { _logger.Warning($"UserEspKeepAwakeHost: rollback release failed: {ex.Message}"); }
                    return;
                }

                _activeCapMinutes = capMinutes;
                // One-shot backstop (NOT periodic): fire once after the cap, then release.
                _safetyCapTimer = new Timer(OnSafetyCapElapsed, null, cap, Timeout.InfiniteTimeSpan);
                _state = HoldState.Engaged;
            }

            Emit(Constants.EventTypes.KeepAwakeEngaged, EventSeverity.Info, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["osAccepted"] = osAccepted ? "true" : "false",
                ["scope"] = "system+display",
                ["capMinutes"] = capMinutes.ToString(),
                ["capSource"] = capSource,
            });
        }

        private int? SafeReadEspTimeoutMinutes()
        {
            try { return _espTimeoutProvider(); }
            catch (Exception ex)
            {
                _logger.Debug($"UserEspKeepAwakeHost: ESP SyncFailureTimeout read threw: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Release the hold exactly once (Idle/Engaging/Engaged → Released). Idempotent — a second
        /// call (or a competing completion / cap / stop trigger) is a no-op. A release that lands
        /// while an Engage is mid-flight (Engaging) wins: the engage path detects the final state
        /// after its blocking controller call and rolls the OS hold back (L16).
        /// </summary>
        private void Release(string reason, EventSeverity severity, bool emit)
        {
            bool wasEngaged;
            lock (_gate)
            {
                if (_state == HoldState.Released) return;
                DisarmSafetyCap();
                wasEngaged = _controller.Release();
                _state = HoldState.Released;
            }

            if (wasEngaged && emit)
            {
                Emit(Constants.EventTypes.KeepAwakeReleased, severity, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["reason"] = reason,
                });
            }
        }

        private void OnSafetyCapElapsed(object? state)
        {
            _logger.Warning($"UserEspKeepAwakeHost: safety cap ({_activeCapMinutes} min) elapsed without User-ESP completion — releasing keep-awake.");
            SafeRelease(ReasonSafetyCap, EventSeverity.Warning);
        }

        // Must be called under _gate.
        private void DisarmSafetyCap()
        {
            var timer = _safetyCapTimer;
            _safetyCapTimer = null;
            try { timer?.Dispose(); } catch { }
        }

        private void Emit(string eventType, EventSeverity severity, Dictionary<string, string> data)
        {
            try
            {
                // NOTE: deliberately no `phase:` argument — InformationalEventPost maps phase to the
                // same payload key ("phase") that the ESP phase stream uses, which would make this
                // lifecycle event re-trigger our own OnSignalPosted handler.
                _post.Emit(
                    eventType: eventType,
                    source: Source,
                    severity: severity,
                    immediateUpload: true,
                    data: data,
                    sourceOrigin: Source);
            }
            catch (Exception ex)
            {
                // Ingress may already be stopped during teardown — best-effort.
                _logger.Debug($"UserEspKeepAwakeHost: emit '{eventType}' threw: {ex.Message}");
            }
        }

        public void Stop()
        {
            if (_observableIngress != null && _signalPostedHandler != null)
            {
                try { _observableIngress.SignalPosted -= _signalPostedHandler; }
                catch { /* best-effort unsubscribe during shutdown */ }
                _signalPostedHandler = null;
            }

            // Release synchronously on teardown. If a completion/cap release already won, this is a
            // no-op (state already Released). Emit is best-effort — the ingress may already be down.
            Release(ReasonHostStop, EventSeverity.Info, emit: true);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Stop();
            try { _controller.Dispose(); } catch { }
        }
    }
}

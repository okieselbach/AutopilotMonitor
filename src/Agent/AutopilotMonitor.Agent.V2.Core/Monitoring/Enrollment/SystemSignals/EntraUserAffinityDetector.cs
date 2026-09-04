#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared.Models;
using SharedConstants = AutopilotMonitor.Shared.Constants;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Single-shot detector for Entra user affinity on Hybrid Azure AD Join devices
    /// (2026-09-04, session a7140f98). After the real user signs in, IME acquires that user's
    /// token through WAM and logs "Successfully get the token" (IntuneTokenManager,
    /// <c>IME-TOKEN-SUCCESS</c>). That line is the only SYSTEM-visible proof that the user
    /// session carries an Entra identity (PRT); a session whose user never gets one shows only
    /// "Failed to get AAD token" lines after the desktop.
    /// <para>
    /// Contract: <see cref="NotifyRealUserDesktop"/> arms the timer when the device is a Hybrid
    /// join (probe injected by the host). The first token success stamped after the desktop
    /// cancels the timer and emits <c>ime_user_token_acquired</c>; the timer emits
    /// <c>entra_user_affinity_pending</c> with the failure codes observed since the desktop.
    /// A token that arrives after the timer fired is still posted once — it tells the
    /// timeline (and the backend rule) that affinity came late rather than never.
    /// Both emissions happen at most once per agent process — a reboot starts a new process,
    /// whose repeat is exactly the confidence signal the backend rule counts. Token lines older
    /// than the desktop (minus a small tolerance for the 30 s desktop poll) belong to the
    /// device phase and are ignored: they appear in every enrollment.
    /// </para>
    /// <para>
    /// Not a decision signal. Never influences completion. The JoinInfo placeholder is reported
    /// as a fact in the payload because it is NOT a login signal on a Hybrid join — the AD
    /// sign-in never replaces it; only the later Entra device registration does.
    /// </para>
    /// </summary>
    internal sealed class EntraUserAffinityDetector : IDisposable
    {
        internal const int DefaultDelayMinutes = 10;
        internal const string SourceLabel = "EntraUserAffinityDetector";
        /// <summary>Token lines this much older than the desktop observation still count as post-desktop.</summary>
        internal static readonly TimeSpan DesktopPollTolerance = TimeSpan.FromMinutes(2);

        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;
        private readonly Func<bool> _isHybridJoinProbe;
        private readonly Func<bool>? _placeholderActiveProbe;
        private readonly Func<DateTime> _utcNow;
        private readonly TimeSpan _delay;
        private readonly object _lock = new object();
        private readonly List<string> _failureCodesSinceDesktop = new List<string>();

        private Timer? _timer;
        private DateTime? _desktopObservedUtc;
        private bool _armed;
        private bool _fired;
        private bool _tokenAcquiredPosted;
        private int _failureCountSinceDesktop;
        private int _disposed;

        internal EntraUserAffinityDetector(
            InformationalEventPost post,
            AgentLogger logger,
            Func<bool> isHybridJoinProbe,
            Func<bool>? placeholderActiveProbe = null,
            TimeSpan? delay = null,
            Func<DateTime>? utcNow = null)
        {
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _isHybridJoinProbe = isHybridJoinProbe ?? throw new ArgumentNullException(nameof(isHybridJoinProbe));
            _placeholderActiveProbe = placeholderActiveProbe;
            _delay = delay ?? TimeSpan.FromMinutes(DefaultDelayMinutes);
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// The DesktopArrivalDetector resolved explorer.exe under a real (non-placeholder,
        /// non-system) user. Arms the timer on Hybrid devices; idempotent; no-op after a
        /// token success or after the timer fired.
        /// </summary>
        public void NotifyRealUserDesktop()
        {
            bool hybrid;
            try { hybrid = _isHybridJoinProbe(); }
            catch (Exception ex)
            {
                _logger.Warning($"EntraUserAffinityDetector: hybrid probe threw — not arming: {ex.Message}");
                return;
            }

            lock (_lock)
            {
                if (_disposed != 0 || _armed || _fired || _tokenAcquiredPosted) return;
                if (!hybrid)
                {
                    _logger.Debug("EntraUserAffinityDetector: real-user desktop on a non-Hybrid device — not armed");
                    return;
                }

                _desktopObservedUtc = _utcNow();
                _armed = true;
                _logger.Info(
                    $"EntraUserAffinityDetector: armed at real-user desktop — will emit {SharedConstants.EventTypes.EntraUserAffinityPending} in {_delay.TotalMinutes:F0} min unless IME acquires a user token");
                _timer = new Timer(OnTimer, null, _delay, Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>IME-TOKEN-SUCCESS line observed; <paramref name="lineUtc"/> is the line's resolved time.</summary>
        public void NotifyUserTokenAcquired(DateTime? lineUtc)
        {
            DateTime desktop;
            double minutesAfterDesktop;
            lock (_lock)
            {
                if (_disposed != 0 || !_armed || _tokenAcquiredPosted) return;
                desktop = _desktopObservedUtc!.Value;
                if (!IsAfterDesktop(lineUtc, desktop)) return;

                _tokenAcquiredPosted = true;
                _timer?.Dispose();
                _timer = null;
                minutesAfterDesktop = ((lineUtc ?? _utcNow()) - desktop).TotalMinutes;
            }

            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["minutesAfterDesktop"] = Math.Max(0, minutesAfterDesktop).ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                ["tokenFailuresBeforeSuccess"] = _failureCountSinceDesktop.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["isHybridJoin"] = "true",
            };

            _post.Emit(
                eventType: SharedConstants.EventTypes.ImeUserTokenAcquired,
                source: SourceLabel,
                message: "Entra user token acquired by IME after the real-user desktop — user affinity established",
                severity: EventSeverity.Info,
                immediateUpload: false,
                data: data,
                occurredAtUtc: lineUtc);

            _logger.Info($"EntraUserAffinityDetector: user token observed {minutesAfterDesktop:F1} min after desktop — timer cancelled");
        }

        /// <summary>IME-TOKEN-FAILURE line observed; counted only when stamped after the desktop.</summary>
        public void NotifyTokenFailureLine(string? errorCode, DateTime? lineUtc)
        {
            lock (_lock)
            {
                if (_disposed != 0 || !_armed || _fired || _tokenAcquiredPosted) return;
                if (!IsAfterDesktop(lineUtc, _desktopObservedUtc!.Value)) return;

                _failureCountSinceDesktop++;
                var code = string.IsNullOrWhiteSpace(errorCode) ? "unknown" : errorCode!.Trim();
                if (!_failureCodesSinceDesktop.Contains(code, StringComparer.OrdinalIgnoreCase))
                    _failureCodesSinceDesktop.Add(code);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            lock (_lock)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }

        // Test seams — same lock and state guards as the production paths.
        internal void TriggerFromTest() => OnTimer(null);
        internal bool IsArmedForTest { get { lock (_lock) { return _armed; } } }
        internal bool HasFiredForTest { get { lock (_lock) { return _fired; } } }
        internal bool TokenAcquiredPostedForTest { get { lock (_lock) { return _tokenAcquiredPosted; } } }

        private static bool IsAfterDesktop(DateTime? lineUtc, DateTime desktopUtc)
        {
            // A line without a timestamp cannot be placed — treat it as current (the tracker
            // only hands us lines it just read), which is after the desktop by construction.
            if (!lineUtc.HasValue) return true;
            return lineUtc.Value >= desktopUtc - DesktopPollTolerance;
        }

        private void OnTimer(object? state)
        {
            string codes;
            int count;
            double minutesSinceDesktop;
            lock (_lock)
            {
                if (_disposed != 0 || !_armed || _fired || _tokenAcquiredPosted) return;
                _fired = true;
                _timer?.Dispose();
                _timer = null;
                codes = string.Join(",", _failureCodesSinceDesktop);
                count = _failureCountSinceDesktop;
                minutesSinceDesktop = (_utcNow() - _desktopObservedUtc!.Value).TotalMinutes;
            }

            var placeholderActive = "unknown";
            try
            {
                if (_placeholderActiveProbe != null)
                    placeholderActive = _placeholderActiveProbe() ? "true" : "false";
            }
            catch { /* observational */ }

            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["delayMinutes"] = ((int)_delay.TotalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["reason"] = "timer_fired",
                ["minutesSinceDesktop"] = Math.Max(0, minutesSinceDesktop).ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                ["tokenFailureCount"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["tokenFailureCodes"] = codes,
                ["placeholderActive"] = placeholderActive,
                ["isHybridJoin"] = "true",
            };

            _post.Emit(
                eventType: SharedConstants.EventTypes.EntraUserAffinityPending,
                source: SourceLabel,
                message: $"Hybrid AAD Join: {(int)_delay.TotalMinutes} min after the real-user desktop IME has not acquired an Entra user token — user affinity pending"
                         + (count > 0 ? $" ({count} token failure(s): {codes})" : string.Empty),
                severity: EventSeverity.Warning,
                immediateUpload: true,
                data: data);

            _logger.Warning($"EntraUserAffinityDetector: emitted {SharedConstants.EventTypes.EntraUserAffinityPending} (failures={count}, codes={codes})");
        }
    }
}

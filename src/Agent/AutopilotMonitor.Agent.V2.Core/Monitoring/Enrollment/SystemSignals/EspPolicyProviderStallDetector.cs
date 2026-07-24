#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Turns the ESP policy-provider contract read by <see cref="EspPolicyProviderProbe"/> into
    /// the one-shot <c>esp_policy_provider_stalled</c> Warning via two independent arms, each
    /// with the same <see cref="DwellMinutes"/>-minute continuous-dwell rule and the full
    /// provider table in the payload:
    /// <list type="bullet">
    ///   <item><b>Arm 1 — <c>reason=provider_incomplete</c></b>: a registered provider stays
    ///     continuously incomplete (e.g. Sidecar itself never sets TrackingPoliciesCreated).</item>
    ///   <item><b>Arm 2 — <c>reason=sidecar_provider_missing</c></b>: at least one
    ///     <c>Setup\Apps</c> provider is registered but none of them is <c>Sidecar</c> (the IME).
    ///     This is the actual co-management field-case signature (issue #106): the foreign
    ///     <c>ConfigMgr</c> provider had <c>TrackingPoliciesCreated=1</c> — i.e. was "complete"
    ///     by the CSP value contract — yet the user ESP stayed parked at "Apps (Identifying)"
    ///     until the key was renamed to <c>Sidecar</c>. The ESP's Apps wait is keyed to the
    ///     Intune-registered provider by NAME, so provider completeness alone must not gate this
    ///     detection. See the probe's header for sources.</item>
    /// </list>
    /// <para>
    /// Dwell semantics: arm 1's clock per provider starts when it is first observed incomplete
    /// and RESETS when the provider completes or disappears; arm 2's clock starts when the
    /// sidecar-missing condition is first observed and RESETS when Sidecar registers (covers the
    /// legitimate "ConfigMgr registered before Sidecar" startup ordering) — only
    /// <i>continuously</i> bad states fire. A missing root key / empty provider list is normal
    /// early enrollment and clears all dwell state (probe D4). Purely observational: emission
    /// goes through <see cref="InformationalEventPost"/> (InformationalEvent pass-through,
    /// dispatch-guard exempt), never into decision state.
    /// </para>
    /// <para>
    /// Duplicate suppression: in-process one-shot per provider key, plus a
    /// <see cref="StartupEventGate"/> fingerprint over the sorted set of currently-stalled keys so
    /// an agent restart into the same stall does not re-report it — while a NEW provider joining
    /// the stall changes the fingerprint and is reported (the event carries the full list).
    /// </para>
    /// </summary>
    internal sealed class EspPolicyProviderStallDetector
    {
        /// <summary>
        /// Wall-clock minutes a provider must stay continuously incomplete before it is reported.
        /// Mirrors the CSP's own DevicePreparation default timeout; the Setup/Apps wait this
        /// tripwire mainly targets has no OS timeout at all.
        /// </summary>
        internal const int DwellMinutes = 15;

        internal const string SourceLabel = "EspPolicyProviderStallDetector";

        /// <summary>Payload <c>reason</c>: a registered provider stayed continuously incomplete (arm 1).</summary>
        internal const string ReasonProviderIncomplete = "provider_incomplete";

        /// <summary>Payload <c>reason</c>: Setup\Apps providers registered but Sidecar absent (arm 2 — dominant when both arms fire in one round).</summary>
        internal const string ReasonSidecarMissing = "sidecar_provider_missing";

        /// <summary>
        /// Sentinel token representing the arm-2 condition in the cross-restart fingerprint.
        /// Cannot collide with provider keys — those always contain <c>|</c> separators.
        /// </summary>
        private const string SidecarMissingFingerprintToken = "sidecarMissing";

        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;
        private readonly IClock _clock;
        private readonly StartupEventGate? _startupGate;
        private readonly TimeSpan _dwell;

        private readonly Dictionary<string, DateTime> _firstSeenIncompleteUtc =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _firedKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Arm-2 dwell state: first observation of "setupApps providers without Sidecar"; null = condition not active.</summary>
        private DateTime? _sidecarMissingSinceUtc;

        /// <summary>Arm-2 in-process one-shot latch (mirror of <see cref="_firedKeys"/>).</summary>
        private bool _sidecarMissingFired;

        public EspPolicyProviderStallDetector(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            IClock clock,
            StartupEventGate? startupGate = null,
            TimeSpan? dwell = null)
        {
            _sessionId = sessionId;
            _tenantId = tenantId;
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _startupGate = startupGate;
            _dwell = dwell ?? TimeSpan.FromMinutes(DwellMinutes);
        }

        /// <summary>Host timer callback and test seam — one probe + evaluation round.</summary>
        internal void Tick()
        {
            var snapshot = EspPolicyProviderProbe.Read(_logger);
            var eval = EvaluateStallTransition(
                snapshot, _firstSeenIncompleteUtc, _firedKeys,
                _sidecarMissingSinceUtc, _sidecarMissingFired, _clock.UtcNow, _dwell);

            _firstSeenIncompleteUtc.Clear();
            foreach (var kv in eval.UpdatedFirstSeenUtc)
                _firstSeenIncompleteUtc[kv.Key] = kv.Value;
            _sidecarMissingSinceUtc = eval.UpdatedSidecarMissingSinceUtc;

            if (eval.NewlyStalled.Count > 0 || eval.SidecarMissingStalledForMinutes != null)
                EmitStalled(snapshot, eval);
        }

        /// <summary>
        /// Pure evaluation of one probe round — the entire detection decision, testable without
        /// registry, timer, or ingress (pattern: <c>PerformanceCollector.EvaluateDiskLowTransition</c>).
        /// </summary>
        /// <param name="snapshot">Current provider snapshot.</param>
        /// <param name="firstSeenIncompleteUtc">Arm-1 dwell state from the previous round (provider key → first-seen-incomplete).</param>
        /// <param name="alreadyFired">Provider keys already reported this run (never re-report).</param>
        /// <param name="sidecarMissingSinceUtc">Arm-2 dwell state from the previous round; null = condition was not active.</param>
        /// <param name="sidecarMissingAlreadyFired">Arm-2 one-shot latch (never re-report).</param>
        /// <param name="nowUtc">Evaluation timestamp.</param>
        /// <param name="dwell">Continuous-condition threshold (shared by both arms).</param>
        internal static StallEvaluation EvaluateStallTransition(
            EspPolicyProviderSnapshot snapshot,
            IReadOnlyDictionary<string, DateTime> firstSeenIncompleteUtc,
            ISet<string> alreadyFired,
            DateTime? sidecarMissingSinceUtc,
            bool sidecarMissingAlreadyFired,
            DateTime nowUtc,
            TimeSpan dwell)
        {
            var result = new StallEvaluation();

            // Missing root key or no registered providers = normal early enrollment: clear all
            // dwell state (a condition must be continuously OBSERVED to accrue dwell).
            if (!snapshot.HasData || snapshot.Providers.Count == 0)
                return result;

            // Arm 1 — a registered provider continuously incomplete.
            foreach (var provider in snapshot.Providers)
            {
                if (provider.IsComplete)
                    continue; // completed → falls out of the dwell map (reset)

                var firstSeen = firstSeenIncompleteUtc.TryGetValue(provider.Key, out var seen)
                    ? seen
                    : nowUtc;
                result.UpdatedFirstSeenUtc[provider.Key] = firstSeen;

                var stalledFor = nowUtc - firstSeen;
                if (stalledFor >= dwell && !alreadyFired.Contains(provider.Key))
                    result.NewlyStalled.Add(new StalledProvider(provider, stalledFor.TotalMinutes));
            }

            // Arm 2 — Setup\Apps providers registered, but none of them is Sidecar. Deliberately
            // ignores provider completeness: the field case (issue #106) had ConfigMgr with
            // TrackingPoliciesCreated=1 and the ESP still waited. Sidecar registering later
            // (legitimate startup ordering) clears the condition and resets the clock.
            if (HasSetupAppsProvider(snapshot) && !snapshot.SidecarRegistered)
            {
                var since = sidecarMissingSinceUtc ?? nowUtc;
                result.UpdatedSidecarMissingSinceUtc = since;

                var missingFor = nowUtc - since;
                if (missingFor >= dwell && !sidecarMissingAlreadyFired)
                    result.SidecarMissingStalledForMinutes = missingFor.TotalMinutes;
            }

            return result;
        }

        private static bool HasSetupAppsProvider(EspPolicyProviderSnapshot snapshot)
            => snapshot.Providers.Any(p => p.Kind == EspPolicyProviderProbe.KindSetupApps);

        private void EmitStalled(EspPolicyProviderSnapshot snapshot, StallEvaluation eval)
        {
            foreach (var stalled in eval.NewlyStalled)
                _firedKeys.Add(stalled.Provider.Key);
            if (eval.SidecarMissingStalledForMinutes != null)
                _sidecarMissingFired = true;

            // Cross-restart dedup: fingerprint = sorted set of all fired conditions that are
            // still active (incomplete providers + the sidecar-missing sentinel). A restart into
            // the same stall re-accrues dwell and lands on the same fingerprint → suppressed; a
            // new condition joining the stall changes it → re-emitted.
            var currentlyStalledKeys = snapshot.Providers
                .Where(p => !p.IsComplete && _firedKeys.Contains(p.Key))
                .Select(p => p.Key)
                .ToList();
            if (_sidecarMissingFired && HasSetupAppsProvider(snapshot) && !snapshot.SidecarRegistered)
                currentlyStalledKeys.Add(SidecarMissingFingerprintToken);
            currentlyStalledKeys.Sort(StringComparer.OrdinalIgnoreCase);
            var fingerprint = StartupEventGate.HashString(string.Join(";", currentlyStalledKeys));
            if (_startupGate != null &&
                !_startupGate.ShouldEmit(Constants.EventTypes.EspPolicyProviderStalled, fingerprint))
            {
                _logger?.Debug(
                    "EspPolicyProviderStallDetector: stalled provider set already reported by a " +
                    "previous agent run (fingerprint match) — suppressed.");
                return;
            }

            // Sidecar-missing is the dominant reason when both arms fire in one round: it
            // explains the ESP hang regardless of the foreign providers' completeness.
            var reason = eval.SidecarMissingStalledForMinutes != null
                ? ReasonSidecarMissing
                : ReasonProviderIncomplete;

            var messageParts = new List<string>();
            if (eval.SidecarMissingStalledForMinutes != null)
            {
                var registeredNames = string.Join(", ", snapshot.Providers
                    .Where(p => p.Kind == EspPolicyProviderProbe.KindSetupApps)
                    .Select(p => $"{p.Name} ({p.Scope})"));
                messageParts.Add(
                    $"expected IME provider '{EspPolicyProviderProbe.SidecarProviderName}' not registered for " +
                    $">= {DwellMinutes} min while Setup/Apps providers exist: {registeredNames}");
            }
            var stalledNames = string.Join(", ",
                eval.NewlyStalled.Select(s => $"{s.Provider.Name} ({s.Provider.Scope}/{s.Provider.Kind})"));
            if (eval.NewlyStalled.Count > 0)
                messageParts.Add($"provider(s) incomplete for >= {DwellMinutes} min: {stalledNames}");

            var data = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["reason"] = reason,
                ["dwellMinutes"] = DwellMinutes,
                ["sidecarRegistered"] = snapshot.SidecarRegistered,
                ["providers"] = snapshot.Providers.Select(p => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = p.Name,
                    ["scope"] = p.Scope,
                    ["kind"] = p.Kind,
                    ["trackingPoliciesCreated"] = p.TrackingPoliciesCreated,
                    ["installationState"] = p.InstallationState,
                    ["complete"] = p.IsComplete,
                }).ToList(),
                ["stalledProviders"] = eval.NewlyStalled.Select(s => new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["name"] = s.Provider.Name,
                    ["scope"] = s.Provider.Scope,
                    ["kind"] = s.Provider.Kind,
                    ["stalledForMinutes"] = Math.Round(s.StalledForMinutes, 1),
                }).ToList(),
            };
            if (eval.SidecarMissingStalledForMinutes != null)
                data["sidecarMissingForMinutes"] = Math.Round(eval.SidecarMissingStalledForMinutes.Value, 1);

            var message =
                "ESP Setup/Apps tracking stalled: " + string.Join("; ", messageParts) +
                " — the ESP waits on its app-tracking providers without any timeout";

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                Timestamp = _clock.UtcNow,
                EventType = Constants.EventTypes.EspPolicyProviderStalled,
                Severity = EventSeverity.Warning,
                Source = SourceLabel,
                Phase = EnrollmentPhase.Unknown,
                ImmediateUpload = true,
                Message = message,
                Data = data
            });
            _startupGate?.MarkEmitted(Constants.EventTypes.EspPolicyProviderStalled);

            _logger?.Warning(
                $"EspPolicyProviderStallDetector: {string.Join("; ", messageParts)} " +
                $"(reason={reason}, sidecarRegistered={snapshot.SidecarRegistered}).");
        }

        /// <summary>Result of one <see cref="EvaluateStallTransition"/> round.</summary>
        internal sealed class StallEvaluation
        {
            /// <summary>Next round's arm-1 dwell state — only currently-incomplete providers survive.</summary>
            public Dictionary<string, DateTime> UpdatedFirstSeenUtc { get; } =
                new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Providers that crossed the dwell threshold this round and were not yet reported (arm 1).</summary>
            public List<StalledProvider> NewlyStalled { get; } = new List<StalledProvider>();

            /// <summary>Next round's arm-2 dwell state; null = sidecar-missing condition not active (reset).</summary>
            public DateTime? UpdatedSidecarMissingSinceUtc { get; set; }

            /// <summary>Non-null when the sidecar-missing condition crossed the dwell threshold this round and was not yet reported (arm 2).</summary>
            public double? SidecarMissingStalledForMinutes { get; set; }
        }

        internal readonly struct StalledProvider
        {
            public StalledProvider(PolicyProviderState provider, double stalledForMinutes)
            {
                Provider = provider;
                StalledForMinutes = stalledForMinutes;
            }

            public PolicyProviderState Provider { get; }
            public double StalledForMinutes { get; }
        }
    }
}

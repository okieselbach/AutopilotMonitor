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
    /// the one-shot <c>esp_policy_provider_stalled</c> Warning: a registered provider that stays
    /// continuously incomplete for <see cref="DwellMinutes"/> minutes is reported once, with the
    /// full provider table in the payload. See the probe's header for the CSP contract, the
    /// official sources, and the co-management field case (ConfigMgr registered without Sidecar
    /// → user ESP parked at "Apps (Identifying)" with no OS timeout) this tripwire exists for.
    /// <para>
    /// Dwell semantics: the dwell clock per provider starts when it is first observed incomplete
    /// and RESETS when the provider completes or disappears — only <i>continuously</i> incomplete
    /// providers fire. A missing root key / empty provider list is normal early enrollment and
    /// clears all dwell state (probe D4). Purely observational: emission goes through
    /// <see cref="InformationalEventPost"/> (InformationalEvent pass-through, dispatch-guard
    /// exempt), never into decision state.
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
            var eval = EvaluateStallTransition(snapshot, _firstSeenIncompleteUtc, _firedKeys, _clock.UtcNow, _dwell);

            _firstSeenIncompleteUtc.Clear();
            foreach (var kv in eval.UpdatedFirstSeenUtc)
                _firstSeenIncompleteUtc[kv.Key] = kv.Value;

            if (eval.NewlyStalled.Count > 0)
                EmitStalled(snapshot, eval);
        }

        /// <summary>
        /// Pure evaluation of one probe round — the entire detection decision, testable without
        /// registry, timer, or ingress (pattern: <c>PerformanceCollector.EvaluateDiskLowTransition</c>).
        /// </summary>
        /// <param name="snapshot">Current provider snapshot.</param>
        /// <param name="firstSeenIncompleteUtc">Dwell state from the previous round (provider key → first-seen-incomplete).</param>
        /// <param name="alreadyFired">Provider keys already reported this run (never re-report).</param>
        /// <param name="nowUtc">Evaluation timestamp.</param>
        /// <param name="dwell">Continuous-incompleteness threshold.</param>
        internal static StallEvaluation EvaluateStallTransition(
            EspPolicyProviderSnapshot snapshot,
            IReadOnlyDictionary<string, DateTime> firstSeenIncompleteUtc,
            ISet<string> alreadyFired,
            DateTime nowUtc,
            TimeSpan dwell)
        {
            var result = new StallEvaluation();

            // Missing root key or no registered providers = normal early enrollment: clear all
            // dwell state (a provider must be continuously OBSERVED incomplete to accrue dwell).
            if (!snapshot.HasData || snapshot.Providers.Count == 0)
                return result;

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

            return result;
        }

        private void EmitStalled(EspPolicyProviderSnapshot snapshot, StallEvaluation eval)
        {
            foreach (var stalled in eval.NewlyStalled)
                _firedKeys.Add(stalled.Provider.Key);

            // Cross-restart dedup: fingerprint = sorted set of all fired keys that are still
            // incomplete. A restart into the same stall re-accrues dwell and lands on the same
            // fingerprint → suppressed; a new provider joining the stall changes it → re-emitted.
            var currentlyStalledKeys = snapshot.Providers
                .Where(p => !p.IsComplete && _firedKeys.Contains(p.Key))
                .Select(p => p.Key)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var fingerprint = StartupEventGate.HashString(string.Join(";", currentlyStalledKeys));
            if (_startupGate != null &&
                !_startupGate.ShouldEmit(Constants.EventTypes.EspPolicyProviderStalled, fingerprint))
            {
                _logger?.Debug(
                    "EspPolicyProviderStallDetector: stalled provider set already reported by a " +
                    "previous agent run (fingerprint match) — suppressed.");
                return;
            }

            var stalledNames = string.Join(", ",
                eval.NewlyStalled.Select(s => $"{s.Provider.Name} ({s.Provider.Scope}/{s.Provider.Kind})"));

            var data = new Dictionary<string, object>(StringComparer.Ordinal)
            {
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
                Message = $"ESP policy provider(s) incomplete for >= {DwellMinutes} min: {stalledNames} " +
                          $"(sidecarRegistered={snapshot.SidecarRegistered}) — the ESP waits on " +
                          "Setup/Apps providers without any timeout",
                Data = data
            });
            _startupGate?.MarkEmitted(Constants.EventTypes.EspPolicyProviderStalled);

            _logger?.Warning(
                $"EspPolicyProviderStallDetector: {stalledNames} incomplete for >= {DwellMinutes} min " +
                $"(sidecarRegistered={snapshot.SidecarRegistered}).");
        }

        /// <summary>Result of one <see cref="EvaluateStallTransition"/> round.</summary>
        internal sealed class StallEvaluation
        {
            /// <summary>Next round's dwell state — only currently-incomplete providers survive.</summary>
            public Dictionary<string, DateTime> UpdatedFirstSeenUtc { get; } =
                new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Providers that crossed the dwell threshold this round and were not yet reported.</summary>
            public List<StalledProvider> NewlyStalled { get; } = new List<StalledProvider>();
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

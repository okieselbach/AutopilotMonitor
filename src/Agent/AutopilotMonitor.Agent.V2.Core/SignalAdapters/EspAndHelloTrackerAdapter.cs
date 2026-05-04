#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.SignalAdapters
{
    /// <summary>
    /// Adapter for <see cref="EspAndHelloTracker"/> — der Coordinator, den der V2-Orchestrator
    /// in Produktion tatsächlich aufsetzt. Plan §2.1a / §2.2.
    /// <para>
    /// EspAndHelloTracker re-raised Events aus den privaten Sub-Trackern (<c>HelloTracker</c>,
    /// <c>ShellCoreTracker</c>, <c>ProvisioningStatusTracker</c>, <c>ModernDeploymentTracker</c>).
    /// In Produktion sind die Sub-Tracker nicht direkt zugänglich, daher kommt das Mapping
    /// hier im Coordinator-Adapter an.
    /// </para>
    /// <para>
    /// Signal-Mapping identisch zu den Sub-Tracker-Adaptern (Single-Tracker-Wiring-Szenarien):
    /// <list type="bullet">
    ///   <item><c>HelloCompleted</c> → <see cref="DecisionSignalKind.HelloResolved"/></item>
    ///   <item><c>FinalizingSetupPhaseTriggered</c> → <see cref="DecisionSignalKind.EspPhaseChanged"/> (phase=FinalizingSetup)</item>
    ///   <item><c>WhiteGloveCompleted</c> → <see cref="DecisionSignalKind.WhiteGloveShellCoreSuccess"/></item>
    ///   <item><c>EspFailureDetected</c> → <see cref="DecisionSignalKind.EspTerminalFailure"/> (merged aus ShellCore + Provisioning)</item>
    ///   <item><c>DeviceSetupProvisioningComplete</c> → <see cref="DecisionSignalKind.DeviceSetupProvisioningComplete"/></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Tech-Debt (M4.5-Cleanup-Kandidat):</b> Signal-Mapping-Logik ist dupliziert zu den
    /// Sub-Tracker-Adaptern. Refactoring-Idee: <c>SignalAdapterEmitters</c>-Static-Helper oder
    /// gemeinsame Mapping-Interfaces auf Tracker-Seite. Pragmatisch erst in M4.5 oder wenn ein
    /// dritter Konsument dazu kommt.
    /// </para>
    /// </summary>
    internal sealed class EspAndHelloTrackerAdapter : IDisposable
    {
        private const string SourceOrigin = "EspAndHelloTracker";
        private const string DetectorId = "esp-and-hello-tracker-v1";

        private readonly EspAndHelloTracker _coordinator;
        private readonly ISignalIngressSink _ingress;
        private readonly IClock _clock;

        private bool _helloPosted;
        private bool _finalizingPosted;
        private bool _whiteGloveSuccessPosted;
        private bool _espFailurePosted;
        private bool _deviceSetupCompletePosted;
        private bool _helloPolicyPosted;

        /// <summary>Tracked HelloOutcome (read via event; coordinator exposes property).</summary>
        public EspAndHelloTrackerAdapter(
            EspAndHelloTracker coordinator,
            ISignalIngressSink ingress,
            IClock clock)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));

            _coordinator.HelloCompleted += OnHelloCompleted;
            _coordinator.FinalizingSetupPhaseTriggered += OnFinalizing;
            _coordinator.WhiteGloveCompleted += OnWhiteGloveCompleted;
            _coordinator.EspFailureDetected += OnEspFailure;
            _coordinator.DeviceSetupProvisioningComplete += OnDeviceSetupComplete;
            _coordinator.HelloPolicyDetected += OnHelloPolicyDetected;
        }

        public void Dispose()
        {
            _coordinator.HelloCompleted -= OnHelloCompleted;
            _coordinator.FinalizingSetupPhaseTriggered -= OnFinalizing;
            _coordinator.WhiteGloveCompleted -= OnWhiteGloveCompleted;
            _coordinator.EspFailureDetected -= OnEspFailure;
            _coordinator.DeviceSetupProvisioningComplete -= OnDeviceSetupComplete;
            _coordinator.HelloPolicyDetected -= OnHelloPolicyDetected;
        }

        private void OnHelloCompleted(object sender, EventArgs e) => EmitHello(ReadHelloOutcome());
        private void OnFinalizing(object sender, string reason) => EmitFinalizing(reason);
        private void OnWhiteGloveCompleted(object sender, EventArgs e) => EmitWhiteGlove();
        private void OnEspFailure(object sender, string failureType) => EmitEspFailure(failureType);
        private void OnDeviceSetupComplete(object sender, EventArgs e) => EmitDeviceSetupComplete();
        private void OnHelloPolicyDetected(bool helloEnabled, string source) => EmitHelloPolicy(helloEnabled, source);

        internal void TriggerHelloFromTest(string? helloOutcome) => EmitHello(helloOutcome);
        internal void TriggerFinalizingFromTest(string reason) => EmitFinalizing(reason);
        internal void TriggerWhiteGloveFromTest() => EmitWhiteGlove();
        internal void TriggerEspFailureFromTest(string failureType) => EmitEspFailure(failureType);
        internal void TriggerDeviceSetupCompleteFromTest() => EmitDeviceSetupComplete();
        internal void TriggerHelloPolicyDetectedFromTest(bool helloEnabled, string source) => EmitHelloPolicy(helloEnabled, source);

        /// <summary>
        /// Reads <c>HelloOutcome</c> from the coordinator's forwarded property
        /// (<see cref="EspAndHelloTracker.HelloOutcome"/> delegates to the inner
        /// <c>HelloTracker.HelloOutcome</c>). Plan §4.x M4.4.4. Falls back to
        /// <c>"unknown"</c> only if the coordinator hasn't resolved Hello yet (race between
        /// completion event and property read — should not happen in practice, but defensive).
        /// </summary>
        private string ReadHelloOutcome() => _coordinator.HelloOutcome ?? "unknown";

        /// <summary>Test-only hook to exercise <see cref="ReadHelloOutcome"/> + the full event-handler emit path without driving the inner HelloTracker's live event sources.</summary>
        internal void TriggerHelloFromCoordinatorPropertyForTest() => EmitHello(ReadHelloOutcome());

        private void EmitHello(string? helloOutcome)
        {
            if (_helloPosted) return;
            _helloPosted = true;

            var outcome = string.IsNullOrEmpty(helloOutcome) ? "unknown" : helloOutcome!;

            _ingress.Post(
                kind: DecisionSignalKind.HelloResolved,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: SourceOrigin,
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: DetectorId,
                    summary: $"Hello resolved via coordinator (outcome={outcome})",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["subSource"] = "HelloTracker",
                        [SignalPayloadKeys.HelloOutcome] = outcome,
                    }),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SignalPayloadKeys.HelloOutcome] = outcome,
                });
        }

        private void EmitFinalizing(string reason)
        {
            if (_finalizingPosted) return;
            _finalizingPosted = true;

            _ingress.Post(
                kind: DecisionSignalKind.EspPhaseChanged,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: SourceOrigin,
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: DetectorId,
                    summary: "ESP Finalizing phase triggered (coordinator-forwarded)",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["subSource"] = "ShellCoreTracker",
                        ["phaseReason"] = reason ?? string.Empty,
                    }),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SignalPayloadKeys.EspPhase] = EnrollmentPhase.FinalizingSetup.ToString(),
                    ["reason"] = reason ?? string.Empty,
                });
        }

        private void EmitWhiteGlove()
        {
            if (_whiteGloveSuccessPosted) return;
            _whiteGloveSuccessPosted = true;

            _ingress.Post(
                kind: DecisionSignalKind.WhiteGloveShellCoreSuccess,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: SourceOrigin,
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: DetectorId,
                    summary: "WhiteGlove sealing success (coordinator-forwarded)",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["subSource"] = "ShellCoreTracker",
                    }));
        }

        private void EmitEspFailure(string failureType)
        {
            if (_espFailurePosted) return;
            _espFailurePosted = true;

            var safeType = string.IsNullOrEmpty(failureType) ? "unknown" : failureType!;

            _ingress.Post(
                kind: DecisionSignalKind.EspTerminalFailure,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: SourceOrigin,
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: DetectorId,
                    summary: $"ESP terminal failure (coordinator-merged, type={safeType})",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["subSource"] = "ShellCoreTracker+ProvisioningStatusTracker (merged)",
                        ["failureType"] = safeType,
                    }),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["failureType"] = safeType,
                });
        }

        // PR4 (882fef64 debrief) — once per session: post HelloPolicyDetected so the engine
        // can read the fact off DecisionState. Re-detection by the tracker (e.g. after a CSP
        // re-poll) is filtered out by the once-flag here; the reducer is also set-once at the
        // engine level, so duplicate posts would be no-ops anyway.
        private void EmitHelloPolicy(bool helloEnabled, string source)
        {
            if (_helloPolicyPosted) return;
            _helloPolicyPosted = true;

            var helloEnabledStr = helloEnabled ? "true" : "false";
            var safeSource = string.IsNullOrEmpty(source) ? "unknown" : source!;

            _ingress.Post(
                kind: DecisionSignalKind.HelloPolicyDetected,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: SourceOrigin,
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: DetectorId,
                    summary: $"Hello policy detected (helloEnabled={helloEnabledStr}, source={safeSource})",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["subSource"] = "HelloTracker",
                        [SignalPayloadKeys.HelloEnabled] = helloEnabledStr,
                        [SignalPayloadKeys.HelloPolicySource] = safeSource,
                    }),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SignalPayloadKeys.HelloEnabled] = helloEnabledStr,
                    [SignalPayloadKeys.HelloPolicySource] = safeSource,
                });
        }

        private void EmitDeviceSetupComplete()
        {
            if (_deviceSetupCompletePosted) return;
            _deviceSetupCompletePosted = true;

            var snapshot = _coordinator.GetProvisioningCategorySnapshot();
            var deviceSetupResolved =
                snapshot.TryGetValue("DeviceSetup", out var ds) && ds.HasValue
                    ? ds.Value.ToString().ToLowerInvariant()
                    : "unknown";

            _ingress.Post(
                kind: DecisionSignalKind.DeviceSetupProvisioningComplete,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: SourceOrigin,
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: DetectorId,
                    summary: "DeviceSetupCategory provisioning completed (coordinator-forwarded)",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["subSource"] = "ProvisioningStatusTracker",
                        ["deviceSetupResolved"] = deviceSetupResolved,
                    }),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["deviceSetupResolved"] = deviceSetupResolved,
                });
        }
    }
}

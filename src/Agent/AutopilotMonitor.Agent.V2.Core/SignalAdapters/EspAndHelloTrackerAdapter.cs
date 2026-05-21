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
    ///   <item><c>AccountSetupProvisioningComplete</c> → <see cref="DecisionSignalKind.AccountSetupProvisioningComplete"/> (session 330f73f3 fix — strong AccountSetup-done gate)</item>
    ///   <item><c>EspExited</c> → <see cref="DecisionSignalKind.EspExiting"/> (no dedup — every 62407 forwarded; reducer guard decides)</item>
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
        private bool _accountSetupCompletePosted;
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
            _coordinator.AccountSetupProvisioningComplete += OnAccountSetupComplete;
            _coordinator.HelloPolicyDetected += OnHelloPolicyDetected;
            _coordinator.EspExited += OnEspExited;
        }

        public void Dispose()
        {
            _coordinator.HelloCompleted -= OnHelloCompleted;
            _coordinator.FinalizingSetupPhaseTriggered -= OnFinalizing;
            _coordinator.WhiteGloveCompleted -= OnWhiteGloveCompleted;
            _coordinator.EspFailureDetected -= OnEspFailure;
            _coordinator.DeviceSetupProvisioningComplete -= OnDeviceSetupComplete;
            _coordinator.AccountSetupProvisioningComplete -= OnAccountSetupComplete;
            _coordinator.HelloPolicyDetected -= OnHelloPolicyDetected;
            _coordinator.EspExited -= OnEspExited;
        }

        private void OnHelloCompleted(object sender, EventArgs e) => EmitHello(ReadHelloOutcome());
        private void OnFinalizing(object sender, string reason) => EmitFinalizing(reason);
        private void OnWhiteGloveCompleted(object sender, EventArgs e) => EmitWhiteGlove();
        private void OnEspFailure(object sender, EspFailureDetectedEventArgs args) => EmitEspFailure(args);
        private void OnDeviceSetupComplete(object sender, EventArgs e) => EmitDeviceSetupComplete();
        private void OnAccountSetupComplete(object sender, EventArgs e) => EmitAccountSetupComplete();
        private void OnHelloPolicyDetected(bool helloEnabled, string source) => EmitHelloPolicy(helloEnabled, source);
        private void OnEspExited(object sender, EspExitedEventArgs args) => EmitEspExiting();

        internal void TriggerHelloFromTest(string? helloOutcome) => EmitHello(helloOutcome);
        internal void TriggerFinalizingFromTest(string reason) => EmitFinalizing(reason);
        internal void TriggerWhiteGloveFromTest() => EmitWhiteGlove();
        internal void TriggerEspFailureFromTest(string failureType)
            => EmitEspFailure(new EspFailureDetectedEventArgs(failureType));
        internal void TriggerEspFailureFromTest(EspFailureDetectedEventArgs args) => EmitEspFailure(args);
        internal void TriggerDeviceSetupCompleteFromTest() => EmitDeviceSetupComplete();
        internal void TriggerAccountSetupCompleteFromTest() => EmitAccountSetupComplete();
        internal void TriggerHelloPolicyDetectedFromTest(bool helloEnabled, string source) => EmitHelloPolicy(helloEnabled, source);
        internal void TriggerEspExitingFromTest() => EmitEspExiting();

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

        /// <summary>
        /// Resolve the timestamp to stamp on a coordinator-forwarded signal: prefer the
        /// originating sub-tracker's source-event time (mirrored on
        /// <see cref="EspAndHelloTracker.LastEventOccurredAtUtc"/>) over wall-clock-now.
        /// Critical on the Shell-Core backfill path where reading <c>_clock.UtcNow</c> would
        /// collapse historical event timestamps to "agent woke up just now". Emits
        /// <c>derivedTimestamp=true</c> in the evidence inputs when falling back so the
        /// Inspector can flag non-source-grounded signals.
        /// </summary>
        private DateTime ResolveOccurredAt(out bool derivedFromClock)
        {
            var src = _coordinator.LastEventOccurredAtUtc;
            if (src.HasValue)
            {
                derivedFromClock = false;
                return src.Value.Kind == DateTimeKind.Utc
                    ? src.Value
                    : DateTime.SpecifyKind(src.Value, DateTimeKind.Utc);
            }
            derivedFromClock = true;
            return _clock.UtcNow;
        }

        private static void TagDerivedTimestamp(IDictionary<string, string> data, bool derivedFromClock)
        {
            if (derivedFromClock) data["derivedTimestamp"] = "true";
        }

        private void EmitHello(string? helloOutcome)
        {
            if (_helloPosted) return;
            _helloPosted = true;

            var outcome = string.IsNullOrEmpty(helloOutcome) ? "unknown" : helloOutcome!;
            var now = ResolveOccurredAt(out var derivedFromClock);

            var derivationInputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["subSource"] = "HelloTracker",
                [SignalPayloadKeys.HelloOutcome] = outcome,
            };
            TagDerivedTimestamp(derivationInputs, derivedFromClock);

            _ingress.Post(
                kind: DecisionSignalKind.HelloResolved,
                occurredAtUtc: now,
                sourceOrigin: SourceOrigin,
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: DetectorId,
                    summary: $"Hello resolved via coordinator (outcome={outcome})",
                    derivationInputs: derivationInputs),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SignalPayloadKeys.HelloOutcome] = outcome,
                });
        }

        private void EmitFinalizing(string reason)
        {
            if (_finalizingPosted) return;
            _finalizingPosted = true;

            var now = ResolveOccurredAt(out var derivedFromClock);
            var derivationInputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["subSource"] = "ShellCoreTracker",
                ["phaseReason"] = reason ?? string.Empty,
            };
            TagDerivedTimestamp(derivationInputs, derivedFromClock);

            _ingress.Post(
                kind: DecisionSignalKind.EspPhaseChanged,
                occurredAtUtc: now,
                sourceOrigin: SourceOrigin,
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: DetectorId,
                    summary: "ESP Finalizing phase triggered (coordinator-forwarded)",
                    derivationInputs: derivationInputs),
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

            var now = ResolveOccurredAt(out var derivedFromClock);
            var derivationInputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["subSource"] = "ShellCoreTracker",
            };
            TagDerivedTimestamp(derivationInputs, derivedFromClock);

            _ingress.Post(
                kind: DecisionSignalKind.WhiteGloveShellCoreSuccess,
                occurredAtUtc: now,
                sourceOrigin: SourceOrigin,
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: DetectorId,
                    summary: "WhiteGlove sealing success (coordinator-forwarded)",
                    derivationInputs: derivationInputs));
        }

        private void EmitEspFailure(EspFailureDetectedEventArgs args)
        {
            if (_espFailurePosted) return;
            _espFailurePosted = true;

            var safeType = args?.FailureType ?? "unknown";
            var errorCode = args?.ErrorCode;
            var failedSubcategory = args?.FailedSubcategory;
            var category = args?.Category;

            var now = ResolveOccurredAt(out var derivedFromClock);
            var derivationInputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["subSource"] = "ShellCoreTracker+ProvisioningStatusTracker (merged)",
                ["failureType"] = safeType,
            };
            if (!string.IsNullOrEmpty(errorCode))
                derivationInputs["errorCode"] = errorCode!;
            if (!string.IsNullOrEmpty(failedSubcategory))
                derivationInputs["failedSubcategory"] = failedSubcategory!;
            if (!string.IsNullOrEmpty(category))
                derivationInputs["category"] = category!;
            TagDerivedTimestamp(derivationInputs, derivedFromClock);

            var payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["failureType"] = safeType,
            };
            if (!string.IsNullOrEmpty(errorCode))
                payload["errorCode"] = errorCode!;
            if (!string.IsNullOrEmpty(failedSubcategory))
                payload["failedSubcategory"] = failedSubcategory!;
            if (!string.IsNullOrEmpty(category))
                payload["category"] = category!;

            _ingress.Post(
                kind: DecisionSignalKind.EspTerminalFailure,
                occurredAtUtc: now,
                sourceOrigin: SourceOrigin,
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: DetectorId,
                    summary: $"ESP terminal failure (coordinator-merged, type={safeType}, errorCode={errorCode ?? "n/a"})",
                    derivationInputs: derivationInputs),
                payload: payload);
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
            var now = ResolveOccurredAt(out var derivedFromClock);

            var derivationInputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["subSource"] = "HelloTracker",
                [SignalPayloadKeys.HelloEnabled] = helloEnabledStr,
                [SignalPayloadKeys.HelloPolicySource] = safeSource,
            };
            TagDerivedTimestamp(derivationInputs, derivedFromClock);

            _ingress.Post(
                kind: DecisionSignalKind.HelloPolicyDetected,
                occurredAtUtc: now,
                sourceOrigin: SourceOrigin,
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: DetectorId,
                    summary: $"Hello policy detected (helloEnabled={helloEnabledStr}, source={safeSource})",
                    derivationInputs: derivationInputs),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SignalPayloadKeys.HelloEnabled] = helloEnabledStr,
                    [SignalPayloadKeys.HelloPolicySource] = safeSource,
                });
        }

        // Coordinator-forwarded EspExiting. NO per-kind dedup — Shell-Core 62407 fires at every
        // ESP phase transition (Device→Account, Account→End), and the reducer's
        // ShouldTransitionToAwaitingHello guard distinguishes the genuine post-AccountSetup exit
        // from intermediate ones. Posting all occurrences keeps the SignalLog complete for the
        // Inspector and lets the reducer be the single source of truth for "is this the real exit".
        // Source-event timestamp is read from the coordinator's LastEventOccurredAtUtc mirror
        // (set by OnEspExited from the EspExitedEventArgs), with a clock fallback tagged in
        // evidence so the Inspector can flag non-source-grounded signals on edge cases.
        private void EmitEspExiting()
        {
            var now = ResolveOccurredAt(out var derivedFromClock);
            var derivationInputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["subSource"] = "ShellCoreTracker",
                ["eventSource"] = "Microsoft-Windows-Shell-Core",
                ["eventId"] = "62407",
            };
            TagDerivedTimestamp(derivationInputs, derivedFromClock);

            _ingress.Post(
                kind: DecisionSignalKind.EspExiting,
                occurredAtUtc: now,
                sourceOrigin: SourceOrigin,
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: DetectorId,
                    summary: "ESP exiting (coordinator-forwarded Shell-Core 62407)",
                    derivationInputs: derivationInputs));
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
            var now = ResolveOccurredAt(out var derivedFromClock);

            var derivationInputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["subSource"] = "ProvisioningStatusTracker",
                ["deviceSetupResolved"] = deviceSetupResolved,
            };
            TagDerivedTimestamp(derivationInputs, derivedFromClock);

            _ingress.Post(
                kind: DecisionSignalKind.DeviceSetupProvisioningComplete,
                occurredAtUtc: now,
                sourceOrigin: SourceOrigin,
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: DetectorId,
                    summary: "DeviceSetupCategory provisioning completed (coordinator-forwarded)",
                    derivationInputs: derivationInputs),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["deviceSetupResolved"] = deviceSetupResolved,
                });
        }

        /// <summary>
        /// Session 330f73f3 fix: emits the strong post-AccountSetup gate signal. Coordinator
        /// already deduped at the tracker layer; this method's own flag is a second-line
        /// defense against duplicate Subscribe calls.
        /// </summary>
        private void EmitAccountSetupComplete()
        {
            if (_accountSetupCompletePosted) return;
            _accountSetupCompletePosted = true;

            var snapshot = _coordinator.GetProvisioningCategorySnapshot();
            var accountSetupResolved =
                snapshot.TryGetValue("AccountSetupCategory.Status", out var asState) && asState.HasValue
                    ? asState.Value.ToString().ToLowerInvariant()
                    : "unknown";
            var now = ResolveOccurredAt(out var derivedFromClock);

            var derivationInputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["subSource"] = "ProvisioningStatusTracker",
                ["accountSetupResolved"] = accountSetupResolved,
            };
            TagDerivedTimestamp(derivationInputs, derivedFromClock);

            _ingress.Post(
                kind: DecisionSignalKind.AccountSetupProvisioningComplete,
                occurredAtUtc: now,
                sourceOrigin: SourceOrigin,
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: DetectorId,
                    summary: "AccountSetupCategory provisioning completed (coordinator-forwarded)",
                    derivationInputs: derivationInputs),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["accountSetupResolved"] = accountSetupResolved,
                });
        }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;

namespace AutopilotMonitor.Agent.V2.Core.SignalAdapters
{
    /// <summary>
    /// Adapter for <see cref="ProvisioningStatusTracker"/> → 3 DecisionSignalKinds.
    /// Plan §2.1a / §2.2.
    /// <para>
    /// Event mapping:
    /// <list type="bullet">
    ///   <item><c>DeviceSetupProvisioningComplete</c> → <see cref="DecisionSignalKind.DeviceSetupProvisioningComplete"/></item>
    ///   <item><c>AccountSetupProvisioningComplete</c> → <see cref="DecisionSignalKind.AccountSetupProvisioningComplete"/> (session 330f73f3 fix)</item>
    ///   <item><c>EspFailureDetected</c> → <see cref="DecisionSignalKind.EspTerminalFailure"/></item>
    /// </list>
    /// </para>
    /// <para>
    /// Duplicate EspTerminalFailure-Signals aus ShellCoreTracker + ProvisioningStatusTracker
    /// sind erwartbar (zwei Detection-Quellen für dasselbe Outcome). Der Reducer handled das
    /// idempotent — erste Failure gewinnt und setzt Stage auf Failed; weitere sind dann Dead-End.
    /// Adapter führt nur lokale per-kind Dedup (innerhalb einer Tracker-Instanz).
    /// </para>
    /// </summary>
    internal sealed class ProvisioningStatusTrackerAdapter : IDisposable
    {
        private readonly ProvisioningStatusTracker _tracker;
        private readonly ISignalIngressSink _ingress;
        private readonly IClock _clock;

        private bool _deviceSetupCompletePosted;
        private bool _accountSetupCompletePosted;
        private bool _espFailurePosted;

        public ProvisioningStatusTrackerAdapter(
            ProvisioningStatusTracker tracker,
            ISignalIngressSink ingress,
            IClock clock)
        {
            _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
            _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));

            _tracker.DeviceSetupProvisioningComplete += OnDeviceSetupComplete;
            _tracker.AccountSetupProvisioningComplete += OnAccountSetupComplete;
            _tracker.EspFailureDetected += OnEspFailure;
        }

        public void Dispose()
        {
            _tracker.DeviceSetupProvisioningComplete -= OnDeviceSetupComplete;
            _tracker.AccountSetupProvisioningComplete -= OnAccountSetupComplete;
            _tracker.EspFailureDetected -= OnEspFailure;
        }

        private void OnDeviceSetupComplete(object sender, EventArgs e) => EmitDeviceSetupComplete();
        private void OnAccountSetupComplete(object sender, EventArgs e) => EmitAccountSetupComplete();
        private void OnEspFailure(object sender, EspFailureDetectedEventArgs args) => EmitEspFailure(args);

        internal void TriggerDeviceSetupCompleteFromTest() => EmitDeviceSetupComplete();
        internal void TriggerAccountSetupCompleteFromTest() => EmitAccountSetupComplete();
        internal void TriggerEspFailureFromTest(string failureType)
            => EmitEspFailure(new EspFailureDetectedEventArgs(failureType));
        internal void TriggerEspFailureFromTest(EspFailureDetectedEventArgs args) => EmitEspFailure(args);

        private void EmitDeviceSetupComplete()
        {
            if (_deviceSetupCompletePosted) return;
            _deviceSetupCompletePosted = true;

            var snapshot = _tracker.GetProvisioningCategorySnapshot();
            var deviceSetupResolved = snapshot.TryGetValue("DeviceSetup", out var dsState) && dsState.HasValue
                ? dsState.Value.ToString().ToLowerInvariant()
                : "unknown";

            _ingress.Post(
                kind: DecisionSignalKind.DeviceSetupProvisioningComplete,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: "ProvisioningStatusTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "provisioning-status-tracker-v1",
                    summary: "DeviceSetupCategory provisioning completed",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["registrySource"] = @"HKLM\SOFTWARE\Microsoft\Provisioning\AutopilotSettings\DeviceSetupCategory.Status",
                        ["deviceSetupResolved"] = deviceSetupResolved,
                    }),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["deviceSetupResolved"] = deviceSetupResolved,
                });
        }

        private void EmitAccountSetupComplete()
        {
            if (_accountSetupCompletePosted) return;
            _accountSetupCompletePosted = true;

            var snapshot = _tracker.GetProvisioningCategorySnapshot();
            var accountSetupResolved = snapshot.TryGetValue("AccountSetupCategory.Status", out var asState) && asState.HasValue
                ? asState.Value.ToString().ToLowerInvariant()
                : "unknown";

            _ingress.Post(
                kind: DecisionSignalKind.AccountSetupProvisioningComplete,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: "ProvisioningStatusTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "provisioning-status-tracker-v1",
                    summary: "AccountSetupCategory provisioning completed",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["registrySource"] = @"HKLM\SOFTWARE\Microsoft\Provisioning\AutopilotSettings\AccountSetupCategory.Status",
                        ["accountSetupResolved"] = accountSetupResolved,
                    }),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["accountSetupResolved"] = accountSetupResolved,
                });
        }

        private void EmitEspFailure(EspFailureDetectedEventArgs args)
        {
            if (_espFailurePosted) return;
            _espFailurePosted = true;

            var safeFailureType = args?.FailureType ?? "unknown";
            var errorCode = args?.ErrorCode;
            var failedSubcategory = args?.FailedSubcategory;
            var category = args?.Category;

            var derivationInputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["registrySource"] = @"HKLM\SOFTWARE\Microsoft\Provisioning\AutopilotSettings\*.Status",
                ["failureType"] = safeFailureType,
            };
            if (!string.IsNullOrEmpty(errorCode))
                derivationInputs["errorCode"] = errorCode!;
            if (!string.IsNullOrEmpty(failedSubcategory))
                derivationInputs["failedSubcategory"] = failedSubcategory!;
            if (!string.IsNullOrEmpty(category))
                derivationInputs["category"] = category!;

            var payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["failureType"] = safeFailureType,
            };
            if (!string.IsNullOrEmpty(errorCode))
                payload["errorCode"] = errorCode!;
            if (!string.IsNullOrEmpty(failedSubcategory))
                payload["failedSubcategory"] = failedSubcategory!;
            if (!string.IsNullOrEmpty(category))
                payload["category"] = category!;

            _ingress.Post(
                kind: DecisionSignalKind.EspTerminalFailure,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: "ProvisioningStatusTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "provisioning-status-tracker-v1",
                    summary: $"ESP terminal failure from provisioning registry (type={safeFailureType}, errorCode={errorCode ?? "n/a"})",
                    derivationInputs: derivationInputs),
                payload: payload);
        }
    }
}

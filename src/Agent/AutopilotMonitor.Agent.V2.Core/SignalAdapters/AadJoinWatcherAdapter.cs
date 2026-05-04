#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using SharedConstants = AutopilotMonitor.Shared.Constants;

namespace AutopilotMonitor.Agent.V2.Core.SignalAdapters
{
    /// <summary>
    /// Adapter for <see cref="AadJoinWatcher"/>:
    /// <list type="bullet">
    /// <item><description><see cref="AadJoinWatcher.AadUserJoined"/> →
    ///   <see cref="DecisionSignalKind.AadUserJoinedLate"/>. Plan §2.1a / §2.2.</description></item>
    /// <item><description><see cref="AadJoinWatcher.PlaceholderUserDetected"/> → emits a
    ///   <c>aad_placeholder_user_detected</c> informational event for backend timeline
    ///   visibility (Hybrid User-Driven completion-gap fix, 2026-05-01). NOT a decision signal —
    ///   the engine has no <see cref="DecisionSignalKind"/> for placeholder presence and the
    ///   placeholder is expected to be replaced by a real user.</description></item>
    /// </list>
    /// </summary>
    internal sealed class AadJoinWatcherAdapter : IDisposable
    {
        private const string SourceLabel = "AadJoinWatcher";

        private readonly AadJoinWatcher _watcher;
        private readonly ISignalIngressSink _ingress;
        private readonly IClock _clock;
        private readonly InformationalEventPost _post;
        private readonly Action? _onRealUserJoined;
        private bool _fired;
        private bool _placeholderFired;

        public AadJoinWatcherAdapter(
            AadJoinWatcher watcher,
            ISignalIngressSink ingress,
            IClock clock,
            Action? onRealUserJoined = null)
        {
            _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
            _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _post = new InformationalEventPost(ingress, clock);
            _onRealUserJoined = onRealUserJoined;

            _watcher.AadUserJoined += OnAadUserJoined;
            _watcher.PlaceholderUserDetected += OnPlaceholderUserDetected;
        }

        public void Dispose()
        {
            _watcher.AadUserJoined -= OnAadUserJoined;
            _watcher.PlaceholderUserDetected -= OnPlaceholderUserDetected;
        }

        private void OnAadUserJoined(object sender, AadUserJoinedEventArgs e) => EmitInternal(e.UserEmail, e.Thumbprint);

        private void OnPlaceholderUserDetected(object sender, AadPlaceholderUserDetectedEventArgs e) =>
            EmitPlaceholderInternal(e.UserEmail);

        internal void TriggerFromTest(string userEmail, string thumbprint) => EmitInternal(userEmail, thumbprint);

        internal void TriggerPlaceholderFromTest(string userEmail) => EmitPlaceholderInternal(userEmail);

        private void EmitInternal(string userEmail, string thumbprint)
        {
            if (_fired) return;
            _fired = true;

            var payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Don't log the full email in decision signals — user PII. Keep domain only
                // for audit trail; full email lives only in local agent logs if at all.
                ["userDomain"] = ExtractDomain(userEmail),
                ["hasThumbprint"] = string.IsNullOrEmpty(thumbprint) ? "false" : "true",
                // The AadJoinWatcher fires AadUserJoined ONLY when isPlaceholderUser==false
                // (placeholder accounts go via the separate PlaceholderUserDetected event).
                // So when this signal posts, by definition the join carries a real user.
                // Without this key, HandleAadUserJoinedLateV1 reads withUser=false and the
                // enrollment_complete audit trail records aad_user_joined_device_only — which
                // also flips WhiteGloveSealingClassifier weights the wrong way.
                [SignalPayloadKeys.AadJoinedWithUser] = "true",
            };

            _ingress.Post(
                kind: DecisionSignalKind.AadUserJoinedLate,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: SourceLabel,
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "aad-join-watcher-v1",
                    summary: "AAD user join observed (JoinInfo registry key)",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["registryKey"] = @"HKLM\SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo",
                    }),
                payload: payload);

            // Codex review 2026-05-01 — also dual-emit as informational event.
            // HandleAadUserJoinedLateV1 in DecisionEngine.Classic is observation-only (no
            // timeline effect), so without this dual-emission the backend Events table never
            // sees the real-user join. FailureSnapshotBuilder (and any other event-stream
            // consumer) needs this anchor to correctly classify aadJoinState=real_user. Same
            // PII-safe payload shape as the decision signal.
            _post.Emit(
                eventType: SharedConstants.EventTypes.AadUserJoinedObserved,
                source: SourceLabel,
                message: $"AAD user join observed (domain={payload["userDomain"]})",
                severity: EventSeverity.Info,
                immediateUpload: true,
                data: payload,
                occurredAtUtc: _clock.UtcNow);

            // Pkt 5 — notify the composition root that a real AAD user has appeared, so the
            // DesktopArrivalDetector can be reset for the AD-user-after-Hybrid-reboot case.
            // Fire AFTER the decision signal so reducer-state ordering remains the canonical source.
            try { _onRealUserJoined?.Invoke(); }
            catch { /* callback is best-effort — never let host wiring break the signal post */ }
        }

        private void EmitPlaceholderInternal(string userEmail)
        {
            if (_placeholderFired) return;
            _placeholderFired = true;

            // Hybrid User-Driven OOBE: foouser@/autopilot@ is the transient provisioning account.
            // Emit a backend-timeline event so operators can see "Foo phase active" — gives the
            // diagnose path an explicit anchor when the real-user join is overdue.
            var placeholderType = ClassifyPlaceholder(userEmail);
            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["userDomain"] = ExtractDomain(userEmail),
                ["placeholderType"] = placeholderType,
                ["registryKey"] = @"HKLM\SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo",
            };

            // PII-friendly message: placeholderType + domain reported separately so the
            // local-part of the email is never glued to the domain in the message string.
            _post.Emit(
                eventType: SharedConstants.EventTypes.AadPlaceholderUserDetected,
                source: SourceLabel,
                message: $"Autopilot provisioning placeholder detected (type={placeholderType}, domain={data["userDomain"]}) — waiting for real AAD user join",
                severity: EventSeverity.Info,
                immediateUpload: true,
                data: data,
                occurredAtUtc: _clock.UtcNow);
        }

        private static string ExtractDomain(string email)
        {
            if (string.IsNullOrEmpty(email)) return "unknown";
            var at = email.IndexOf('@');
            return at >= 0 && at + 1 < email.Length ? email.Substring(at + 1) : "unknown";
        }

        private static string ClassifyPlaceholder(string email)
        {
            if (string.IsNullOrEmpty(email)) return "unknown";
            if (email.StartsWith("foouser@", StringComparison.OrdinalIgnoreCase)) return "foouser";
            if (email.StartsWith("autopilot@", StringComparison.OrdinalIgnoreCase)) return "autopilot";
            return "unknown";
        }
    }
}

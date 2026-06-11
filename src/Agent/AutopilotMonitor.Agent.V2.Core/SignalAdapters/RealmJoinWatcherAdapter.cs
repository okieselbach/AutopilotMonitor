#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using SharedConstants = AutopilotMonitor.Shared.Constants;

namespace AutopilotMonitor.Agent.V2.Core.SignalAdapters
{
    /// <summary>
    /// Adapter for <see cref="RealmJoinWatcher"/>. Maps the watcher's lifecycle events to
    /// (1) <see cref="DecisionSignalKind"/> posts that mutate engine state via the new
    /// RealmJoin handlers and (2) <c>InformationalEvent</c> dual-emissions so the timeline
    /// receives a matching <c>realmjoin_*</c> entry per event.
    /// </summary>
    internal sealed class RealmJoinWatcherAdapter : IDisposable
    {
        private const string SourceLabel = "RealmJoinWatcher";
        private const string RegistryKeyHint = @"HKLM\SYSTEM\CurrentControlSet\Services\realmjoin\Parameters";

        private readonly RealmJoinWatcher _watcher;
        private readonly ISignalIngressSink _ingress;
        private readonly IClock _clock;
        private readonly InformationalEventPost _post;

        public RealmJoinWatcherAdapter(RealmJoinWatcher watcher, ISignalIngressSink ingress, IClock clock)
        {
            _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
            _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _post = new InformationalEventPost(ingress, clock);

            _watcher.RealmJoinDetected += OnDetected;
            _watcher.RealmJoinResolved += OnResolved;
            _watcher.RealmJoinPhaseChanged += OnPhaseChanged;
            _watcher.RealmJoinPackageStarted += OnPackageStarted;
            _watcher.RealmJoinPackageCompleted += OnPackageCompleted;
        }

        public void Dispose()
        {
            _watcher.RealmJoinDetected -= OnDetected;
            _watcher.RealmJoinResolved -= OnResolved;
            _watcher.RealmJoinPhaseChanged -= OnPhaseChanged;
            _watcher.RealmJoinPackageStarted -= OnPackageStarted;
            _watcher.RealmJoinPackageCompleted -= OnPackageCompleted;
        }

        // Test seams
        internal void TriggerDetectedFromTest(int phase, string? productVersion = null, string? releaseChannel = null) =>
            OnDetected(this, new RealmJoinDetectedEventArgs(phase, productVersion, releaseChannel));
        internal void TriggerResolvedFromTest(int phase) => OnResolved(this, new RealmJoinResolvedEventArgs(phase));
        internal void TriggerPhaseChangedFromTest(int prev, int curr) => OnPhaseChanged(this, new RealmJoinPhaseChangedEventArgs(prev, curr));
        internal void TriggerPackageStartedFromTest(string scope, RealmJoinPackageSnapshot snap) => OnPackageStarted(this, new RealmJoinPackageEventArgs(scope, snap));
        internal void TriggerPackageCompletedFromTest(string scope, RealmJoinPackageSnapshot snap) => OnPackageCompleted(this, new RealmJoinPackageEventArgs(scope, snap));

        private void OnDetected(object? sender, RealmJoinDetectedEventArgs e)
        {
            var payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = e.DeploymentPhase.ToString(),
            };
            if (!string.IsNullOrEmpty(e.ProductVersion))
            {
                payload[DecisionEngine.RealmJoinPayloadKeys.ProductVersion] = e.ProductVersion!;
            }
            if (!string.IsNullOrEmpty(e.ReleaseChannel))
            {
                payload[DecisionEngine.RealmJoinPayloadKeys.ReleaseChannel] = e.ReleaseChannel!;
            }

            _ingress.Post(
                kind: DecisionSignalKind.RealmJoinDetected,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: SourceLabel,
                evidence: BuildEvidence(
                    "realmjoin-detected-v1",
                    $"RealmJoin Parameters key observed (phase={e.DeploymentPhase}, productVersion={e.ProductVersion ?? "<unknown>"}, releaseChannel={e.ReleaseChannel ?? "<unknown>"})"),
                payload: payload);

            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["deploymentPhase"] = e.DeploymentPhase.ToString(),
                ["registryKey"] = RegistryKeyHint,
            };
            if (!string.IsNullOrEmpty(e.ProductVersion))
            {
                data["productVersion"] = e.ProductVersion!;
            }
            if (!string.IsNullOrEmpty(e.ReleaseChannel))
            {
                data["releaseChannel"] = e.ReleaseChannel!;
            }
            var versionTail = string.IsNullOrEmpty(e.ProductVersion) ? string.Empty : $", version={e.ProductVersion}";
            var channelTail = string.IsNullOrEmpty(e.ReleaseChannel) ? string.Empty : $", channel={e.ReleaseChannel}";
            _post.Emit(
                eventType: SharedConstants.EventTypes.RealmJoinDetected,
                source: SourceLabel,
                message: $"RealmJoin deployment detected (phase={e.DeploymentPhase}{versionTail}{channelTail})",
                severity: EventSeverity.Info,
                immediateUpload: true,
                data: data,
                occurredAtUtc: _clock.UtcNow);
        }

        private void OnResolved(object? sender, RealmJoinResolvedEventArgs e)
        {
            var payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = e.DeploymentPhase.ToString(),
            };

            _ingress.Post(
                kind: DecisionSignalKind.RealmJoinResolved,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: SourceLabel,
                evidence: BuildEvidence("realmjoin-resolved-v1", $"RealmJoin reached phase {e.DeploymentPhase}"),
                payload: payload);

            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["deploymentPhase"] = e.DeploymentPhase.ToString(),
            };
            _post.Emit(
                eventType: SharedConstants.EventTypes.RealmJoinResolved,
                source: SourceLabel,
                message: $"RealmJoin first deployment completed (phase={e.DeploymentPhase})",
                severity: EventSeverity.Info,
                immediateUpload: true,
                data: data,
                occurredAtUtc: _clock.UtcNow);
        }

        private void OnPhaseChanged(object? sender, RealmJoinPhaseChangedEventArgs e)
        {
            // Phase change is observability-only: no DecisionSignalKind, just a timeline event.
            // Both raw int (deploymentPhase / previousPhase) and human-readable name
            // (deploymentPhaseName / previousPhaseName) flow through — int stays for downstream
            // filters / KQL, name fields make the message + UI rows readable without a lookup.
            var currentName = RealmJoinInfo.PhaseDisplayName(e.CurrentPhase);
            var previousName = RealmJoinInfo.PhaseDisplayName(e.PreviousPhase);
            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["deploymentPhase"] = e.CurrentPhase.ToString(),
                ["deploymentPhaseName"] = currentName,
                ["previousPhase"] = e.PreviousPhase.ToString(),
                ["previousPhaseName"] = previousName,
            };
            _post.Emit(
                eventType: SharedConstants.EventTypes.RealmJoinPhaseChanged,
                source: SourceLabel,
                message: $"RealmJoin DeploymentPhase {previousName} -> {currentName}",
                severity: EventSeverity.Info,
                immediateUpload: false,
                data: data,
                occurredAtUtc: _clock.UtcNow);
        }

        private void OnPackageStarted(object? sender, RealmJoinPackageEventArgs e)
        {
            var displayName = TruncateDisplayName(e.DisplayName);

            var payload = BuildPackagePayload(e, displayName);

            _ingress.Post(
                kind: DecisionSignalKind.RealmJoinPackageStarted,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: SourceLabel,
                evidence: BuildEvidence(
                    identifier: $"realmjoin-package-started:{e.Scope}:{e.PackageId}",
                    summary: $"RealmJoin package install started (scope={e.Scope}, id={e.PackageId})"),
                payload: payload);

            _post.Emit(
                eventType: SharedConstants.EventTypes.RealmJoinPackageStarted,
                source: SourceLabel,
                message: $"RealmJoin package install started: {displayName} (id={e.PackageId}, scope={e.Scope})",
                severity: EventSeverity.Info,
                immediateUpload: false,
                data: payload,
                occurredAtUtc: _clock.UtcNow);
        }

        private void OnPackageCompleted(object? sender, RealmJoinPackageEventArgs e)
        {
            var displayName = TruncateDisplayName(e.DisplayName);

            var payload = BuildPackagePayload(e, displayName);
            if (e.Success.HasValue) payload[DecisionEngine.RealmJoinPayloadKeys.Success] = e.Success.Value ? "true" : "false";
            if (e.LastExitCode.HasValue) payload[DecisionEngine.RealmJoinPayloadKeys.LastExitCode] = e.LastExitCode.Value.ToString();

            _ingress.Post(
                kind: DecisionSignalKind.RealmJoinPackageCompleted,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: SourceLabel,
                evidence: BuildEvidence(
                    identifier: $"realmjoin-package-completed:{e.Scope}:{e.PackageId}",
                    summary: $"RealmJoin package install completed (scope={e.Scope}, id={e.PackageId}, success={e.Success}, exitCode={e.LastExitCode})"),
                payload: payload);

            var severity = e.Success == false ? EventSeverity.Warning : EventSeverity.Info;
            _post.Emit(
                eventType: SharedConstants.EventTypes.RealmJoinPackageCompleted,
                source: SourceLabel,
                message: $"RealmJoin package install {(e.Success == true ? "succeeded" : "completed")}: {displayName} (id={e.PackageId}, scope={e.Scope}, exitCode={e.LastExitCode})",
                severity: severity,
                immediateUpload: false,
                data: payload,
                occurredAtUtc: _clock.UtcNow);
        }

        // ---- helpers ----------------------------------------------------------------------

        private static Dictionary<string, string> BuildPackagePayload(RealmJoinPackageEventArgs e, string displayName)
        {
            var payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DecisionEngine.RealmJoinPayloadKeys.PackageId] = e.PackageId ?? string.Empty,
                [DecisionEngine.RealmJoinPayloadKeys.DisplayName] = displayName,
                [DecisionEngine.RealmJoinPayloadKeys.Scope] = e.Scope ?? RealmJoinPackageFact.ScopeMachine,
            };
            if (!string.IsNullOrEmpty(e.Version))
            {
                payload[DecisionEngine.RealmJoinPayloadKeys.Version] = e.Version!;
            }
            return payload;
        }

        private static string TruncateDisplayName(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value!.Length <= RealmJoinPackageFact.MaxDisplayNameLength
                ? value
                : value.Substring(0, RealmJoinPackageFact.MaxDisplayNameLength);
        }

        private static Evidence BuildEvidence(string identifier, string summary) =>
            new Evidence(
                kind: EvidenceKind.Derived,
                identifier: identifier,
                summary: summary,
                derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["registryKey"] = RegistryKeyHint,
                });
    }
}

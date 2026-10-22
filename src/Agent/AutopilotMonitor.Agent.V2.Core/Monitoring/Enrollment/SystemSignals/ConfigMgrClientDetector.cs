#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// The V1 ConfigMgr client check, run on every poll: emits <c>configmgr_client_detected</c>
    /// with the V1 payload when the client is first seen, and again when what it reports changes
    /// (the service starts, a site code is assigned) — the client often installs after the agent
    /// started. A <see cref="StartupEventGate"/> fingerprint keeps a restart into the same state
    /// silent. Observational only: emission goes through <see cref="InformationalEventPost"/>.
    /// </summary>
    internal sealed class ConfigMgrClientDetector
    {
        internal const string SourceLabel = "ConfigMgrClientDetector";

        /// <summary>Emissions per agent run; a flapping service must not flood the timeline.</summary>
        internal const int MaxEmissionsPerRun = 10;

        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;
        private readonly IClock _clock;
        private readonly StartupEventGate? _startupGate;

        private string? _lastFingerprint;
        private int _emissions;
        private bool _checkedOnce;

        public ConfigMgrClientDetector(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            IClock clock,
            StartupEventGate? startupGate = null)
        {
            _sessionId = sessionId;
            _tenantId = tenantId;
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _startupGate = startupGate;
        }

        /// <summary>Host timer callback and test seam — one probe round.</summary>
        internal void Check()
        {
            var trigger = _checkedOnce ? "poll" : "startup";
            _checkedOnce = true;

            var snapshot = ConfigMgrClientProbe.Read(_logger);
            if (!snapshot.Detected || snapshot.Fingerprint == _lastFingerprint || _emissions >= MaxEmissionsPerRun)
                return;

            _lastFingerprint = snapshot.Fingerprint;
            if (_startupGate != null && !_startupGate.ShouldEmit(
                    Constants.EventTypes.ConfigMgrClientDetected, StartupEventGate.HashString(snapshot.Fingerprint)))
            {
                _logger?.Debug("ConfigMgrClientDetector: state already reported by a previous agent run — suppressed.");
                return;
            }

            _emissions++;
            var score = snapshot.ConfidenceScore;
            var versionSuffix = snapshot.Version != null ? $", v{snapshot.Version}" : "";
            var siteSuffix = snapshot.SiteCode != null ? $", site {snapshot.SiteCode}" : "";

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                Timestamp = _clock.UtcNow,
                EventType = Constants.EventTypes.ConfigMgrClientDetected,
                Severity = EventSeverity.Info,
                Source = SourceLabel,
                Phase = EnrollmentPhase.Unknown,
                Message = $"ConfigMgr client detected (confidence {score}/100): CcmExec {snapshot.ServiceState}{versionSuffix}{siteSuffix}",
                Data = new Dictionary<string, object>
                {
                    ["ccmRegistryFound"] = snapshot.RegistryFound,
                    ["ccmDirectoryFound"] = snapshot.DirectoryFound,
                    ["ccmServiceState"] = snapshot.ServiceState,
                    ["ccmVersion"] = snapshot.Version ?? "unknown",
                    ["siteCode"] = snapshot.SiteCode ?? "unknown",
                    ["confidenceScore"] = score,
                    ["detectedAt"] = trigger,
                }
            });
            _startupGate?.MarkEmitted(Constants.EventTypes.ConfigMgrClientDetected);

            _logger?.Info(
                $"ConfigMgrClientDetector: client detected (trigger={trigger}, confidence={score}, " +
                $"service={snapshot.ServiceState}, version={snapshot.Version ?? "?"}, site={snapshot.SiteCode ?? "?"}).");
        }
    }
}

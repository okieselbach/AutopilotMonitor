using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather
{
    /// <summary>
    /// Shared dependencies passed to each gather rule collector.
    /// </summary>
    public class GatherRuleContext
    {
        public AgentLogger Logger { get; }
        public string SessionId { get; }
        public string TenantId { get; }
        public Action<EnrollmentEvent> OnEventCollected { get; }
        public string ImeLogPathOverride { get; }
        public LogFilePositionTracker FilePositionTracker { get; }
        public bool UnrestrictedMode { get; set; }

        public GatherRuleContext(
            AgentLogger logger,
            string sessionId,
            string tenantId,
            Action<EnrollmentEvent> onEventCollected,
            string imeLogPathOverride,
            LogFilePositionTracker filePositionTracker)
        {
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            TenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            OnEventCollected = onEventCollected ?? throw new ArgumentNullException(nameof(onEventCollected));
            ImeLogPathOverride = imeLogPathOverride;
            FilePositionTracker = filePositionTracker ?? throw new ArgumentNullException(nameof(filePositionTracker));
        }

        /// <summary>
        /// Emits a security_warning event and returns an empty result dictionary.
        /// Called when a gather rule targets a path/query not on the allowlist.
        /// </summary>
        public Dictionary<string, object> EmitSecurityWarning(GatherRule rule, string collectorType, string target)
        {
            Logger.Warning($"SECURITY: {collectorType} path blocked by guard: {target} (Rule: {rule.RuleId})");

            var data = new Dictionary<string, object>
            {
                ["blocked"] = true,
                ["reason"] = $"{collectorType} target not on allowlist",
                ["target"] = target,
                ["ruleId"] = rule.RuleId,
            };

            OnEventCollected(new EnrollmentEvent
            {
                SessionId = SessionId,
                TenantId = TenantId,
                Timestamp = DateTime.UtcNow,
                EventType = Constants.EventTypes.SecurityWarning,
                Severity = EventSeverity.Warning,
                Source = "GatherRuleExecutor",
                Message = $"Blocked {collectorType} target not on allowlist: {target} (Rule: {rule.RuleId})",
                Data = data
            });

            return new Dictionary<string, object>();
        }

        /// <summary>
        /// Parses a severity string into an EventSeverity enum value.
        /// </summary>
        public static EventSeverity ParseSeverity(string severity)
        {
            if (string.IsNullOrEmpty(severity))
                return EventSeverity.Info;

            switch (severity.ToLower())
            {
                case "debug": return EventSeverity.Debug;
                case "info": return EventSeverity.Info;
                case "warning": return EventSeverity.Warning;
                case "error": return EventSeverity.Error;
                case "critical": return EventSeverity.Critical;
                default: return EventSeverity.Info;
            }
        }
    }
}

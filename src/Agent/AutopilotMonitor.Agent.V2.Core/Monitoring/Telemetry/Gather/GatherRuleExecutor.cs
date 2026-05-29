using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather.Collectors;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather
{
    /// <summary>
    /// Executes gather rules received from the backend API
    /// Supports registry, eventlog, wmi, file, command_allowlisted, logparser, json, and xml collector types
    /// </summary>
    public class GatherRuleExecutor : IDisposable
    {
        private readonly AgentLogger _logger;
        private readonly GatherRuleContext _context;
        private readonly Dictionary<string, IGatherRuleCollector> _collectors;

        private List<GatherRule> _activeRules = new List<GatherRule>();
        private readonly Dictionary<string, Timer> _intervalTimers = new Dictionary<string, Timer>();
        private readonly HashSet<string> _startupRulesExecuted = new HashSet<string>();
        private readonly HashSet<string> _phaseRulesExecuted = new HashSet<string>();
        private CountdownEvent _startupRulesLatch;   // non-null only while startup rules are pending

        /// <summary>
        /// When true, guardrails are relaxed: all registry, WMI, and command targets are allowed.
        /// File paths allow everything except C:\Users. Set from tenant configuration.
        /// </summary>
        public bool UnrestrictedMode
        {
            get { return _context.UnrestrictedMode; }
            set { _context.UnrestrictedMode = value; }
        }

        public GatherRuleExecutor(string sessionId, string tenantId, Action<EnrollmentEvent> onEventCollected,
            AgentLogger logger, string imeLogPathOverride = null)
        {
            if (sessionId == null) throw new ArgumentNullException(nameof(sessionId));
            if (tenantId == null) throw new ArgumentNullException(nameof(tenantId));
            if (onEventCollected == null) throw new ArgumentNullException(nameof(onEventCollected));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            _logger = logger;

            var filePositionTracker = new LogFilePositionTracker();
            _context = new GatherRuleContext(logger, sessionId, tenantId, onEventCollected, imeLogPathOverride, filePositionTracker);

            // Register all collector strategies
            var collectorList = new IGatherRuleCollector[]
            {
                new RegistryCollector(),
                new WmiCollector(),
                new CommandCollector(),
                new FileCollector(),
                new EventLogCollector(),
                new LogParserCollector(),
                new JsonCollector(),
                new XmlCollector(),
            };

            _collectors = new Dictionary<string, IGatherRuleCollector>(StringComparer.OrdinalIgnoreCase);
            foreach (var collector in collectorList)
            {
                _collectors[collector.CollectorType] = collector;
            }

            // Register legacy alias: "command" maps to the same collector as "command_allowlisted"
            _collectors["command"] = _collectors["command_allowlisted"];
        }

        /// <summary>
        /// Updates the active rules and starts/stops execution accordingly
        /// </summary>
        public void UpdateRules(List<GatherRule> rules)
        {
            if (rules == null)
                return;

            _logger.Info($"GatherRuleExecutor: updating with {rules.Count} active rules");

            // Stop existing interval timers
            StopAllTimers();

            _activeRules = rules.Where(r => r.Enabled).ToList();

            // Execute startup rules — track completion via CountdownEvent so callers can wait
            var pendingStartup = _activeRules
                .Where(r => r.Trigger == "startup" && !_startupRulesExecuted.Contains(r.RuleId))
                .ToList();

            if (pendingStartup.Count > 0)
            {
                _startupRulesLatch?.Dispose();
                _startupRulesLatch = new CountdownEvent(pendingStartup.Count);

                foreach (var rule in pendingStartup)
                {
                    _startupRulesExecuted.Add(rule.RuleId);
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try   { ExecuteRule(rule); }
                        finally { _startupRulesLatch?.Signal(); }
                    });
                }
            }

            // Set up interval timers
            foreach (var rule in _activeRules.Where(r => r.Trigger == "interval" && r.IntervalSeconds.HasValue))
            {
                var interval = TimeSpan.FromSeconds(rule.IntervalSeconds.Value);
                var timer = new Timer(
                    _ => ExecuteRule(rule),
                    null,
                    interval, // Initial delay = one interval
                    interval
                );
                _intervalTimers[rule.RuleId] = timer;
                _logger.Info($"  Interval rule {rule.RuleId} scheduled every {rule.IntervalSeconds}s");
            }

            _logger.Info($"GatherRuleExecutor: {_activeRules.Count(r => r.Trigger == "startup")} startup, " +
                         $"{_intervalTimers.Count} interval rules active");
        }

        /// <summary>
        /// Called when a phase change event occurs - executes rules triggered by phase changes
        /// </summary>
        public void OnPhaseChanged(EnrollmentPhase newPhase)
        {
            var phaseName = newPhase.ToString();

            foreach (var rule in _activeRules.Where(r => r.Trigger == "phase_change"))
            {
                if (string.IsNullOrEmpty(rule.TriggerPhase) ||
                    string.Equals(rule.TriggerPhase, phaseName, StringComparison.OrdinalIgnoreCase))
                {
                    // Deduplicate: only fire once per (ruleId, phase) combination
                    var deduplicationKey = $"{rule.RuleId}|{phaseName}";
                    if (!_phaseRulesExecuted.Add(deduplicationKey))
                    {
                        _logger.Debug($"Phase rule {rule.RuleId} already executed for phase {phaseName}, skipping");
                        continue;
                    }

                    _logger.Info($"Phase change triggered rule {rule.RuleId} (phase: {phaseName})");
                    ThreadPool.QueueUserWorkItem(_ => ExecuteRule(rule));
                }
            }
        }

        /// <summary>
        /// Called when a specific event type is emitted - executes on_event rules
        /// </summary>
        public void OnEvent(string eventType)
        {
            foreach (var rule in _activeRules.Where(r => r.Trigger == "on_event"))
            {
                if (string.Equals(rule.TriggerEventType, eventType, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Info($"Event triggered rule {rule.RuleId} (event: {eventType})");
                    ThreadPool.QueueUserWorkItem(_ => ExecuteRule(rule));
                }
            }
        }

        private void ExecuteRule(GatherRule rule)
        {
            try
            {
                _logger.Info($"Executing gather rule: {rule.RuleId} ({rule.Title})");

                var collectorType = rule.CollectorType?.ToLower();

                IGatherRuleCollector collector;
                if (collectorType == null || !_collectors.TryGetValue(collectorType, out collector))
                {
                    _logger.Warning($"Unknown collector type: {rule.CollectorType} for rule {rule.RuleId}");
                    return;
                }

                Dictionary<string, object> result = collector.Execute(rule, _context);

                // LogParser (and potentially others) emit events directly and return null
                if (result == null)
                    return;

                if (result.Count > 0)
                {
                    result["ruleId"] = rule.RuleId;
                    result["ruleTitle"] = rule.Title;

                    var eventType = !string.IsNullOrEmpty(rule.OutputEventType) ? rule.OutputEventType : Constants.EventTypes.GatherResult;

                    // Allow collectors to override severity via _severityOverride in result
                    object severityOverride;
                    EventSeverity severity;
                    if (result.TryGetValue("_severityOverride", out severityOverride) && severityOverride is string sev)
                    {
                        severity = GatherRuleContext.ParseSeverity(sev);
                        result.Remove("_severityOverride");
                    }
                    else
                    {
                        severity = GatherRuleContext.ParseSeverity(rule.OutputSeverity);
                    }

                    _context.OnEventCollected(new EnrollmentEvent
                    {
                        SessionId = _context.SessionId,
                        TenantId = _context.TenantId,
                        Timestamp = DateTime.UtcNow,
                        EventType = eventType,
                        Severity = severity,
                        Source = "GatherRuleExecutor",
                        Message = $"Gather: {rule.Title}",
                        Data = result
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Gather rule {rule.RuleId} failed: {ex.Message}");
            }
        }

        private EventSeverity ParseSeverity(string severity)
        {
            return GatherRuleContext.ParseSeverity(severity);
        }

        private void StopAllTimers()
        {
            foreach (var timer in _intervalTimers.Values)
            {
                timer.Dispose();
            }
            _intervalTimers.Clear();
        }

        /// <summary>
        /// Blocks until all startup rules that were queued by the most recent <see cref="UpdateRules"/>
        /// call have finished executing, or the timeout elapses.
        /// Returns true if all rules completed within the timeout; false if timed out.
        /// </summary>
        public bool WaitForStartupRules(int timeoutSeconds = 120)
        {
            var latch = _startupRulesLatch;
            if (latch == null)
                return true;  // no startup rules were queued

            _logger.Debug($"WaitForStartupRules: waiting up to {timeoutSeconds}s for startup rules to complete...");
            var completed = latch.Wait(TimeSpan.FromSeconds(timeoutSeconds));
            _logger.Debug($"WaitForStartupRules: {(completed ? "all rules completed" : "timed out")}");
            return completed;
        }

        public void Dispose()
        {
            StopAllTimers();
            _startupRulesLatch?.Dispose();
            _startupRulesLatch = null;
        }
    }
}

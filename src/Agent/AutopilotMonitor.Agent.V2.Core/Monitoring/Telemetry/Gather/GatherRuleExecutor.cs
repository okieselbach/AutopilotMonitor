using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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

        // Phase-scope + emit-mode state (ActivePhases / ActiveFromPhase / EmitMode on GatherRule).
        // Guarded by _scopeLock; collector work always runs outside the lock. The state deliberately
        // survives UpdateRules config refreshes — an agent restart causes at most one re-emit per
        // on_change rule (acceptable under the per-enrollment lifecycle).
        private readonly object _scopeLock = new object();
        private EnrollmentPhase _currentPhase = EnrollmentPhase.Unknown;
        private readonly HashSet<string> _fromPhaseLatched = new HashSet<string>();
        private readonly Dictionary<string, string> _lastEmittedHash = new Dictionary<string, string>();
        private readonly Dictionary<string, (int Count, DateTime SinceUtc)> _suppressed =
            new Dictionary<string, (int Count, DateTime SinceUtc)>();
        private readonly HashSet<string> _scopeBypassLogged = new HashSet<string>();

        /// <summary>
        /// When true, phase scoping (ActivePhases / ActiveFromPhase) is bypassed and scoped rules
        /// execute unconditionally. Used by the standalone <c>--run-gather-rules</c> diagnostic
        /// mode, which has no enrollment phase context. EmitMode is unaffected.
        /// </summary>
        public bool IgnorePhaseScope { get; set; }

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

            // Execute startup rules — track completion via CountdownEvent so callers can wait.
            // Scoped startup rules that are not yet in scope are deferred: they run once from
            // OnPhaseChanged when their scope activates (same _startupRulesExecuted dedup — a
            // rule runs either here or there, never both). Unscoped rules behave as before.
            List<GatherRule> pendingStartup;
            lock (_scopeLock)
            {
                // A config refresh may deliver new from-phase rules after their phase was
                // already reached — evaluate latches against the current phase first.
                EvaluateFromPhaseLatchesLocked();

                pendingStartup = _activeRules
                    .Where(r => r.Trigger == "startup" && !_startupRulesExecuted.Contains(r.RuleId))
                    .Where(r => IsRuleInScopeLocked(r))
                    .ToList();

                foreach (var rule in pendingStartup)
                {
                    _startupRulesExecuted.Add(rule.RuleId);
                }
            }

            if (pendingStartup.Count > 0)
            {
                _startupRulesLatch?.Dispose();
                _startupRulesLatch = new CountdownEvent(pendingStartup.Count);

                foreach (var rule in pendingStartup)
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try   { ExecuteRule(rule); }
                        finally { _startupRulesLatch?.Signal(); }
                    });
                }
            }

            // Set up interval timers. Timers for scoped rules keep running while out of scope —
            // the tick gate makes an out-of-scope tick a free no-op, avoiding start/stop
            // lifecycle races on phase transitions.
            foreach (var rule in _activeRules.Where(r => r.Trigger == "interval" && r.IntervalSeconds.HasValue))
            {
                var intervalRule = rule;
                var interval = TimeSpan.FromSeconds(intervalRule.IntervalSeconds.Value);
                var timer = new Timer(
                    _ =>
                    {
                        if (!IsRuleInScope(intervalRule))
                            return;
                        ExecuteRule(intervalRule);
                    },
                    null,
                    interval, // Initial delay = one interval
                    interval
                );
                _intervalTimers[intervalRule.RuleId] = timer;
                _logger.Info($"  Interval rule {intervalRule.RuleId} scheduled every {intervalRule.IntervalSeconds}s");
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

            // Update the phase + from-phase latches FIRST so deferred startup activation and
            // the phase_change rules below all evaluate against the NEW phase.
            List<GatherRule> deferredStartup;
            lock (_scopeLock)
            {
                _currentPhase = newPhase;
                EvaluateFromPhaseLatchesLocked();

                deferredStartup = _activeRules
                    .Where(r => r.Trigger == "startup" && HasPhaseScope(r)
                                && !_startupRulesExecuted.Contains(r.RuleId)
                                && IsRuleInScopeLocked(r))
                    .ToList();

                foreach (var rule in deferredStartup)
                {
                    _startupRulesExecuted.Add(rule.RuleId);
                }
            }

            foreach (var rule in deferredStartup)
            {
                var deferredRule = rule;
                _logger.Info($"Phase {phaseName} activated deferred startup rule {deferredRule.RuleId}");
                ThreadPool.QueueUserWorkItem(_ => ExecuteRule(deferredRule));
            }

            foreach (var rule in _activeRules.Where(r => r.Trigger == "phase_change"))
            {
                if (string.IsNullOrEmpty(rule.TriggerPhase) ||
                    string.Equals(rule.TriggerPhase, phaseName, StringComparison.OrdinalIgnoreCase))
                {
                    // Scope gate BEFORE the dedup add — an out-of-scope pass must not consume
                    // the once-per-(rule, phase) execution slot.
                    if (!IsRuleInScope(rule))
                        continue;

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
                    if (!IsRuleInScope(rule))
                        continue;

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
                    // EmitMode "on_change": hash the raw collector result (before ruleId/title
                    // injection) and only emit when it differs from the last emitted one. An
                    // empty result never reaches this point, so an emitOnlyIfExists miss neither
                    // emits nor updates the hash — composing to "one event on appearance, then
                    // only on change".
                    if (IsOnChangeMode(rule) && !ShouldEmitOnChange(rule, result))
                        return;

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

        // ===== Phase scope (ActivePhases / ActiveFromPhase) =====

        private static bool HasPhaseScope(GatherRule rule)
        {
            return (rule.ActivePhases != null && rule.ActivePhases.Count > 0)
                || !string.IsNullOrEmpty(rule.ActiveFromPhase);
        }

        // Internal so the scope matrix is testable synchronously (no timer/ThreadPool races).
        internal bool IsRuleInScope(GatherRule rule)
        {
            lock (_scopeLock)
            {
                return IsRuleInScopeLocked(rule);
            }
        }

        private bool IsRuleInScopeLocked(GatherRule rule)
        {
            if (!HasPhaseScope(rule))
                return true; // unscoped — legacy behavior

            if (IgnorePhaseScope)
            {
                if (_scopeBypassLogged.Add(rule.RuleId))
                    _logger.Info($"Gather rule {rule.RuleId}: phase scope ignored (diagnostic mode)");
                return true;
            }

            // Both fields set is rejected by backend validation; defensively prefer ActivePhases.
            if (rule.ActivePhases != null && rule.ActivePhases.Count > 0)
            {
                // No phase signal yet (or scope tokens that never match, e.g. "Unknown"):
                // scoped rules stay inactive.
                if (_currentPhase == EnrollmentPhase.Unknown)
                    return false;

                var phaseName = _currentPhase.ToString();
                foreach (var phase in rule.ActivePhases)
                {
                    if (string.Equals(phase, phaseName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            return _fromPhaseLatched.Contains(rule.RuleId);
        }

        /// <summary>
        /// Latches every from-phase rule whose threshold the current phase has reached.
        /// Never latches on Unknown/Failed (Failed=99 would ordinal-satisfy every threshold);
        /// once latched a rule stays active for the session — including through Failed.
        /// Caller must hold <see cref="_scopeLock"/>.
        /// </summary>
        private void EvaluateFromPhaseLatchesLocked()
        {
            if (_currentPhase == EnrollmentPhase.Unknown || _currentPhase == EnrollmentPhase.Failed)
                return;

            foreach (var rule in _activeRules)
            {
                if (string.IsNullOrEmpty(rule.ActiveFromPhase)) continue;
                if (rule.ActivePhases != null && rule.ActivePhases.Count > 0) continue; // ActivePhases wins
                if (_fromPhaseLatched.Contains(rule.RuleId)) continue;

                if (Enum.TryParse<EnrollmentPhase>(rule.ActiveFromPhase, ignoreCase: true, out var fromPhase)
                    && fromPhase != EnrollmentPhase.Unknown
                    && fromPhase != EnrollmentPhase.Failed
                    && (int)_currentPhase >= (int)fromPhase)
                {
                    _fromPhaseLatched.Add(rule.RuleId);
                    _logger.Info($"Gather rule {rule.RuleId}: from-phase scope latched at {_currentPhase} (activeFromPhase={rule.ActiveFromPhase})");
                }
            }
        }

        // ===== Emit mode (on_change) =====

        private static bool IsOnChangeMode(GatherRule rule)
        {
            return string.Equals(rule.EmitMode, "on_change", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// on_change decision for a non-empty collector result: true = emit (first result for the
        /// rule, or the result changed), false = suppress. Hashes the result BEFORE ruleId/title
        /// injection. On emit after a suppression streak, injects <c>suppressedPolls</c> and
        /// <c>suppressedSinceUtc</c> into the result so the gap is observable.
        /// Internal so the state machine is testable synchronously (no timer/ThreadPool races).
        /// </summary>
        internal bool ShouldEmitOnChange(GatherRule rule, Dictionary<string, object> result)
        {
            var hash = ComputeCanonicalHash(result);
            bool emit;
            lock (_scopeLock)
            {
                string lastHash;
                if (_lastEmittedHash.TryGetValue(rule.RuleId, out lastHash) && lastHash == hash)
                {
                    (int Count, DateTime SinceUtc) streak;
                    _suppressed[rule.RuleId] = _suppressed.TryGetValue(rule.RuleId, out streak)
                        ? (streak.Count + 1, streak.SinceUtc)
                        : (1, DateTime.UtcNow);
                    emit = false;
                }
                else
                {
                    _lastEmittedHash[rule.RuleId] = hash;
                    (int Count, DateTime SinceUtc) streak;
                    if (_suppressed.TryGetValue(rule.RuleId, out streak) && streak.Count > 0)
                    {
                        result["suppressedPolls"] = streak.Count;
                        result["suppressedSinceUtc"] = streak.SinceUtc.ToString("o", CultureInfo.InvariantCulture);
                    }
                    _suppressed.Remove(rule.RuleId);
                    emit = true;
                }
            }

            if (!emit)
                _logger.Debug($"Gather rule {rule.RuleId}: result unchanged, emit suppressed (on_change)");
            return emit;
        }

        /// <summary>
        /// Canonical hash of a collector result: keys ordinal-sorted at every dictionary level,
        /// values rendered with invariant formatting, then SHA-256. Internal for direct test
        /// coverage of the canonicalization.
        /// </summary>
        internal static string ComputeCanonicalHash(Dictionary<string, object> result)
        {
            var sb = new StringBuilder();
            AppendCanonicalValue(sb, result);
            using (var sha = SHA256.Create())
            {
                var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return Convert.ToBase64String(hashBytes);
            }
        }

        private static void AppendCanonicalValue(StringBuilder sb, object value)
        {
            if (value == null)
            {
                sb.Append("<null>");
                return;
            }

            // string first — it is IEnumerable and must not be treated as a sequence
            if (value is string str)
            {
                sb.Append('"').Append(str).Append('"');
                return;
            }

            if (value is System.Collections.IDictionary dict)
            {
                sb.Append('{');
                var keys = dict.Keys.Cast<object>()
                    .Select(k => k?.ToString() ?? string.Empty)
                    .OrderBy(k => k, StringComparer.Ordinal);
                foreach (var key in keys)
                {
                    sb.Append(key).Append('=');
                    AppendCanonicalValue(sb, dict[key]);
                    sb.Append(';');
                }
                sb.Append('}');
                return;
            }

            if (value is System.Collections.IEnumerable sequence)
            {
                sb.Append('[');
                foreach (var item in sequence)
                {
                    AppendCanonicalValue(sb, item);
                    sb.Append(';');
                }
                sb.Append(']');
                return;
            }

            if (value is IFormattable formattable)
            {
                sb.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
            }

            sb.Append(value);
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
        /// call have finished executing, or the timeout elapses. Deferred scoped startup rules
        /// (waiting for their phase scope) are not part of the latch.
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

using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Turns the stops of an analysis run (<see cref="AnalysisOutcome.Stops"/>, D-410) into
    /// <c>RuleEvaluationStopped</c> ops events (singleton). A tenant decides how many custom rules it
    /// has and how often its sessions are analysed, so the alarm is capped per instance: one event
    /// per tenant and rule per hour, and <see cref="MaxEventsPerWindow"/> per hour in total. The
    /// engine logs every stop at Warning; this only decides which ones also reach the operators.
    /// </summary>
    public sealed class RuleEvaluationStopReporter
    {
        internal static readonly TimeSpan Window = TimeSpan.FromHours(1);
        internal const int MaxEventsPerWindow = 20;

        private readonly OpsEventService _opsEvents;
        private readonly object _lock = new();
        private readonly Dictionary<string, DateTime> _lastEventByRule = new(StringComparer.Ordinal);
        private DateTime _windowStart = DateTime.MinValue;
        private int _eventsInWindow;

        public RuleEvaluationStopReporter(OpsEventService opsEvents)
        {
            _opsEvents = opsEvents;
        }

        public async Task ReportAsync(string tenantId, string sessionId, AnalyzeRunKind kind, AnalysisOutcome outcome)
        {
            foreach (var stop in outcome.Stops)
            {
                if (!Take(tenantId, stop.RuleId, DateTime.UtcNow))
                    continue;

                // Only a stop by the spent budget leaves later rules unevaluated.
                var skipped = stop.Reason == EvaluationStopReasons.RegexTimeout ? 0 : outcome.SkippedRuleIds.Count;
                await _opsEvents.RecordRuleEvaluationStoppedAsync(
                    tenantId, sessionId, stop.RuleId, stop.Reason, stop.IsCatalogRule, kind.ToString(), skipped);
            }
        }

        /// <summary>True when this stop may raise an event: first of its tenant and rule within the
        /// window, and the instance's window still has room.</summary>
        internal bool Take(string tenantId, string ruleId, DateTime utcNow)
        {
            lock (_lock)
            {
                if (utcNow - _windowStart >= Window)
                {
                    _windowStart = utcNow;
                    _eventsInWindow = 0;
                    // Entries come only from taken events, so the map never outgrows two windows' cap.
                    foreach (var stale in _lastEventByRule.Where(e => utcNow - e.Value >= Window).Select(e => e.Key).ToList())
                        _lastEventByRule.Remove(stale);
                }

                if (_eventsInWindow >= MaxEventsPerWindow)
                    return false;

                var key = tenantId + "|" + ruleId;
                if (_lastEventByRule.TryGetValue(key, out var last) && utcNow - last < Window)
                    return false;

                _lastEventByRule[key] = utcNow;
                _eventsInWindow++;
                return true;
            }
        }
    }
}

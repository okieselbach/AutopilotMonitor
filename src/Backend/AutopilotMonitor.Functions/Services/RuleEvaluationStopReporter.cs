namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Turns the stops of an analysis run (<see cref="AnalysisOutcome.Stops"/>, D-410) into
    /// <c>RuleEvaluationStopped</c> ops events (singleton). A tenant decides how many custom rules it
    /// has and how often its sessions are analysed, so the alarm is capped per instance and hour:
    /// one event per tenant and rule, <see cref="MaxCustomEventsPerTenant"/> custom-rule events per
    /// tenant and <see cref="MaxCustomEventsPerWindow"/> in all. Catalog-rule stops (Error: our pattern
    /// or the instance) have their own <see cref="MaxCatalogEventsPerWindow"/>, so no tenant's custom
    /// rules can use up the slots they need. The engine logs every stop at Warning; this only decides
    /// which ones also reach the operators.
    /// </summary>
    public sealed class RuleEvaluationStopReporter
    {
        internal static readonly TimeSpan Window = TimeSpan.FromHours(1);
        internal const int MaxCustomEventsPerTenant = 5;
        internal const int MaxCustomEventsPerWindow = 50;
        internal const int MaxCatalogEventsPerWindow = 20;

        private readonly OpsEventService _opsEvents;
        private readonly object _lock = new();
        private readonly Dictionary<string, DateTime> _lastEventByRule = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _customEventsByTenant = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _windowStart = DateTime.MinValue;
        private int _customEvents;
        private int _catalogEvents;

        public RuleEvaluationStopReporter(OpsEventService opsEvents)
        {
            _opsEvents = opsEvents;
        }

        public async Task ReportAsync(string tenantId, string sessionId, AnalyzeRunKind kind, AnalysisOutcome outcome)
        {
            foreach (var stop in outcome.Stops)
            {
                if (!Take(tenantId, stop.RuleId, stop.IsCatalogRule, DateTime.UtcNow))
                    continue;

                // Only the stop that used up the run leaves later rules unevaluated.
                var skipped = stop.EndsRun ? outcome.SkippedRuleIds.Count : 0;
                await _opsEvents.RecordRuleEvaluationStoppedAsync(
                    tenantId, sessionId, stop.RuleId, stop.Reason, stop.IsCatalogRule, stop.EndsRun, kind.ToString(), skipped);
            }
        }

        /// <summary>True when this stop may raise an event: first of its tenant and rule within the
        /// window, and its class (catalog, or custom of this tenant and in all) still has room.</summary>
        internal bool Take(string tenantId, string ruleId, bool isCatalogRule, DateTime utcNow)
        {
            lock (_lock)
            {
                if (utcNow - _windowStart >= Window)
                {
                    _windowStart = utcNow;
                    _customEvents = 0;
                    _catalogEvents = 0;
                    _customEventsByTenant.Clear();
                    // Entries come only from taken events, so the map never outgrows two windows' caps.
                    foreach (var stale in _lastEventByRule.Where(e => utcNow - e.Value >= Window).Select(e => e.Key).ToList())
                        _lastEventByRule.Remove(stale);
                }

                var key = tenantId + "|" + ruleId;
                if (_lastEventByRule.TryGetValue(key, out var last) && utcNow - last < Window)
                    return false;

                if (isCatalogRule)
                {
                    if (_catalogEvents >= MaxCatalogEventsPerWindow)
                        return false;
                    _catalogEvents++;
                }
                else
                {
                    _customEventsByTenant.TryGetValue(tenantId, out var ofTenant);
                    if (_customEvents >= MaxCustomEventsPerWindow || ofTenant >= MaxCustomEventsPerTenant)
                        return false;
                    _customEvents++;
                    _customEventsByTenant[tenantId] = ofTenant + 1;
                }

                _lastEventByRule[key] = utcNow;
                return true;
            }
        }
    }
}

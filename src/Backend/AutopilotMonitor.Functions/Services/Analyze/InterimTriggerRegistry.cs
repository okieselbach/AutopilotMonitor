using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Analyze
{
    /// <summary>
    /// The per-tenant set of interim analyze triggers (evaluateOn) currently in effect,
    /// cached so the hot ingest path can decide "does this batch warrant an interim analyze
    /// enqueue?" without a rules-table read per batch. 5-minute TTL — the same staleness
    /// budget the worker's tenant-config cache uses; a freshly authored on_event rule starts
    /// triggering within one TTL. Fail-soft: a rules-load failure yields an empty trigger set
    /// (no interim enqueue — behavior degrades to the terminal-only baseline, never throws
    /// into the ingest path). The engine re-checks rule scope anyway, so registry staleness
    /// can only cost a wasted (cheap, rule-less) queue round-trip, never a wrong evaluation.
    /// See internal/docs/rules/analyze-rule-triggers.md.
    /// </summary>
    public class InterimTriggerRegistry
    {
        public sealed record TenantInterimTriggers(
            IReadOnlySet<string> OnEventTypes,
            bool HasWhitegloveSealedRules)
        {
            public static readonly TenantInterimTriggers Empty =
                new(new HashSet<string>(), false);
        }

        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

        private readonly AnalyzeRuleService _ruleService;
        private readonly ILogger<InterimTriggerRegistry> _logger;
        private readonly ConcurrentDictionary<string, (DateTime FetchedAtUtc, TenantInterimTriggers Triggers)> _cache
            = new(StringComparer.OrdinalIgnoreCase);

        public InterimTriggerRegistry(AnalyzeRuleService ruleService, ILogger<InterimTriggerRegistry> logger)
        {
            _ruleService = ruleService;
            _logger = logger;
        }

        public async Task<TenantInterimTriggers> GetAsync(string tenantId)
        {
            if (string.IsNullOrEmpty(tenantId))
                return TenantInterimTriggers.Empty;

            if (_cache.TryGetValue(tenantId, out var cached)
                && DateTime.UtcNow - cached.FetchedAtUtc < CacheTtl)
            {
                return cached.Triggers;
            }

            try
            {
                var rules = await _ruleService.GetActiveRulesForTenantAsync(tenantId).ConfigureAwait(false);

                var onEventTypes = rules
                    .SelectMany(AnalyzeRuleTriggers.OnEventTypes)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var hasWgSealed = rules.Any(AnalyzeRuleTriggers.RunsAtWhitegloveSealed);

                var triggers = new TenantInterimTriggers(onEventTypes, hasWgSealed);
                _cache[tenantId] = (DateTime.UtcNow, triggers);
                return triggers;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "InterimTriggerRegistry: rule load failed for tenant {TenantId} — returning empty trigger set (no interim analyze this batch)",
                    tenantId);
                return TenantInterimTriggers.Empty;
            }
        }
    }
}

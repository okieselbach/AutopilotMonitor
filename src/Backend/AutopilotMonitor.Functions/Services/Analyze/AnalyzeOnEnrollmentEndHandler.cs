using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Analyze
{
    /// <summary>
    /// Consumes <see cref="AnalyzeOnEnrollmentEndEnvelope"/> messages off the
    /// <c>analyze-on-enrollment-end</c> queue and runs the rule engine. Replaces the
    /// previous in-function fire-and-forget Task.Run that could be killed mid-flight by
    /// Azure Functions scale-in.
    /// <para>
    /// <b>Branching by Reason:</b>
    /// <list type="bullet">
    ///   <item><c>enrollment_complete</c> / <c>enrollment_failed</c> — full primary path:
    ///     persist results, SignalR notify, IssuesDetected platform-stat, RecordAnalyzeRuleStats.</item>
    ///   <item><c>vulnerability_correlated</c> — incremental rerun after async vulnerability
    ///     correlation. Persist results + SignalR only; skip platform-stat and rule-fire stats
    ///     to match the legacy <c>ReanalyzeAfterVulnerabilityEmitAsync</c> behavior (avoids
    ///     double-counting eval rows on already-stat'd rules).</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Failure semantics:</b> envelopes with missing TenantId/SessionId are dropped
    /// (returning normally so the worker deletes them — retrying won't help). Critical
    /// failures throw to the worker so the message is left un-deleted: rule-engine storage
    /// exceptions propagate through, and a <c>false</c> return from <see cref="IRuleRepository.StoreRuleResultAsync"/>
    /// is surfaced as <see cref="InvalidOperationException"/>. The worker then leaves the
    /// message visible for retry after the visibility timeout, eventually moving to poison
    /// after <c>MaxDequeueCount</c> attempts. Side-effect failures (SignalR, platform stats,
    /// per-rule stats) are caught locally so they don't trigger a full re-evaluation.
    /// </para>
    /// </summary>
    public class AnalyzeOnEnrollmentEndHandler
    {
        private readonly AnalyzeRuleService _ruleService;
        private readonly IRuleRepository _ruleRepo;
        private readonly ISessionRepository _sessionRepo;
        private readonly IMetricsRepository _metricsRepo;
        private readonly SignalRNotificationService _signalRNotification;
        private readonly TenantConfigurationService _configService;
        private readonly Notifications.WebhookNotificationService _webhookNotification;
        private readonly ILogger<AnalyzeOnEnrollmentEndHandler> _logger;

        public const string ReasonEnrollmentComplete     = "enrollment_complete";
        public const string ReasonEnrollmentFailed       = "enrollment_failed";
        public const string ReasonVulnerabilityCorrelated = "vulnerability_correlated";

        public AnalyzeOnEnrollmentEndHandler(
            AnalyzeRuleService ruleService,
            IRuleRepository ruleRepo,
            ISessionRepository sessionRepo,
            IMetricsRepository metricsRepo,
            SignalRNotificationService signalRNotification,
            TenantConfigurationService configService,
            Notifications.WebhookNotificationService webhookNotification,
            ILogger<AnalyzeOnEnrollmentEndHandler> logger)
        {
            _ruleService = ruleService;
            _ruleRepo = ruleRepo;
            _sessionRepo = sessionRepo;
            _metricsRepo = metricsRepo;
            _signalRNotification = signalRNotification;
            _configService = configService;
            _webhookNotification = webhookNotification;
            _logger = logger;
        }

        public async Task HandleAsync(AnalyzeOnEnrollmentEndEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (envelope is null)
            {
                _logger.LogWarning("Analyze handler: null envelope — dropping");
                return;
            }
            if (string.IsNullOrEmpty(envelope.TenantId) || string.IsNullOrEmpty(envelope.SessionId))
            {
                _logger.LogWarning(
                    "Analyze handler: dropping envelope with missing TenantId/SessionId (reason={Reason})",
                    envelope.Reason);
                return;
            }

            var sessionPrefix = $"[Session: {envelope.SessionId.Substring(0, Math.Min(8, envelope.SessionId.Length))}]";
            var isVulnerabilityRerun = string.Equals(
                envelope.Reason, ReasonVulnerabilityCorrelated, StringComparison.OrdinalIgnoreCase);

            // RuleEngine internally dedupes — a re-delivery of the same envelope (or a vuln-rerun
            // arriving after the primary run) only re-evaluates rules that haven't stored results.
            // Storage exceptions from rule loading or event reading propagate through and the
            // worker leaves the message un-deleted for retry.
            var ruleEngine = new RuleEngine(_ruleService, _ruleRepo, _sessionRepo, _logger);
            var outcome = await ruleEngine.AnalyzeSessionAsync(envelope.TenantId, envelope.SessionId).ConfigureAwait(false);

            foreach (var result in outcome.Results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stored = await _ruleRepo.StoreRuleResultAsync(result).ConfigureAwait(false);
                if (!stored)
                {
                    // Throw to trigger worker retry. RuleEngine dedup makes a partial-success
                    // retry idempotent: rule rows already persisted are skipped on the rerun,
                    // only the missing rows get re-attempted.
                    throw new InvalidOperationException(
                        $"StoreRuleResultAsync returned false for rule {result.RuleId} (session {envelope.SessionId}, tenant {envelope.TenantId}) — queue retry will reattempt");
                }
            }

            if (outcome.Results.Count > 0)
            {
                // Match historical log strings so existing diagnostic searches keep matching.
                var label = isVulnerabilityRerun
                    ? "Vulnerability re-analysis (queue)"
                    : "Enrollment-end analysis (queue)";

                _logger.LogInformation(
                    "{Prefix} {Label}: {Count} issue(s) detected (reason={Reason}, lagMs={Lag})",
                    sessionPrefix, label, outcome.Results.Count, envelope.Reason,
                    (long)(DateTime.UtcNow - envelope.EnqueuedAt).TotalMilliseconds);

                await SafeNotifySignalRAsync(envelope, sessionPrefix, outcome.Results.Count).ConfigureAwait(false);

                // Rule-level channel notifications. Anti-spam by construction: outcome.Results
                // only contains NEWLY detected findings (the engine dedupes against stored
                // results), and the manual "Analyze Now" reanalyze path never enters this
                // handler. Vulnerability reruns are included — their findings are new too.
                await SafeNotifyRuleChannelsAsync(envelope, sessionPrefix, outcome).ConfigureAwait(false);

                if (!isVulnerabilityRerun)
                {
                    await SafeIncrementIssuesDetectedAsync(sessionPrefix, outcome.Results.Count).ConfigureAwait(false);
                }
            }

            if (!isVulnerabilityRerun)
            {
                await SafeRecordAnalyzeRuleStatsAsync(envelope.TenantId, outcome).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Sends an outbound alert to the tenant's selected notification channels for each newly
        /// fired rule whose effective Notify flag is on (tenant RuleState override ?? rule default)
        /// and that has at least one resolvable channel id. Side-effect failures are swallowed —
        /// a webhook outage must never trigger a re-evaluation of the whole envelope.
        /// </summary>
        private async Task SafeNotifyRuleChannelsAsync(
            AnalyzeOnEnrollmentEndEnvelope envelope, string sessionPrefix, AnalysisOutcome outcome)
        {
            try
            {
                // EvaluatedRules carries the tenant-merged rule objects (Notify/NotifyChannelIds
                // applied from RuleStates) for exactly the rules evaluated this run.
                var rulesById = outcome.EvaluatedRules.ToDictionary(r => r.RuleId);
                var candidates = outcome.Results
                    .Where(result => rulesById.TryGetValue(result.RuleId, out var rule)
                        && (rule.Notify ?? rule.NotifyDefault)
                        && rule.NotifyChannelIds is { Count: > 0 })
                    .Select(result => (Result: result, Rule: rulesById[result.RuleId]))
                    .ToList();

                if (candidates.Count == 0)
                    return;

                var tenantConfig = await _configService.GetConfigurationAsync(envelope.TenantId).ConfigureAwait(false);
                var channelsById = tenantConfig.GetNotificationChannels()
                    .Where(c => c.Enabled)
                    .ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

                if (channelsById.Count == 0)
                    return;

                var session = await _sessionRepo.GetSessionAsync(envelope.TenantId, envelope.SessionId).ConfigureAwait(false);
                var sessionUrl = $"https://portal.autopilotmonitor.com/sessions/{envelope.SessionId}";

                foreach (var (result, rule) in candidates)
                {
                    var targets = rule.NotifyChannelIds!
                        .Where(id => channelsById.ContainsKey(id))
                        .Select(id => channelsById[id])
                        .ToList();
                    if (targets.Count == 0)
                        continue;

                    var alert = Notifications.NotificationAlertBuilder.BuildRuleFiredAlert(
                        result, session?.DeviceName, session?.SerialNumber, sessionUrl);

                    await _webhookNotification.SendToChannelsAsync(targets, alert).ConfigureAwait(false);

                    _logger.LogInformation(
                        "{Prefix} Rule-notify: {RuleId} → {ChannelCount} channel(s)",
                        sessionPrefix, rule.RuleId, targets.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Prefix} Rule-notify dispatch failed (non-fatal)", sessionPrefix);
            }
        }

        private async Task SafeNotifySignalRAsync(AnalyzeOnEnrollmentEndEnvelope envelope, string sessionPrefix, int count)
        {
            try
            {
                await _signalRNotification.NotifyRuleResultsAvailableAsync(
                    envelope.TenantId, envelope.SessionId, count).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // UI fetches results on next poll regardless — do not retry the whole envelope.
                _logger.LogWarning(ex, "{Prefix} SignalR notify failed (non-fatal)", sessionPrefix);
            }
        }

        private async Task SafeIncrementIssuesDetectedAsync(string sessionPrefix, int count)
        {
            try
            {
                await _metricsRepo.IncrementPlatformStatAsync("IssuesDetected", count).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Prefix} IncrementPlatformStatAsync IssuesDetected failed (non-fatal)", sessionPrefix);
            }
        }

        private async Task SafeRecordAnalyzeRuleStatsAsync(string tenantId, AnalysisOutcome outcome)
        {
            try
            {
                var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
                var firedRuleIds = new HashSet<string>(outcome.Results.Select(r => r.RuleId));

                foreach (var rule in outcome.EvaluatedRules)
                {
                    var fired = firedRuleIds.Contains(rule.RuleId);
                    int? confidence = null;
                    if (fired)
                    {
                        var result = outcome.Results.FirstOrDefault(r => r.RuleId == rule.RuleId);
                        confidence = result?.ConfidenceScore;
                    }

                    await _metricsRepo.IncrementRuleStatAsync(
                        today, tenantId, rule.RuleId, "analyze",
                        rule.Title, rule.Category, rule.Severity,
                        fired, confidence).ConfigureAwait(false);

                    await _metricsRepo.IncrementRuleStatAsync(
                        today, "global", rule.RuleId, "analyze",
                        rule.Title, rule.Category, rule.Severity,
                        fired, confidence).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record analyze rule stats (non-fatal)");
            }
        }
    }
}

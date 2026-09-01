using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Evaluates ops alert rules and dispatches notifications to all enabled providers.
    /// Called fire-and-forget from OpsEventService — failures are logged but never thrown.
    /// </summary>
    public class OpsAlertDispatchService
    {
        private readonly AdminConfigurationService _adminConfigService;
        private readonly NotificationChannelDispatcher _channelDispatcher;
        private readonly ILogger<OpsAlertDispatchService> _logger;

        public OpsAlertDispatchService(
            AdminConfigurationService adminConfigService,
            NotificationChannelDispatcher channelDispatcher,
            ILogger<OpsAlertDispatchService> logger)
        {
            _adminConfigService = adminConfigService;
            _channelDispatcher = channelDispatcher;
            _logger = logger;
        }

        /// <summary>
        /// Evaluates alert rules for the given ops event and dispatches to the channels those
        /// rules target. Safe to call fire-and-forget — never throws.
        /// </summary>
        public async Task DispatchAsync(string category, string eventType, string severity,
            string message, string? tenantId, string? detailsJson = null)
        {
            try
            {
                var config = await _adminConfigService.GetConfigurationAsync();
                if (config == null) return;

                // ALL matching rules, not just the first: two rules on the same event type with
                // different severities and different channels is the whole point of per-rule
                // routing (e.g. "Info → Sales" alongside "Error → operator push").
                var matchingRules = config.GetOpsAlertRules()
                    .Where(r => r.Enabled
                        && r.EventType == eventType
                        && SeverityRank(severity) >= SeverityRank(r.MinSeverity))
                    .ToList();

                if (matchingRules.Count == 0) return;

                var channels = config.GetOpsNotificationChannels();
                var (withPayload, plain) = ResolveTargets(matchingRules, channels);
                if (withPayload.Count == 0 && plain.Count == 0) return;

                if (plain.Count > 0)
                {
                    await _channelDispatcher.SendToChannelsAsync(plain,
                        BuildAlert(category, eventType, severity, message, tenantId, detailsJson: null));
                }

                if (withPayload.Count > 0)
                {
                    await _channelDispatcher.SendToChannelsAsync(withPayload,
                        BuildAlert(category, eventType, severity, message, tenantId, detailsJson));
                }

                _logger.LogInformation(
                    "Ops alert dispatched for {Category}/{EventType} to {ChannelCount} channel(s) ({PayloadCount} with payload)",
                    category, eventType, plain.Count + withPayload.Count, withPayload.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispatch ops alert for {Category}/{EventType}", category, eventType);
            }
        }

        /// <summary>
        /// Splits the channels the matching rules target into the ones that get the event's
        /// payload and the ones that get the baseline alert only, preserving the configured
        /// channel order and never sending twice to the same channel.
        /// <para>
        /// A rule with no channel ids means "every enabled channel" — that is what every rule
        /// written before per-rule routing existed carries, so their behavior is unchanged.
        /// Ids that no longer resolve are dropped silently: a rule whose only channel was deleted
        /// must go nowhere, NOT fall back to broadcasting.
        /// </para>
        /// <para>
        /// When a channel is targeted by both a payload rule and a plain one it appears in the
        /// payload group only — one message per channel, and the explicit opt-in wins over the
        /// default rather than being cancelled by an unrelated sibling rule.
        /// </para>
        /// </summary>
        internal static (List<NotificationChannel> WithPayload, List<NotificationChannel> Plain) ResolveTargets(
            IReadOnlyList<OpsAlertRule> matchingRules,
            IReadOnlyList<NotificationChannel> channels)
        {
            var enabled = channels.Where(c => c.Enabled).ToList();
            if (enabled.Count == 0)
                return (new List<NotificationChannel>(), new List<NotificationChannel>());

            var payloadIds = TargetedIds(matchingRules.Where(r => r.IncludePayload), enabled);
            var plainIds = TargetedIds(matchingRules.Where(r => !r.IncludePayload), enabled);

            return (
                enabled.Where(c => payloadIds.Contains(c.Id)).ToList(),
                enabled.Where(c => plainIds.Contains(c.Id) && !payloadIds.Contains(c.Id)).ToList());
        }

        /// <summary>Channel ids one group of rules targets; empty binding = every enabled channel.</summary>
        private static HashSet<string> TargetedIds(
            IEnumerable<OpsAlertRule> rules, IReadOnlyList<NotificationChannel> enabled)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var materialized = rules.ToList();
            if (materialized.Count == 0)
                return ids;

            if (materialized.Any(r => r.NotifyChannelIds == null || r.NotifyChannelIds.Count == 0))
            {
                foreach (var channel in enabled)
                    ids.Add(channel.Id);
                return ids;
            }

            foreach (var rule in materialized)
            {
                foreach (var id in rule.NotifyChannelIds!)
                    ids.Add(id);
            }

            return ids;
        }

        /// <summary>Caps on the flattened detail facts — a card must stay readable.</summary>
        internal const int MaxDetailFacts = 12;
        internal const int MaxDetailValueLength = 256;

        private static NotificationAlert BuildAlert(string category, string eventType,
            string severity, string message, string? tenantId, string? detailsJson)
        {
            var notifSeverity = severity switch
            {
                OpsEventSeverity.Critical => NotificationSeverity.Error,
                OpsEventSeverity.Error => NotificationSeverity.Error,
                OpsEventSeverity.Warning => NotificationSeverity.Warning,
                _ => NotificationSeverity.Info
            };

            var themeColor = severity switch
            {
                OpsEventSeverity.Critical => "8B0000",
                OpsEventSeverity.Error => "FF4500",
                OpsEventSeverity.Warning => "FFA500",
                _ => "4682B4"
            };

            var facts = new List<NotificationFact>
            {
                new() { Name = "Category", Value = category },
                new() { Name = "Event", Value = eventType },
                new() { Name = "Severity", Value = severity },
            };

            if (!string.IsNullOrWhiteSpace(tenantId))
                facts.Add(new NotificationFact { Name = "Tenant", Value = tenantId });

            facts.AddRange(FlattenDetails(detailsJson));

            return new NotificationAlert
            {
                // Machine-readable routing key for generic consumers — the same value the
                // "Event" fact already carries, so this adds no information, only a stable key
                // to branch on. Set regardless of the payload opt-in.
                EventType = eventType,
                Title = $"Ops Alert: {category}/{eventType}",
                Summary = message,
                Severity = notifSeverity,
                ThemeColor = themeColor,
                Facts = facts,
                DataJson = detailsJson,
            };
        }

        /// <summary>
        /// Turns the ops event's structured details into readable facts, so card channels (Teams,
        /// Slack, Discord) and the plain-text Telegram message carry the same information the
        /// generic JSON consumer gets in <c>data</c>. Reached only for rules that opted in via
        /// <see cref="OpsAlertRule.IncludePayload"/> — the caller passes null otherwise, so the
        /// default alert stays the category/event/severity/tenant baseline.
        /// <para>
        /// Deliberately shallow: only top-level scalars. Nested objects and arrays have no
        /// sensible one-line rendering, and a payload is not a UI. Nulls are skipped (an absent
        /// value is not news), values are truncated, and the count is capped.
        /// </para>
        /// </summary>
        internal static List<NotificationFact> FlattenDetails(string? detailsJson)
        {
            var facts = new List<NotificationFact>();
            if (string.IsNullOrWhiteSpace(detailsJson))
                return facts;

            try
            {
                using var doc = JsonDocument.Parse(detailsJson!);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return facts;

                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (facts.Count >= MaxDetailFacts)
                        break;

                    var value = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString(),
                        JsonValueKind.Number => property.Value.GetRawText(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => null,   // null, object, array
                    };

                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    if (value!.Length > MaxDetailValueLength)
                        value = value.Substring(0, MaxDetailValueLength) + "…";

                    facts.Add(new NotificationFact { Name = Humanize(property.Name), Value = value });
                }
            }
            catch (JsonException)
            {
                // Details are best-effort context; a malformed payload must never cost the alert.
            }

            return facts;
        }

        /// <summary>"trialExpiresUtc" → "Trial Expires Utc" — the payload uses camelCase keys, the
        /// surrounding facts use title case, and a card mixing both reads like a bug.</summary>
        internal static string Humanize(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName))
                return propertyName;

            var sb = new System.Text.StringBuilder(propertyName.Length + 8);
            sb.Append(char.ToUpperInvariant(propertyName[0]));

            for (var i = 1; i < propertyName.Length; i++)
            {
                var ch = propertyName[i];
                if (char.IsUpper(ch) && !char.IsUpper(propertyName[i - 1]))
                    sb.Append(' ');
                sb.Append(ch);
            }

            return sb.ToString();
        }

        // Single source for the severity ladder — shared with the ops-events query filters
        // (OpsEventQueryFilters.MinSeverity), which must rank identically or an operator's
        // "Warning and above" read would disagree with what the alert rules actually fired on.
        private static int SeverityRank(string severity) => OpsEventSeverity.Rank(severity);
    }
}

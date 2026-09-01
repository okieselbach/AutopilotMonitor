using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Platform (ops) notification channels: legacy-slot synthesis so the pre-channels dispatch
/// behavior survives the migration untouched, per-rule channel routing, and redaction of the
/// new secret-bearing field.
/// </summary>
public class OpsNotificationChannelsTests
{
    private static AdminConfiguration LegacyConfig(
        bool telegramEnabled = true, string telegramChat = "-1003785642894",
        bool teamsEnabled = true, string teamsUrl = "https://teams.example/hook",
        bool slackEnabled = false, string slackUrl = "")
        => new()
        {
            UpdatedBy = "test",
            OpsAlertTelegramEnabled = telegramEnabled,
            OpsAlertTelegramChatId = telegramChat,
            OpsAlertTeamsEnabled = teamsEnabled,
            OpsAlertTeamsWebhookUrl = teamsUrl,
            OpsAlertSlackEnabled = slackEnabled,
            OpsAlertSlackWebhookUrl = slackUrl,
        };

    private static NotificationChannel Channel(string id, bool enabled = true, int providerType = 20)
        => new() { Id = id, Name = id, ProviderType = providerType, Url = "https://x.example/" + id, Enabled = enabled };

    private static OpsAlertRule Rule(string eventType, params string[] channelIds)
        => new()
        {
            EventType = eventType,
            MinSeverity = OpsEventSeverity.Info,
            Enabled = true,
            NotifyChannelIds = channelIds.Length == 0 ? null : channelIds.ToList(),
        };

    private static OpsAlertRule PayloadRule(string eventType, params string[] channelIds)
    {
        var rule = Rule(eventType, channelIds);
        rule.IncludePayload = true;
        return rule;
    }

    /// <summary>Channels that receive the baseline alert (no payload) - the default for every rule.</summary>
    private static IEnumerable<string> Plain(IReadOnlyList<OpsAlertRule> rules, IReadOnlyList<NotificationChannel> channels)
        => OpsAlertDispatchService.ResolveTargets(rules, channels).Plain.Select(c => c.Id);

    /// <summary>Channels that receive the event structured payload (opted in per rule).</summary>
    private static IEnumerable<string> WithPayload(IReadOnlyList<OpsAlertRule> rules, IReadOnlyList<NotificationChannel> channels)
        => OpsAlertDispatchService.ResolveTargets(rules, channels).WithPayload.Select(c => c.Id);

    // ── Legacy synthesis ──────────────────────────────────────────────────

    [Fact]
    public void GetOpsNotificationChannels_SynthesizesConfiguredLegacySlots()
    {
        var channels = LegacyConfig().GetOpsNotificationChannels();

        Assert.Equal(2, channels.Count); // Slack slot is empty → not synthesized
        var telegram = channels[0];
        Assert.Equal(AdminConfiguration.LegacyTelegramChannelId, telegram.Id);
        Assert.Equal(AdminConfiguration.LegacyTelegramChannelName, telegram.Name);
        Assert.Equal((int)WebhookProviderType.Telegram, telegram.ProviderType);
        Assert.Equal("-1003785642894", telegram.Url);
        Assert.True(telegram.Enabled);

        Assert.Equal(AdminConfiguration.LegacyTeamsChannelId, channels[1].Id);
        Assert.Equal((int)WebhookProviderType.TeamsWorkflowWebhook, channels[1].ProviderType);
    }

    [Fact]
    public void GetOpsNotificationChannels_CarriesLegacyEnabledFlags()
    {
        // A configured-but-disabled provider must stay disabled, or the migration would start
        // sending to a destination the operator had deliberately switched off.
        var channels = LegacyConfig(telegramEnabled: false).GetOpsNotificationChannels();

        Assert.False(channels.Single(c => c.Id == AdminConfiguration.LegacyTelegramChannelId).Enabled);
    }

    [Fact]
    public void GetOpsNotificationChannels_EmptyWhenNothingConfigured()
        => Assert.Empty(new AdminConfiguration { UpdatedBy = "t" }.GetOpsNotificationChannels());

    [Fact]
    public void GetOpsNotificationChannels_StoredListWinsOverLegacySlots()
    {
        var config = LegacyConfig();
        config.OpsNotificationChannelsJson = NotificationChannel.SerializeList(new[] { Channel("sales") });

        var channels = config.GetOpsNotificationChannels();

        Assert.Single(channels);
        Assert.Equal("sales", channels[0].Id);
    }

    // ── Rule → channel routing ────────────────────────────────────────────

    [Fact]
    public void ResolveTargets_RuleWithoutChannelIds_HitsEveryEnabledChannel()
    {
        var channels = new[] { Channel("a"), Channel("b") };

        Assert.Equal(new[] { "a", "b" }, Plain(new[] { Rule("X") }, channels));
    }

    [Fact]
    public void ResolveTargets_RuleWithChannelIds_HitsOnlyThose()
    {
        var channels = new[] { Channel("push"), Channel("sales") };

        Assert.Equal(new[] { "sales" }, Plain(new[] { Rule("X", "sales") }, channels));
    }

    [Fact]
    public void ResolveTargets_NeverSendsToADisabledChannel()
    {
        var channels = new[] { Channel("push"), Channel("sales", enabled: false) };

        Assert.Empty(Plain(new[] { Rule("X", "sales") }, channels));
    }

    [Fact]
    public void ResolveTargets_UnknownChannelIdDoesNotFallBackToBroadcast()
    {
        // A rule whose only channel was deleted must go nowhere. Falling back to "all" would
        // silently leak e.g. a sales-only event into the operator push channel.
        var channels = new[] { Channel("push") };

        Assert.Empty(Plain(new[] { Rule("X", "deleted-channel") }, channels));
    }

    [Fact]
    public void ResolveTargets_MultipleRules_UnionOfChannelsWithoutDuplicates()
    {
        var channels = new[] { Channel("push"), Channel("sales"), Channel("audit") };

        Assert.Equal(new[] { "sales", "audit" },
            Plain(new[] { Rule("X", "sales"), Rule("X", "sales", "audit") }, channels));
    }

    [Fact]
    public void ResolveTargets_OneBroadcastRuleWins()
    {
        // If any matching rule is unrestricted, the union is everything - a narrower sibling rule
        // cannot shrink an existing broadcast rule reach.
        var channels = new[] { Channel("push"), Channel("sales") };

        Assert.Equal(2, Plain(new[] { Rule("X", "sales"), Rule("X") }, channels).Count());
    }

    [Fact]
    public void ResolveTargets_ChannelIdMatchIsCaseInsensitive()
    {
        var channels = new[] { Channel("Sales") };

        Assert.Single(Plain(new[] { Rule("X", "sales") }, channels));
    }

    // ── Payload opt-in ────────────────────────────

    [Fact]
    public void ResolveTargets_PayloadIsOffUnlessTheRuleOptsIn()
    {
        // The default information level of every pre-existing rule: category, event, severity,
        // tenant id. Nothing from the event payload.
        var channels = new[] { Channel("push") };

        Assert.Empty(WithPayload(new[] { Rule("X") }, channels));
        Assert.Equal(new[] { "push" }, Plain(new[] { Rule("X") }, channels));
    }

    [Fact]
    public void ResolveTargets_OptedInRuleSendsPayloadToItsChannelsOnly()
    {
        var channels = new[] { Channel("push"), Channel("sales") };
        var rules = new[] { Rule("X", "push"), PayloadRule("X", "sales") };

        Assert.Equal(new[] { "sales" }, WithPayload(rules, channels));
        Assert.Equal(new[] { "push" }, Plain(rules, channels));
    }

    [Fact]
    public void ResolveTargets_ChannelInBothGroupsGetsThePayloadOnceOnly()
    {
        // One message per channel, and the explicit opt-in must not be cancelled by an
        // unrelated sibling rule that happens to target the same channel.
        var channels = new[] { Channel("sales") };
        var rules = new[] { Rule("X", "sales"), PayloadRule("X", "sales") };

        Assert.Equal(new[] { "sales" }, WithPayload(rules, channels));
        Assert.Empty(Plain(rules, channels));
    }

    [Fact]
    public void ResolveTargets_OptedInBroadcastRuleReachesEveryEnabledChannel()
    {
        var channels = new[] { Channel("push"), Channel("sales") };

        Assert.Equal(new[] { "push", "sales" }, WithPayload(new[] { PayloadRule("X") }, channels));
        Assert.Empty(Plain(new[] { PayloadRule("X") }, channels));
    }

    // ── Redaction ─────────────────────────────────────────────────────────

    [Fact]
    public void RedactedCopyForReader_RedactsOpsChannelDestinationsButKeepsStructure()
    {
        var config = new AdminConfiguration { UpdatedBy = "t" };
        config.OpsNotificationChannelsJson = NotificationChannel.SerializeList(new[]
        {
            new NotificationChannel
            {
                Id = "sales", Name = "Sales", ProviderType = 20,
                Url = "https://sales.example/hook", SigningSecret = "s3cr3t-signing-key-value",
                Enabled = true,
            },
        });

        var redacted = NotificationChannel.ParseList(
            config.RedactedCopyForReader().OpsNotificationChannelsJson).Single();

        Assert.Equal(Constants.RedactedSecretPlaceholder, redacted.Url);
        Assert.Equal(Constants.RedactedSecretPlaceholder, redacted.SigningSecret);
        Assert.Equal("Sales", redacted.Name);      // structure stays readable
        Assert.Equal(20, redacted.ProviderType);
    }

    [Fact]
    public void RestoreRedactedList_RestoresSentinelDestinationById()
    {
        var stored = NotificationChannel.SerializeList(new[]
        {
            new NotificationChannel { Id = "sales", Name = "Sales", ProviderType = 20, Url = "https://sales.example/hook", Enabled = true },
        });
        var roundTripped = NotificationChannel.SerializeList(new[]
        {
            new NotificationChannel { Id = "sales", Name = "Sales renamed", ProviderType = 20, Url = Constants.RedactedSecretPlaceholder, Enabled = true },
        });

        var restored = NotificationChannel.ParseList(
            NotificationChannel.RestoreRedactedList(roundTripped, stored)).Single();

        Assert.Equal("https://sales.example/hook", restored.Url);
        Assert.Equal("Sales renamed", restored.Name); // non-secret edits survive
    }

    [Fact]
    public void RestoreRedactedList_WholeStringSentinelKeepsStoredList()
    {
        var stored = NotificationChannel.SerializeList(new[] { Channel("sales") });

        Assert.Equal(stored, NotificationChannel.RestoreRedactedList(Constants.RedactedSecretPlaceholder, stored));
    }

    [Fact]
    public void RestoreRedactedList_PassesThroughWhenNoSentinelPresent()
    {
        var candidate = NotificationChannel.SerializeList(new[] { Channel("sales") });

        Assert.Equal(candidate, NotificationChannel.RestoreRedactedList(candidate, ""));
    }
}

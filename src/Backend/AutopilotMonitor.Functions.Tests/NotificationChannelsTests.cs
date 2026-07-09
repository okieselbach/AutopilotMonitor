using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Multi-channel notification config: JSON parse hardening, legacy single-webhook synthesis
/// (behavior parity for non-migrated tenants), and per-channel secret redaction/restore.
/// </summary>
public class NotificationChannelsTests
{
    private static string ChannelJson(string id, int providerType = 20, string url = "https://hooks.example/x",
        string extra = "")
        => $"{{\"id\":\"{id}\",\"name\":\"{id}\",\"providerType\":{providerType},\"url\":\"{url}\",\"enabled\":true{extra}}}";

    // ── ParseList hardening ───────────────────────────────────────────────

    [Fact]
    public void ParseList_NullEmptyOrMalformed_YieldsEmpty()
    {
        Assert.Empty(NotificationChannel.ParseList(null));
        Assert.Empty(NotificationChannel.ParseList(""));
        Assert.Empty(NotificationChannel.ParseList("   "));
        Assert.Empty(NotificationChannel.ParseList("not-json"));
        Assert.Empty(NotificationChannel.ParseList("{\"id\":\"obj-not-array\"}"));
    }

    [Fact]
    public void ParseList_DropsEntriesWithoutIdOrWithInvalidProvider()
    {
        var json = "[" + string.Join(",",
            ChannelJson("ok", providerType: 10),
            "{\"name\":\"no-id\",\"providerType\":20,\"url\":\"https://x.example\"}",
            ChannelJson("none-provider", providerType: 0),
            ChannelJson("unknown-provider", providerType: 99)) + "]";

        var parsed = NotificationChannel.ParseList(json);

        Assert.Single(parsed);
        Assert.Equal("ok", parsed[0].Id);
    }

    [Fact]
    public void ParseList_CapsAtMaxChannelsPerTenant()
    {
        var entries = Enumerable.Range(0, NotificationChannel.MaxChannelsPerTenant + 5)
            .Select(i => ChannelJson($"ch-{i}"));
        var parsed = NotificationChannel.ParseList("[" + string.Join(",", entries) + "]");

        Assert.Equal(NotificationChannel.MaxChannelsPerTenant, parsed.Count);
    }

    [Fact]
    public void GetCustomHeaders_OnlyForGenericJson()
    {
        var headers = "{\"X-Api-Key\":\"k\",\"Host\":\"evil\"}";
        var generic = new NotificationChannel { Id = "g", ProviderType = 20, CustomHeadersJson = headers };
        var slack = new NotificationChannel { Id = "s", ProviderType = 10, CustomHeadersJson = headers };

        Assert.Equal("k", generic.GetCustomHeaders()["X-Api-Key"]);
        Assert.False(generic.GetCustomHeaders().ContainsKey("Host")); // restricted header dropped
        Assert.Empty(slack.GetCustomHeaders());
    }

    // ── Legacy synthesis (GetNotificationChannels) ────────────────────────

    [Fact]
    public void GetNotificationChannels_NoWebhookAtAll_Empty()
    {
        Assert.Empty(new TenantConfiguration().GetNotificationChannels());
    }

    [Fact]
    public void GetNotificationChannels_LegacyWebhookFields_SynthesizesOneChannelWithParity()
    {
        var cfg = new TenantConfiguration
        {
            WebhookProviderType = 20,
            WebhookUrl = "https://desk.example/hook",
            WebhookCustomHeadersJson = "{\"X-Api-Key\":\"k\"}",
            WebhookNotifyOnSuccess = false,
            WebhookNotifyOnFailure = true,
            WebhookNotifyOnStart = true,
            WebhookNotifyOnHardwareRejection = true,
        };

        var channels = cfg.GetNotificationChannels();

        var ch = Assert.Single(channels);
        Assert.Equal(TenantConfiguration.LegacyChannelId, ch.Id);
        Assert.Equal(20, ch.ProviderType);
        Assert.Equal("https://desk.example/hook", ch.Url);
        Assert.True(ch.Enabled);
        Assert.False(ch.NotifyOnSuccess);
        Assert.True(ch.NotifyOnFailure);
        Assert.True(ch.NotifyOnStart);
        Assert.True(ch.NotifyOnHardwareRejection);
        Assert.True(ch.NotifyOnSlaEvents); // legacy: SLA alerts always went to the single webhook
        Assert.Equal("k", ch.GetCustomHeaders()["X-Api-Key"]);
    }

    [Fact]
    public void GetNotificationChannels_LegacyTeamsOnly_SynthesizesTeamsLegacyConnector()
    {
        var cfg = new TenantConfiguration
        {
            TeamsWebhookUrl = "https://teams.example/hook",
            TeamsNotifyOnSuccess = true,
            TeamsNotifyOnFailure = false,
        };

        var ch = Assert.Single(cfg.GetNotificationChannels());
        Assert.Equal((int)WebhookProviderType.TeamsLegacyConnector, ch.ProviderType);
        Assert.Equal("https://teams.example/hook", ch.Url);
        Assert.True(ch.NotifyOnSuccess);
        Assert.False(ch.NotifyOnFailure);
    }

    [Fact]
    public void GetNotificationChannels_ChannelsJsonWinsOverLegacyFields()
    {
        var cfg = new TenantConfiguration
        {
            WebhookProviderType = 20,
            WebhookUrl = "https://legacy.example/hook",
            NotificationChannelsJson = "[" + ChannelJson("ch-1", providerType: 10, url: "https://slack.example/h") + "]",
        };

        var ch = Assert.Single(cfg.GetNotificationChannels());
        Assert.Equal("ch-1", ch.Id);
        Assert.Equal("https://slack.example/h", ch.Url);
    }

    // ── Redaction / restore ───────────────────────────────────────────────

    [Fact]
    public void RedactedCopy_RedactsPerChannelSecretsButKeepsStructure()
    {
        var cfg = new TenantConfiguration
        {
            NotificationChannelsJson = "[" +
                ChannelJson("ch-1", extra: ",\"customHeadersJson\":\"{\\\"X-Api-Key\\\":\\\"k\\\"}\"") + "," +
                ChannelJson("ch-2", providerType: 10, url: "https://slack.example/h") + "]",
        };

        var redacted = NotificationChannel.ParseList(cfg.RedactedCopyForReader().NotificationChannelsJson);

        Assert.Equal(2, redacted.Count);
        Assert.Equal("ch-1", redacted[0].Id);                                  // structure visible
        Assert.Equal(Constants.RedactedSecretPlaceholder, redacted[0].Url);    // secrets masked
        Assert.Equal(Constants.RedactedSecretPlaceholder, redacted[0].CustomHeadersJson);
        Assert.Equal(Constants.RedactedSecretPlaceholder, redacted[1].Url);
        Assert.Null(redacted[1].CustomHeadersJson);                            // unset stays unset
    }

    [Fact]
    public void RedactedCopy_UnparseableChannelsJson_FallsBackToWholeStringRedaction()
    {
        var cfg = new TenantConfiguration { NotificationChannelsJson = "not-json-but-maybe-secret" };
        Assert.Equal(Constants.RedactedSecretPlaceholder, cfg.RedactedCopyForReader().NotificationChannelsJson);
    }

    [Fact]
    public void RestoreRedactedSecrets_RestoresChannelSecretsById()
    {
        var existing = new TenantConfiguration
        {
            NotificationChannelsJson = "[" +
                ChannelJson("ch-1", url: "https://real.example/hook",
                    extra: ",\"customHeadersJson\":\"{\\\"X-Api-Key\\\":\\\"real\\\"}\"") + "]",
        };

        // Round-trip the redacted view back onto a save (defense-in-depth path).
        var incoming = existing.RedactedCopyForReader();
        incoming.RestoreRedactedSecretsFrom(existing);

        var ch = Assert.Single(NotificationChannel.ParseList(incoming.NotificationChannelsJson));
        Assert.Equal("https://real.example/hook", ch.Url);
        Assert.Equal("{\"X-Api-Key\":\"real\"}", ch.CustomHeadersJson);
    }

    [Fact]
    public void RestoreRedactedSecrets_KeepsGenuineChannelEdits()
    {
        var existing = new TenantConfiguration
        {
            NotificationChannelsJson = "[" + ChannelJson("ch-1", url: "https://old.example/hook") + "]",
        };
        var incoming = new TenantConfiguration
        {
            // Genuine edit of ch-1 plus a brand-new ch-2: nothing carries the sentinel,
            // so restore must not touch anything.
            NotificationChannelsJson = "[" + ChannelJson("ch-1", url: "https://NEW.example/hook") + ","
                + ChannelJson("ch-2", url: "https://second.example/hook") + "]",
        };

        incoming.RestoreRedactedSecretsFrom(existing);

        var channels = NotificationChannel.ParseList(incoming.NotificationChannelsJson);
        Assert.Equal("https://NEW.example/hook", channels[0].Url);
        Assert.Equal("https://second.example/hook", channels[1].Url);
    }

    // ── Save-time validation (UpdateTenantConfigurationFunction) ─────────

    [Fact]
    public void ValidateNotificationChannels_NullEmptyOrValidList_PassesThrough()
    {
        Assert.Null(UpdateTenantConfigurationFunction.ValidateNotificationChannels(null));
        Assert.Null(UpdateTenantConfigurationFunction.ValidateNotificationChannels(""));
        Assert.Null(UpdateTenantConfigurationFunction.ValidateNotificationChannels(
            "[" + ChannelJson("ch-1") + "," + ChannelJson("ch-2", providerType: 10) + "]"));
    }

    [Theory]
    [InlineData("not-json", "not valid JSON")]
    [InlineData("[{\"name\":\"no-id\",\"providerType\":20}]", "needs an id")]
    [InlineData("[{\"id\":\"a\",\"providerType\":99}]", "invalid provider type")]
    [InlineData("[{\"id\":\"a\",\"providerType\":20,\"url\":\"http://plain.example\"}]", "")] // non-https rejected by SsrfGuard format check
    public void ValidateNotificationChannels_RejectsInvalidEntries(string json, string expectedFragment)
    {
        var error = UpdateTenantConfigurationFunction.ValidateNotificationChannels(json);
        Assert.NotNull(error);
        if (expectedFragment.Length > 0)
            Assert.Contains(expectedFragment, error);
    }

    [Fact]
    public void ValidateNotificationChannels_RejectsDuplicateIds()
    {
        var error = UpdateTenantConfigurationFunction.ValidateNotificationChannels(
            "[" + ChannelJson("ch-1") + "," + ChannelJson("ch-1") + "]");
        Assert.NotNull(error);
        Assert.Contains("duplicate", error);
    }

    [Fact]
    public void ValidateNotificationChannels_RejectsBadPerChannelHeaders()
    {
        var json = "[" + ChannelJson("ch-1", extra: ",\"customHeadersJson\":\"not-json\"") + "]";
        var error = UpdateTenantConfigurationFunction.ValidateNotificationChannels(json);
        Assert.NotNull(error);
        Assert.Contains("headers", error);
    }
}

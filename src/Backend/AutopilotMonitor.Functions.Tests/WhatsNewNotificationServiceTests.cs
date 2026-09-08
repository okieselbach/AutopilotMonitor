using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.WhatsNew;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;
using AutopilotMonitor.Shared.Models.WhatsNew;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// What's new → channel digest: feed parsing, watermark semantics (baseline on first run, diff by
/// entry key, claim-then-send with ETag CAS), per-channel opt-in routing, and the digest shape.
/// </summary>
public class WhatsNewNotificationServiceTests
{
    // ── Fakes ─────────────────────────────────────────────────────────────

    private sealed class InMemoryStateRepository : IWhatsNewNotificationStateRepository
    {
        private WhatsNewNotificationState? _row;
        private long _version = 0;
        public int ConflictsToInject { get; set; }
        public int Writes { get; private set; }

        public WhatsNewNotificationState? Current => _row;

        public void Seed(params string[] knownKeys)
        {
            _row = new WhatsNewNotificationState { KnownEntryKeys = new HashSet<string>(knownKeys, StringComparer.Ordinal) };
            _version++;
        }

        public Task<(WhatsNewNotificationState? State, string? ETag)> GetWithETagAsync()
            => Task.FromResult(_row == null ? ((WhatsNewNotificationState?)null, (string?)null) : (Clone(_row), _version.ToString()));

        public Task<bool> TryUpsertAsync(WhatsNewNotificationState state, string? ifMatchETag)
        {
            if (ConflictsToInject > 0)
            {
                ConflictsToInject--;
                _version++;
                return Task.FromResult(false);
            }
            if (ifMatchETag is null && _row != null) return Task.FromResult(false);
            if (ifMatchETag is not null && ifMatchETag != _version.ToString()) return Task.FromResult(false);

            _row = Clone(state);
            _version++;
            Writes++;
            return Task.FromResult(true);
        }

        private static WhatsNewNotificationState Clone(WhatsNewNotificationState s) => new()
        {
            KnownEntryKeys = new HashSet<string>(s.KnownEntryKeys, StringComparer.Ordinal),
            LastDocsCommit = s.LastDocsCommit,
            LastRunUtc = s.LastRunUtc,
            LastNotifiedUtc = s.LastNotifiedUtc,
            LastNotifiedEntryCount = s.LastNotifiedEntryCount,
        };
    }

    private sealed class Harness
    {
        public InMemoryStateRepository StateRepo { get; } = new();
        public Mock<IWhatsNewFeedClient> Feed { get; } = new();
        public Mock<IConfigRepository> ConfigRepo { get; } = new();
        public Mock<NotificationChannelDispatcher> Dispatcher { get; }
        public List<(List<NotificationChannel> Channels, NotificationAlert Alert)> Sent { get; } = new();
        public WhatsNewNotificationService Service { get; }
        public DateTime Now { get; } = new(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);

        public Harness()
        {
            var webhook = new WebhookNotificationService(new HttpClient(), NullLogger<WebhookNotificationService>.Instance);
            var telegram = new TelegramNotificationService(new HttpClient(), Mock.Of<IConfigRepository>(), NullLogger<TelegramNotificationService>.Instance);
            Dispatcher = new Mock<NotificationChannelDispatcher>(webhook, telegram);
            Dispatcher
                .Setup(d => d.SendToChannelsAsync(It.IsAny<IEnumerable<NotificationChannel>>(), It.IsAny<NotificationAlert>()))
                .Returns<IEnumerable<NotificationChannel>, NotificationAlert>((channels, alert) =>
                {
                    Sent.Add((channels.ToList(), alert));
                    return Task.CompletedTask;
                });

            ConfigRepo.Setup(r => r.GetAllTenantConfigurationsAsync()).ReturnsAsync(new List<TenantConfiguration>());

            Service = new WhatsNewNotificationService(
                Feed.Object,
                StateRepo,
                ConfigRepo.Object,
                Dispatcher.Object,
                new TelemetryClient(new TelemetryConfiguration()),
                NullLogger<WhatsNewNotificationService>.Instance,
                () => Now);
        }

        public void SetFeed(WhatsNewFeed? feed)
            => Feed.Setup(f => f.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(feed);

        public void SetTenants(params TenantConfiguration[] tenants)
            => ConfigRepo.Setup(r => r.GetAllTenantConfigurationsAsync()).ReturnsAsync(tenants.ToList());
    }

    private static WhatsNewFeedEntry Entry(string channel, string id, string? title = null, string body = "Body text.", int daysAgo = 0)
        => new()
        {
            Channel = channel,
            Id = id,
            AddedUtc = new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc).AddDays(-daysAgo),
            Period = "September 2026",
            Title = title,
            Body = body,
            Link = null,
        };

    private static WhatsNewFeed Feed(params WhatsNewFeedEntry[] entries) => new()
    {
        DocsCommit = "abc1234",
        Entries = entries,
        DocsUrls = new Dictionary<string, string>
        {
            [WhatsNewFeed.PlatformChannel] = Constants.DocsBaseUrl + "/changelog/platform-changelog",
            [WhatsNewFeed.AgentChannel] = Constants.DocsBaseUrl + "/changelog/agent-changelog",
        },
    };

    private static TenantConfiguration Tenant(string id, params NotificationChannel[] channels) => new()
    {
        TenantId = id,
        NotificationChannelsJson = NotificationChannel.SerializeList(channels),
    };

    private static NotificationChannel Channel(string id, bool whatsNew, bool enabled = true, string url = "https://hooks.example/x")
        => new() { Id = id, Name = id, ProviderType = 20, Url = url, Enabled = enabled, NotifyOnWhatsNew = whatsNew, NotifyOnSuccess = true };

    // ── Feed parsing ──────────────────────────────────────────────────────

    [Fact]
    public void Parse_ValidPayload_YieldsEntriesForBothChannels()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "generatedUtc": "2026-09-08T10:00:00.000Z",
          "docsCommit": "deadbee",
          "channels": {
            "platform": { "docsUrl": "https://docs.autopilotmonitor.com/changelog/platform-changelog", "entries": [
              { "id": "0a1b2c3d", "addedUtc": "2026-09-07T09:00:00.000Z", "period": "September 2026", "title": "**Bold lead**", "body": "Body with [link](https://docs.autopilotmonitor.com/x).", "link": "https://docs.autopilotmonitor.com/x" }
            ] },
            "agent": { "docsUrl": "https://docs.autopilotmonitor.com/changelog/agent-changelog", "entries": [
              { "id": "ffffffff", "addedUtc": "2026-09-01T09:00:00.000Z", "period": "September 2026", "title": null, "body": "Agent bullet.", "link": null }
            ] }
          }
        }
        """;

        var feed = WhatsNewFeed.Parse(json);

        Assert.NotNull(feed);
        Assert.Equal("deadbee", feed!.DocsCommit);
        Assert.Equal(2, feed.Entries.Count);
        Assert.Equal("platform:0a1b2c3d", feed.Entries[0].Key);
        Assert.Equal("agent:ffffffff", feed.Entries[1].Key);
        Assert.Equal(new DateTime(2026, 9, 7, 9, 0, 0, DateTimeKind.Utc), feed.Entries[0].AddedUtc);
        Assert.Equal("https://docs.autopilotmonitor.com/changelog/agent-changelog", feed.DocsUrls[WhatsNewFeed.AgentChannel]);
    }

    [Fact]
    public void Parse_OffShape_YieldsNull()
    {
        Assert.Null(WhatsNewFeed.Parse(null));
        Assert.Null(WhatsNewFeed.Parse(""));
        Assert.Null(WhatsNewFeed.Parse("not json"));
        Assert.Null(WhatsNewFeed.Parse("""{"schemaVersion":2,"channels":{"platform":{"entries":[]},"agent":{"entries":[]}}}"""));
        // Missing channel
        Assert.Null(WhatsNewFeed.Parse("""{"schemaVersion":1,"channels":{"platform":{"entries":[]}}}"""));
        // Entry without id
        Assert.Null(WhatsNewFeed.Parse("""{"schemaVersion":1,"channels":{"platform":{"entries":[{"addedUtc":"2026-09-07T09:00:00Z","body":"x"}]},"agent":{"entries":[]}}}"""));
    }

    // ── Run semantics ─────────────────────────────────────────────────────

    [Fact]
    public async Task Run_FeedUnavailable_DoesNotTouchStateOrSend()
    {
        var h = new Harness();
        h.SetFeed(null);
        h.StateRepo.Seed("platform:old");

        var result = await h.Service.RunAsync();

        Assert.True(result.FeedUnavailable);
        Assert.Equal(0, h.StateRepo.Writes);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task Run_FirstRun_BaselinesWithoutSending()
    {
        var h = new Harness();
        h.SetFeed(Feed(Entry("platform", "a"), Entry("agent", "b")));
        h.SetTenants(Tenant("t1", Channel("c1", whatsNew: true)));

        var result = await h.Service.RunAsync();

        Assert.True(result.Baselined);
        Assert.Empty(h.Sent);
        Assert.NotNull(h.StateRepo.Current);
        Assert.Equal(new[] { "agent:b", "platform:a" }, h.StateRepo.Current!.KnownEntryKeys.OrderBy(k => k));
        Assert.Equal("abc1234", h.StateRepo.Current.LastDocsCommit);
        Assert.Null(h.StateRepo.Current.LastNotifiedUtc);
    }

    [Fact]
    public async Task Run_EmptyKnownSet_IsTreatedAsBaseline()
    {
        var h = new Harness();
        h.StateRepo.Seed(); // row exists, no keys
        h.SetFeed(Feed(Entry("platform", "a")));
        h.SetTenants(Tenant("t1", Channel("c1", whatsNew: true)));

        var result = await h.Service.RunAsync();

        Assert.True(result.Baselined);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task Run_NothingNew_NoSendAndNoWriteWhenUnchanged()
    {
        var h = new Harness();
        h.StateRepo.Seed("platform:a", "agent:b");
        h.SetFeed(Feed(Entry("platform", "a"), Entry("agent", "b")));
        h.SetTenants(Tenant("t1", Channel("c1", whatsNew: true)));

        var result = await h.Service.RunAsync();

        Assert.Equal(0, result.NewEntries);
        Assert.Empty(h.Sent);
        Assert.Equal(0, h.StateRepo.Writes);
    }

    [Fact]
    public async Task Run_EntriesRolledOut_UpdatesWatermarkWithoutSending()
    {
        var h = new Harness();
        h.StateRepo.Seed("platform:a", "platform:old");
        h.SetFeed(Feed(Entry("platform", "a")));

        var result = await h.Service.RunAsync();

        Assert.Equal(0, result.NewEntries);
        Assert.Empty(h.Sent);
        Assert.Equal(1, h.StateRepo.Writes);
        Assert.Equal(new[] { "platform:a" }, h.StateRepo.Current!.KnownEntryKeys.ToArray());
    }

    [Fact]
    public async Task Run_NewEntries_SendsOneDigestPerTenantToOptedInChannelsOnly()
    {
        var h = new Harness();
        h.StateRepo.Seed("platform:a");
        h.SetFeed(Feed(Entry("platform", "a"), Entry("platform", "b", title: "**New** thing"), Entry("agent", "c")));
        h.SetTenants(
            Tenant("opted-in", Channel("yes", whatsNew: true), Channel("no", whatsNew: false), Channel("disabled", whatsNew: true, enabled: false)),
            Tenant("not-opted-in", Channel("no", whatsNew: false)),
            Tenant("no-channels"));

        var result = await h.Service.RunAsync();

        Assert.Equal(2, result.NewEntries);
        Assert.Equal(1, result.TenantsNotified);
        Assert.Equal(1, result.ChannelsNotified);

        var (channels, alert) = Assert.Single(h.Sent);
        Assert.Equal("yes", Assert.Single(channels).Id);
        Assert.Equal("whats_new", alert.EventType);
        Assert.Equal(2, alert.Sections.Count);
        Assert.Contains("2 new updates", alert.Summary);
        Assert.Contains("1 platform, 1 agent", alert.Summary);

        // Watermark advanced to the full live set and marked as notified.
        var state = h.StateRepo.Current!;
        Assert.Equal(new[] { "agent:c", "platform:a", "platform:b" }, state.KnownEntryKeys.OrderBy(k => k));
        Assert.Equal(h.Now, state.LastNotifiedUtc);
        Assert.Equal(2, state.LastNotifiedEntryCount);
    }

    [Fact]
    public async Task Run_SkipsDisabledTenants()
    {
        var h = new Harness();
        h.StateRepo.Seed("platform:a");
        h.SetFeed(Feed(Entry("platform", "a"), Entry("platform", "b")));
        var disabled = Tenant("off", Channel("yes", whatsNew: true));
        disabled.Disabled = true;
        h.SetTenants(disabled);

        var result = await h.Service.RunAsync();

        Assert.Equal(1, result.NewEntries);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task Run_LostRace_DoesNotSend()
    {
        var h = new Harness();
        h.StateRepo.Seed("platform:a");
        h.StateRepo.ConflictsToInject = 1;
        h.SetFeed(Feed(Entry("platform", "a"), Entry("platform", "b")));
        h.SetTenants(Tenant("t1", Channel("c1", whatsNew: true)));

        var result = await h.Service.RunAsync();

        Assert.True(result.LostRace);
        Assert.Empty(h.Sent);
        h.ConfigRepo.Verify(r => r.GetAllTenantConfigurationsAsync(), Times.Never);
    }

    [Fact]
    public async Task Run_SecondRunAfterAnnouncement_IsQuiet()
    {
        var h = new Harness();
        h.StateRepo.Seed("platform:a");
        h.SetFeed(Feed(Entry("platform", "a"), Entry("platform", "b")));
        h.SetTenants(Tenant("t1", Channel("c1", whatsNew: true)));

        await h.Service.RunAsync();
        var second = await h.Service.RunAsync();

        Assert.Equal(0, second.NewEntries);
        Assert.Single(h.Sent);
    }

    [Fact]
    public async Task Run_LegacySingleWebhookTenant_NeverReceivesWhatsNew()
    {
        var h = new Harness();
        h.StateRepo.Seed("platform:a");
        h.SetFeed(Feed(Entry("platform", "a"), Entry("platform", "b")));
        h.SetTenants(new TenantConfiguration
        {
            TenantId = "legacy",
            WebhookUrl = "https://hooks.example/legacy",
            WebhookProviderType = 2,
            WebhookNotifyOnSuccess = true,
        });

        var result = await h.Service.RunAsync();

        Assert.Equal(1, result.NewEntries);
        Assert.Empty(h.Sent);
    }

    // ── Digest shape ──────────────────────────────────────────────────────

    [Fact]
    public void BuildWhatsNewAlert_NewestFirst_PlainText_Actions()
    {
        var entries = new List<WhatsNewFeedEntry>
        {
            Entry("agent", "old", body: "Older agent `fix` here.", daysAgo: 5),
            Entry("platform", "new", title: "**Rules editor** revamp", body: "Read the [guide](https://docs.autopilotmonitor.com/g) for _details_."),
        };
        var docs = new Dictionary<string, string>
        {
            [WhatsNewFeed.PlatformChannel] = Constants.DocsBaseUrl + "/changelog/platform-changelog",
            [WhatsNewFeed.AgentChannel] = Constants.DocsBaseUrl + "/changelog/agent-changelog",
        };

        var alert = NotificationAlertBuilder.BuildWhatsNewAlert(entries, docs, WhatsNewNotificationService.PortalWhatsNewUrl("platform"));

        Assert.Equal(2, alert.Sections.Count);
        Assert.StartsWith("Platform \u00b7 Sep 8, 2026 \u2014 Rules editor revamp", alert.Sections[0].Title);
        Assert.Equal("Read the guide for details.", alert.Sections[0].Text);
        Assert.StartsWith("Agent \u00b7 Sep 3, 2026", alert.Sections[1].Title);
        Assert.Equal("Older agent fix here.", alert.Sections[1].Text);

        Assert.Equal(2, alert.Actions.Count);
        Assert.Equal(Constants.PortalBaseUrl + "/dashboard?whats-new=platform", alert.Actions[0].Url);
        // Tie (1 platform, 1 agent) → platform changelog.
        Assert.Equal(Constants.DocsBaseUrl + "/changelog/platform-changelog", alert.Actions[1].Url);
    }

    [Fact]
    public void BuildWhatsNewAlert_CapsSectionsAndFoldsRemainder()
    {
        var entries = Enumerable.Range(0, NotificationAlertBuilder.WhatsNewMaxSections + 3)
            .Select(i => Entry("agent", $"id{i}", body: $"Bullet {i}", daysAgo: i))
            .ToList();

        var alert = NotificationAlertBuilder.BuildWhatsNewAlert(entries, null, WhatsNewNotificationService.PortalWhatsNewUrl("agent"));

        Assert.Equal(NotificationAlertBuilder.WhatsNewMaxSections + 1, alert.Sections.Count);
        Assert.Contains("+ 3 more updates", alert.Sections[^1].Text);
        Assert.Single(alert.Actions); // no docs URLs → only the portal button
        Assert.Contains($"{entries.Count} new updates", alert.Summary);
    }

    [Fact]
    public void BuildWhatsNewAlert_TelegramText_CarriesSectionsAndLinks()
    {
        var alert = NotificationAlertBuilder.BuildWhatsNewAlert(
            new List<WhatsNewFeedEntry> { Entry("platform", "p", title: "**Lead**", body: "Body.") },
            null,
            WhatsNewNotificationService.PortalWhatsNewUrl("platform"));

        var text = TelegramNotificationService.RenderAlertText(alert);

        Assert.Contains("What's new in Autopilot Monitor", text);
        Assert.Contains("Platform \u00b7 Sep 8, 2026 \u2014 Lead", text);
        Assert.Contains("Body.", text);
        Assert.Contains("Open What's new: " + Constants.PortalBaseUrl + "/dashboard?whats-new=platform", text);
    }

    [Theory]
    [InlineData("**Bold** text", "Bold text")]
    [InlineData("See [the docs](https://docs.autopilotmonitor.com/x) now", "See the docs now")]
    [InlineData("Run `agent.exe --verbose`", "Run agent.exe --verbose")]
    [InlineData("_emphasis_ and *more*", "emphasis and more")]
    [InlineData("snake_case_name stays", "snake_case_name stays")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData(null, "")]
    public void StripInlineMarkdown_Flattens(string? input, string expected)
        => Assert.Equal(expected, NotificationAlertBuilder.StripInlineMarkdown(input));

    // ── Channel model ─────────────────────────────────────────────────────

    [Fact]
    public void NotifyOnWhatsNew_RoundTripsThroughJson_AndDefaultsOff()
    {
        var json = NotificationChannel.SerializeList(new[] { Channel("c", whatsNew: true) });
        Assert.Contains("\"notifyOnWhatsNew\":true", json);
        Assert.True(NotificationChannel.ParseList(json).Single().NotifyOnWhatsNew);

        var legacyShape = "[{\"id\":\"x\",\"name\":\"x\",\"providerType\":20,\"url\":\"https://hooks.example/x\",\"enabled\":true}]";
        Assert.False(NotificationChannel.ParseList(legacyShape).Single().NotifyOnWhatsNew);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The notification-dispatch consumer resolves channel ids against the tenant's CURRENT
/// configuration — no URL or secret ever rides in the queue — and sends only to channels that
/// are still enabled. Plus the wire contract: the envelope round-trips through Newtonsoft with
/// the alert's facts, sections and severity intact.
/// </summary>
public class NotificationDispatchHandlerTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task Sends_to_enabled_channels_matching_the_envelope_ids_only()
    {
        var h = new Harness(new[]
        {
            Channel("teams", enabled: true),
            Channel("slack", enabled: true),
            Channel("old", enabled: false),
        });

        await h.Sut.HandleAsync(Envelope("teams", "old"), CancellationToken.None);

        var sent = Assert.Single(h.Sent);
        Assert.Equal(new[] { "teams" }, sent.Channels.Select(c => c.Id));
        Assert.Equal("enrollment_succeeded", sent.Alert.EventType);
    }

    [Fact]
    public async Task Unknown_channel_ids_are_skipped_without_sending()
    {
        var h = new Harness(new[] { Channel("teams", enabled: true) });

        await h.Sut.HandleAsync(Envelope("deleted-meanwhile"), CancellationToken.None);

        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task Missing_tenant_configuration_drops_the_alert()
    {
        var h = new Harness(channels: null);

        await h.Sut.HandleAsync(Envelope("teams"), CancellationToken.None);

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Envelope_round_trips_through_newtonsoft()
    {
        var envelope = Envelope("teams");
        envelope.Alert.Facts.Add(new NotificationFact { Name = "Device", Value = "PC-0001" });
        envelope.Alert.Severity = NotificationSeverity.Error;

        var restored = JsonConvert.DeserializeObject<NotificationDispatchEnvelope>(
            JsonConvert.SerializeObject(envelope))!;

        Assert.Equal(Tenant, restored.TenantId);
        Assert.Equal(new[] { "teams" }, restored.ChannelIds);
        Assert.Equal("enrollment_succeeded", restored.Alert.EventType);
        Assert.Equal(NotificationSeverity.Error, restored.Alert.Severity);
        var fact = Assert.Single(restored.Alert.Facts);
        Assert.Equal("PC-0001", fact.Value);
    }

    // ============================================================ Helpers ====

    private static NotificationChannel Channel(string id, bool enabled) => new()
    {
        Id = id,
        Name = id,
        ProviderType = (int)WebhookProviderType.Slack,
        Url = $"https://example.invalid/{id}",
        Enabled = enabled,
        NotifyOnSuccess = true,
    };

    private static NotificationDispatchEnvelope Envelope(params string[] channelIds) => new()
    {
        TenantId = Tenant,
        SessionId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        ChannelIds = channelIds.ToList(),
        Alert = new NotificationAlert
        {
            EventType = "enrollment_succeeded",
            Title = "Enrollment Succeeded",
            Summary = "PC-0001 enrolled",
            Severity = NotificationSeverity.Info,
            ThemeColor = "00AA00",
        },
        EnqueuedAt = DateTime.UtcNow,
    };

    private sealed class Harness
    {
        public List<(IReadOnlyList<NotificationChannel> Channels, NotificationAlert Alert)> Sent { get; } = new();
        public NotificationDispatchHandler Sut { get; }

        public Harness(IEnumerable<NotificationChannel>? channels)
        {
            var configService = new Mock<TenantConfigurationService>(
                Mock.Of<IConfigRepository>(),
                NullLogger<TenantConfigurationService>.Instance,
                new MemoryCache(new MemoryCacheOptions()))
            { CallBase = false };

            if (channels is null)
            {
                configService.Setup(c => c.TryGetConfigurationAsync(Tenant))
                    .ReturnsAsync((new TenantConfiguration { TenantId = Tenant }, false));
            }
            else
            {
                var config = new TenantConfiguration
                {
                    TenantId = Tenant,
                    NotificationChannelsJson = NotificationChannel.SerializeList(channels),
                };
                configService.Setup(c => c.TryGetConfigurationAsync(Tenant))
                    .ReturnsAsync((config, true));
            }

            var dispatcher = new Mock<NotificationChannelDispatcher>(null!, null!);
            dispatcher.Setup(d => d.SendToChannelsAsync(It.IsAny<IEnumerable<NotificationChannel>>(), It.IsAny<NotificationAlert>()))
                .Callback<IEnumerable<NotificationChannel>, NotificationAlert>((c, a) => Sent.Add((c.ToList(), a)))
                .Returns(Task.CompletedTask);

            Sut = new NotificationDispatchHandler(
                configService.Object, dispatcher.Object, NullLogger<NotificationDispatchHandler>.Instance);
        }
    }
}

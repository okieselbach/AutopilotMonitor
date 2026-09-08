using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Notifications;
using AutopilotMonitor.Shared.Models.WhatsNew;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.WhatsNew
{
    /// <summary>
    /// Announces newly published What's new entries to every tenant channel that opted in
    /// (<see cref="NotificationChannel.NotifyOnWhatsNew"/>).
    /// <para>
    /// "New" means: present in the live <c>/whats-new.json</c> but absent from the platform-wide
    /// watermark (<see cref="WhatsNewNotificationState.KnownEntryKeys"/>) — i.e. the entry became
    /// visible in the portal since the last run. Diffing on ids rather than dates matters because
    /// the payload is regenerated only on web deploys: a bullet's docs commit date can be days
    /// older than the moment it actually appears. All new entries of a run go out as ONE digest
    /// per channel, so a release that adds five bullets is one message, not five.
    /// </para>
    /// <para>
    /// Ordering is claim-then-send: the watermark is advanced with an ETag compare-and-swap
    /// BEFORE any channel is contacted. A lost race means another run owns this batch and this
    /// one exits without sending; a crash after the claim loses the batch rather than
    /// double-announcing it (at-most-once, like every other channel notification here).
    /// The very first run — no state row, or an empty known set — only baselines and never
    /// announces: a fresh deployment must not blast the entire changelog at every tenant.
    /// </para>
    /// </summary>
    public class WhatsNewNotificationService
    {
        /// <summary>Portal deep link that opens the panel on load (WhatsNewPanelHost reads <c>?whats-new=</c>).</summary>
        internal static string PortalWhatsNewUrl(string channel)
            => $"{Constants.PortalBaseUrl}/dashboard?whats-new={Uri.EscapeDataString(channel)}";

        private readonly IWhatsNewFeedClient _feedClient;
        private readonly IWhatsNewNotificationStateRepository _stateRepo;
        private readonly IConfigRepository _configRepo;
        private readonly NotificationChannelDispatcher _channelDispatcher;
        private readonly TelemetryClient _telemetryClient;
        private readonly ILogger<WhatsNewNotificationService> _logger;
        private readonly Func<DateTime> _nowProvider;

        public WhatsNewNotificationService(
            IWhatsNewFeedClient feedClient,
            IWhatsNewNotificationStateRepository stateRepo,
            IConfigRepository configRepo,
            NotificationChannelDispatcher channelDispatcher,
            TelemetryClient telemetryClient,
            ILogger<WhatsNewNotificationService> logger,
            Func<DateTime>? nowProvider = null)
        {
            _feedClient = feedClient;
            _stateRepo = stateRepo;
            _configRepo = configRepo;
            _channelDispatcher = channelDispatcher;
            _telemetryClient = telemetryClient;
            _logger = logger;
            _nowProvider = nowProvider ?? (() => DateTime.UtcNow);
        }

        public sealed class RunResult
        {
            public bool FeedUnavailable { get; init; }
            public bool Baselined { get; init; }
            public bool LostRace { get; init; }
            public int NewEntries { get; init; }
            public int TenantsNotified { get; init; }
            public int ChannelsNotified { get; init; }
        }

        /// <summary>Timer entry point. Never throws for expected conditions (feed down, nothing new, lost race).</summary>
        public async Task<RunResult> RunAsync(CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();

            var feed = await _feedClient.GetAsync(ct);
            if (feed == null)
            {
                _logger.LogWarning("What's new notifier: feed unavailable — skipping run");
                return new RunResult { FeedUnavailable = true };
            }

            var (state, etag) = await _stateRepo.GetWithETagAsync();
            var now = _nowProvider();
            var currentKeys = new HashSet<string>(feed.Entries.Select(e => e.Key), StringComparer.Ordinal);

            var next = new WhatsNewNotificationState
            {
                KnownEntryKeys = currentKeys,
                LastDocsCommit = feed.DocsCommit,
                LastRunUtc = now,
                LastNotifiedUtc = state?.LastNotifiedUtc,
                LastNotifiedEntryCount = state?.LastNotifiedEntryCount ?? 0,
            };

            // First run (or a wiped/malformed watermark): record what is live, announce nothing.
            if (state == null || state.KnownEntryKeys.Count == 0)
            {
                var stored = await _stateRepo.TryUpsertAsync(next, etag);
                _logger.LogInformation("What's new notifier: baselined {Count} entries (docs @ {Commit}), stored={Stored}",
                    currentKeys.Count, feed.DocsCommit ?? "unknown", stored);
                return new RunResult { Baselined = true };
            }

            var newEntries = feed.Entries.Where(e => !state.KnownEntryKeys.Contains(e.Key)).ToList();
            if (newEntries.Count == 0)
            {
                // Only touch storage when the payload actually changed (entries rolled out of the window).
                if (!currentKeys.SetEquals(state.KnownEntryKeys))
                    await _stateRepo.TryUpsertAsync(next, etag);
                _logger.LogDebug("What's new notifier: nothing new ({Count} entries live)", currentKeys.Count);
                return new RunResult();
            }

            next.LastNotifiedUtc = now;
            next.LastNotifiedEntryCount = newEntries.Count;

            if (!await _stateRepo.TryUpsertAsync(next, etag))
            {
                _logger.LogInformation("What's new notifier: watermark changed underneath this run — another run owns the batch");
                return new RunResult { LostRace = true, NewEntries = newEntries.Count };
            }

            var (tenantsNotified, channelsNotified) = await DispatchAsync(feed, newEntries, ct);

            _logger.LogInformation(
                "What's new notifier: announced {New} new entries to {Channels} channels across {Tenants} tenants in {ElapsedMs}ms",
                newEntries.Count, channelsNotified, tenantsNotified, sw.ElapsedMilliseconds);

            _telemetryClient.TrackEvent("WhatsNewNotificationSent", new Dictionary<string, string>
            {
                { "NewEntries", newEntries.Count.ToString() },
                { "PlatformEntries", newEntries.Count(e => e.Channel == WhatsNewFeed.PlatformChannel).ToString() },
                { "AgentEntries", newEntries.Count(e => e.Channel == WhatsNewFeed.AgentChannel).ToString() },
                { "Tenants", tenantsNotified.ToString() },
                { "Channels", channelsNotified.ToString() },
                { "DocsCommit", feed.DocsCommit ?? "unknown" },
            });

            return new RunResult
            {
                NewEntries = newEntries.Count,
                TenantsNotified = tenantsNotified,
                ChannelsNotified = channelsNotified,
            };
        }

        private async Task<(int Tenants, int Channels)> DispatchAsync(
            WhatsNewFeed feed, List<WhatsNewFeedEntry> newEntries, CancellationToken ct)
        {
            var platformCount = newEntries.Count(e => e.Channel == WhatsNewFeed.PlatformChannel);
            var primaryChannel = newEntries.Count - platformCount > platformCount
                ? WhatsNewFeed.AgentChannel
                : WhatsNewFeed.PlatformChannel;
            var alert = NotificationAlertBuilder.BuildWhatsNewAlert(newEntries, feed.DocsUrls, PortalWhatsNewUrl(primaryChannel));

            var configs = await _configRepo.GetAllTenantConfigurationsAsync();
            int tenants = 0, channels = 0;

            foreach (var config in configs)
            {
                if (ct.IsCancellationRequested) break;
                if (config.IsCurrentlyDisabled()) continue;

                var targets = config.GetNotificationChannels()
                    .Where(c => c.Enabled && c.NotifyOnWhatsNew && !string.IsNullOrEmpty(c.Url))
                    .ToList();
                if (targets.Count == 0) continue;

                try
                {
                    await _channelDispatcher.SendToChannelsAsync(targets, alert);
                    tenants++;
                    channels += targets.Count;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "What's new notifier: dispatch failed for tenant {TenantId}", config.TenantId);
                }
            }

            return (tenants, channels);
        }
    }
}

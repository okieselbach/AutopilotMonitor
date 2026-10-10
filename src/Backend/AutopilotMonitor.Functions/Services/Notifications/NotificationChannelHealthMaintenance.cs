using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Notifications
{
    /// <summary>Counts of one <see cref="NotificationChannelHealthMaintenance"/> run.</summary>
    public sealed class ChannelHealthMaintenanceResult
    {
        public int RowsScanned { get; set; }
        public int RowsDeleted { get; set; }
        public int ScopesSkipped { get; set; }
    }

    /// <summary>
    /// Deletes the health rows of deleted channels, folded into the 2-hourly maintenance run like
    /// the push retention. A row goes when its channel is no longer in the stored channel list of
    /// its scope AND it saw no send for <see cref="OrphanRetentionDays"/> days — a revert of the
    /// configuration shortly after a delete brings the channel's status back with it. Never at save
    /// time: the channel lists have several write paths, and none of them should know this table.
    /// <para>
    /// The channel lists are read strictly: a failed read skips the scope, so a storage hiccup can
    /// never read as "no channels" and wipe live rows. A tenant without a configuration row has no
    /// channels left; a missing platform configuration is an anomaly and skips the scope.
    /// </para>
    /// </summary>
    public class NotificationChannelHealthMaintenance
    {
        public const int OrphanRetentionDays = 30;

        private readonly INotificationChannelHealthRepository _repository;
        private readonly IConfigRepository _configRepository;
        private readonly ILogger<NotificationChannelHealthMaintenance> _logger;

        public NotificationChannelHealthMaintenance(
            INotificationChannelHealthRepository repository,
            IConfigRepository configRepository,
            ILogger<NotificationChannelHealthMaintenance> logger)
        {
            _repository = repository;
            _configRepository = configRepository;
            _logger = logger;
        }

        /// <summary>Never throws; reports what it did and logs a Warning only when it deleted or skipped something.</summary>
        public async Task<ChannelHealthMaintenanceResult> RunAsync(DateTime? nowUtc = null)
        {
            var cutoff = (nowUtc ?? DateTime.UtcNow).AddDays(-OrphanRetentionDays);
            var result = new ChannelHealthMaintenanceResult();

            List<(NotificationChannelHealth Row, string ETag)> rows;
            try
            {
                rows = await _repository.ListAllAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Channel health maintenance: listing the health rows failed");
                return result;
            }
            result.RowsScanned = rows.Count;

            foreach (var scope in rows.GroupBy(r => r.Row.ScopeKey, StringComparer.OrdinalIgnoreCase))
            {
                // Only rows that are old enough can go; a scope without any skips the config read.
                var candidates = scope.Where(r => (r.Row.LastAttemptUtc ?? DateTime.MinValue) < cutoff).ToList();
                if (candidates.Count == 0)
                    continue;

                HashSet<string>? liveIds;
                try
                {
                    liveIds = await LoadChannelIdsAsync(scope.Key).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Channel health maintenance: reading the channel list of scope {Scope} failed; scope skipped", scope.Key);
                    result.ScopesSkipped++;
                    continue;
                }
                if (liveIds == null)
                {
                    result.ScopesSkipped++;
                    continue;
                }

                foreach (var (row, etag) in candidates.Where(r => !liveIds.Contains(r.Row.ChannelId)))
                {
                    try
                    {
                        if (await _repository.TryDeleteAsync(row, etag).ConfigureAwait(false))
                            result.RowsDeleted++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Channel health maintenance: deleting {Scope}/{ChannelId} failed", row.ScopeKey, row.ChannelId);
                    }
                }
            }

            if (result.RowsDeleted > 0 || result.ScopesSkipped > 0)
            {
                _logger.LogWarning(
                    "Channel health maintenance: scanned={Scanned} deleted={Deleted} scopesSkipped={Skipped}",
                    result.RowsScanned, result.RowsDeleted, result.ScopesSkipped);
            }
            return result;
        }

        /// <summary>
        /// Channel ids of the scope's stored configuration — the same lists the dispatcher sends
        /// to, legacy synthesis included. Null = do not touch the scope.
        /// </summary>
        private async Task<HashSet<string>?> LoadChannelIdsAsync(string scopeKey)
        {
            IEnumerable<NotificationChannel> channels;
            if (string.Equals(scopeKey, Constants.Push.PlatformScope, StringComparison.Ordinal))
            {
                var admin = await _configRepository.GetAdminConfigurationAsync().ConfigureAwait(false);
                if (admin == null)
                    return null;
                channels = admin.GetOpsNotificationChannels();
            }
            else
            {
                var tenant = await _configRepository.GetTenantConfigurationAsync(scopeKey).ConfigureAwait(false);
                channels = tenant?.GetNotificationChannels() ?? Enumerable.Empty<NotificationChannel>();
            }

            return new HashSet<string>(
                channels.Where(c => c != null && !string.IsNullOrWhiteSpace(c.Id)).Select(c => c.Id),
                StringComparer.OrdinalIgnoreCase);
        }
    }
}

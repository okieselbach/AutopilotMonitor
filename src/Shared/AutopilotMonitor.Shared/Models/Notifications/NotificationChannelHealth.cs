using System;

namespace AutopilotMonitor.Shared.Models.Notifications
{
    /// <summary>
    /// Delivery record of one notification channel: one row per channel in the
    /// <c>NotificationChannelHealth</c> table (PartitionKey = the scope key — lowercase tenant id
    /// or the platform scope for ops channels; RowKey = <see cref="NotificationChannel.Id"/>).
    /// Kept out of the channel JSON on purpose: every delivery writes this row, and the channel
    /// list is secret-bearing configuration with its own guarded write path.
    /// </summary>
    public class NotificationChannelHealth
    {
        /// <summary>Most recent outcomes kept in <see cref="Recent"/>.</summary>
        public const int RecentCapacity = 20;

        public string ScopeKey { get; set; } = string.Empty;

        public string ChannelId { get; set; } = string.Empty;

        /// <summary>
        /// Hash of the channel's destination (provider, URL, headers, signing secret). A row whose
        /// fingerprint differs from the current channel describes an earlier destination and is
        /// treated as absent, so a corrected URL starts without the old failures.
        /// </summary>
        public string Fingerprint { get; set; } = string.Empty;

        /// <summary>Outcomes of the last <see cref="RecentCapacity"/> sends, oldest first: '1' delivered, '0' failed.</summary>
        public string Recent { get; set; } = string.Empty;

        /// <summary>Failed sends since the last successful one.</summary>
        public int ConsecutiveFailures { get; set; }

        public DateTime? LastAttemptUtc { get; set; }

        public DateTime? LastSuccessUtc { get; set; }

        public DateTime? LastFailureUtc { get; set; }

        /// <summary>First failure of the current run of consecutive failures; null after a success.</summary>
        public DateTime? FailingSinceUtc { get; set; }

        /// <summary>HTTP status of the last failure, when the destination answered at all.</summary>
        public int? LastStatusCode { get; set; }

        /// <summary>Short classified reason of the last failure (never a response body).</summary>
        public string? LastError { get; set; }

        /// <summary>
        /// Set when the bell for the current failing episode was raised; cleared by the next
        /// success. The row is the episode's dedup — one bell per episode, however many sends fail.
        /// </summary>
        public DateTime? FailingNotifiedUtc { get; set; }
    }

    /// <summary>Delivery status of a notification channel, derived from its <see cref="NotificationChannelHealth"/> row.</summary>
    public enum NotificationChannelHealthStatus
    {
        /// <summary>No send recorded for the channel's current destination.</summary>
        Unknown,

        /// <summary>Delivering, and none of the last 20 sends failed within the last seven days.</summary>
        Ok,

        /// <summary>One of the last 20 sends failed within the last seven days, but the channel is not failing.</summary>
        Degraded,

        /// <summary>Several sends in a row failed; nothing is being delivered.</summary>
        Failing,
    }
}

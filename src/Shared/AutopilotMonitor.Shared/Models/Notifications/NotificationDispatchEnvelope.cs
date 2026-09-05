using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models.Notifications
{
    /// <summary>
    /// Queue-message envelope for the <c>notification-dispatch</c> queue: an enrollment or
    /// hardware-rejection alert that must reach the tenant's channels even though the HTTP
    /// request that produced it has already been answered. Replaces the fire-and-forget sends
    /// in the ingest and distress paths, which the Functions host does not count as open work
    /// at shutdown and which a Flex scale-in could therefore drop.
    /// <para>
    /// The envelope names the target channels by <see cref="ChannelIds"/> only. The consumer
    /// re-resolves them from the tenant configuration at send time, so no webhook URL, custom
    /// header or signing secret is ever copied into a queue message, and a channel disabled
    /// between enqueue and send is skipped.
    /// </para>
    /// </summary>
    public sealed class NotificationDispatchEnvelope
    {
        /// <summary>Schema version — bump on breaking envelope changes so consumers can reject or migrate.</summary>
        public string EnvelopeVersion { get; set; } = "1";

        public string TenantId { get; set; } = string.Empty;

        /// <summary>Session the alert is about; null for alerts without one (hardware rejection).</summary>
        public string? SessionId { get; set; }

        /// <summary>Ids of the channels the producer selected (already filtered by their NotifyOn* toggle).</summary>
        public List<string> ChannelIds { get; set; } = new();

        /// <summary>The fully built, channel-agnostic alert; renderers run in the consumer.</summary>
        public NotificationAlert Alert { get; set; } = default!;

        /// <summary>UTC time the producer enqueued the message; useful for measuring queue lag.</summary>
        public DateTime EnqueuedAt { get; set; }
    }
}

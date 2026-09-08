using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models.WhatsNew
{
    /// <summary>
    /// Platform-wide watermark of the What's new notifier: the channel-qualified entry keys that
    /// were present in <c>/whats-new.json</c> the last time it was processed. A run diffs the live
    /// payload against this set — keys not in it are "new" and get announced to every tenant
    /// channel that opted in. Stored as one row; written with an ETag compare-and-swap so two
    /// overlapping runs cannot both claim (and both announce) the same entries.
    /// </summary>
    public sealed class WhatsNewNotificationState
    {
        /// <summary>Entry keys (<c>{channel}:{id}</c>) of the last processed payload.</summary>
        public HashSet<string> KnownEntryKeys { get; set; } = new(StringComparer.Ordinal);

        /// <summary>Docs commit the last processed payload was generated from (diagnostics only).</summary>
        public string? LastDocsCommit { get; set; }

        /// <summary>When the state was last written.</summary>
        public DateTime? LastRunUtc { get; set; }

        /// <summary>When a digest was last dispatched to tenant channels.</summary>
        public DateTime? LastNotifiedUtc { get; set; }

        /// <summary>Number of entries announced in the last dispatched digest.</summary>
        public int LastNotifiedEntryCount { get; set; }
    }
}

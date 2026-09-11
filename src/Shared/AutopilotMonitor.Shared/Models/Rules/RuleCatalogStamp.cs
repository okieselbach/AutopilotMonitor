using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Which source last wrote a global rule catalog (<c>gather</c>, <c>analyze</c>, <c>ime</c>)
    /// and when. Written by every explicit reseed; read by the embedded self-seed that runs on
    /// each fresh backend instance.
    /// <para>
    /// The self-seed treats the deployed binary's embedded catalog as authoritative. That is only
    /// true while the binary is the newest source: a GitHub reseed that ran AFTER the binary was
    /// built carries newer content, and a self-seed that still applied the embedded catalog would
    /// resurrect deleted rows and revert changed ones. <see cref="EmbeddedSeedAllowed"/> is that
    /// comparison — the later explicit action wins.
    /// </para>
    /// </summary>
    public class RuleCatalogStamp
    {
        public const string KindGather = "gather";
        public const string KindAnalyze = "analyze";
        public const string KindIme = "ime";

        public const string SourceGitHub = "github";
        public const string SourceEmbedded = "embedded";

        public string Kind { get; set; } = default!;

        /// <summary><see cref="SourceGitHub"/> or <see cref="SourceEmbedded"/>.</summary>
        public string Source { get; set; } = default!;

        public DateTime StampedAt { get; set; }

        /// <summary>Rows written by that reseed.</summary>
        public int Count { get; set; }

        /// <summary>
        /// False only when a GitHub reseed is newer than the binary that wants to seed: then the
        /// embedded catalog is the older source and must not touch the table. No stamp, an
        /// embedded stamp, or a stamp older than the build all let the self-seed run.
        /// </summary>
        public static bool EmbeddedSeedAllowed(RuleCatalogStamp? stamp, DateTime buildUtc)
        {
            if (stamp == null) return true;
            if (!string.Equals(stamp.Source, SourceGitHub, StringComparison.OrdinalIgnoreCase)) return true;
            return stamp.StampedAt <= buildUtc;
        }
    }
}

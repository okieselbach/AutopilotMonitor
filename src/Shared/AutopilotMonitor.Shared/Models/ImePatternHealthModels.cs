using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// One row of the global <c>ImePatternStats</c> table: how often a shipped IME log pattern
    /// matched across the sessions that ran a given IME version (PartitionKey = IME version,
    /// RowKey = patternId). Fed by the agent's session-end <c>ime_pattern_hits</c> event.
    /// </summary>
    public class ImePatternStatsEntry
    {
        public string Version { get; set; } = default!;
        public string PatternId { get; set; } = default!;

        /// <summary>Sessions on this version that delivered a histogram (the shared denominator).</summary>
        public int Sessions { get; set; }

        /// <summary>Sessions in which the pattern matched at least once.</summary>
        public int SessionsWithHit { get; set; }

        /// <summary>Total matches over all sessions.</summary>
        public long Hits { get; set; }

        public DateTime? LastHitAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        /// <summary>Set once when drift was suspected for this version×pattern (one OpsEvent per cell).</summary>
        public DateTime? DriftFlaggedAt { get; set; }

        public double HitRate => Sessions > 0 ? (double)SessionsWithHit / Sessions : 0;
    }

    /// <summary>Response of <c>GET metrics/ime-pattern-health</c>.</summary>
    public class ImePatternHealthResponse
    {
        public string? BaselineVersion { get; set; }
        public int MinBaselineSessions { get; set; }
        public double ExpectedHitRate { get; set; }
        public int MinCandidateSessions { get; set; }
        public List<ImePatternHealthVersion> Versions { get; set; } = new();
        public List<ImePatternHealthPattern> Patterns { get; set; } = new();
        public List<ImePatternHealthCell> Cells { get; set; } = new();
        public List<ImePatternDriftAlert> Alerts { get; set; } = new();
        public DateTime GeneratedAt { get; set; }
    }

    public class ImePatternHealthVersion
    {
        public string Version { get; set; } = default!;
        /// <summary>Sessions that delivered a histogram on this version (not the ImeVersionHistory session count).</summary>
        public int Sessions { get; set; }
        public DateTime? FirstSeenAt { get; set; }
        public DateTime? LastSeenAt { get; set; }
        /// <summary>Total sessions per ImeVersionHistory (includes sessions without a terminal run).</summary>
        public int? FleetSessions { get; set; }
    }

    public class ImePatternHealthPattern
    {
        public string PatternId { get; set; } = default!;
        public string? Category { get; set; }
        public bool Enabled { get; set; }
        /// <summary>Hit rate on the baseline version; null without a baseline.</summary>
        public double? BaselineRate { get; set; }
        /// <summary>True when the baseline rate is at or above the expected threshold.</summary>
        public bool Expected { get; set; }
    }

    public class ImePatternHealthCell
    {
        public string Version { get; set; } = default!;
        public string PatternId { get; set; } = default!;
        public int Sessions { get; set; }
        public int SessionsWithHit { get; set; }
        public long Hits { get; set; }
        public double Rate { get; set; }
        public DateTime? DriftFlaggedAt { get; set; }
    }

    public class ImePatternDriftAlert
    {
        public string Version { get; set; } = default!;
        public string PatternId { get; set; } = default!;
        public string BaselineVersion { get; set; } = default!;
        public double BaselineRate { get; set; }
        public int Sessions { get; set; }
        public DateTime? FlaggedAt { get; set; }
    }
}

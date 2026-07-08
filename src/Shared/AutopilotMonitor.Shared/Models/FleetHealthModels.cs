using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Server-computed Fleet Health aggregates. Replaces the client-side pass that drained
    /// up to 200k raw sessions into the browser and aggregated on the main thread. Built once
    /// per request from the windowed session list — see <c>MetricsMath.BuildFleetHealthPayload</c>.
    /// Presentation-only derivations (bar-chart maxima, weekday/month axis labels) stay on the
    /// client; this payload carries the data, not the formatting.
    /// </summary>
    public sealed class FleetHealthMetrics
    {
        public bool Success { get; set; } = true;
        public int Days { get; set; }
        public FleetHealthStats Stats { get; set; } = new();
        /// <summary>One entry per day in the window, oldest-first. Date is UTC "yyyy-MM-dd".</summary>
        public List<FleetDailyPoint> DailyData { get; set; } = new();
        /// <summary>Top failure reasons by count (descending).</summary>
        public List<FleetFailureReason> FailureReasons { get; set; } = new();
        /// <summary>Device models by enrollment volume (descending).</summary>
        public List<FleetModelHealth> ModelHealth { get; set; } = new();
        /// <summary>Device models by average successful enrollment duration (descending).</summary>
        public List<FleetSlowModel> SlowestModels { get; set; } = new();
        /// <summary>Device models by failure count (descending).</summary>
        public List<FleetFailingModel> TopFailingModels { get; set; } = new();
        public DateTime ComputedAt { get; set; }
    }

    public sealed class FleetHealthStats
    {
        public int Total { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int InProgress { get; set; }
        /// <summary>Terminal, non-failure sessions (timeout reclassification). Surfaced as its own count; not a failure.</summary>
        public int Incomplete { get; set; }
        /// <summary>Succeeded / total * 100 (one decimal). Note: over all sessions, not just terminal.</summary>
        public double SuccessRate { get; set; }
        /// <summary>Average duration in minutes over non-in-progress sessions that carry a duration.</summary>
        public int AvgDurationMinutes { get; set; }
    }

    public sealed class FleetDailyPoint
    {
        public string Date { get; set; } = default!;
        public int Success { get; set; }
        public int Failed { get; set; }
    }

    public sealed class FleetFailureReason
    {
        public string Reason { get; set; } = default!;
        public int Count { get; set; }
    }

    public sealed class FleetModelHealth
    {
        public string Model { get; set; } = default!;
        public int Total { get; set; }
        public int Succeeded { get; set; }
    }

    public sealed class FleetSlowModel
    {
        public string Model { get; set; } = default!;
        public int AvgMinutes { get; set; }
        public int Count { get; set; }
    }

    public sealed class FleetFailingModel
    {
        public string Model { get; set; } = default!;
        public int Failed { get; set; }
        public int Total { get; set; }
        public int FailureRate { get; set; }
    }
}

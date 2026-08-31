using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    // Declaration order == wire order. Moved verbatim from the Functions-local
    // VerdictCalibrationResponse builder (GetVerdictCalibrationFunction.cs) so the shared
    // manifest exports the calibration matrix as wire types.

    /// <summary>
    /// Response of <c>GET global/metrics/verdict-calibration</c>: per verdict path, how many
    /// sessions it produced in the window, its share, overrides attributed to the prior path,
    /// the 7-day re-enrollment proxy, a 7d-vs-28d trend, and the active drift alerts.
    /// Operator-only classifier diagnostics.
    /// </summary>
    public class VerdictCalibrationResponse : IApiResponse
    {
        public bool Success { get; set; }
        /// <summary>Partition echo: a tenant GUID, or "global" for the cross-tenant aggregate.</summary>
        public string TenantId { get; set; } = string.Empty;
        public int WindowDays { get; set; }
        /// <summary>Inclusive window start ("yyyy-MM-dd").</summary>
        public string WindowStart { get; set; } = string.Empty;
        /// <summary>Inclusive window end ("yyyy-MM-dd", today).</summary>
        public string WindowEnd { get; set; } = string.Empty;
        /// <summary>Newest aggregate compute time in the window; absent when the window holds no rows.</summary>
        public DateTime? ComputedAt { get; set; }
        /// <summary>Distinct aggregation algorithm versions contributing to the window, ascending.</summary>
        public int[] Versions { get; set; } = default!;
        public VerdictCalibrationTotals Totals { get; set; } = default!;
        public VerdictCalibrationTrendMeta Trend { get; set; } = default!;
        /// <summary>Rows ordered by count descending, then path/status ordinal.</summary>
        public IReadOnlyList<VerdictCalibrationPathRow> Paths { get; set; } = default!;
        /// <summary>Active drift episodes, newest first.</summary>
        public IReadOnlyList<VerdictCalibrationAlert> Alerts { get; set; } = default!;
    }

    /// <summary>Window totals of the calibration matrix.</summary>
    public class VerdictCalibrationTotals
    {
        public int Sessions { get; set; }
        public int Terminal { get; set; }
        public int Derived { get; set; }
        /// <summary>Aggregate days that contributed to the window.</summary>
        public int Days { get; set; }
    }

    /// <summary>Trend denominators shared by every row (single source, never per-row copies).</summary>
    public class VerdictCalibrationTrendMeta
    {
        public int WindowDays { get; set; }
        public int BaselineDays { get; set; }
        public int WindowSessions { get; set; }
        public int BaselineSessions { get; set; }
    }

    /// <summary>One verdict path × status row of the calibration matrix.</summary>
    public class VerdictCalibrationPathRow
    {
        public string VerdictPath { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int Count { get; set; }
        public double SharePct { get; set; }
        public int DerivedCount { get; set; }
        public int Eligible7d { get; set; }
        public int ReEnrolled7d { get; set; }
        /// <summary>Null below the minimum eligible count — never a rate on a handful of sessions.</summary>
        public double? ReEnrollRatePct { get; set; }
        public int OverriddenByAdmin { get; set; }
        public int OverriddenByLateCompletion { get; set; }
        public int OverriddenOther { get; set; }
        public VerdictCalibrationTrendWindow Window7 { get; set; } = new();
        public VerdictCalibrationTrendWindow Baseline28 { get; set; } = new();
        /// <summary>Window share ÷ baseline share; null when the baseline share is 0 (a new path has no finite lift — never invented).</summary>
        public double? Lift { get; set; }
    }

    /// <summary>One trend window of a path row.</summary>
    public class VerdictCalibrationTrendWindow
    {
        public int Count { get; set; }
        public int Sessions { get; set; }
        public double SharePct { get; set; }
    }
}

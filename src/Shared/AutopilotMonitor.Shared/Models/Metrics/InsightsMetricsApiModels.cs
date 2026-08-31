using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    // Declaration order == wire order. Envelopes of the F1/F2 insights fleet endpoints,
    // lifted from the Functions-local anonymous builders so the manifest exports them.

    /// <summary>
    /// Response of <c>GET metrics/time-attribution</c> and <c>GET global/metrics/time-attribution</c>:
    /// the rolling 30-day range statistics per enrollment class plus the daily rows for the
    /// per-day trend. The range window is FIXED at the sweep's 30 days.
    /// </summary>
    public class TimeAttributionMetricsResponse : IApiResponse
    {
        public bool Success { get; set; }
        public int WindowDays { get; set; }
        /// <summary>Range statistics per enrollment class (never mixed), class-name ordinal order.</summary>
        public IReadOnlyList<TimeAttributionDailyAggregate> Classes { get; set; } = default!;
        /// <summary>Daily rows of the window, date ordinal order.</summary>
        public IReadOnlyList<TimeAttributionDailyAggregate> Daily { get; set; } = default!;
    }

    /// <summary>
    /// Response of <c>GET metrics/device-journeys</c> and <c>GET global/metrics/device-journeys</c>:
    /// daily First-Time-Right rows of the window plus their sums, the merged attempt histogram,
    /// and the repeat-devices violator list (absent on the cross-tenant aggregate).
    /// </summary>
    public class DeviceJourneyMetricsResponse : IApiResponse
    {
        public bool Success { get; set; }
        public int WindowDays { get; set; }
        public DeviceJourneyWindowTotals Totals { get; set; } = default!;
        /// <summary>Daily rows of the window, date ordinal order.</summary>
        public IReadOnlyList<DeviceJourneyDailyAggregate> Daily { get; set; } = default!;
        /// <summary>Devices whose current journey took at least 2 attempts; absent on the cross-tenant aggregate (no per-device drill there).</summary>
        public IReadOnlyList<DeviceJourneyRepeatDevice>? RepeatDevices { get; set; }
    }

    /// <summary>Window totals of the device-journey response (additive sums over the daily rows).</summary>
    public class DeviceJourneyWindowTotals
    {
        public int CompletedJourneys { get; set; }
        public int FirstTimeRight { get; set; }

        /// <summary>Null with zero completed journeys — no rate claim, never 0 (truthfulness rule 1).</summary>
        public double? FtrRatePct { get; set; }

        public int ExcludedSessions { get; set; }
        public List<DeviceJourneyAttemptBucket> AttemptHistogram { get; set; } = new();
    }

    /// <summary>One repeat-device violator row (current journey took at least 2 attempts, newest terminal session in the window).</summary>
    public class DeviceJourneyRepeatDevice
    {
        public string SerialNumber { get; set; } = string.Empty;
        public string Manufacturer { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public int Attempts { get; set; }
        public int JourneyCount { get; set; }
        public string LastStatus { get; set; } = string.Empty;
        public string LastSessionId { get; set; } = string.Empty;
        public DateTime LastStartedAt { get; set; }
        /// <summary>Failure reason of the newest failed attempt; empty when unavailable (fail-soft point-read).</summary>
        public string LastFailureReason { get; set; } = string.Empty;
    }
}

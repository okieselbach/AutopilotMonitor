using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    // Declaration order == wire order.

    /// <summary>
    /// Response of <c>GET global/metrics/growth</c>: the signups of the last N UTC days (today
    /// included), how far they got, and the activity of every tenant that exists today. A signup is a
    /// configuration row with a domain, dated by the row's insert time; offboarding deletes the row,
    /// so the figures are net. Operator-only.
    /// </summary>
    public class GrowthMetricsResponse : IApiResponse
    {
        public int WindowDays { get; set; }
        /// <summary>Inclusive window start ("yyyy-MM-dd", UTC).</summary>
        public string WindowStart { get; set; } = string.Empty;
        /// <summary>Inclusive window end ("yyyy-MM-dd", UTC, today).</summary>
        public string WindowEnd { get; set; } = string.Empty;
        /// <summary>IANA time zone of the hour and weekday counts.</summary>
        public string TimeZone { get; set; } = string.Empty;
        /// <summary>First day ("yyyy-MM-dd") whose signup times are exact; earlier rows carry their approval time and stay out of the hour and weekday counts.</summary>
        public string ExactSignupTimesFrom { get; set; } = string.Empty;
        /// <summary>Last day ("yyyy-MM-dd", yesterday) the daily usage rows cover; a first enrollment after it is pending.</summary>
        public string UsageDaysThrough { get; set; } = string.Empty;
        public GrowthSignups Signups { get; set; } = default!;
        /// <summary>One entry per Monday-anchored UTC signup week of the window, oldest first.</summary>
        public IReadOnlyList<GrowthCohortWeek> Cohorts { get; set; } = default!;
        public GrowthActivation Activation { get; set; } = default!;
        public GrowthTenantSegments AllTenants { get; set; } = default!;
        public GrowthPlans Plans { get; set; } = default!;
        /// <summary>One row per signup of the window, oldest first; only with includeTenants=true.</summary>
        public IReadOnlyList<GrowthTenantRow>? Tenants { get; set; }
    }

    /// <summary>Signups of the window.</summary>
    public class GrowthSignups
    {
        public int Total { get; set; }
        /// <summary>Signups of the window of the same length just before.</summary>
        public int PreviousWindowTotal { get; set; }
        /// <summary>"day" up to 90 days, "week" (Monday-anchored, clipped to the window) beyond.</summary>
        public string BucketSize { get; set; } = string.Empty;
        /// <summary>Oldest first, empty buckets included.</summary>
        public IReadOnlyList<GrowthBucket> Buckets { get; set; } = default!;
        /// <summary>Days with at least 3 signups and at least twice the median of the 14 days before, oldest first.</summary>
        public IReadOnlyList<GrowthPeakDay> PeakDays { get; set; } = default!;
        /// <summary>24 counts; the index is the hour of day in the response's time zone.</summary>
        public int[] ByHour { get; set; } = default!;
        /// <summary>7 counts, Monday first, in the response's time zone.</summary>
        public int[] ByWeekday { get; set; } = default!;
    }

    /// <summary>Signups of one day or week bucket.</summary>
    public class GrowthBucket
    {
        /// <summary>First UTC day of the bucket ("yyyy-MM-dd").</summary>
        public string Start { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    /// <summary>A day that stands out against the two weeks before it.</summary>
    public class GrowthPeakDay
    {
        /// <summary>UTC day ("yyyy-MM-dd").</summary>
        public string Day { get; set; } = string.Empty;
        public int Count { get; set; }
        /// <summary>Median signups per day of the 14 days before.</summary>
        public double BaselineMedian { get; set; }
    }

    /// <summary>The signups of one Monday-anchored UTC week and how far they got.</summary>
    public class GrowthCohortWeek
    {
        /// <summary>Monday of the week ("yyyy-MM-dd"); in the window's first week the window may start after it.</summary>
        public string WeekStart { get; set; } = string.Empty;
        public int Signups { get; set; }
        /// <summary>Registered at least one enrollment session.</summary>
        public int Enrolled { get; set; }
        /// <summary>At least one session reached Succeeded.</summary>
        public int Succeeded { get; set; }
        /// <summary>Had sessions in the last 7 days.</summary>
        public int ActiveLast7Days { get; set; }
        /// <summary>Median UTC days from signup to the first day with sessions; absent while no first day is known.</summary>
        public double? MedianDaysToFirstEnrollment { get; set; }
    }

    /// <summary>The window's signups by the UTC days from signup to their first day with sessions.</summary>
    public class GrowthActivation
    {
        public int Enrolled { get; set; }
        public int Succeeded { get; set; }
        public int NeverEnrolled { get; set; }
        public int FirstEnrollmentSameDay { get; set; }
        public int FirstEnrollmentNextDay { get; set; }
        public int FirstEnrollmentDays2To7 { get; set; }
        public int FirstEnrollmentLater { get; set; }
        /// <summary>Enrolled today for the first time; the daily usage row is written tomorrow.</summary>
        public int FirstEnrollmentPending { get; set; }
        /// <summary>Enrolled, but no daily usage row since signup (beyond the rows' 180 days).</summary>
        public int FirstEnrollmentUnknown { get; set; }
        /// <summary>Median UTC days to the first day with sessions; absent while no first day is known.</summary>
        public double? MedianDaysToFirstEnrollment { get; set; }
    }

    /// <summary>Every tenant that exists today, whenever it signed up.</summary>
    public class GrowthTenantSegments
    {
        public int Tenants { get; set; }
        /// <summary>No enrollment on record. The counters started on 2026-07-11 from the sessions kept then, so a tenant whose sessions had all aged out by that day counts here.</summary>
        public int NeverEnrolled { get; set; }
        public int Enrolled { get; set; }
        /// <summary>Enrolled, but no session reached Succeeded. Rows from before 2026-09-18 may lack the success counter.</summary>
        public int EnrolledWithoutSuccess { get; set; }
        /// <summary>Sessions in the last 7 days.</summary>
        public int Active7Days { get; set; }
        /// <summary>Sessions in the last 30 days.</summary>
        public int Active30Days { get; set; }
        /// <summary>Enrolled, but no sessions in the last 30 days.</summary>
        public int Dormant { get; set; }
    }

    /// <summary>Plan state of every tenant that exists today.</summary>
    public class GrowthPlans
    {
        /// <summary>Tenants per stored plan tier, most first.</summary>
        public IReadOnlyList<GrowthPlanCount> ByPlanTier { get; set; } = default!;
        public int TrialsStartedInWindow { get; set; }
        /// <summary>Trials whose expiry lies in the future.</summary>
        public int TrialsActive { get; set; }
        public int PayingCustomers { get; set; }
    }

    /// <summary>Tenants of one stored plan tier.</summary>
    public class GrowthPlanCount
    {
        public string PlanTier { get; set; } = string.Empty;
        public int Tenants { get; set; }
    }

    /// <summary>One signup of the window: ids and dates only, no domain.</summary>
    public class GrowthTenantRow
    {
        public string TenantId { get; set; } = string.Empty;
        public DateTime OnboardedAt { get; set; }
        public string PlanTier { get; set; } = string.Empty;
        public bool Enrolled { get; set; }
        public bool Succeeded { get; set; }
        /// <summary>First UTC day with sessions since signup ("yyyy-MM-dd"); absent while pending or unknown.</summary>
        public string? FirstEnrollmentDay { get; set; }
        /// <summary>Latest UTC day with sessions ("yyyy-MM-dd"; today when the counters moved today); absent without any.</summary>
        public string? LastActiveDay { get; set; }
    }
}

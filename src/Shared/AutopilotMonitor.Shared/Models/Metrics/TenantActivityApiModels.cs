using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Lifecycle stage of a signed-up tenant in the tenant activity figures. Every tenant has exactly one: the
    /// stages are tested in the order of <see cref="All"/> and the first that applies wins.
    /// </summary>
    public static class TenantActivityStages
    {
        /// <summary>No enrollment on record (the tenant's lifetime counter is zero).</summary>
        public const string NeverEnrolled = "neverEnrolled";

        /// <summary>Enrolled, but no activity in the last 30 days, today included — the dormant rule of the growth figures.</summary>
        public const string Dormant = "dormant";

        /// <summary>
        /// The first day with sessions lies in the window, or the first sessions came after the last daily row.
        /// Only decided for signups inside the 180 days the daily rows reach.
        /// </summary>
        public const string New = "new";

        /// <summary>At least 5 enrollments in the previous window and at most half as many in this one.</summary>
        public const string Declining = "declining";

        /// <summary>Enrollments in the window.</summary>
        public const string Active = "active";

        /// <summary>Enrolled and active in the last 30 days, but no enrollment in the window.</summary>
        public const string Quiet = "quiet";

        public static readonly string[] All = { NeverEnrolled, Dormant, New, Declining, Active, Quiet };
    }

    // Declaration order == wire order.

    /// <summary>
    /// Response of <c>GET global/metrics/tenant-activity</c>: how every signed-up tenant used the product in the
    /// last N full UTC days, against the N days before. Built from the daily usage rows (written once per day at
    /// the first maintenance run after midnight UTC), the per-tenant counters and the portal sign-in history.
    /// Operator-only (Global Admin and Global Reader).
    /// </summary>
    public class GetTenantActivityResponse : IApiResponse
    {
        public int WindowDays { get; set; }
        /// <summary>Inclusive window start ("yyyy-MM-dd", UTC).</summary>
        public string WindowStart { get; set; } = string.Empty;
        /// <summary>
        /// Inclusive window end ("yyyy-MM-dd", UTC): the last day the daily usage rows cover — yesterday, or the
        /// day before while yesterday is not aggregated yet. Today is never in the window.
        /// </summary>
        public string UsageDaysThrough { get; set; } = string.Empty;
        /// <summary>Inclusive start of the previous window of the same length ("yyyy-MM-dd").</summary>
        public string PreviousWindowStart { get; set; } = string.Empty;
        /// <summary>First day the daily usage rows reach ("yyyy-MM-dd", 180 days back); a first day with sessions before it is unknown.</summary>
        public string RowsFrom { get; set; } = string.Empty;
        /// <summary>False when the portal sign-in history could not be read; every portal figure is then absent.</summary>
        public bool PortalActivityAvailable { get; set; }
        public TenantActivityTotals Totals { get; set; } = default!;
        /// <summary>One entry per window day, oldest first, days without enrollments included.</summary>
        public IReadOnlyList<TenantActivityDailyItem> Daily { get; set; } = default!;
        /// <summary>Every signed-up tenant: most enrollments in the window first, then most enrollments since signup.</summary>
        public IReadOnlyList<TenantActivityTenantItem> Tenants { get; set; } = default!;
    }

    /// <summary>Platform totals of the window; the tenant rows add up to them.</summary>
    public class TenantActivityTotals
    {
        /// <summary>Configuration rows with a domain: the signups that exist today.</summary>
        public int SignedUpTenants { get; set; }
        /// <summary>Tenants with enrollments in the window.</summary>
        public int ActiveTenants { get; set; }
        /// <summary>Tenants with enrollments in the previous window.</summary>
        public int ActiveTenantsPrevious { get; set; }
        /// <summary>Change of the active tenants against the previous window, whole percent; absent when the previous window had none.</summary>
        public int? ActiveTenantsTrendPercent { get; set; }
        /// <summary>Enrollment sessions started in the window.</summary>
        public int Enrollments { get; set; }
        /// <summary>Enrollment sessions started in the previous window.</summary>
        public int EnrollmentsPrevious { get; set; }
        /// <summary>Change of the enrollments against the previous window, whole percent; absent when the previous window had none.</summary>
        public int? EnrollmentsTrendPercent { get; set; }
        /// <summary>Of the window's enrollments, Succeeded when their day was aggregated.</summary>
        public int Succeeded { get; set; }
        /// <summary>Of the window's enrollments, Failed when their day was aggregated.</summary>
        public int Failed { get; set; }
        /// <summary>Succeeded / (Succeeded + Failed), whole percent; absent without a finished session.</summary>
        public int? SuccessPercent { get; set; }
        /// <summary>Share of the window's enrollments that the 10 tenants with the most hold, whole percent; absent without enrollments.</summary>
        public int? Top10SharePercent { get; set; }
        /// <summary>Tenants per lifecycle stage; every signed-up tenant is in exactly one.</summary>
        public TenantActivityStageCounts Stages { get; set; } = default!;
        /// <summary>People with a portal visit in the window, summed over the tenants; absent without the sign-in history.</summary>
        public int? PortalUsers { get; set; }
        /// <summary>Tenants with at least one such person; absent without the sign-in history.</summary>
        public int? PortalTenants { get; set; }
    }

    /// <summary>Tenants per lifecycle stage (<see cref="TenantActivityStages"/>).</summary>
    public class TenantActivityStageCounts
    {
        public int NeverEnrolled { get; set; }
        public int Dormant { get; set; }
        public int New { get; set; }
        public int Declining { get; set; }
        public int Active { get; set; }
        public int Quiet { get; set; }
    }

    /// <summary>One UTC day of the window, summed over the signed-up tenants.</summary>
    public class TenantActivityDailyItem
    {
        /// <summary>UTC day ("yyyy-MM-dd").</summary>
        public string Day { get; set; } = string.Empty;
        public int Enrollments { get; set; }
        /// <summary>Succeeded when the day was aggregated.</summary>
        public int Succeeded { get; set; }
        /// <summary>Failed when the day was aggregated; the rest was still running or ended incomplete.</summary>
        public int Failed { get; set; }
        /// <summary>Tenants with enrollments that day.</summary>
        public int ActiveTenants { get; set; }
    }

    /// <summary>One signed-up tenant.</summary>
    public class TenantActivityTenantItem
    {
        public string TenantId { get; set; } = string.Empty;
        public string DomainName { get; set; } = string.Empty;
        public string? CompanyName { get; set; }
        /// <summary>Effective edition (<see cref="TenantEditionNames"/>).</summary>
        public string Edition { get; set; } = string.Empty;
        /// <summary>Lifecycle stage (<see cref="TenantActivityStages"/>).</summary>
        public string Stage { get; set; } = string.Empty;
        /// <summary>When the configuration row was created (the signup); absent on rows without the field.</summary>
        public DateTime? OnboardedAt { get; set; }
        /// <summary>Enrollment sessions started in the window.</summary>
        public int Enrollments { get; set; }
        /// <summary>Enrollment sessions started in the previous window.</summary>
        public int EnrollmentsPrevious { get; set; }
        /// <summary>Change against the previous window, whole percent; absent when the previous window had none.</summary>
        public int? TrendPercent { get; set; }
        /// <summary>Of the window's enrollments, Succeeded when their day was aggregated.</summary>
        public int Succeeded { get; set; }
        /// <summary>Of the window's enrollments, Failed when their day was aggregated.</summary>
        public int Failed { get; set; }
        /// <summary>Succeeded / (Succeeded + Failed), whole percent; absent without a finished session.</summary>
        public int? SuccessPercent { get; set; }
        /// <summary>Window days with enrollments.</summary>
        public int ActiveDays { get; set; }
        /// <summary>Enrollments per window day, oldest first; absent without enrollments in the window.</summary>
        public int[]? DailyEnrollments { get; set; }
        /// <summary>
        /// First UTC day with sessions since signup ("yyyy-MM-dd"); absent when unknown — a signup before
        /// <see cref="GetTenantActivityResponse.RowsFrom"/>, or first sessions that only the counters have seen yet.
        /// </summary>
        public string? FirstEnrollmentDay { get; set; }
        /// <summary>Latest UTC day with sessions ("yyyy-MM-dd"; a day after UsageDaysThrough when only the counters moved since); absent without any.</summary>
        public string? LastActiveDay { get; set; }
        /// <summary>
        /// When the tenant's counters last moved: an agent registration, a first success or an ingest batch — rarely
        /// a maintenance repair of the counter floors. Absent without counters.
        /// </summary>
        public DateTime? LastCounterUpdateAt { get; set; }
        /// <summary>Enrollment sessions since signup (retention-independent counter).</summary>
        public long LifetimeEnrollments { get; set; }
        /// <summary>Sessions since signup that reached Succeeded.</summary>
        public long LifetimeSucceeded { get; set; }
        /// <summary>Events stored since signup.</summary>
        public long LifetimeEvents { get; set; }
        /// <summary>People with a portal visit in the window (counted in the home tenant of their token); absent without the sign-in history.</summary>
        public int? PortalUsers { get; set; }
        /// <summary>Latest portal visit in the sign-in history (kept 90 days); absent without one or without the history.</summary>
        public DateTime? LastPortalVisitAt { get; set; }
    }
}

using AutopilotMonitor.Functions.Functions.Metrics;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Counting rules of the growth figures: window membership on the UTC day, activation from the
/// counters, the first enrollment from the daily usage rows (pending while only today's counters
/// moved), activity from the usage rows plus today's counters, hour and weekday in the requested
/// time zone, no approval-time rows in the hours or peaks, and excluded tenants in no figure.
/// </summary>
public class GrowthMetricsBuilderTests
{
    // Wednesday; Europe/Berlin switched from CEST to CET on Sunday 2026-10-25.
    private static readonly DateTime Now = new(2026, 10, 28, 15, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Berlin = ResolveBerlin();

    private static TimeZoneInfo ResolveBerlin()
    {
        Assert.True(GrowthMetricsBuilder.TryResolveTimeZone("Europe/Berlin", out var tz));
        return tz;
    }

    private static readonly string[] NoExclusions = Array.Empty<string>();

    private static string Tid(char c) => $"{new string(c, 8)}-0000-4000-8000-000000000000";

    private static TenantConfiguration Config(char id, DateTime? onboardedAt, string domain = "contoso.example", string plan = "free")
        => new() { TenantId = Tid(id), DomainName = domain, OnboardedAt = onboardedAt, PlanTier = plan };

    private static TenantStats Stats(char id, long total, long succeeded, DateTime lastUpdated)
        => new() { TenantId = Tid(id), TotalEnrollments = total, SuccessfulEnrollments = succeeded, LastUpdated = lastUpdated };

    private static TenantActivityDay Active(char id, int year, int month, int day)
        => new() { TenantId = Tid(id), Day = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc), SessionsTotal = 1 };

    private static DateTime Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static GrowthMetricsResponse Scenario(bool includeTenants = false, params char[] excluded)
    {
        var configs = new List<TenantConfiguration>
        {
            Config('a', Utc(10, 22, 9), plan: "pro"),       // enrolled the same day, still active this week
            Config('b', Utc(10, 23, 13, 30)),               // first sessions today: pending
            Config('c', Utc(10, 24, 7)),                    // never enrolled
            Config('d', Utc(10, 26, 8, 15)),                // enrolled the next day, trial started
            Config('e', Utc(9, 1, 12)),                     // older tenant, active 23 days ago
            Config('f', Utc(10, 16, 12)),                   // previous window, enrolled, silent for 30 days
            Config('g', Utc(10, 25, 12), domain: ""),       // read of an unknown tenant id, not a signup
            Config('h', Utc(10, 27, 23, 30)),               // 00:30 on Wednesday in Berlin; never enrolled
        };
        configs[0].PayingCustomer = true;
        configs[3].TrialStartedUtc = Utc(10, 26, 10);
        configs[3].TrialExpiresUtc = Utc(11, 25, 10);

        var stats = new List<TenantStats>
        {
            Stats('a', 3, 2, Utc(10, 27, 8)),
            Stats('b', 1, 0, Utc(10, 28, 10)),
            Stats('d', 2, 1, Utc(10, 27, 9)),
            Stats('e', 10, 9, Utc(10, 5, 9)),
            Stats('f', 5, 5, Utc(9, 1, 9)),
        };
        var activity = new List<TenantActivityDay>
        {
            Active('a', 2026, 10, 22),
            Active('d', 2026, 10, 27),
            Active('e', 2026, 10, 5),
        };

        return GrowthMetricsBuilder.Build(configs, stats, activity, Now, 7, Berlin, includeTenants, excluded.Select(Tid).ToList());
    }

    [Fact]
    public void Window_is_the_last_n_utc_days_including_today()
    {
        var r = Scenario();

        Assert.Equal("2026-10-22", r.WindowStart);
        Assert.Equal("2026-10-28", r.WindowEnd);
        Assert.Equal("2026-10-27", r.UsageDaysThrough);
        Assert.Equal("Europe/Berlin", r.TimeZone);
        Assert.Equal(0, r.ExcludedTenants);
        Assert.Equal(5, r.Signups.Total);
        Assert.Equal(1, r.Signups.PreviousWindowTotal);
        Assert.Equal("day", r.Signups.BucketSize);
        Assert.Equal(new[] { 1, 1, 1, 0, 1, 1, 0 }, r.Signups.Buckets.Select(b => b.Count));
        Assert.Equal("2026-10-22", r.Signups.Buckets[0].Start);
        Assert.Empty(r.Signups.PeakDays);
    }

    [Fact]
    public void Hours_and_weekdays_follow_the_time_zone_across_the_clock_change()
    {
        var r = Scenario();

        var expectedHours = new int[24];
        expectedHours[0] = 1;  // h: 23:30Z on 10-27 = 00:30 CET
        expectedHours[9] = 2;  // c: 07:00Z CEST, d: 08:15Z CET
        expectedHours[11] = 1; // a: 09:00Z CEST
        expectedHours[15] = 1; // b: 13:30Z CEST
        Assert.Equal(expectedHours, r.Signups.ByHour);
        // Monday first: d Mon, h Wed, a Thu, b Fri, c Sat.
        Assert.Equal(new[] { 1, 0, 1, 1, 1, 1, 0 }, r.Signups.ByWeekday);
    }

    [Fact]
    public void Activation_counts_the_first_day_with_sessions_and_holds_todays_first_enrollment_as_pending()
    {
        var a = Scenario().Activation;

        Assert.Equal(3, a.Enrolled);
        Assert.Equal(2, a.Succeeded);
        Assert.Equal(2, a.NeverEnrolled);
        Assert.Equal(1, a.FirstEnrollmentSameDay);
        Assert.Equal(1, a.FirstEnrollmentNextDay);
        Assert.Equal(0, a.FirstEnrollmentDays2To7);
        Assert.Equal(0, a.FirstEnrollmentLater);
        Assert.Equal(1, a.FirstEnrollmentPending);
        Assert.Equal(0, a.FirstEnrollmentUnknown);
        Assert.Equal(0.5, a.MedianDaysToFirstEnrollment);
    }

    [Fact]
    public void Cohorts_are_monday_anchored_utc_weeks()
    {
        var cohorts = Scenario().Cohorts;

        Assert.Equal(new[] { "2026-10-19", "2026-10-26" }, cohorts.Select(c => c.WeekStart));
        Assert.Equal(new[] { 3, 2 }, cohorts.Select(c => c.Signups));
        Assert.Equal(new[] { 2, 1 }, cohorts.Select(c => c.Enrolled));
        Assert.Equal(new[] { 1, 1 }, cohorts.Select(c => c.Succeeded));
        Assert.Equal(new[] { 2, 1 }, cohorts.Select(c => c.ActiveLast7Days));
        Assert.Equal(new double?[] { 0, 1 }, cohorts.Select(c => c.MedianDaysToFirstEnrollment));
    }

    [Fact]
    public void Segments_cover_every_tenant_with_a_domain()
    {
        var s = Scenario().AllTenants;

        Assert.Equal(7, s.Tenants);
        Assert.Equal(2, s.NeverEnrolled);
        Assert.Equal(5, s.Enrolled);
        Assert.Equal(1, s.EnrolledWithoutSuccess);
        Assert.Equal(3, s.Active7Days);  // a (10-22), b (counters today), d (10-27)
        Assert.Equal(4, s.Active30Days); // plus e (10-05)
        Assert.Equal(1, s.Dormant);      // f
    }

    [Fact]
    public void Plans_count_tiers_trials_and_paying_customers()
    {
        var p = Scenario().Plans;

        Assert.Equal(new[] { ("free", 6), ("pro", 1) }, p.ByPlanTier.Select(x => (x.PlanTier, x.Tenants)));
        Assert.Equal(1, p.TrialsStartedInWindow);
        Assert.Equal(1, p.TrialsActive);
        Assert.Equal(1, p.PayingCustomers);
    }

    [Fact]
    public void Tenant_rows_appear_only_on_request_in_signup_order()
    {
        Assert.Null(Scenario().Tenants);

        var rows = Scenario(includeTenants: true).Tenants!;
        Assert.Equal(new[] { Tid('a'), Tid('b'), Tid('c'), Tid('d'), Tid('h') }, rows.Select(r => r.TenantId));
        var b = rows[1];
        Assert.True(b.Enrolled);
        Assert.Null(b.FirstEnrollmentDay);
        Assert.Equal("2026-10-28", b.LastActiveDay);
        var c = rows[2];
        Assert.False(c.Enrolled);
        Assert.Null(c.LastActiveDay);
        Assert.Equal("pro", rows[0].PlanTier);
        Assert.Equal("2026-10-22", rows[0].FirstEnrollmentDay);
    }

    [Fact]
    public void Excluded_tenants_leave_every_figure_and_only_signed_up_rows_count_as_excluded()
    {
        // a: in-window pro signup, paying, enrolled the same day; e: older tenant active 23 days ago;
        // g: a row without a domain (never a tenant); z: no configuration row at all.
        var r = Scenario(true, 'a', 'e', 'g', 'z');

        Assert.Equal(2, r.ExcludedTenants);
        Assert.Equal(4, r.Signups.Total);
        Assert.Equal(new[] { 0, 1, 1, 0, 1, 1, 0 }, r.Signups.Buckets.Select(b => b.Count));
        Assert.Equal(0, r.Signups.ByHour[11]);
        Assert.Equal(2, r.Activation.Enrolled);
        Assert.Equal(0, r.Activation.FirstEnrollmentSameDay);
        Assert.Equal(1, r.Activation.MedianDaysToFirstEnrollment);
        Assert.Equal(new[] { 2, 2 }, r.Cohorts.Select(c => c.Signups)); // b, c | d, h
        Assert.Equal(5, r.AllTenants.Tenants);
        Assert.Equal(2, r.AllTenants.Active7Days);
        Assert.Equal(2, r.AllTenants.Active30Days);
        Assert.Equal(new[] { ("free", 5) }, r.Plans.ByPlanTier.Select(x => (x.PlanTier, x.Tenants)));
        Assert.Equal(0, r.Plans.PayingCustomers);
        Assert.Equal(new[] { Tid('b'), Tid('c'), Tid('d'), Tid('h') }, r.Tenants!.Select(t => t.TenantId));
    }

    [Theory]
    [InlineData(null, true, "")]
    [InlineData("", true, "")]
    [InlineData(" {AAAAAAAA-0000-4000-8000-000000000000} ,,bbbbbbbb-0000-4000-8000-000000000000", true,
        "aaaaaaaa-0000-4000-8000-000000000000,bbbbbbbb-0000-4000-8000-000000000000")]
    [InlineData("aaaaaaaa-0000-4000-8000-000000000000,contoso.example", false, "")]
    public void Excluded_tenants_are_tenant_ids_in_the_lowercase_d_form(string? raw, bool ok, string expected)
    {
        Assert.Equal(ok, GrowthMetricsBuilder.TryParseExcludedTenants(raw, out var ids));
        if (ok) Assert.Equal(expected, string.Join(",", ids));
    }

    [Fact]
    public void At_most_twenty_tenants_can_be_excluded()
    {
        static string Ids(int n) => string.Join(",", Enumerable.Range(0, n).Select(i => $"{i:x8}-0000-4000-8000-000000000000"));

        Assert.True(GrowthMetricsBuilder.TryParseExcludedTenants(Ids(GrowthMetricsBuilder.MaxExcludedTenants), out _));
        Assert.False(GrowthMetricsBuilder.TryParseExcludedTenants(Ids(GrowthMetricsBuilder.MaxExcludedTenants + 1), out _));
    }

    [Fact]
    public void A_first_enrollment_without_a_usage_row_and_without_activity_today_is_unknown()
    {
        var configs = new[] { Config('a', Utc(10, 20, 9)) };
        var stats = new[] { Stats('a', 4, 4, Utc(10, 27, 8)) };

        var a = GrowthMetricsBuilder.Build(configs, stats, Array.Empty<TenantActivityDay>(), Now, 30, TimeZoneInfo.Utc, false, NoExclusions).Activation;

        Assert.Equal(1, a.FirstEnrollmentUnknown);
        Assert.Equal(0, a.FirstEnrollmentPending);
        Assert.Null(a.MedianDaysToFirstEnrollment);
    }

    [Fact]
    public void A_peak_needs_three_signups_and_twice_the_median_of_the_two_weeks_before()
    {
        var configs = new List<TenantConfiguration>();
        var next = 'a';
        void Signups(int month, int day, int count)
        {
            for (var i = 0; i < count; i++) configs.Add(Config(next++, Utc(month, day, 10, i)));
        }
        // One signup a day from 10-01 to 10-14 sets the baseline median to 1.
        for (var day = 1; day <= 14; day++) Signups(10, day, 1);
        Signups(10, 15, 3);   // 3 >= 2 × 1: peak
        Signups(10, 16, 1);

        var r = GrowthMetricsBuilder.Build(configs, Array.Empty<TenantStats>(), Array.Empty<TenantActivityDay>(),
            new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc), 7, TimeZoneInfo.Utc, false, NoExclusions);

        var peak = Assert.Single(r.Signups.PeakDays);
        Assert.Equal("2026-10-15", peak.Day);
        Assert.Equal(3, peak.Count);
        Assert.Equal(1, peak.BaselineMedian);
    }

    [Fact]
    public void Approval_time_rows_stay_out_of_the_hours_and_peaks_but_count_as_signups()
    {
        // Before 2026-03-18 OnboardedAt is the approval time: four rows within milliseconds.
        var configs = Enumerable.Range(0, 4)
            .Select(i => Config((char)('a' + i), new DateTime(2026, 2, 24, 7, 46, 30, i * 10, DateTimeKind.Utc)))
            .Append(Config('k', Utc(10, 20, 14)))
            .ToList();

        var r = GrowthMetricsBuilder.Build(configs, Array.Empty<TenantStats>(), Array.Empty<TenantActivityDay>(),
            Now, 365, TimeZoneInfo.Utc, false, NoExclusions);

        Assert.Equal(5, r.Signups.Total);
        Assert.Equal(1, r.Signups.ByHour.Sum());
        Assert.Equal(1, r.Signups.ByHour[14]);
        Assert.Empty(r.Signups.PeakDays);
        Assert.Equal("2026-03-18", r.ExactSignupTimesFrom);
    }

    [Fact]
    public void Windows_beyond_90_days_bucket_by_monday_anchored_week_clipped_to_the_window()
    {
        var configs = new[] { Config('a', Utc(10, 20, 9)), Config('b', Utc(8, 3, 9)) };

        var r = GrowthMetricsBuilder.Build(configs, Array.Empty<TenantStats>(), Array.Empty<TenantActivityDay>(),
            Now, 120, TimeZoneInfo.Utc, false, NoExclusions);

        Assert.Equal("week", r.Signups.BucketSize);
        Assert.Equal(r.WindowStart, r.Signups.Buckets[0].Start);
        Assert.All(r.Signups.Buckets.Skip(1), b => Assert.Equal(DayOfWeek.Monday, DateTime.Parse(b.Start).DayOfWeek));
        Assert.Equal(2, r.Signups.Buckets.Sum(b => b.Count));
    }

    [Fact]
    public void Usage_rows_are_read_for_the_window_or_at_least_the_30_day_look_back()
    {
        var today = Now.Date;
        Assert.Equal(today.AddDays(-29), GrowthMetricsBuilder.UsageReadStart(today, 7));
        Assert.Equal(today.AddDays(-89), GrowthMetricsBuilder.UsageReadStart(today, 90));
    }

    [Theory]
    [InlineData(null, true, "UTC")]
    [InlineData("", true, "UTC")]
    [InlineData("Europe/Berlin", true, "Europe/Berlin")]
    [InlineData("America/Argentina/Buenos_Aires", true, "America/Argentina/Buenos_Aires")]
    [InlineData("../../etc/passwd", false, null)]
    [InlineData("/etc/localtime", false, null)]
    [InlineData("Europe/Nowhere", false, null)]
    public void Time_zones_are_iana_ids_the_host_knows(string? id, bool ok, string? expectedId)
    {
        Assert.Equal(ok, GrowthMetricsBuilder.TryResolveTimeZone(id, out var tz));
        if (ok) Assert.Equal(expectedId, tz.Id);
    }
}

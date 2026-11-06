using AutopilotMonitor.Functions.Functions.Metrics;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Counting rules of the tenant activity (GA → Metrics → Tenant Activity, MCP get_tenant_activity): the window is
/// the N full UTC days through the last aggregated day, every signup has exactly one lifecycle stage, dormant and
/// never-enrolled agree with the growth figures, the totals add up from the tenant rows, and the portal figures
/// count people inside the window only.
/// </summary>
public class TenantActivityBuilderTests
{
    // Friday; yesterday (2026-11-05) is aggregated unless a test says otherwise.
    private static readonly DateTime Now = new(2026, 11, 6, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Today = Now.Date;

    private static string Tid(char c) => $"{new string(c, 8)}-0000-4000-8000-000000000000";

    private static DateTime DaysAgo(int days, int hour = 0) => Today.AddDays(-days).AddHours(hour);

    private static TenantConfiguration Config(char id, DateTime? onboardedAt, string domain = "contoso.example")
        => new() { TenantId = Tid(id), DomainName = domain, OnboardedAt = onboardedAt };

    private static TenantStats Stats(char id, long total, DateTime lastUpdated, long succeeded = 0, long events = 0)
        => new() { TenantId = Tid(id), TotalEnrollments = total, SuccessfulEnrollments = succeeded, TotalEventsProcessed = events, LastUpdated = lastUpdated };

    private static TenantActivityDay Row(char id, int daysAgo, int total, int succeeded = 0, int failed = 0)
        => new() { TenantId = Tid(id), Day = DaysAgo(daysAgo), SessionsTotal = total, SessionsSucceeded = succeeded, SessionsFailed = failed };

    private static PortalVisit Visit(char id, string person, DateTime at)
        => new() { TenantId = Tid(id), PersonKey = person, At = at };

    private static GetTenantActivityResponse Build(
        IReadOnlyList<TenantConfiguration> configs,
        IReadOnlyList<TenantStats> stats,
        IReadOnlyList<TenantActivityDay> rows,
        int days = 30,
        IReadOnlyList<PortalVisit>? visits = null,
        bool portalAvailable = true,
        bool yesterdayAggregated = true)
        => TenantActivityBuilder.Build(
            configs, stats, rows, portalAvailable ? visits ?? Array.Empty<PortalVisit>() : null, Now, days, yesterdayAggregated);

    private static TenantActivityTenantItem Tenant(GetTenantActivityResponse response, char id)
        => Assert.Single(response.Tenants, t => t.TenantId == Tid(id));

    // ---------- window ----------

    [Fact]
    public void Window_is_the_n_full_days_through_yesterday_and_the_previous_window_before_it()
    {
        var response = Build(
            new[] { Config('a', DaysAgo(100)) },
            new[] { Stats('a', 7, DaysAgo(1, 9)) },
            new[] { Row('a', 1, 2), Row('a', 30, 1), Row('a', 31, 4) });

        Assert.Equal("2026-10-07", response.WindowStart);
        Assert.Equal("2026-11-05", response.UsageDaysThrough);
        Assert.Equal("2026-09-07", response.PreviousWindowStart);
        Assert.Equal("2026-05-10", response.RowsFrom);
        var tenant = Tenant(response, 'a');
        Assert.Equal(3, tenant.Enrollments);
        Assert.Equal(4, tenant.EnrollmentsPrevious);
        Assert.Equal(30, response.Daily.Count);
        Assert.Equal(("2026-10-07", 1), (response.Daily[0].Day, response.Daily[0].Enrollments));
        Assert.Equal(("2026-11-05", 2), (response.Daily[^1].Day, response.Daily[^1].Enrollments));
    }

    [Theory]
    [InlineData(1, "2026-11-05")]
    [InlineData(7, "2026-10-30")]
    [InlineData(30, "2026-10-07")]
    [InlineData(90, "2026-08-08")]
    public void The_window_holds_exactly_n_days_ending_yesterday(int days, string start)
    {
        var response = Build(Array.Empty<TenantConfiguration>(), Array.Empty<TenantStats>(), Array.Empty<TenantActivityDay>(), days);

        Assert.Equal(start, response.WindowStart);
        Assert.Equal(days, response.Daily.Count);
        Assert.Equal(start, response.Daily[0].Day);
        Assert.Equal("2026-11-05", response.Daily[^1].Day);
    }

    [Fact]
    public void Before_yesterday_is_aggregated_the_window_ends_the_day_before_and_yesterdays_counters_keep_a_tenant_active()
    {
        // Sessions only yesterday: the counters moved, the daily row is not written yet.
        var configs = new[] { Config('b', DaysAgo(120)) };
        var stats = new[] { Stats('b', 3, DaysAgo(1, 22)) };
        var rows = new[] { Row('b', 40, 1) };

        var early = Build(configs, stats, rows, yesterdayAggregated: false);

        Assert.Equal("2026-11-04", early.UsageDaysThrough);
        Assert.Equal("2026-10-06", early.WindowStart);
        var tenant = Tenant(early, 'b');
        Assert.Equal("2026-11-05", tenant.LastActiveDay);
        Assert.Equal(TenantActivityStages.Quiet, tenant.Stage);

        // Once aggregated, a day without a row had no sessions: the counter move alone keeps nobody active.
        Assert.Equal(TenantActivityStages.Dormant, Tenant(Build(configs, stats, rows), 'b').Stage);
    }

    // ---------- stages ----------

    [Fact]
    public void Every_signup_has_exactly_one_stage_in_order()
    {
        var configs = new[]
        {
            Config('n', DaysAgo(10)),                  // never enrolled
            Config('d', DaysAgo(120)),                 // dormant: last sessions 40 days ago
            Config('w', DaysAgo(20)),                  // new: first sessions 10 days ago
            Config('p', DaysAgo(2)),                   // new: first sessions today, only the counters know
            Config('c', DaysAgo(150)),                 // declining: 10 before, 4 now
            Config('a', DaysAgo(300)),                 // active, signed up before the rows' horizon
            Config('q', DaysAgo(100)),                 // quiet: only today's counters moved in 30 days
            Config('g', DaysAgo(5), domain: ""),       // a read of an unknown id, not a signup
        };
        var stats = new[]
        {
            Stats('d', 5, DaysAgo(40, 9)),
            Stats('w', 2, DaysAgo(10, 9)),
            Stats('p', 1, Today.AddHours(8)),
            Stats('c', 30, DaysAgo(5, 9)),
            Stats('a', 100, DaysAgo(1, 9)),
            Stats('q', 4, Today.AddHours(7)),
            Stats('g', 9, DaysAgo(1, 9)),
        };
        var rows = new[]
        {
            Row('d', 40, 2),
            Row('w', 10, 2),
            Row('c', 40, 10), Row('c', 5, 4),
            Row('a', 35, 6), Row('a', 3, 6),
            Row('q', 50, 1),
            Row('g', 2, 9),
        };

        var response = Build(configs, stats, rows);

        Assert.Equal(TenantActivityStages.NeverEnrolled, Tenant(response, 'n').Stage);
        Assert.Equal(TenantActivityStages.Dormant, Tenant(response, 'd').Stage);
        Assert.Equal(TenantActivityStages.New, Tenant(response, 'w').Stage);
        Assert.Equal(TenantActivityStages.New, Tenant(response, 'p').Stage);
        Assert.Equal(TenantActivityStages.Declining, Tenant(response, 'c').Stage);
        Assert.Equal(TenantActivityStages.Active, Tenant(response, 'a').Stage);
        Assert.Equal(TenantActivityStages.Quiet, Tenant(response, 'q').Stage);
        Assert.DoesNotContain(response.Tenants, t => t.TenantId == Tid('g'));

        var pending = Tenant(response, 'p');
        Assert.Null(pending.FirstEnrollmentDay);
        Assert.Equal("2026-11-06", pending.LastActiveDay);
        Assert.Equal("2026-10-27", Tenant(response, 'w').FirstEnrollmentDay);
        Assert.Null(Tenant(response, 'a').FirstEnrollmentDay);     // signup before the rows' first day: unknown

        var stages = response.Totals.Stages;
        Assert.Equal((1, 1, 2, 1, 1, 1), (stages.NeverEnrolled, stages.Dormant, stages.New, stages.Declining, stages.Active, stages.Quiet));
        Assert.Equal(response.Totals.SignedUpTenants,
            stages.NeverEnrolled + stages.Dormant + stages.New + stages.Declining + stages.Active + stages.Quiet);
        Assert.Equal(7, response.Totals.SignedUpTenants);
    }

    [Fact]
    public void Dormant_wins_over_declining_and_a_short_window_leaves_recent_tenants_quiet()
    {
        var configs = new[] { Config('c', DaysAgo(150)), Config('q', DaysAgo(150)) };
        var stats = new[] { Stats('c', 20, DaysAgo(40, 9)), Stats('q', 20, DaysAgo(10, 9)) };
        var rows = new[] { Row('c', 40, 10), Row('q', 120, 5), Row('q', 10, 3) };

        Assert.Equal(TenantActivityStages.Dormant, Tenant(Build(configs, stats, rows), 'c').Stage);
        Assert.Equal(TenantActivityStages.Quiet, Tenant(Build(configs, stats, rows, days: 7), 'q').Stage);
    }

    [Fact]
    public void A_return_after_a_pause_is_not_new_for_a_signup_before_the_rows_horizon()
    {
        var response = Build(
            new[] { Config('a', DaysAgo(200)) },
            new[] { Stats('a', 40, DaysAgo(2, 9)) },
            new[] { Row('a', 2, 3) });

        var tenant = Tenant(response, 'a');
        Assert.Equal(TenantActivityStages.Active, tenant.Stage);
        Assert.Null(tenant.FirstEnrollmentDay);
    }

    [Fact]
    public void Dormant_starts_at_30_days_without_activity_and_matches_the_growth_figures()
    {
        var configs = new[] { Config('x', DaysAgo(100)), Config('y', DaysAgo(100)), Config('n', DaysAgo(3)) };
        var stats = new[] { Stats('x', 2, DaysAgo(29, 9)), Stats('y', 2, DaysAgo(30, 9)) };
        var rows = new[] { Row('x', 29, 2), Row('y', 30, 2) };

        var activity = Build(configs, stats, rows);
        var growth = GrowthMetricsBuilder.Build(configs, stats, rows, Now, 30, TimeZoneInfo.Utc, false, Array.Empty<string>());

        Assert.NotEqual(TenantActivityStages.Dormant, Tenant(activity, 'x').Stage);
        Assert.Equal(TenantActivityStages.Dormant, Tenant(activity, 'y').Stage);
        Assert.Equal(growth.AllTenants.Dormant, activity.Totals.Stages.Dormant);
        Assert.Equal(growth.AllTenants.NeverEnrolled, activity.Totals.Stages.NeverEnrolled);
        Assert.Equal(1, activity.Totals.Stages.Dormant);
    }

    // ---------- figures ----------

    [Fact]
    public void Tenant_figures_and_totals_add_up_from_the_window_rows()
    {
        var configs = new[] { Config('a', DaysAgo(100)), Config('b', DaysAgo(100)), Config('c', DaysAgo(100)) };
        var stats = new[]
        {
            Stats('a', 50, DaysAgo(1, 9), succeeded: 40, events: 9_000),
            Stats('b', 8, DaysAgo(3, 9)),
            Stats('c', 4, DaysAgo(45, 9)),
        };
        var rows = new[]
        {
            Row('a', 1, 4, succeeded: 3, failed: 1), Row('a', 2, 2, succeeded: 2),
            Row('a', 40, 4),
            Row('b', 3, 1),
            Row('c', 45, 4),
        };

        var response = Build(configs, stats, rows);

        var a = Tenant(response, 'a');
        Assert.Equal((6, 4, 5, 1, 2), (a.Enrollments, a.EnrollmentsPrevious, a.Succeeded, a.Failed, a.ActiveDays));
        Assert.Equal(50, a.TrendPercent);
        Assert.Equal(83, a.SuccessPercent);
        Assert.Equal((50L, 40L, 9_000L), (a.LifetimeEnrollments, a.LifetimeSucceeded, a.LifetimeEvents));
        Assert.Equal(30, a.DailyEnrollments!.Length);
        Assert.Equal(4, a.DailyEnrollments[^1]);
        Assert.Equal(2, a.DailyEnrollments[^2]);

        var b = Tenant(response, 'b');
        Assert.Null(b.TrendPercent);          // nothing in the previous window
        Assert.Null(b.SuccessPercent);        // no finished session
        var c = Tenant(response, 'c');
        Assert.Equal(-100, c.TrendPercent);
        Assert.Null(c.DailyEnrollments);

        var totals = response.Totals;
        Assert.Equal((2, 2, 7, 8), (totals.ActiveTenants, totals.ActiveTenantsPrevious, totals.Enrollments, totals.EnrollmentsPrevious));
        Assert.Equal(0, totals.ActiveTenantsTrendPercent);
        Assert.Equal(-13, totals.EnrollmentsTrendPercent);
        Assert.Equal((5, 1, (int?)83), (totals.Succeeded, totals.Failed, totals.SuccessPercent));
        Assert.Equal(100, totals.Top10SharePercent);
        Assert.Equal(totals.Enrollments, response.Daily.Sum(d => d.Enrollments));
        Assert.Equal(1, response.Daily[^1].ActiveTenants);
        Assert.Equal(2, response.Daily[^3].ActiveTenants + response.Daily[^1].ActiveTenants);
    }

    [Fact]
    public void Tenants_come_by_window_enrollments_then_lifetime_and_the_top_10_share_needs_enrollments()
    {
        var configs = new[] { Config('a', DaysAgo(50)), Config('b', DaysAgo(50)), Config('c', DaysAgo(50)) };
        var stats = new[] { Stats('a', 1, DaysAgo(45, 9)), Stats('b', 9, DaysAgo(45, 9)), Stats('c', 3, DaysAgo(45, 9)) };

        var response = Build(configs, stats, new[] { Row('a', 45, 1), Row('b', 45, 9), Row('c', 45, 3) });

        Assert.Equal(new[] { Tid('b'), Tid('c'), Tid('a') }, response.Tenants.Select(t => t.TenantId));
        Assert.Null(response.Totals.Top10SharePercent);
        Assert.Equal(-100, response.Totals.EnrollmentsTrendPercent);
    }

    [Fact]
    public void Tenant_ids_join_case_insensitively()
    {
        var config = new TenantConfiguration { TenantId = Tid('a').ToUpperInvariant(), DomainName = "contoso.example", OnboardedAt = DaysAgo(60) };
        var response = Build(new[] { config }, new[] { Stats('a', 5, DaysAgo(1, 9)) }, new[] { Row('a', 1, 5) },
            visits: new[] { Visit('a', "oid-1", DaysAgo(2, 10)) });

        var tenant = Assert.Single(response.Tenants);
        Assert.Equal(5, tenant.Enrollments);
        Assert.Equal(5, tenant.LifetimeEnrollments);
        Assert.Equal(1, tenant.PortalUsers);
    }

    // ---------- portal ----------

    [Fact]
    public void Portal_users_count_people_inside_the_window_and_the_last_visit_takes_the_whole_history()
    {
        var visits = new[]
        {
            Visit('a', "oid-1", DaysAgo(30)),            // the window's first day, midnight
            Visit('a', "oid-1", DaysAgo(3, 8)),          // the same person again
            Visit('a', "oid-2", DaysAgo(31, 23)),        // the day before the window
            Visit('a', "oid-3", Today.AddHours(9)),      // today, after the window
            Visit('b', "oid-9", DaysAgo(60, 12)),        // never enrolled, came back two months ago
        };

        var response = Build(
            new[] { Config('a', DaysAgo(100)), Config('b', DaysAgo(80)) },
            new[] { Stats('a', 3, DaysAgo(1, 9)) },
            new[] { Row('a', 1, 3) },
            visits: visits);

        Assert.True(response.PortalActivityAvailable);
        var a = Tenant(response, 'a');
        Assert.Equal(1, a.PortalUsers);
        Assert.Equal(Today.AddHours(9), a.LastPortalVisitAt);
        var b = Tenant(response, 'b');
        Assert.Equal(0, b.PortalUsers);
        Assert.Equal(DaysAgo(60, 12), b.LastPortalVisitAt);
        Assert.Equal(((int?)1, (int?)1), (response.Totals.PortalUsers, response.Totals.PortalTenants));
    }

    [Fact]
    public void Without_the_sign_in_history_the_portal_figures_are_absent_not_zero()
    {
        var response = Build(new[] { Config('a', DaysAgo(100)) }, new[] { Stats('a', 3, DaysAgo(1, 9)) }, new[] { Row('a', 1, 3) },
            portalAvailable: false);

        Assert.False(response.PortalActivityAvailable);
        Assert.Null(response.Totals.PortalUsers);
        Assert.Null(response.Totals.PortalTenants);
        var tenant = Assert.Single(response.Tenants);
        Assert.Null(tenant.PortalUsers);
        Assert.Null(tenant.LastPortalVisitAt);
        Assert.Equal(3, tenant.Enrollments);
    }

    // ---------- identity ----------

    [Fact]
    public void Edition_follows_msp_then_plan_then_a_running_trial()
    {
        var msp = Config('m', DaysAgo(10));
        msp.ManagedByProTenantId = Tid('z');
        msp.PlanTier = "pro";
        var pro = Config('p', DaysAgo(10));
        pro.PlanTier = "pro";
        var trial = Config('t', DaysAgo(10));
        trial.TrialExpiresUtc = Now.AddDays(5);
        var expired = Config('e', DaysAgo(10));
        expired.TrialExpiresUtc = Now.AddDays(-1);
        var community = Config('c', DaysAgo(10));

        var response = Build(new[] { msp, pro, trial, expired, community }, Array.Empty<TenantStats>(), Array.Empty<TenantActivityDay>());

        Assert.Equal(TenantEditionNames.ProMsp, Tenant(response, 'm').Edition);
        Assert.Equal(TenantEditionNames.Pro, Tenant(response, 'p').Edition);
        Assert.Equal(TenantEditionNames.ProTrial, Tenant(response, 't').Edition);
        Assert.Equal(TenantEditionNames.Community, Tenant(response, 'e').Edition);
        Assert.Equal(TenantEditionNames.Community, Tenant(response, 'c').Edition);
    }

    [Fact]
    public void A_tenant_row_carries_its_names_signup_and_counter_time()
    {
        var config = Config('a', DaysAgo(30, 10), domain: "fabrikam.example");
        config.CompanyName = "Fabrikam";
        var blank = Config('b', DaysAgo(30, 10));
        blank.CompanyName = " ";

        var response = Build(new[] { config, blank }, new[] { Stats('a', 1, DaysAgo(2, 11)) }, new[] { Row('a', 2, 1) });

        var a = Tenant(response, 'a');
        Assert.Equal(("fabrikam.example", "Fabrikam"), (a.DomainName, a.CompanyName));
        Assert.Equal(DaysAgo(30, 10), a.OnboardedAt);
        Assert.Equal(DaysAgo(2, 11), a.LastCounterUpdateAt);
        var b = Tenant(response, 'b');
        Assert.Null(b.CompanyName);
        Assert.Null(b.LastCounterUpdateAt);
    }
}

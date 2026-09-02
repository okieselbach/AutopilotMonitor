using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Wire-identity proof for the Metrics function folder (anonymous-object → typed-DTO
/// migration). Each fact serializes the OLD anonymous literal exactly as it stood at the
/// call site (copied from the pre-migration code, filled with realistic sample values)
/// against the NEW DTO carrying the same values, via
/// <see cref="ApiResponseWireParityTests.AssertWireIdentical"/> — key names, key order and
/// key presence/absence (WhenWritingNull) must match ordinally. Nullable slots additionally
/// get a null case proving the key vanishes identically on both sides.
/// </summary>
public class MetricsWireParityTests
{
    // ---- GetDeviceHistory ----------------------------------------------------------------

    [Fact]
    public void GetDeviceHistoryResponse_matches_the_history_shape()
    {
        DeviceHistory? history = new DeviceHistory
        {
            TenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d",
            SerialKey = "sn-0042",
            SerialNumber = "SN-0042",
            Manufacturer = "Fabrikam Inc.",
            Model = "Latitude 9999",
            Chain = new List<DeviceSessionRef>
            {
                new DeviceSessionRef
                {
                    SessionId = "0b6f7a37-1111-4d61-9c93-0aa111111111",
                    StartedAt = new DateTime(2026, 8, 28, 9, 0, 0, DateTimeKind.Utc),
                    CompletedAt = new DateTime(2026, 8, 28, 10, 5, 0, DateTimeKind.Utc),
                    Status = "Failed",
                    EnrollmentType = "v1",
                    IsPreProvisioned = false,
                    DurationSeconds = 3900,
                    AdminMarked = false,
                },
                new DeviceSessionRef
                {
                    SessionId = "0b6f7a37-2222-4d61-9c93-0aa222222222",
                    StartedAt = new DateTime(2026, 8, 28, 11, 0, 0, DateTimeKind.Utc),
                    CompletedAt = new DateTime(2026, 8, 28, 11, 50, 0, DateTimeKind.Utc),
                    Status = "Succeeded",
                    EnrollmentType = "v1",
                    IsPreProvisioned = false,
                    DurationSeconds = 3000,
                    AdminMarked = false,
                },
            },
            CurrentJourneyAttempts = 2,
            JourneyCount = 1,
            JourneyVersion = 1,
            LastUpdated = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc),
        };
        int? attemptNumber = 2;

        AssertParity(
            new { success = true, history, attemptNumber },
            new GetDeviceHistoryResponse
            {
                Success = true,
                History = history,
                AttemptNumber = attemptNumber,
            });
    }

    [Fact]
    public void GetDeviceHistoryResponse_omits_a_null_history_and_attemptNumber()
    {
        DeviceHistory? history = null;
        int? attemptNumber = null;

        AssertParity(
            new { success = true, history, attemptNumber },
            new GetDeviceHistoryResponse
            {
                Success = true,
                History = null,
                AttemptNumber = null,
            });
    }

    // ---- GetGeographicLocationSessions / GetGlobalGeographicLocationSessions -------------

    private static LocationSessionRow SampleLocationRow(
        string sessionId, DateTime? completedAt, int? durationSeconds) => new LocationSessionRow
    {
        SessionId = sessionId,
        TenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d",
        SerialNumber = "SN-0042",
        DeviceName = "DESKTOP-CONTOSO1",
        Manufacturer = "Fabrikam Inc.",
        Model = "Latitude 9999",
        StartedAt = new DateTime(2026, 8, 30, 8, 0, 0, DateTimeKind.Utc),
        CompletedAt = completedAt,
        Status = SessionStatus.Succeeded,
        FailureReason = string.Empty,
        DurationSeconds = durationSeconds,
        EnrollmentType = "v1",
        GeoCountry = "DE",
        GeoRegion = "Hessen",
        GeoCity = "Frankfurt",
        TotalAppCount = 7,
        HasDoTelemetry = true,
        DoPercentPeerCaching = 42.5,
    };

    [Fact]
    public void GeographicLocationSessionsResponse_matches_the_full_shape()
    {
        var rows = new List<LocationSessionRow>
        {
            SampleLocationRow(
                "3f1c9d55-aaaa-4e0e-8888-52f3aaaa0001",
                new DateTime(2026, 8, 30, 9, 10, 0, DateTimeKind.Utc),
                4200),
        };

        AssertParity(
            new { success = true, sessions = rows, totalCount = rows.Count },
            new GeographicLocationSessionsResponse
            {
                Success = true,
                Sessions = rows,
                TotalCount = rows.Count,
            });
    }

    [Fact]
    public void GeographicLocationSessionsLeanResponse_matches_the_lean_projection_shape()
    {
        var r = SampleLocationRow(
            "3f1c9d55-bbbb-4e0e-8888-52f3aaaa0002",
            new DateTime(2026, 8, 30, 9, 40, 0, DateTimeKind.Utc),
            5100);

        // Old site: ToLeanRow returned `object` — an anonymous projection of the row.
        var lean = new List<object>
        {
            new
            {
                sessionId = r.SessionId,
                tenantId = r.TenantId,
                serialNumber = r.SerialNumber,
                deviceName = r.DeviceName,
                manufacturer = r.Manufacturer,
                model = r.Model,
                startedAt = r.StartedAt,
                completedAt = r.CompletedAt,
                status = r.Status,
                failureReason = r.FailureReason,
                durationSeconds = r.DurationSeconds,
                enrollmentType = r.EnrollmentType,
                geoCountry = r.GeoCountry,
                geoCity = r.GeoCity,
                totalAppCount = r.TotalAppCount,
                hasDoTelemetry = r.HasDoTelemetry,
                doPercentPeerCaching = r.DoPercentPeerCaching,
            },
        };
        var leanTyped = new List<LocationSessionLeanRow>
        {
            new LocationSessionLeanRow
            {
                SessionId = r.SessionId,
                TenantId = r.TenantId,
                SerialNumber = r.SerialNumber,
                DeviceName = r.DeviceName,
                Manufacturer = r.Manufacturer,
                Model = r.Model,
                StartedAt = r.StartedAt,
                CompletedAt = r.CompletedAt,
                Status = r.Status,
                FailureReason = r.FailureReason,
                DurationSeconds = r.DurationSeconds,
                EnrollmentType = r.EnrollmentType,
                GeoCountry = r.GeoCountry,
                GeoCity = r.GeoCity,
                TotalAppCount = r.TotalAppCount,
                HasDoTelemetry = r.HasDoTelemetry,
                DoPercentPeerCaching = r.DoPercentPeerCaching,
            },
        };

        AssertParity(
            new { success = true, sessions = lean, totalCount = lean.Count },
            new GeographicLocationSessionsLeanResponse
            {
                Success = true,
                Sessions = leanTyped,
                TotalCount = leanTyped.Count,
            });
    }

    [Fact]
    public void GeographicLocationSessionsLeanResponse_omits_null_completedAt_and_durationSeconds()
    {
        var r = SampleLocationRow("3f1c9d55-cccc-4e0e-8888-52f3aaaa0003", null, null);

        var lean = new List<object>
        {
            new
            {
                sessionId = r.SessionId,
                tenantId = r.TenantId,
                serialNumber = r.SerialNumber,
                deviceName = r.DeviceName,
                manufacturer = r.Manufacturer,
                model = r.Model,
                startedAt = r.StartedAt,
                completedAt = r.CompletedAt,
                status = r.Status,
                failureReason = r.FailureReason,
                durationSeconds = r.DurationSeconds,
                enrollmentType = r.EnrollmentType,
                geoCountry = r.GeoCountry,
                geoCity = r.GeoCity,
                totalAppCount = r.TotalAppCount,
                hasDoTelemetry = r.HasDoTelemetry,
                doPercentPeerCaching = r.DoPercentPeerCaching,
            },
        };
        var leanTyped = new List<LocationSessionLeanRow>
        {
            new LocationSessionLeanRow
            {
                SessionId = r.SessionId,
                TenantId = r.TenantId,
                SerialNumber = r.SerialNumber,
                DeviceName = r.DeviceName,
                Manufacturer = r.Manufacturer,
                Model = r.Model,
                StartedAt = r.StartedAt,
                CompletedAt = null,
                Status = r.Status,
                FailureReason = r.FailureReason,
                DurationSeconds = null,
                EnrollmentType = r.EnrollmentType,
                GeoCountry = r.GeoCountry,
                GeoCity = r.GeoCity,
                TotalAppCount = r.TotalAppCount,
                HasDoTelemetry = r.HasDoTelemetry,
                DoPercentPeerCaching = r.DoPercentPeerCaching,
            },
        };

        AssertParity(
            new { success = true, sessions = lean, totalCount = lean.Count },
            new GeographicLocationSessionsLeanResponse
            {
                Success = true,
                Sessions = leanTyped,
                TotalCount = leanTyped.Count,
            });
    }

    // ---- GetPlatformStats ----------------------------------------------------------------

    [Fact]
    public void GetPlatformStatsResponse_matches_the_zero_shape()
    {
        AssertParity(
            new
            {
                totalEnrollments = 0,
                totalUsers = 0,
                totalTenants = 0,
                uniqueDeviceModels = 0,
                totalEventsProcessed = 0,
                successfulEnrollments = 0,
                issuesDetected = 0,
                lastUpdated = (DateTime?)null
            },
            new GetPlatformStatsResponse
            {
                TotalEnrollments = 0,
                TotalUsers = 0,
                TotalTenants = 0,
                // TotalSignedUpTenants stays null → the key is absent, exactly like the old
                // literal that never declared it.
                UniqueDeviceModels = 0,
                TotalEventsProcessed = 0,
                SuccessfulEnrollments = 0,
                IssuesDetected = 0,
                LastUpdated = null
            });
    }

    [Fact]
    public void GetPlatformStatsResponse_matches_the_computed_stats_shape()
    {
        var stats = new PlatformStats
        {
            TotalEnrollments = 123456,
            TotalUsers = 789,
            TotalTenants = 321,
            TotalSignedUpTenants = 345,
            UniqueDeviceModels = 210,
            TotalEventsProcessed = 9876543,
            SuccessfulEnrollments = 111111,
            IssuesDetected = 2222,
        };

        AssertParity(
            new
            {
                totalEnrollments = stats.TotalEnrollments,
                totalUsers = stats.TotalUsers,
                totalTenants = stats.TotalTenants,
                totalSignedUpTenants = stats.TotalSignedUpTenants,
                uniqueDeviceModels = stats.UniqueDeviceModels,
                totalEventsProcessed = stats.TotalEventsProcessed,
                successfulEnrollments = stats.SuccessfulEnrollments,
                issuesDetected = stats.IssuesDetected
            },
            new GetPlatformStatsResponse
            {
                TotalEnrollments = stats.TotalEnrollments,
                TotalUsers = stats.TotalUsers,
                TotalTenants = stats.TotalTenants,
                TotalSignedUpTenants = stats.TotalSignedUpTenants,
                UniqueDeviceModels = stats.UniqueDeviceModels,
                TotalEventsProcessed = stats.TotalEventsProcessed,
                SuccessfulEnrollments = stats.SuccessfulEnrollments,
                IssuesDetected = stats.IssuesDetected
                // LastUpdated stays null → the key is absent, exactly like the old literal
                // that never declared it.
            });
    }

    // ---- GetSessionTimeAttribution -------------------------------------------------------

    [Fact]
    public void GetSessionTimeAttributionResponse_matches_the_breakdown_shape()
    {
        SessionTimeBreakdown? breakdown = new SessionTimeBreakdown
        {
            TenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d",
            SessionId = "5c8e1b20-dddd-49a2-8bd1-52f3aaaa0004",
            AttributionVersion = 2,
            EventCountAtCompute = 250,
            WallClockSeconds = 3600,
            UnattributedSeconds = 120,
            RebootSeconds = 90,
            SleepSeconds = 0,
            BlockingAppCount = 3,
            EspAppsOccupancySeconds = 1500,
        };

        AssertParity(
            new { success = true, breakdown },
            new GetSessionTimeAttributionResponse
            {
                Success = true,
                Breakdown = breakdown,
            });
    }

    [Fact]
    public void GetSessionTimeAttributionResponse_omits_a_null_breakdown()
    {
        SessionTimeBreakdown? breakdown = null;

        AssertParity(
            new { success = true, breakdown },
            new GetSessionTimeAttributionResponse
            {
                Success = true,
                Breakdown = null,
            });
    }

    // ---- GetMyMcpUsage -------------------------------------------------------------------

    private static List<UserUsageRecord> SampleUsageRecords(string userId) => new List<UserUsageRecord>
    {
        new UserUsageRecord
        {
            UserId = userId,
            UserPrincipalName = "admin@contoso.com",
            TenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d",
            Endpoint = "get_session_summary",
            Date = "20260830",
            RequestCount = 12,
            LastRequestAt = new DateTime(2026, 8, 30, 14, 30, 0, DateTimeKind.Utc),
        },
    };

    [Fact]
    public void GetMyMcpUsageResponse_matches_the_self_service_shape()
    {
        var userId = "7aa20c11-0001-4b7c-a1d2-52f3aaaa0007";
        string? upn = "admin@contoso.com";
        string? usagePlan = "pro";
        var effectivePlan = "pro";
        var dailyLimit = 500;
        var monthlyLimit = 10000;
        long dailyUsed = 42;
        long monthlyUsed = 950;
        var resetUtc = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        var tenantPlan = "community";
        var tenantDailyLimit = 300;
        var tenantMonthlyLimit = 9000;
        long tenantDailyUsed = 120;
        long tenantMonthlyUsed = 4100;
        var records = SampleUsageRecords(userId);

        AssertParity(
            new
            {
                userId,
                upn,
                usagePlan,
                effectivePlan,
                quota = new
                {
                    dailyLimit,
                    monthlyLimit,
                    dailyUsed,
                    monthlyUsed,
                    resetUtc,
                    tenantPlan,
                    tenantDailyLimit,
                    tenantMonthlyLimit,
                    tenantDailyUsed,
                    tenantMonthlyUsed
                },
                records
            },
            new GetMyMcpUsageResponse
            {
                UserId = userId,
                Upn = upn,
                UsagePlan = usagePlan,
                EffectivePlan = effectivePlan,
                Quota = new McpUsageQuotaNode
                {
                    DailyLimit = dailyLimit,
                    MonthlyLimit = monthlyLimit,
                    DailyUsed = dailyUsed,
                    MonthlyUsed = monthlyUsed,
                    ResetUtc = resetUtc,
                    TenantPlan = tenantPlan,
                    TenantDailyLimit = tenantDailyLimit,
                    TenantMonthlyLimit = tenantMonthlyLimit,
                    TenantDailyUsed = tenantDailyUsed,
                    TenantMonthlyUsed = tenantMonthlyUsed
                },
                Records = records
            });
    }

    [Fact]
    public void GetMyMcpUsageResponse_omits_null_upn_and_usagePlan()
    {
        var userId = "7aa20c11-0002-4b7c-a1d2-52f3aaaa0008";
        string? upn = null;
        string? usagePlan = null;
        var effectivePlan = "free";
        var records = new List<UserUsageRecord>();

        AssertParity(
            new
            {
                userId,
                upn,
                usagePlan,
                effectivePlan,
                quota = new
                {
                    dailyLimit = 50,
                    monthlyLimit = 1000,
                    dailyUsed = 0L,
                    monthlyUsed = 0L,
                    resetUtc = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc),
                    tenantPlan = "free",
                    tenantDailyLimit = 0,
                    tenantMonthlyLimit = 0,
                    tenantDailyUsed = 0L,
                    tenantMonthlyUsed = 0L
                },
                records
            },
            new GetMyMcpUsageResponse
            {
                UserId = userId,
                Upn = null,
                UsagePlan = null,
                EffectivePlan = effectivePlan,
                Quota = new McpUsageQuotaNode
                {
                    DailyLimit = 50,
                    MonthlyLimit = 1000,
                    DailyUsed = 0,
                    MonthlyUsed = 0,
                    ResetUtc = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc),
                    TenantPlan = "free",
                    TenantDailyLimit = 0,
                    TenantMonthlyLimit = 0,
                    TenantDailyUsed = 0,
                    TenantMonthlyUsed = 0
                },
                Records = records
            });
    }

    // ---- GetMcpUserUsage -----------------------------------------------------------------

    [Fact]
    public void GetMcpUserUsageResponse_matches_the_user_usage_shape()
    {
        var userId = "8bb31d22-0003-4b7c-a1d2-52f3aaaa0009";
        var records = SampleUsageRecords(userId);

        AssertParity(
            new { userId, records },
            new GetMcpUserUsageResponse { UserId = userId, Records = records });
    }

    // ---- GetGlobalMcpUsage ---------------------------------------------------------------

    [Fact]
    public void GetGlobalMcpUsageResponse_matches_the_tenant_usage_shape()
    {
        string? tenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d";
        var records = SampleUsageRecords("9cc42e33-0004-4b7c-a1d2-52f3aaaa0010");

        AssertParity(
            new { tenantId, records },
            new GetGlobalMcpUsageResponse { TenantId = tenantId, Records = records });
    }

    [Fact]
    public void GetGlobalMcpUsageResponse_omits_a_null_tenantId()
    {
        string? tenantId = null;
        var records = new List<UserUsageRecord>();

        AssertParity(
            new { tenantId, records },
            new GetGlobalMcpUsageResponse { TenantId = null, Records = records });
    }

    // ---- GetGlobalMcpUsageDaily ----------------------------------------------------------

    [Fact]
    public void GetGlobalMcpUsageDailyResponse_matches_the_daily_summary_shape()
    {
        string? tenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d";
        var summaries = new List<UserUsageDailySummary>
        {
            new UserUsageDailySummary
            {
                Date = "20260830",
                TenantId = tenantId,
                TotalRequests = 321,
                UniqueUsers = 4,
                UniqueEndpoints = 11,
            },
        };

        AssertParity(
            new { tenantId, summaries },
            new GetGlobalMcpUsageDailyResponse { TenantId = tenantId, Summaries = summaries });
    }

    [Fact]
    public void GetGlobalMcpUsageDailyResponse_omits_a_null_tenantId()
    {
        string? tenantId = null;
        var summaries = new List<UserUsageDailySummary>();

        AssertParity(
            new { tenantId, summaries },
            new GetGlobalMcpUsageDailyResponse { TenantId = null, Summaries = summaries });
    }

    // ---- MetricsSummary / MetricsSummaryGlobal -------------------------------------------

    [Fact]
    public void MetricsSummaryResponse_matches_the_tally_shape()
    {
        var days = 30;
        // Left: the exact anonymous per-tenant row GetMetricsSummaryAsync built before the
        // MetricsSummaryTenantItem substitution — proves the DTO item is wire-identical
        // (incl. the windowDays duplicate on every item).
        var summary = new List<object>
        {
            new
            {
                tenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d",
                totalSessions = 10,
                succeeded = 6,
                failed = 2,
                inProgress = 1,
                pending = 0,
                stalled = 0,
                awaitingUser = 0,
                incomplete = 1,
                other = 0,
                failureRate = 25.0,
                windowDays = days
            },
        };

        AssertParity(
            new { success = true, summary, windowDays = days },
            new MetricsSummaryResponse
            {
                Success = true,
                Summary = new List<MetricsSummaryTenantItem>
                {
                    new MetricsSummaryTenantItem
                    {
                        TenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d",
                        TotalSessions = 10,
                        Succeeded = 6,
                        Failed = 2,
                        InProgress = 1,
                        Pending = 0,
                        Stalled = 0,
                        AwaitingUser = 0,
                        Incomplete = 1,
                        Other = 0,
                        FailureRate = 25.0,
                        WindowDays = days,
                    },
                },
                WindowDays = days,
            });
    }

    // ---- GetRuleHitSessions --------------------------------------------------------------

    [Fact]
    public void GetRuleHitSessionsResponse_matches_the_hit_listing_shape()
    {
        var ruleId = "APP-017";
        var days = 14;
        var sessionIds = new List<string>
        {
            "add53f44-0005-4b7c-a1d2-52f3aaaa0011",
            "bee64055-0006-4b7c-a1d2-52f3aaaa0012",
        };
        const int maxSessionIds = 2000;

        AssertParity(
            new
            {
                ruleId,
                days,
                sessionIds,
                truncated = sessionIds.Count >= maxSessionIds
            },
            new GetRuleHitSessionsResponse
            {
                RuleId = ruleId,
                Days = days,
                SessionIds = sessionIds,
                Truncated = sessionIds.Count >= maxSessionIds
            });
    }

    // ---- GetMcpOrganizationUsage --------------------------------------------------------

    [Fact]
    public void GetMcpOrganizationUsageResponse_matches_the_by_user_shape()
    {
        var tenantId = "7aa20c11-0002-4b7c-a1d2-52f3aaaa0001";
        var userId = "7aa20c11-0002-4b7c-a1d2-52f3aaaa0009";
        var homeTenantId = "7aa20c11-0002-4b7c-a1d2-52f3aaaa0002";
        var lastRequestAt = new DateTime(2026, 9, 2, 14, 5, 0, DateTimeKind.Utc);

        AssertParity(
            new
            {
                tenantId,
                dateFrom = "20260901",
                dateTo = "20260902",
                users = new[]
                {
                    new
                    {
                        userId,
                        userPrincipalName = "msp@partner.example",
                        delegated = true,
                        homeTenantId,
                        requestsToday = 4L,
                        requestsThisMonth = 120L,
                        requestsInRange = 120L,
                        lastRequestAt,
                    },
                },
            },
            new GetMcpOrganizationUsageResponse
            {
                TenantId = tenantId,
                DateFrom = "20260901",
                DateTo = "20260902",
                Users = new List<McpOrganizationUsageItem>
                {
                    new()
                    {
                        UserId = userId,
                        UserPrincipalName = "msp@partner.example",
                        Delegated = true,
                        HomeTenantId = homeTenantId,
                        RequestsToday = 4,
                        RequestsThisMonth = 120,
                        RequestsInRange = 120,
                        LastRequestAt = lastRequestAt,
                    },
                },
            });
    }

    [Fact]
    public void GetMcpOrganizationUsageResponse_omits_null_upn_home_and_lastRequest()
    {
        // A row written before the attribution columns existed: own member, no UPN, no timestamp.
        var tenantId = "7aa20c11-0002-4b7c-a1d2-52f3aaaa0001";
        var userId = "7aa20c11-0002-4b7c-a1d2-52f3aaaa0009";
        string? userPrincipalName = null;
        string? homeTenantId = null;
        DateTime? lastRequestAt = null;

        AssertParity(
            new
            {
                tenantId,
                dateFrom = "20260901",
                dateTo = "20260902",
                users = new[]
                {
                    new
                    {
                        userId,
                        userPrincipalName,
                        delegated = false,
                        homeTenantId,
                        requestsToday = 0L,
                        requestsThisMonth = 3L,
                        requestsInRange = 3L,
                        lastRequestAt,
                    },
                },
            },
            new GetMcpOrganizationUsageResponse
            {
                TenantId = tenantId,
                DateFrom = "20260901",
                DateTo = "20260902",
                Users = new List<McpOrganizationUsageItem>
                {
                    new() { UserId = userId, Delegated = false, RequestsToday = 0, RequestsThisMonth = 3, RequestsInRange = 3 },
                },
            });
    }

    private static void AssertParity(object anonymousLiteral, IApiResponse typed)
        => ApiResponseWireParityTests.AssertWireIdentical(anonymousLiteral, typed);
}

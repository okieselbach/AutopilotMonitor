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
        // Three purchased delegation slots: the limits above already grew by them; the breakdown rides along.
        int? purchasedDelegatedSlots = 3;
        int? slotDailyLimit = 300;
        int? slotMonthlyLimit = 6000;
        int? slotTenantDailyLimit = 900;
        int? slotTenantMonthlyLimit = 18000;
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
                    tenantMonthlyUsed,
                    purchasedDelegatedSlots,
                    slotDailyLimit,
                    slotMonthlyLimit,
                    slotTenantDailyLimit,
                    slotTenantMonthlyLimit
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
                    TenantMonthlyUsed = tenantMonthlyUsed,
                    PurchasedDelegatedSlots = purchasedDelegatedSlots,
                    SlotDailyLimit = slotDailyLimit,
                    SlotMonthlyLimit = slotMonthlyLimit,
                    SlotTenantDailyLimit = slotTenantDailyLimit,
                    SlotTenantMonthlyLimit = slotTenantMonthlyLimit
                },
                Records = records
            });
    }

    [Fact]
    public void GetMyMcpUsageResponse_omits_null_upn_and_usagePlan()
    {
        // …and the slot breakdown, which is absent when no delegation slot was purchased.
        var userId = "7aa20c11-0002-4b7c-a1d2-52f3aaaa0008";
        string? upn = null;
        string? usagePlan = null;
        var effectivePlan = "free";
        int? purchasedDelegatedSlots = null;
        int? slotDailyLimit = null;
        int? slotMonthlyLimit = null;
        int? slotTenantDailyLimit = null;
        int? slotTenantMonthlyLimit = null;
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
                    tenantMonthlyUsed = 0L,
                    purchasedDelegatedSlots,
                    slotDailyLimit,
                    slotMonthlyLimit,
                    slotTenantDailyLimit,
                    slotTenantMonthlyLimit
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
                        userPrincipalName = "admin@contoso.com",
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
                        UserPrincipalName = "admin@contoso.com",
                        RequestsToday = 4,
                        RequestsThisMonth = 120,
                        RequestsInRange = 120,
                        LastRequestAt = lastRequestAt,
                    },
                },
            });
    }

    [Fact]
    public void GetMcpOrganizationUsageResponse_omits_null_upn_and_lastRequest()
    {
        // A row written before the attribution columns existed: no UPN, no timestamp.
        var tenantId = "7aa20c11-0002-4b7c-a1d2-52f3aaaa0001";
        var userId = "7aa20c11-0002-4b7c-a1d2-52f3aaaa0009";
        string? userPrincipalName = null;
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
                    new() { UserId = userId, RequestsToday = 0, RequestsThisMonth = 3, RequestsInRange = 3 },
                },
            });
    }

    [Fact]
    public void GetMcpOrganizationUsageResponse_quota_carries_the_slot_breakdown()
    {
        // Three purchased slots: 3 000 + 3 × 900 arrive as the grown limit; the breakdown lets the page show the sum.
        var tenantId = "7aa20c11-0002-4b7c-a1d2-52f3aaaa0001";
        var daily = new List<McpOrganizationDailyItem>
        {
            new() { Date = "20260901", Requests = 12 },
            new() { Date = "20260902", Requests = 7 },
        };

        AssertParity(
            new
            {
                tenantId,
                dateFrom = "20260901",
                dateTo = "20260902",
                users = Array.Empty<object>(),
                quota = new
                {
                    tenantPlan = "pro",
                    dailyLimit = 5700,
                    monthlyLimit = 114000,
                    dailyUsed = 7L,
                    monthlyUsed = 19L,
                    purchasedDelegatedSlots = 3,
                    slotDailyLimit = 900,
                    slotMonthlyLimit = 18000,
                },
                daily,
            },
            new GetMcpOrganizationUsageResponse
            {
                TenantId = tenantId,
                DateFrom = "20260901",
                DateTo = "20260902",
                Users = new List<McpOrganizationUsageItem>(),
                Quota = new McpOrganizationQuotaNode
                {
                    TenantPlan = "pro",
                    DailyLimit = 5700,
                    MonthlyLimit = 114000,
                    DailyUsed = 7,
                    MonthlyUsed = 19,
                    PurchasedDelegatedSlots = 3,
                    SlotDailyLimit = 900,
                    SlotMonthlyLimit = 18000,
                },
                Daily = daily,
            });
    }

    // ---- GetGlobalMcpUsageOverview ------------------------------------------------------

    [Fact]
    public void GetGlobalMcpUsageOverviewResponse_matches_the_overview_shape()
    {
        // One tenant with one account, one tool with one route, one quota refusal, one per-minute row — every
        // optional slot filled, so every key is pinned in its wire position.
        var at = new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);
        var tenantId = "7aa20c11-0002-4b7c-a1d2-52f3aaaa0001";
        var quota = new McpOrganizationQuotaNode { TenantPlan = "community", DailyLimit = 100, MonthlyLimit = 250, DailyUsed = 35, MonthlyUsed = 60 };

        AssertParity(
            new
            {
                dateFrom = "20261103",
                dateTo = "20261105",
                includesPlatformAdmins = false,
                telemetry = new { requestsAvailable = true, mcpLogAvailable = true, mcpLogFrom = "20261104", partial = true },
                totals = new
                {
                    calls = 10L, mcpOnlyCalls = 4L, tools = 2, requests = 50L, tenants = 1, accounts = 1, servicePrincipals = 0,
                    quotaHitAccounts = 1, minuteRefusedAccounts = 1, limitAccounts = 1,
                },
                daily = new[] { new { date = "20261105", calls = 10L, requests = 35L, accounts = 3, callsByPlan = new[] { new { plan = "community", calls = 10L } } } },
                tenants = new[]
                {
                    new
                    {
                        tenantId,
                        domainName = "contoso.com",
                        quota,
                        calls = 7L,
                        requests = 50L,
                        quotaHits = 1,
                        lastRequestAt = at,
                        accounts = new[]
                        {
                            new
                            {
                                userId = "oid-1",
                                principal = "admin@contoso.com",
                                role = "Admin",
                                isPlatformAdmin = false,
                                quota = new { plan = "community", dailyLimit = 50, monthlyLimit = 250, dailyUsed = 30L, monthlyUsed = 60L },
                                calls = 7L,
                                requests = 50L,
                                peakDay = "20261105",
                                peakDayRequests = 30L,
                                quotaHits = 1,
                                topTools = new[] { new { tool = "get_session_events", calls = 3L } },
                                lastRequestAt = at,
                            },
                        },
                    },
                },
                tools = new[]
                {
                    new
                    {
                        tool = "get_session_summary",
                        calls = 3L,
                        accounts = 2,
                        tenants = 1,
                        requestsPerCall = 5.0,
                        errorRate = 0.0,
                        p95DurationMs = 600L,
                        avgResultChars = 19000L,
                        overCapCalls = 0L,
                        routes = new[] { new { route = "GET sessions/{id}", requests = 3L } },
                    },
                },
                quotaHits = new[]
                {
                    new
                    {
                        date = "20261105", tenantId, principal = "admin@contoso.com", level = "user", scope = "daily", plan = "community",
                        limit = 50L, used = 50L, refused = 3L, firstAt = at, lastAt = at,
                    },
                },
                minuteLimits = new
                {
                    mcpServerLimit = 60,
                    accounts = new[]
                    {
                        new
                        {
                            tenantId, principal = "admin@contoso.com", sharePercent = 33,
                            peakCalls = 12L, peakCallsAt = at, mcpRefused = 7L, mcpLastRefusedAt = at,
                            peakRequests = 40L, peakRequestsAt = at, backendLimit = 120, backendRefused = 2L, backendLastRefusedAt = at,
                        },
                    },
                },
            },
            new GetGlobalMcpUsageOverviewResponse
            {
                DateFrom = "20261103",
                DateTo = "20261105",
                IncludesPlatformAdmins = false,
                Telemetry = new McpUsageTelemetryStatus { RequestsAvailable = true, McpLogAvailable = true, McpLogFrom = "20261104", Partial = true },
                Totals = new McpUsageTotalsNode
                {
                    Calls = 10, McpOnlyCalls = 4, Tools = 2, Requests = 50, Tenants = 1, Accounts = 1, ServicePrincipals = 0,
                    QuotaHitAccounts = 1, MinuteRefusedAccounts = 1, LimitAccounts = 1,
                },
                Daily = new List<McpUsageDayItem>
                {
                    new() { Date = "20261105", Calls = 10, Requests = 35, Accounts = 3, CallsByPlan = new List<McpUsagePlanCallsItem> { new() { Plan = "community", Calls = 10 } } },
                },
                Tenants = new List<McpUsageTenantItem>
                {
                    new()
                    {
                        TenantId = tenantId,
                        DomainName = "contoso.com",
                        Quota = quota,
                        Calls = 7,
                        Requests = 50,
                        QuotaHits = 1,
                        LastRequestAt = at,
                        Accounts = new List<McpUsageAccountItem>
                        {
                            new()
                            {
                                UserId = "oid-1",
                                Principal = "admin@contoso.com",
                                Role = "Admin",
                                IsPlatformAdmin = false,
                                Quota = new McpUsageAccountQuotaNode { Plan = "community", DailyLimit = 50, MonthlyLimit = 250, DailyUsed = 30, MonthlyUsed = 60 },
                                Calls = 7,
                                Requests = 50,
                                PeakDay = "20261105",
                                PeakDayRequests = 30,
                                QuotaHits = 1,
                                TopTools = new List<McpUsageToolCallsItem> { new() { Tool = "get_session_events", Calls = 3 } },
                                LastRequestAt = at,
                            },
                        },
                    },
                },
                Tools = new List<McpUsageToolItem>
                {
                    new()
                    {
                        Tool = "get_session_summary",
                        Calls = 3,
                        Accounts = 2,
                        Tenants = 1,
                        RequestsPerCall = 5.0,
                        ErrorRate = 0.0,
                        P95DurationMs = 600,
                        AvgResultChars = 19000,
                        OverCapCalls = 0,
                        Routes = new List<McpUsageRouteItem> { new() { Route = "GET sessions/{id}", Requests = 3 } },
                    },
                },
                QuotaHits = new List<McpUsageQuotaHitItem>
                {
                    new()
                    {
                        Date = "20261105", TenantId = tenantId, Principal = "admin@contoso.com", Level = "user", Scope = "daily", Plan = "community",
                        Limit = 50, Used = 50, Refused = 3, FirstAt = at, LastAt = at,
                    },
                },
                MinuteLimits = new McpUsageMinuteLimits
                {
                    McpServerLimit = 60,
                    Accounts = new List<McpUsageMinuteItem>
                    {
                        new()
                        {
                            TenantId = tenantId, Principal = "admin@contoso.com", SharePercent = 33,
                            PeakCalls = 12, PeakCallsAt = at, McpRefused = 7, McpLastRefusedAt = at,
                            PeakRequests = 40, PeakRequestsAt = at, BackendLimit = 120, BackendRefused = 2, BackendLastRefusedAt = at,
                        },
                    },
                },
            });
    }

    [Fact]
    public void GetGlobalMcpUsageOverviewResponse_omits_what_a_store_did_not_answer()
    {
        // Telemetry down, an account known only from the telemetry shape (no object id, no quota), an MCP-only
        // tool (no reach, no quality), no MCP limit reported yet: every nullable key vanishes.
        var tenantId = "7aa20c11-0002-4b7c-a1d2-52f3aaaa0001";
        var quota = new McpOrganizationQuotaNode { TenantPlan = "community", DailyLimit = 100, MonthlyLimit = 250, DailyUsed = 0, MonthlyUsed = 0 };

        AssertParity(
            new
            {
                dateFrom = "20261105",
                dateTo = "20261105",
                includesPlatformAdmins = true,
                telemetry = new { requestsAvailable = false, mcpLogAvailable = false },
                totals = new { tools = 1, requests = 0L, tenants = 1, accounts = 1, servicePrincipals = 1, quotaHitAccounts = 0, minuteRefusedAccounts = 0, limitAccounts = 0 },
                daily = Array.Empty<object>(),
                tenants = new[]
                {
                    new
                    {
                        tenantId,
                        quota,
                        calls = 4L,
                        requests = 0L,
                        quotaHits = 0,
                        accounts = new[]
                        {
                            new { principal = "app:11111111-2222-3333-4444-555555555555", isPlatformAdmin = false, calls = 4L, requests = 0L, quotaHits = 0, topTools = Array.Empty<object>() },
                        },
                    },
                },
                tools = new[] { new { tool = "search_docs", calls = 4L, requestsPerCall = 0.0, routes = Array.Empty<object>() } },
                quotaHits = Array.Empty<object>(),
                minuteLimits = new { accounts = Array.Empty<object>() },
            },
            new GetGlobalMcpUsageOverviewResponse
            {
                DateFrom = "20261105",
                DateTo = "20261105",
                IncludesPlatformAdmins = true,
                Telemetry = new McpUsageTelemetryStatus { RequestsAvailable = false, McpLogAvailable = false },
                Totals = new McpUsageTotalsNode { Tools = 1, Requests = 0, Tenants = 1, Accounts = 1, ServicePrincipals = 1 },
                Daily = new List<McpUsageDayItem>(),
                Tenants = new List<McpUsageTenantItem>
                {
                    new()
                    {
                        TenantId = tenantId,
                        Quota = quota,
                        Calls = 4,
                        Requests = 0,
                        QuotaHits = 0,
                        Accounts = new List<McpUsageAccountItem>
                        {
                            new() { Principal = "app:11111111-2222-3333-4444-555555555555", Calls = 4, TopTools = new List<McpUsageToolCallsItem>() },
                        },
                    },
                },
                Tools = new List<McpUsageToolItem> { new() { Tool = "search_docs", Calls = 4, RequestsPerCall = 0, Routes = new List<McpUsageRouteItem>() } },
                QuotaHits = new List<McpUsageQuotaHitItem>(),
                MinuteLimits = new McpUsageMinuteLimits { Accounts = new List<McpUsageMinuteItem>() },
            });
    }

    // ---- GetGrowthMetrics ---------------------------------------------------------------

    private static GrowthMetricsResponse GrowthResponse(double? median, IReadOnlyList<GrowthTenantRow>? tenants) => new()
    {
        WindowDays = 7,
        WindowStart = "2026-10-22",
        WindowEnd = "2026-10-28",
        TimeZone = "Europe/Berlin",
        ExactSignupTimesFrom = "2026-03-18",
        UsageDaysThrough = "2026-10-27",
        ExcludedTenants = 1,
        Signups = new GrowthSignups
        {
            Total = 5,
            PreviousWindowTotal = 1,
            BucketSize = "day",
            Buckets = new List<GrowthBucket> { new() { Start = "2026-10-22", Count = 5 } },
            PeakDays = new List<GrowthPeakDay> { new() { Day = "2026-10-22", Count = 5, BaselineMedian = 1 } },
            ByHour = new int[24],
            ByWeekday = new int[7],
        },
        Cohorts = new List<GrowthCohortWeek>
        {
            new() { WeekStart = "2026-10-19", Signups = 5, Enrolled = 3, Succeeded = 2, ActiveLast7Days = 3, MedianDaysToFirstEnrollment = median },
        },
        Activation = new GrowthActivation
        {
            Enrolled = 3, Succeeded = 2, NeverEnrolled = 2,
            FirstEnrollmentSameDay = 1, FirstEnrollmentNextDay = 1, FirstEnrollmentDays2To7 = 0, FirstEnrollmentLater = 0,
            FirstEnrollmentPending = 1, FirstEnrollmentUnknown = 0, MedianDaysToFirstEnrollment = median,
        },
        AllTenants = new GrowthTenantSegments
        {
            Tenants = 7, NeverEnrolled = 2, Enrolled = 5, EnrolledWithoutSuccess = 1, Active7Days = 3, Active30Days = 4, Dormant = 1,
        },
        Plans = new GrowthPlans
        {
            ByPlanTier = new List<GrowthPlanCount> { new() { PlanTier = "free", Tenants = 6 } },
            TrialsStartedInWindow = 1,
            TrialsActive = 1,
            PayingCustomers = 1,
        },
        Tenants = tenants,
    };

    private static object GrowthLiteral(double? median, object? tenants) => new
    {
        windowDays = 7,
        windowStart = "2026-10-22",
        windowEnd = "2026-10-28",
        timeZone = "Europe/Berlin",
        exactSignupTimesFrom = "2026-03-18",
        usageDaysThrough = "2026-10-27",
        excludedTenants = 1,
        signups = new
        {
            total = 5,
            previousWindowTotal = 1,
            bucketSize = "day",
            buckets = new[] { new { start = "2026-10-22", count = 5 } },
            peakDays = new[] { new { day = "2026-10-22", count = 5, baselineMedian = 1.0 } },
            byHour = new int[24],
            byWeekday = new int[7],
        },
        cohorts = new[]
        {
            new { weekStart = "2026-10-19", signups = 5, enrolled = 3, succeeded = 2, activeLast7Days = 3, medianDaysToFirstEnrollment = median },
        },
        activation = new
        {
            enrolled = 3, succeeded = 2, neverEnrolled = 2,
            firstEnrollmentSameDay = 1, firstEnrollmentNextDay = 1, firstEnrollmentDays2To7 = 0, firstEnrollmentLater = 0,
            firstEnrollmentPending = 1, firstEnrollmentUnknown = 0, medianDaysToFirstEnrollment = median,
        },
        allTenants = new
        {
            tenants = 7, neverEnrolled = 2, enrolled = 5, enrolledWithoutSuccess = 1, active7Days = 3, active30Days = 4, dormant = 1,
        },
        plans = new
        {
            byPlanTier = new[] { new { planTier = "free", tenants = 6 } },
            trialsStartedInWindow = 1,
            trialsActive = 1,
            payingCustomers = 1,
        },
        tenants,
    };

    [Fact]
    public void GrowthMetricsResponse_matches_the_shape_with_tenant_rows()
    {
        var tenantId = "8bb31d22-0003-4c8d-b2e3-63f4bbbb0001";
        var onboardedAt = new DateTime(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);

        AssertParity(
            GrowthLiteral(0.5, new[]
            {
                new
                {
                    tenantId,
                    onboardedAt,
                    planTier = "free",
                    enrolled = true,
                    succeeded = true,
                    firstEnrollmentDay = "2026-10-22",
                    lastActiveDay = "2026-10-27",
                },
            }),
            GrowthResponse(0.5, new List<GrowthTenantRow>
            {
                new()
                {
                    TenantId = tenantId,
                    OnboardedAt = onboardedAt,
                    PlanTier = "free",
                    Enrolled = true,
                    Succeeded = true,
                    FirstEnrollmentDay = "2026-10-22",
                    LastActiveDay = "2026-10-27",
                },
            }));
    }

    [Fact]
    public void GrowthMetricsResponse_omits_unknown_medians_and_the_tenant_rows()
    {
        // No first enrollment known yet and no includeTenants: the three keys vanish identically.
        AssertParity(GrowthLiteral(null, null), GrowthResponse(null, null));
    }

    // ---------- GET global/metrics/tenant-activity ----------

    private const string ActivityTenantId = "8bb31d22-0004-4c8d-b2e3-63f4bbbb0001";
    private static readonly DateTime ActivityOnboardedAt = new(2026, 6, 30, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ActivityCounterAt = new(2026, 11, 6, 7, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime ActivityVisitAt = new(2026, 11, 6, 8, 15, 0, DateTimeKind.Utc);

    private static GetTenantActivityResponse TenantActivityResponse(bool full) => new()
    {
        WindowDays = 30,
        WindowStart = "2026-10-07",
        UsageDaysThrough = "2026-11-05",
        PreviousWindowStart = "2026-09-07",
        RowsFrom = "2026-05-10",
        PortalActivityAvailable = full,
        Totals = new TenantActivityTotals
        {
            SignedUpTenants = 2,
            ActiveTenants = 1,
            ActiveTenantsPrevious = 1,
            ActiveTenantsTrendPercent = full ? 0 : null,
            Enrollments = 6,
            EnrollmentsPrevious = 4,
            EnrollmentsTrendPercent = full ? 50 : null,
            Succeeded = 5,
            Failed = 1,
            SuccessPercent = full ? 83 : null,
            Top10SharePercent = full ? 100 : null,
            Stages = new TenantActivityStageCounts { NeverEnrolled = 1, Active = 1 },
            PortalUsers = full ? 3 : null,
            PortalTenants = full ? 1 : null,
        },
        Daily = new List<TenantActivityDailyItem>
        {
            new() { Day = "2026-11-05", Enrollments = 6, Succeeded = 5, Failed = 1, ActiveTenants = 1 },
        },
        Tenants = new List<TenantActivityTenantItem>
        {
            new()
            {
                TenantId = ActivityTenantId,
                DomainName = "contoso.example",
                CompanyName = full ? "Contoso" : null,
                Edition = TenantEditionNames.ProTrial,
                Stage = TenantActivityStages.Active,
                OnboardedAt = full ? ActivityOnboardedAt : null,
                Enrollments = 6,
                EnrollmentsPrevious = 4,
                TrendPercent = full ? 50 : null,
                Succeeded = 5,
                Failed = 1,
                SuccessPercent = full ? 83 : null,
                ActiveDays = 1,
                DailyEnrollments = full ? new[] { 6 } : null,
                FirstEnrollmentDay = full ? "2026-07-01" : null,
                LastActiveDay = full ? "2026-11-05" : null,
                LastCounterUpdateAt = full ? ActivityCounterAt : null,
                LifetimeEnrollments = 120,
                LifetimeSucceeded = 100,
                LifetimeEvents = 50_000,
                PortalUsers = full ? 3 : null,
                LastPortalVisitAt = full ? ActivityVisitAt : null,
            },
        },
    };

    [Fact]
    public void GetTenantActivityResponse_matches_the_shape_with_every_optional_key()
    {
        AssertParity(new
        {
            windowDays = 30,
            windowStart = "2026-10-07",
            usageDaysThrough = "2026-11-05",
            previousWindowStart = "2026-09-07",
            rowsFrom = "2026-05-10",
            portalActivityAvailable = true,
            totals = new
            {
                signedUpTenants = 2,
                activeTenants = 1,
                activeTenantsPrevious = 1,
                activeTenantsTrendPercent = 0,
                enrollments = 6,
                enrollmentsPrevious = 4,
                enrollmentsTrendPercent = 50,
                succeeded = 5,
                failed = 1,
                successPercent = 83,
                top10SharePercent = 100,
                stages = new { neverEnrolled = 1, dormant = 0, @new = 0, declining = 0, active = 1, quiet = 0 },
                portalUsers = 3,
                portalTenants = 1,
            },
            daily = new[] { new { day = "2026-11-05", enrollments = 6, succeeded = 5, failed = 1, activeTenants = 1 } },
            tenants = new[]
            {
                new
                {
                    tenantId = ActivityTenantId,
                    domainName = "contoso.example",
                    companyName = "Contoso",
                    edition = "pro-trial",
                    stage = "active",
                    onboardedAt = ActivityOnboardedAt,
                    enrollments = 6,
                    enrollmentsPrevious = 4,
                    trendPercent = 50,
                    succeeded = 5,
                    failed = 1,
                    successPercent = 83,
                    activeDays = 1,
                    dailyEnrollments = new[] { 6 },
                    firstEnrollmentDay = "2026-07-01",
                    lastActiveDay = "2026-11-05",
                    lastCounterUpdateAt = ActivityCounterAt,
                    lifetimeEnrollments = 120L,
                    lifetimeSucceeded = 100L,
                    lifetimeEvents = 50_000L,
                    portalUsers = 3,
                    lastPortalVisitAt = ActivityVisitAt,
                },
            },
        }, TenantActivityResponse(full: true));
    }

    [Fact]
    public void GetTenantActivityResponse_omits_every_absent_figure()
    {
        // No sign-in history, no previous window, no finished session, no names beyond the domain.
        AssertParity(new
        {
            windowDays = 30,
            windowStart = "2026-10-07",
            usageDaysThrough = "2026-11-05",
            previousWindowStart = "2026-09-07",
            rowsFrom = "2026-05-10",
            portalActivityAvailable = false,
            totals = new
            {
                signedUpTenants = 2,
                activeTenants = 1,
                activeTenantsPrevious = 1,
                enrollments = 6,
                enrollmentsPrevious = 4,
                succeeded = 5,
                failed = 1,
                stages = new { neverEnrolled = 1, dormant = 0, @new = 0, declining = 0, active = 1, quiet = 0 },
            },
            daily = new[] { new { day = "2026-11-05", enrollments = 6, succeeded = 5, failed = 1, activeTenants = 1 } },
            tenants = new[]
            {
                new
                {
                    tenantId = ActivityTenantId,
                    domainName = "contoso.example",
                    edition = "pro-trial",
                    stage = "active",
                    enrollments = 6,
                    enrollmentsPrevious = 4,
                    succeeded = 5,
                    failed = 1,
                    activeDays = 1,
                    lifetimeEnrollments = 120L,
                    lifetimeSucceeded = 100L,
                    lifetimeEvents = 50_000L,
                },
            },
        }, TenantActivityResponse(full: false));
    }

    private static void AssertParity(object anonymousLiteral, IApiResponse typed)
        => ApiResponseWireParityTests.AssertWireIdentical(anonymousLiteral, typed);
}

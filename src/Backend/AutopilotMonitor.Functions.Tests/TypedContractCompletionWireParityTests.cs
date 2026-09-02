using System;
using System.Collections.Generic;
using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Backup;
using AutopilotMonitor.Shared.Models.Metrics;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Wire-identity proofs for the 2026-08-31 typed-contract COMPLETION pass: the endpoints the
/// original migration missed because their anonymous literal was smuggled past the ratchet
/// (variable indirection, object-returning builders, Serialize+WriteString, local WriteJson
/// wrappers). Same contract as <see cref="ApiResponseWireParityTests"/>: OLD anonymous
/// literal (copied from the pre-conversion diff) vs NEW DTO, ordinal JSON equality, plus a
/// null-slot case wherever a key must vanish. Sites converted by MOVING an already-named
/// class (PlatformAgentMetricsResponse family, AgentEfficiency family, VerdictCalibration
/// path rows, DeviceJourneyWindowTotals, OffboardResponse) are wire-identical by
/// construction (System.Text.Json ignores namespaces) and carry no fact.
/// </summary>
public class TypedContractCompletionWireParityTests
{
    private static void AssertWireIdentical(object anonymousLiteral, IApiResponse typed)
        => ApiResponseWireParityTests.AssertWireIdentical(anonymousLiteral, typed);

    // ── rule-stats (tenant + global) ─────────────────────────────────────────────────────

    [Fact]
    public void RuleStatsResponse_matches_the_tenant_shape_without_uniqueRules()
    {
        var regressions = new List<RuleRegressionAlert>();
        AssertWireIdentical(
            new
            {
                rules = new[]
                {
                    new
                    {
                        ruleId = "ANALYZE-NET-001",
                        ruleType = "analyze",
                        ruleTitle = "Network issue",
                        category = "network",
                        severity = "warning",
                        fireCount = 3,
                        evaluationCount = 10,
                        sessionsEvaluated = 9,
                        hitRate = 30.0,
                        avgConfidenceScore = 71.5,
                        trend = new[] { new { date = "2026-08-30", fireCount = 3, evaluationCount = 10 } },
                    },
                },
                regressions,
                summary = new
                {
                    totalEvaluations = 10,
                    totalFires = 3,
                    overallHitRate = 30.0,
                    topRuleByFireCount = (string?)"ANALYZE-NET-001",
                    period = new { start = "2026-08-01", end = "2026-08-31" },
                },
            },
            new RuleStatsResponse
            {
                Rules = new[]
                {
                    new RuleStatsRuleAggregate
                    {
                        RuleId = "ANALYZE-NET-001",
                        RuleType = "analyze",
                        RuleTitle = "Network issue",
                        Category = "network",
                        Severity = "warning",
                        FireCount = 3,
                        EvaluationCount = 10,
                        SessionsEvaluated = 9,
                        HitRate = 30.0,
                        AvgConfidenceScore = 71.5,
                        Trend = new[] { new RuleTrendPoint { Date = "2026-08-30", FireCount = 3, EvaluationCount = 10 } },
                    },
                },
                Regressions = regressions,
                // Tenant route: UniqueRules stays null → the key is absent, matching the
                // historical tenant shape which never carried it.
                Summary = new RuleStatsSummary
                {
                    TotalEvaluations = 10,
                    TotalFires = 3,
                    OverallHitRate = 30.0,
                    TopRuleByFireCount = "ANALYZE-NET-001",
                    UniqueRules = null,
                    Period = new RuleStatsPeriod { Start = "2026-08-01", End = "2026-08-31" },
                },
            });
    }

    [Fact]
    public void RuleStatsResponse_matches_the_global_shape_with_uniqueRules_and_null_topRule()
    {
        var regressions = new List<RuleRegressionAlert>();
        AssertWireIdentical(
            new
            {
                rules = Array.Empty<object>(),
                regressions,
                summary = new
                {
                    totalEvaluations = 0,
                    totalFires = 0,
                    overallHitRate = 0.0,
                    topRuleByFireCount = (string?)null, // empty window → key vanishes
                    uniqueRules = 0,
                    period = new { start = "2026-08-01", end = "2026-08-31" },
                },
            },
            new RuleStatsResponse
            {
                Rules = Array.Empty<RuleStatsRuleAggregate>(),
                Regressions = regressions,
                Summary = new RuleStatsSummary
                {
                    TotalEvaluations = 0,
                    TotalFires = 0,
                    OverallHitRate = 0.0,
                    TopRuleByFireCount = null,
                    UniqueRules = 0,
                    Period = new RuleStatsPeriod { Start = "2026-08-01", End = "2026-08-31" },
                },
            });
    }

    // ── verdict-calibration ──────────────────────────────────────────────────────────────

    [Fact]
    public void VerdictCalibrationResponse_matches_the_envelope_incl_computedAt_omission()
    {
        // Path rows and alerts were ALREADY named classes on the old wire (moved verbatim) —
        // the envelope keys are what the conversion changed, so the fact pins those with the
        // row lists empty; the no-data window additionally proves computedAt vanishes.
        var paths = new List<VerdictCalibrationPathRow>();
        var alerts = new List<VerdictCalibrationAlert>();
        AssertWireIdentical(
            new
            {
                success = true,
                tenantId = "global",
                windowDays = 30,
                windowStart = "2026-08-02",
                windowEnd = "2026-08-31",
                computedAt = (DateTime?)null,
                versions = new[] { 1, 2 },
                totals = new { sessions = 0, terminal = 0, derived = 0, days = 0 },
                trend = new { windowDays = 7, baselineDays = 28, windowSessions = 0, baselineSessions = 0 },
                paths,
                alerts,
            },
            new VerdictCalibrationResponse
            {
                Success = true,
                TenantId = "global",
                WindowDays = 30,
                WindowStart = "2026-08-02",
                WindowEnd = "2026-08-31",
                ComputedAt = null,
                Versions = new[] { 1, 2 },
                Totals = new VerdictCalibrationTotals { Sessions = 0, Terminal = 0, Derived = 0, Days = 0 },
                Trend = new VerdictCalibrationTrendMeta { WindowDays = 7, BaselineDays = 28, WindowSessions = 0, BaselineSessions = 0 },
                Paths = paths,
                Alerts = alerts,
            });
    }

    // ── metrics/app ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AppMetricsResponse_matches_the_anonymous_shape()
    {
        AssertWireIdentical(
            new
            {
                success = true,
                totalApps = 1,
                totalInstalls = 4,
                totalSkipped = 1,
                totalUnmeasured = 1,
                totalCollisionExcluded = 0,
                slowestApps = new[]
                {
                    new
                    {
                        appName = "Contoso App",
                        totalInstalls = 4,
                        succeeded = 2,
                        skipped = 1,
                        unmeasured = 1,
                        failed = 1,
                        failureRate = 33.3,
                        avgDurationSeconds = 120.0,
                        maxDurationSeconds = 240,
                        measuredInstalls = 1,
                        avgDownloadBytes = 1048576L,
                        doTotalBytesDownloaded = 2097152L,
                        doBytesFromPeers = 1048576L,
                        doBytesFromCacheServer = 0L,
                        doBytesFromHttp = 1048576L,
                        peerOffloadPercent = 50.0,
                        topFailureCodes = new[] { new { code = "0x80070005", count = 1 } },
                    },
                },
                topFailingApps = Array.Empty<object>(),
                deliveryOptimization = new
                {
                    totalBytesDownloaded = 2097152L,
                    fromPeers = 1048576L,
                    fromCacheServer = 0L,
                    fromHttp = 1048576L,
                    peerOffloadPercent = 50.0,
                },
            },
            new AppMetricsResponse
            {
                Success = true,
                TotalApps = 1,
                TotalInstalls = 4,
                TotalSkipped = 1,
                TotalUnmeasured = 1,
                TotalCollisionExcluded = 0,
                SlowestApps = new[]
                {
                    new AppMetricsAppGroup
                    {
                        AppName = "Contoso App",
                        TotalInstalls = 4,
                        Succeeded = 2,
                        Skipped = 1,
                        Unmeasured = 1,
                        Failed = 1,
                        FailureRate = 33.3,
                        AvgDurationSeconds = 120.0,
                        MaxDurationSeconds = 240,
                        MeasuredInstalls = 1,
                        AvgDownloadBytes = 1048576L,
                        DoTotalBytesDownloaded = 2097152L,
                        DoBytesFromPeers = 1048576L,
                        DoBytesFromCacheServer = 0L,
                        DoBytesFromHttp = 1048576L,
                        PeerOffloadPercent = 50.0,
                        TopFailureCodes = new[] { new AppFailureCodeCount { Code = "0x80070005", Count = 1 } },
                    },
                },
                TopFailingApps = Array.Empty<AppMetricsAppGroup>(),
                DeliveryOptimization = new AppMetricsDeliveryOptimization
                {
                    TotalBytesDownloaded = 2097152L,
                    FromPeers = 1048576L,
                    FromCacheServer = 0L,
                    FromHttp = 1048576L,
                    PeerOffloadPercent = 50.0,
                },
            });
    }

    // ── metrics/ime-versions (non-global projection) ─────────────────────────────────────

    [Fact]
    public void ImeVersionHistoryLeanEntry_matches_the_anonymous_projection()
    {
        var seen = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var expected = JsonSerializer.Serialize(
            new { Version = "1.104.102.0", FirstSeenAt = seen, LastSeenAt = seen.AddDays(3), SessionCount = 12 },
            ApiJsonOptions.Create());
        var actual = JsonSerializer.Serialize(
            new ImeVersionHistoryLeanEntry
            {
                Version = "1.104.102.0",
                FirstSeenAt = seen,
                LastSeenAt = seen.AddDays(3),
                SessionCount = 12,
            },
            ApiJsonOptions.Create());
        Assert.Equal(expected, actual);
    }

    // ── metrics/time-attribution (fleet) ─────────────────────────────────────────────────

    [Fact]
    public void TimeAttributionMetricsResponse_matches_the_envelope()
    {
        // Row lists are TimeAttributionDailyAggregate on BOTH sides (identical class) —
        // the envelope keys are the conversion.
        var rows = new List<TimeAttributionDailyAggregate>();
        AssertWireIdentical(
            new { success = true, windowDays = 30, classes = rows, daily = rows },
            new TimeAttributionMetricsResponse { Success = true, WindowDays = 30, Classes = rows, Daily = rows });
    }

    // ── metrics/device-journeys ──────────────────────────────────────────────────────────

    [Fact]
    public void DeviceJourneyMetricsResponse_matches_the_envelope_incl_repeatDevices_omission()
    {
        var totals = new DeviceJourneyWindowTotals { CompletedJourneys = 0, FirstTimeRight = 0, FtrRatePct = null, ExcludedSessions = 0 };
        var daily = new List<DeviceJourneyDailyAggregate>();
        // Global aggregate: repeatDevices=null → the key vanishes (the disclosed gap).
        AssertWireIdentical(
            new { success = true, windowDays = 30, totals, daily, repeatDevices = (List<object>?)null },
            new DeviceJourneyMetricsResponse { Success = true, WindowDays = 30, Totals = totals, Daily = daily, RepeatDevices = null });
    }

    [Fact]
    public void DeviceJourneyRepeatDevice_matches_the_anonymous_row()
    {
        var started = new DateTime(2026, 8, 29, 8, 0, 0, DateTimeKind.Utc);
        AssertWireIdentical(
            new
            {
                success = true,
                windowDays = 30,
                totals = new DeviceJourneyWindowTotals(),
                daily = new List<DeviceJourneyDailyAggregate>(),
                repeatDevices = new[]
                {
                    new
                    {
                        serialNumber = "SER123",
                        manufacturer = "Contoso",
                        model = "Book 3",
                        attempts = 3,
                        journeyCount = 2,
                        lastStatus = "Failed",
                        lastSessionId = "22222222-2222-2222-2222-222222222222",
                        lastStartedAt = started,
                        lastFailureReason = "ESP timeout",
                    },
                },
            },
            new DeviceJourneyMetricsResponse
            {
                Success = true,
                WindowDays = 30,
                Totals = new DeviceJourneyWindowTotals(),
                Daily = new List<DeviceJourneyDailyAggregate>(),
                RepeatDevices = new[]
                {
                    new DeviceJourneyRepeatDevice
                    {
                        SerialNumber = "SER123",
                        Manufacturer = "Contoso",
                        Model = "Book 3",
                        Attempts = 3,
                        JourneyCount = 2,
                        LastStatus = "Failed",
                        LastSessionId = "22222222-2222-2222-2222-222222222222",
                        LastStartedAt = started,
                        LastFailureReason = "ESP timeout",
                    },
                },
            });
    }

    // ── config/{tenantId}/feature-flags ──────────────────────────────────────────────────

    [Fact]
    public void TenantFeatureFlagsResponse_matches_the_anonymous_shape_incl_null_omissions()
    {
        AssertWireIdentical(
            new
            {
                bootstrapTokenEnabled = true,
                diagnosticsUploadConfigured = false,
                validateAutopilotDevice = true,
                appHomingFunnelActive = false,
                showScriptOutput = true,
                enableSoftwareInventoryAnalyzer = false,
                enableIntegrityBypassAnalyzer = true,
                unrestrictedMode = false,
                edition = "community",
                isTrial = false,
                trialExpiresUtc = (DateTime?)null,      // not on trial → key vanishes
                trialAvailable = true,
                contactEmailSet = false,
                companyNameSet = false,
                entitlements = new
                {
                    retentionCapDays = 90,
                    userRateLimitPerMinute = (int?)null, // platform default → key vanishes
                    delegatedAdminAllowed = false,
                    mcpUsagePlan = "community",
                    maxDelegatedTenants = 0,
                },
            },
            new TenantFeatureFlagsResponse
            {
                BootstrapTokenEnabled = true,
                DiagnosticsUploadConfigured = false,
                ValidateAutopilotDevice = true,
                AppHomingFunnelActive = false,
                ShowScriptOutput = true,
                EnableSoftwareInventoryAnalyzer = false,
                EnableIntegrityBypassAnalyzer = true,
                UnrestrictedMode = false,
                Edition = "community",
                IsTrial = false,
                TrialExpiresUtc = null,
                TrialAvailable = true,
                ContactEmailSet = false,
                CompanyNameSet = false,
                Entitlements = new TenantFeatureEntitlements
                {
                    RetentionCapDays = 90,
                    UserRateLimitPerMinute = null,
                    DelegatedAdminAllowed = false,
                    McpUsagePlan = "community",
                    MaxDelegatedTenants = 0,
                },
            });
    }

    // ── diagnostics/paths ────────────────────────────────────────────────────────────────

    [Fact]
    public void DiagnosticsPathsResponse_matches_the_anonymous_shape()
    {
        var globalPaths = new List<DiagnosticsLogPath>();
        AssertWireIdentical(
            new
            {
                builtIn = new[]
                {
                    new
                    {
                        id = "AgentLogs",
                        zipFolder = "AgentLogs",
                        sourceFolder = "%ProgramData%/AutopilotMonitor",
                        patterns = new[] { "*.log" },
                        includeSubfolders = false,
                        description = "Agent log files",
                        condition = "Always",
                    },
                },
                globalPaths,
            },
            new DiagnosticsPathsResponse
            {
                BuiltIn = new[]
                {
                    new DiagnosticsBuiltInSectionWire
                    {
                        Id = "AgentLogs",
                        ZipFolder = "AgentLogs",
                        SourceFolder = "%ProgramData%/AutopilotMonitor",
                        Patterns = new[] { "*.log" },
                        IncludeSubfolders = false,
                        Description = "Agent log files",
                        Condition = "Always",
                    },
                },
                GlobalPaths = globalPaths,
            });
    }

    // ── sessions restore / delete-queued ─────────────────────────────────────────────────

    [Fact]
    public void SessionRestoreResponse_matches_the_anonymous_shape_incl_null_omissions()
    {
        var rows = new Dictionary<string, int>(StringComparer.Ordinal) { ["Sessions"] = 1 };
        var empty = new Dictionary<string, int>(StringComparer.Ordinal);
        AssertWireIdentical(
            new
            {
                success = true,
                outcome = "Restored",
                mode = (string?)"full",
                message = (string?)null,          // clean success → key vanishes
                currentState = (string?)null,
                pendingManifestId = (string?)null,
                rowsRestoredByTable = rows,
                rowsSkippedByTable = empty,
                wouldRestoreByTable = empty,
                inventoryReIncrements = 2,
                durationMs = 1234L,
            },
            new SessionRestoreResponse
            {
                Success = true,
                Outcome = "Restored",
                Mode = "full",
                Message = null,
                CurrentState = null,
                PendingManifestId = null,
                RowsRestoredByTable = rows,
                RowsSkippedByTable = empty,
                WouldRestoreByTable = empty,
                InventoryReIncrements = 2,
                DurationMs = 1234L,
            });
    }

    [Fact]
    public void SessionDeletionQueuedResponse_matches_the_anonymous_Enqueued_arm()
    {
        AssertWireIdentical(
            new
            {
                success = true,
                status = "queued",
                manifestId = (string?)"01J0123456789ABCDEFGHIJKLM",
                message = "Cascade deletion queued; worker will drain asynchronously.",
            },
            new SessionDeletionQueuedResponse
            {
                Success = true,
                Status = "queued",
                ManifestId = "01J0123456789ABCDEFGHIJKLM",
                Message = "Cascade deletion queued; worker will drain asynchronously.",
            });
    }

    // ── feedback ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Feedback_responses_match_the_anonymous_shapes()
    {
        AssertWireIdentical(new { eligible = true }, new FeedbackEligibilityResponse { Eligible = true });

        AssertWireIdentical(
            new
            {
                feedback = new[]
                {
                    new
                    {
                        type = "InApp",
                        upn = "alice@contoso.com",
                        tenantId = "11111111-1111-1111-1111-111111111111",
                        displayName = "Alice",
                        rating = (int?)5,
                        comment = (string?)"Great",
                        dismissed = false,
                        submitted = true,
                        interactedAt = (string?)"2026-08-30T10:00:00.0000000Z",
                        historyRowKey = (string?)null, // in-app entry → key vanishes
                        domainName = (string?)null,
                    },
                },
            },
            new FeedbackListResponse
            {
                Feedback = new[]
                {
                    new FeedbackEntryWire
                    {
                        Type = "InApp",
                        Upn = "alice@contoso.com",
                        TenantId = "11111111-1111-1111-1111-111111111111",
                        DisplayName = "Alice",
                        Rating = 5,
                        Comment = "Great",
                        Dismissed = false,
                        Submitted = true,
                        InteractedAt = "2026-08-30T10:00:00.0000000Z",
                        HistoryRowKey = null,
                        DomainName = null,
                    },
                },
            });
    }

    // ── global/backups (custom serializer options) ───────────────────────────────────────

    [Fact]
    public void ListBackupsResponse_matches_the_anonymous_shape_under_backup_options()
    {
        var ids = new List<string> { "20260831T010203Z_abcd1234" };
        var expected = JsonSerializer.Serialize(new { backupIds = ids }, BackupManifestJson.SerializerOptions);
        var actual = JsonSerializer.Serialize(new ListBackupsResponse { BackupIds = ids }, BackupManifestJson.SerializerOptions);
        Assert.Equal(expected, actual);
    }

    // ── apps list / analytics / sessions ─────────────────────────────────────────────────

    [Fact]
    public void AppsListResponse_matches_the_legacy_and_paged_shapes()
    {
        var item = new AppsListItem
        {
            AppName = "Contoso App",
            AppType = "Win32",
            TotalInstalls = 5,
            Succeeded = 3,
            Skipped = 1,
            Unmeasured = 0,
            Failed = 1,
            FailureRate = 25.0,
            AvgDurationSeconds = 60.0,
            MaxDurationSeconds = 120,
            AvgDownloadBytes = 1024L,
            Trend = "stable",
            TrendDelta = null, // under 5 finished installs in a half → key vanishes
            LastSeenAt = new DateTime(2026, 8, 30, 9, 0, 0, DateTimeKind.Utc),
        };
        var anonymousItem = new
        {
            appName = "Contoso App",
            appType = "Win32",
            totalInstalls = 5,
            succeeded = 3,
            skipped = 1,
            unmeasured = 0,
            failed = 1,
            failureRate = 25.0,
            avgDurationSeconds = 60.0,
            maxDurationSeconds = 120,
            avgDownloadBytes = 1024L,
            trend = "stable",
            trendDelta = (double?)null,
            lastSeenAt = new DateTime(2026, 8, 30, 9, 0, 0, DateTimeKind.Utc),
        };

        // Legacy mode: no paging keys at all.
        AssertWireIdentical(
            new { success = true, totalApps = 1, totalInstalls = 5, collisionExcluded = 0, windowDays = 30, apps = new[] { anonymousItem } },
            new AppsListResponse
            {
                Success = true,
                TotalApps = 1,
                TotalInstalls = 5,
                CollisionExcluded = 0,
                WindowDays = 30,
                Apps = new[] { item },
            });

        // Paged mode incl. last-page nextLink omission.
        AssertWireIdentical(
            new
            {
                success = true,
                totalApps = 1,
                totalInstalls = 5,
                collisionExcluded = 0,
                windowDays = 30,
                count = 1,
                offset = 0,
                pageSize = 100,
                apps = new[] { anonymousItem },
                nextLink = (string?)null,
            },
            new AppsListResponse
            {
                Success = true,
                TotalApps = 1,
                TotalInstalls = 5,
                CollisionExcluded = 0,
                WindowDays = 30,
                Count = 1,
                Offset = 0,
                PageSize = 100,
                Apps = new[] { item },
                NextLink = null,
            });
    }

    [Fact]
    public void AppAnalyticsResponse_matches_the_empty_window_shape()
    {
        var regressions = new List<AppVersionRegressionAlert>();
        AssertWireIdentical(
            new
            {
                success = true,
                appName = "Contoso App",
                appType = string.Empty,
                windowDays = 30,
                collisionExcluded = 0,
                bucket = "day",
                summary = new
                {
                    totalInstalls = 0,
                    succeeded = 0,
                    skipped = 0,
                    unmeasured = 0,
                    failed = 0,
                    failureRate = 0.0,
                    avgDurationSeconds = 0.0,
                    p95DurationSeconds = 0,
                    avgDownloadBytes = 0L,
                    trend = "stable",
                    trendDelta = (double?)null,
                    flakinessScore = 0.0,
                },
                timeSeries = Array.Empty<object>(),
                versionBreakdown = Array.Empty<object>(),
                installerPhaseBreakdown = Array.Empty<object>(),
                topFailureCodes = Array.Empty<object>(),
                detectionLiesCount = 0,
                deviceModelBreakdown = Array.Empty<object>(),
                versionRegressions = regressions,
            },
            new AppAnalyticsResponse
            {
                Success = true,
                AppName = "Contoso App",
                AppType = string.Empty,
                WindowDays = 30,
                CollisionExcluded = 0,
                Bucket = "day",
                Summary = new AppAnalyticsSummary
                {
                    TotalInstalls = 0,
                    Succeeded = 0,
                    Skipped = 0,
                    Unmeasured = 0,
                    Failed = 0,
                    FailureRate = 0.0,
                    AvgDurationSeconds = 0.0,
                    P95DurationSeconds = 0,
                    AvgDownloadBytes = 0L,
                    Trend = "stable",
                    TrendDelta = null,
                    FlakinessScore = 0.0,
                },
                TimeSeries = Array.Empty<AppAnalyticsTimeBucket>(),
                VersionBreakdown = Array.Empty<AppVersionBreakdownItem>(),
                InstallerPhaseBreakdown = Array.Empty<AppInstallerPhaseCount>(),
                TopFailureCodes = Array.Empty<AppAnalyticsFailureCode>(),
                DetectionLiesCount = 0,
                DeviceModelBreakdown = Array.Empty<AppDeviceModelBreakdownItem>(),
                VersionRegressions = regressions,
            });
    }

    [Fact]
    public void AppAnalytics_inner_rows_match_their_anonymous_shapes()
    {
        var bucketStart = new DateTime(2026, 8, 29, 0, 0, 0, DateTimeKind.Utc);
        var wire = ApiJsonOptions.Create();

        Assert.Equal(
            JsonSerializer.Serialize(new
            {
                bucketStart,
                installs = 4,
                succeeded = 3,
                failed = 1,
                failureRate = 25.0,
                avgDurationSeconds = 90.0,
            }, wire),
            JsonSerializer.Serialize(new AppAnalyticsTimeBucket
            {
                BucketStart = bucketStart,
                Installs = 4,
                Succeeded = 3,
                Failed = 1,
                FailureRate = 25.0,
                AvgDurationSeconds = 90.0,
            }, wire));

        Assert.Equal(
            JsonSerializer.Serialize(new
            {
                appVersion = "1.2.3",
                installs = 4,
                failed = 1,
                failureRate = 25.0,
                measuredInstalls = 3,
                medianDurationSeconds = 60,
                p95DurationSeconds = 110,
            }, wire),
            JsonSerializer.Serialize(new AppVersionBreakdownItem
            {
                AppVersion = "1.2.3",
                Installs = 4,
                Failed = 1,
                FailureRate = 25.0,
                MeasuredInstalls = 3,
                MedianDurationSeconds = 60,
                P95DurationSeconds = 110,
            }, wire));

        Assert.Equal(
            JsonSerializer.Serialize(new { phase = "DownloadStart", failed = 2 }, wire),
            JsonSerializer.Serialize(new AppInstallerPhaseCount { Phase = "DownloadStart", Failed = 2 }, wire));

        Assert.Equal(
            JsonSerializer.Serialize(new
            {
                code = "0x80070005",
                exitCode = (int?)5,
                count = 2,
                sampleMessage = "Access denied",
            }, wire),
            JsonSerializer.Serialize(new AppAnalyticsFailureCode
            {
                Code = "0x80070005",
                ExitCode = 5,
                Count = 2,
                SampleMessage = "Access denied",
            }, wire));

        Assert.Equal(
            JsonSerializer.Serialize(new
            {
                manufacturer = "Contoso",
                model = "Book 3",
                installs = 8,
                failed = 2,
                failureRate = 25.0,
                liftVsBaseline = 1.5,
            }, wire),
            JsonSerializer.Serialize(new AppDeviceModelBreakdownItem
            {
                Manufacturer = "Contoso",
                Model = "Book 3",
                Installs = 8,
                Failed = 2,
                FailureRate = 25.0,
                LiftVsBaseline = 1.5,
            }, wire));
    }

    [Fact]
    public void AppSessionsResponse_matches_the_anonymous_shape()
    {
        var started = new DateTime(2026, 8, 30, 7, 30, 0, DateTimeKind.Utc);
        AssertWireIdentical(
            new
            {
                success = true,
                total = 1,
                offset = 0,
                limit = 50,
                items = new[]
                {
                    new
                    {
                        sessionId = "22222222-2222-2222-2222-222222222222",
                        tenantId = "11111111-1111-1111-1111-111111111111",
                        deviceName = "DESKTOP-1",
                        manufacturer = "Contoso",
                        model = "Book 3",
                        appVersion = "1.2.3",
                        status = "Failed",
                        installerPhase = "Install",
                        failureCode = "0x80070005",
                        exitCode = (int?)5,
                        attemptNumber = 2,
                        startedAt = started,
                        durationSeconds = 90,
                        installPassCount = 2,
                    },
                },
            },
            new AppSessionsResponse
            {
                Success = true,
                Total = 1,
                Offset = 0,
                Limit = 50,
                Items = new[]
                {
                    new AppSessionItem
                    {
                        SessionId = "22222222-2222-2222-2222-222222222222",
                        TenantId = "11111111-1111-1111-1111-111111111111",
                        DeviceName = "DESKTOP-1",
                        Manufacturer = "Contoso",
                        Model = "Book 3",
                        AppVersion = "1.2.3",
                        Status = "Failed",
                        InstallerPhase = "Install",
                        FailureCode = "0x80070005",
                        ExitCode = 5,
                        AttemptNumber = 2,
                        StartedAt = started,
                        DurationSeconds = 90,
                        InstallPassCount = 2,
                    },
                },
            });
    }
}

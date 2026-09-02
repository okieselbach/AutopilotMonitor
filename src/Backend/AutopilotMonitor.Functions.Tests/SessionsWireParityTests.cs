using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Wire-identity proof for the Sessions function folder (anonymous-object → typed-DTO
/// migration). Each fact serializes the OLD anonymous literal exactly as it stood at the
/// call site (copied from the pre-migration code, filled with realistic sample values)
/// against the NEW DTO carrying the same values, via
/// <see cref="ApiResponseWireParityTests.AssertWireIdentical"/> — key names, key order and
/// key presence/absence (WhenWritingNull) must match ordinally. Nullable slots additionally
/// get a null case proving the key vanishes identically on both sides.
/// </summary>
public class SessionsWireParityTests
{
    private static SessionSummary SampleSummary(string sessionId) => new SessionSummary
    {
        SessionId = sessionId,
        TenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d",
        SerialNumber = "SN-0042",
        DeviceName = "DESKTOP-CONTOSO1",
        Manufacturer = "Fabrikam Inc.",
        Model = "Latitude 9999",
        StartedAt = new DateTime(2026, 8, 30, 11, 15, 0, DateTimeKind.Utc),
        CurrentPhase = 3,
        CurrentPhaseDetail = "Device Setup",
        Status = SessionStatus.InProgress,
        FailureReason = string.Empty,
    };

    // ---- GetSessions / GetAllSessions / SearchSessionsByCve / SearchSessionsByEvent ------

    [Fact]
    public void SessionListResponse_matches_the_paged_listing_shape()
    {
        IReadOnlyList<SessionSummary> items = new List<SessionSummary>
        {
            SampleSummary("0b6f7a37-1111-4d61-9c93-0aa111111111"),
            SampleSummary("0b6f7a37-2222-4d61-9c93-0aa222222222"),
        };
        string? nextLink = "/api/sessions?pageSize=2&continuation=abc";

        AssertParity(
            new
            {
                success = true,
                count = items.Count,
                sessions = items,
                nextLink,
            },
            new SessionListResponse
            {
                Success = true,
                Count = items.Count,
                Sessions = items,
                NextLink = nextLink,
            });
    }

    [Fact]
    public void SessionListResponse_omits_nextLink_on_the_last_page()
    {
        IReadOnlyList<SessionSummary> items = new List<SessionSummary>();
        string? nextLink = null;

        AssertParity(
            new
            {
                success = true,
                count = items.Count,
                sessions = items,
                nextLink,
            },
            new SessionListResponse
            {
                Success = true,
                Count = items.Count,
                Sessions = items,
                NextLink = null,
            });
    }

    // ---- SearchSessions ------------------------------------------------------------------

    [Fact]
    public void SearchSessionsResponse_matches_the_unprojected_shape()
    {
        IReadOnlyList<SessionSummary> pageItems = new List<SessionSummary>
        {
            SampleSummary("3f1c9d55-aaaa-4e0e-8888-52f3aaaa0001"),
        };
        // Old site: `object sessionsPayload` — either the full items or the fields= projection.
        object sessionsPayload = pageItems;
        string? nextLink = "/api/search/sessions?pageSize=1&continuation=xyz";

        AssertParity(
            new
            {
                success = true,
                count = pageItems.Count,
                sessions = sessionsPayload,
                nextLink,
            },
            new SearchSessionsResponse
            {
                Success = true,
                Count = pageItems.Count,
                Sessions = pageItems,
                NextLink = nextLink,
            });
    }

    [Fact]
    public void SearchSessionsResponse_matches_the_fields_projected_shape_and_omits_null_nextLink()
    {
        var projected = new List<Dictionary<string, object?>>
        {
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["sessionId"] = "3f1c9d55-bbbb-4e0e-8888-52f3aaaa0002",
                ["status"] = "Failed",
                ["startedAt"] = new DateTime(2026, 8, 29, 7, 0, 0, DateTimeKind.Utc),
            },
        };
        object sessionsPayload = projected;
        string? nextLink = null;

        AssertParity(
            new
            {
                success = true,
                count = 1,
                sessions = sessionsPayload,
                nextLink,
            },
            new SearchSessionsResponse
            {
                Success = true,
                Count = 1,
                Sessions = projected,
                NextLink = null,
            });
    }

    // ---- GetSessionStats / GetAllSessionStats --------------------------------------------

    [Fact]
    public void SessionStatsResponse_matches_the_stats_shape()
    {
        var stats = new SessionStats
        {
            Days = 7,
            ActiveCount = 3,
            TotalLastNDays = 42,
            SucceededLastNDays = 30,
            FailedLastNDays = 5,
            IncompleteLastNDays = 7,
            SuccessRatePct = 86,
            AvgDurationMinutes = 55,
            MedianDurationMinutes = 48,
            P90DurationMinutes = 92,
            TotalToday = 4,
            FailedToday = 1,
            ComputedAt = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc),
        };

        AssertParity(
            new { success = true, stats },
            new SessionStatsResponse { Success = true, Stats = stats });
    }

    // ---- GetSession ----------------------------------------------------------------------

    [Fact]
    public void GetSessionResponse_matches_the_single_session_shape()
    {
        var session = SampleSummary("9d2f5f70-cccc-4f31-9f1e-52f3aaaa0003");

        AssertParity(
            new
            {
                success = true,
                session
            },
            new GetSessionResponse
            {
                Success = true,
                Session = session
            });
    }

    // ---- GetSessionEvents (unpaginated + paginated) --------------------------------------

    [Fact]
    public void GetSessionEventsResponse_matches_the_unpaginated_shape_without_a_nextLink_key()
    {
        var sessionId = "5c8e1b20-dddd-49a2-8bd1-52f3aaaa0004";
        // EventFieldProjection.Project returns List<object> (full events or dictionary projections).
        var events = new List<object>
        {
            new EnrollmentEvent
            {
                EventId = "evt-0001",
                SessionId = sessionId,
                EventType = "phase_transition",
                Timestamp = new DateTime(2026, 8, 30, 11, 20, 0, DateTimeKind.Utc),
            },
            new Dictionary<string, object?> { ["eventType"] = "app_install_start" },
        };

        AssertParity(
            new
            {
                success = true,
                sessionId,
                count = events.Count,
                events,
            },
            new GetSessionEventsResponse
            {
                Success = true,
                SessionId = sessionId,
                Count = events.Count,
                Events = events,
                // NextLink stays null → the key is absent, exactly like the old literal
                // that never declared it.
            });
    }

    [Fact]
    public void GetSessionEventsResponse_matches_the_paginated_shape_with_a_nextLink()
    {
        var sessionId = "5c8e1b20-eeee-49a2-8bd1-52f3aaaa0005";
        var events = new List<object>();
        string? nextLink = "/api/sessions/5c8e1b20-eeee-49a2-8bd1-52f3aaaa0005/events?pageSize=100&continuation=tok";

        AssertParity(
            new
            {
                success = true,
                sessionId,
                count = events.Count,
                events,
                nextLink,
            },
            new GetSessionEventsResponse
            {
                Success = true,
                SessionId = sessionId,
                Count = events.Count,
                Events = events,
                NextLink = nextLink,
            });
    }

    [Fact]
    public void GetSessionEventsResponse_omits_a_null_nextLink_on_the_paginated_path()
    {
        var sessionId = "5c8e1b20-ffff-49a2-8bd1-52f3aaaa0006";
        var events = new List<object>();
        string? nextLink = null;

        AssertParity(
            new
            {
                success = true,
                sessionId,
                count = events.Count,
                events,
                nextLink,
            },
            new GetSessionEventsResponse
            {
                Success = true,
                SessionId = sessionId,
                Count = events.Count,
                Events = events,
                NextLink = null,
            });
    }

    // ---- GetSessionSignals ---------------------------------------------------------------

    [Fact]
    public void GetSessionSignalsResponse_matches_the_signal_log_shape()
    {
        var sessionId = "7aa20c11-0001-4b7c-a1d2-52f3aaaa0007";
        var signals = new List<SignalRecord>
        {
            new SignalRecord
            {
                TenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d",
                SessionId = sessionId,
                SessionSignalOrdinal = 1,
                SessionTraceOrdinal = 5,
                Kind = "EspPhaseObserved",
                KindSchemaVersion = 1,
                OccurredAtUtc = new DateTime(2026, 8, 30, 11, 25, 0, DateTimeKind.Utc),
                SourceOrigin = "agent",
                PayloadJson = "{\"phase\":\"DeviceSetup\"}",
            },
        };

        AssertParity(
            new
            {
                success = true,
                sessionId,
                count = signals.Count,
                truncated = false,
                signals,
            },
            new GetSessionSignalsResponse
            {
                Success = true,
                SessionId = sessionId,
                Count = signals.Count,
                Truncated = false,
                Signals = signals,
            });
    }

    // ---- GetSessionReducerVerification ---------------------------------------------------

    [Fact]
    public void GetSessionReducerVerificationResponse_matches_the_report_shape()
    {
        var report = new ReducerVerificationReport
        {
            TenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d",
            SessionId = "8bb31d22-0002-4b7c-a1d2-52f3aaaa0008",
            SignalCount = 12,
            TransitionCount = 11,
            StoredReducerVersion = "r-2026.08",
            CurrentReducerVersion = "r-2026.08",
            SignalOrdinalsContiguous = true,
            SignalOrdinalFirst = 1,
            SignalOrdinalLast = 12,
            StepIndicesContiguous = true,
            StepIndexFirst = 0,
            StepIndexLast = 10,
            SemanticReplayPerformed = true,
            SemanticReplayFinalStageMatches = true,
            ReplayedFinalStage = "Completed",
            Issues = new List<VerificationIssue>
            {
                new VerificationIssue { Severity = "Info", Kind = "empty_session", Message = "sample" },
            },
        };

        AssertParity(
            new
            {
                success = true,
                truncated = false,
                report,
            },
            new GetSessionReducerVerificationResponse
            {
                Success = true,
                Truncated = false,
                Report = report,
            });
    }

    // ---- GetSessionDecisionGraph ---------------------------------------------------------

    [Fact]
    public void GetSessionDecisionGraphResponse_matches_the_graph_shape()
    {
        var projection = new DecisionGraphProjection
        {
            TenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d",
            SessionId = "9cc42e33-0003-4b7c-a1d2-52f3aaaa0009",
            Nodes = new List<DecisionGraphNode>
            {
                new DecisionGraphNode { Id = "EspInProgress", IsTerminal = false, VisitCount = 1 },
            },
            Edges = new List<DecisionGraphEdge>
            {
                new DecisionGraphEdge
                {
                    StepIndex = 0,
                    FromStage = "Registered",
                    ToStage = "EspInProgress",
                    Trigger = "EspPhaseObserved",
                    Taken = true,
                    SignalOrdinalRef = 1,
                    OccurredAtUtc = new DateTime(2026, 8, 30, 11, 26, 0, DateTimeKind.Utc),
                },
            },
            ReducerVersion = "r-2026.08",
        };

        AssertParity(
            new
            {
                success = true,
                truncated = false,
                graph = projection,
            },
            new GetSessionDecisionGraphResponse
            {
                Success = true,
                Truncated = false,
                Graph = projection,
            });
    }

    // ---- GetSessionDeletionsList ---------------------------------------------------------

    [Fact]
    public void GetSessionDeletionsListResponse_matches_the_deletion_listing_shape()
    {
        var state = "Queued";
        int? strandedSinceMinutes = 30;
        var tenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d";
        var sessionId = "add53f44-0004-4b7c-a1d2-52f3aaaa0010";
        var deletionState = "Queued";
        var manifestId = "m-20260830-01";
        var timestamp = new DateTime(2026, 8, 30, 10, 0, 0, DateTimeKind.Utc).ToString("o");
        var ageMinutes = 65;

        var anonymousSessions = new List<object>
        {
            new
            {
                tenantId,
                sessionId,
                deletionState,
                manifestId,
                timestamp,
                ageMinutes,
            },
        };
        var typedSessions = new List<SessionDeletionListItem>
        {
            new SessionDeletionListItem
            {
                TenantId = tenantId,
                SessionId = sessionId,
                DeletionState = deletionState,
                ManifestId = manifestId,
                Timestamp = timestamp,
                AgeMinutes = ageMinutes,
            },
        };

        AssertParity(
            new
            {
                success = true,
                state,
                strandedSinceMinutes,
                count = anonymousSessions.Count,
                sessions = anonymousSessions,
            },
            new GetSessionDeletionsListResponse
            {
                Success = true,
                State = state,
                StrandedSinceMinutes = strandedSinceMinutes,
                Count = typedSessions.Count,
                Sessions = typedSessions,
            });
    }

    [Fact]
    public void GetSessionDeletionsListResponse_omits_a_null_strandedSinceMinutes()
    {
        var state = "Poisoned";
        int? strandedSinceMinutes = null;
        var sessions = new List<object>();

        AssertParity(
            new
            {
                success = true,
                state,
                strandedSinceMinutes,
                count = sessions.Count,
                sessions,
            },
            new GetSessionDeletionsListResponse
            {
                Success = true,
                State = state,
                StrandedSinceMinutes = null,
                Count = 0,
                Sessions = new List<SessionDeletionListItem>(),
            });
    }

    // ---- GetTenantDeletionManifests ------------------------------------------------------

    [Fact]
    public void GetTenantDeletionManifestsResponse_matches_the_manifest_tree_shape()
    {
        var tenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d";
        string? sessionFilter = "bee64055-0005-4b7c-a1d2-52f3aaaa0011";
        var manifestId = "m-20260829-02";
        long sizeBytes = 20480;
        var lastModifiedUtc = new DateTime(2026, 8, 29, 18, 30, 0, DateTimeKind.Utc).ToString("o");

        var anonymousManifests = new List<object>
        {
            new
            {
                manifestId,
                sizeBytes,
                lastModifiedUtc,
            },
        };
        var anonymousSessions = new List<object>
        {
            new
            {
                sessionId = sessionFilter,
                manifestCount = anonymousManifests.Count,
                latestManifestUtc = lastModifiedUtc,
                manifests = anonymousManifests,
            },
        };

        var typedManifests = new List<TenantDeletionManifestItem>
        {
            new TenantDeletionManifestItem
            {
                ManifestId = manifestId,
                SizeBytes = sizeBytes,
                LastModifiedUtc = lastModifiedUtc,
            },
        };
        var typedSessions = new List<TenantDeletionManifestSessionNode>
        {
            new TenantDeletionManifestSessionNode
            {
                SessionId = sessionFilter!,
                ManifestCount = typedManifests.Count,
                LatestManifestUtc = lastModifiedUtc,
                Manifests = typedManifests,
            },
        };

        AssertParity(
            new
            {
                success = true,
                tenantId,
                sessionFilter,
                sessionCount = anonymousSessions.Count,
                manifestCount = 1,
                sessions = anonymousSessions,
            },
            new GetTenantDeletionManifestsResponse
            {
                Success = true,
                TenantId = tenantId,
                SessionFilter = sessionFilter,
                SessionCount = typedSessions.Count,
                ManifestCount = 1,
                Sessions = typedSessions,
            });
    }

    [Fact]
    public void GetTenantDeletionManifestsResponse_omits_a_null_sessionFilter()
    {
        var tenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d";
        string? sessionFilter = null;
        var sessions = new List<object>();

        AssertParity(
            new
            {
                success = true,
                tenantId,
                sessionFilter,
                sessionCount = sessions.Count,
                manifestCount = 0,
                sessions,
            },
            new GetTenantDeletionManifestsResponse
            {
                Success = true,
                TenantId = tenantId,
                SessionFilter = null,
                SessionCount = 0,
                ManifestCount = 0,
                Sessions = new List<TenantDeletionManifestSessionNode>(),
            });
    }

    // ---- GetTenantsWithDeletionManifests -------------------------------------------------

    [Fact]
    public void GetTenantsWithDeletionManifestsResponse_matches_the_tenant_id_listing_shape()
    {
        var sorted = new List<string>
        {
            "0a0a35a2-30b2-4f2f-9a1b-6d9f1a2b3c01",
            "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d",
        };

        AssertParity(
            new
            {
                success = true,
                count = sorted.Count,
                tenantIds = sorted,
            },
            new GetTenantsWithDeletionManifestsResponse
            {
                Success = true,
                Count = sorted.Count,
                TenantIds = sorted,
            });
    }

    // ---- QuickSearchSessions -------------------------------------------------------------

    [Fact]
    public void QuickSearchSessionsResponse_matches_the_typeahead_shape()
    {
        var results = new List<QuickSearchResult>
        {
            new QuickSearchResult
            {
                SessionId = "cff75166-0006-4b7c-a1d2-52f3aaaa0012",
                SerialNumber = "SN-0042",
                DeviceName = "DESKTOP-CONTOSO1",
                Status = SessionStatus.Succeeded,
                StartedAt = new DateTime(2026, 8, 30, 9, 0, 0, DateTimeKind.Utc),
                MatchedField = "serialNumber",
            },
        };

        AssertParity(
            new
            {
                success = true,
                count = results.Count,
                results
            },
            new QuickSearchSessionsResponse
            {
                Success = true,
                Count = results.Count,
                Results = results
            });
    }

    // ---- GetSessionDeletePreview (mode=summary) ------------------------------------------

    [Fact]
    public void GetSessionDeletePreviewResponse_matches_the_summary_shape()
    {
        string? inFlightHint = "Cascade in progress (state=Running, manifestId=m-1); preview shows current data anyway.";
        var preflightCounts = new Dictionary<string, int> { ["Sessions"] = 1, ["Events"] = 250 };
        var anonymousSampleKeys = new Dictionary<string, List<object>>(StringComparer.Ordinal)
        {
            ["Events"] = new List<object> { new { pk = "tenant_session", rk = "0000000000000000001" } },
        };
        var typedSampleKeys = new Dictionary<string, List<DeletionRowKeySample>>(StringComparer.Ordinal)
        {
            ["Events"] = new List<DeletionRowKeySample>
            {
                new DeletionRowKeySample { Pk = "tenant_session", Rk = "0000000000000000001" },
            },
        };
        long totalRowCount = 251;
        long estimatedBytes = 40960;
        long builderDurationMs = 320;
        var schemaHash = "sha256:abcd";
        var manifestId = "m-20260830-03";

        AssertParity(
            new
            {
                success = true,
                mode = "summary",
                inFlightHint,
                preflightCounts,
                sampleKeys = anonymousSampleKeys,
                estimatedRowCount = totalRowCount,
                estimatedSnapshotBytes = estimatedBytes,
                builderDurationMs,
                schemaHash,
                manifestId,
            },
            new GetSessionDeletePreviewResponse
            {
                Success = true,
                Mode = "summary",
                InFlightHint = inFlightHint,
                PreflightCounts = preflightCounts,
                SampleKeys = typedSampleKeys,
                EstimatedRowCount = totalRowCount,
                EstimatedSnapshotBytes = estimatedBytes,
                BuilderDurationMs = builderDurationMs,
                SchemaHash = schemaHash,
                ManifestId = manifestId,
            });
    }

    [Fact]
    public void GetSessionDeletePreviewResponse_omits_a_null_inFlightHint()
    {
        string? inFlightHint = null;
        var preflightCounts = new Dictionary<string, int>();
        var sampleKeys = new Dictionary<string, List<object>>(StringComparer.Ordinal);

        AssertParity(
            new
            {
                success = true,
                mode = "summary",
                inFlightHint,
                preflightCounts,
                sampleKeys,
                estimatedRowCount = 0L,
                estimatedSnapshotBytes = -1L,
                builderDurationMs = 12L,
                schemaHash = "sha256:abcd",
                manifestId = "m-20260830-04",
            },
            new GetSessionDeletePreviewResponse
            {
                Success = true,
                Mode = "summary",
                InFlightHint = null,
                PreflightCounts = preflightCounts,
                SampleKeys = new Dictionary<string, List<DeletionRowKeySample>>(StringComparer.Ordinal),
                EstimatedRowCount = 0,
                EstimatedSnapshotBytes = -1,
                BuilderDurationMs = 12,
                SchemaHash = "sha256:abcd",
                ManifestId = "m-20260830-04",
            });
    }

    // ---- GetSessionDeletionManifest (mode=summary) ---------------------------------------

    [Fact]
    public void GetSessionDeletionManifestResponse_matches_the_stored_summary_shape_with_progress()
    {
        var manifestId = "m-20260830-05";
        var schemaHash = "sha256:ef01";
        var snapshotSha = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
        var preflightCounts = new Dictionary<string, int> { ["Sessions"] = 1 };
        var anonymousSampleKeys = new Dictionary<string, List<object>>(StringComparer.Ordinal)
        {
            ["Sessions"] = new List<object> { new { pk = "6a6a35a2", rk = "sess-1" } },
        };
        var typedSampleKeys = new Dictionary<string, List<DeletionRowKeySample>>(StringComparer.Ordinal)
        {
            ["Sessions"] = new List<DeletionRowKeySample> { new DeletionRowKeySample { Pk = "6a6a35a2", Rk = "sess-1" } },
        };
        var completedSteps = new HashSet<int> { 0, 1, 2 };
        DateTime? completedAt = new DateTime(2026, 8, 30, 12, 5, 0, DateTimeKind.Utc);

        AssertParity(
            new
            {
                success = true,
                mode = "summary",
                source = "stored",
                manifestId,
                schemaHash,
                snapshotSha256 = snapshotSha,
                estimatedRowCount = 1L,
                estimatedSnapshotBytes = 2048L,
                preflightCounts,
                sampleKeys = anonymousSampleKeys,
                progress = new
                {
                    SnapshotSha256 = snapshotSha,
                    completedStepOrders = completedSteps,
                    VerificationDone = true,
                    TombstoneStarted = true,
                    CompletedAt = completedAt,
                    aggregateDecrementsApplied = 2,
                    restoreReIncrementsApplied = 0,
                    LastFailureType = (string?)"ResidualRows",
                    LastFailureMessage = (string?)"3 residual rows observed",
                    LastObservedResidualCount = (int?)3,
                    LastResidualSampleJson = (string?)"[{\"pk\":\"a\",\"rk\":\"b\"}]",
                },
            },
            new GetSessionDeletionManifestResponse
            {
                Success = true,
                Mode = "summary",
                Source = "stored",
                ManifestId = manifestId,
                SchemaHash = schemaHash,
                SnapshotSha256 = snapshotSha,
                EstimatedRowCount = 1,
                EstimatedSnapshotBytes = 2048,
                PreflightCounts = preflightCounts,
                SampleKeys = typedSampleKeys,
                Progress = new SessionDeletionProgressWire
                {
                    SnapshotSha256 = snapshotSha,
                    CompletedStepOrders = completedSteps,
                    VerificationDone = true,
                    TombstoneStarted = true,
                    CompletedAt = completedAt,
                    AggregateDecrementsApplied = 2,
                    RestoreReIncrementsApplied = 0,
                    LastFailureType = "ResidualRows",
                    LastFailureMessage = "3 residual rows observed",
                    LastObservedResidualCount = 3,
                    LastResidualSampleJson = "[{\"pk\":\"a\",\"rk\":\"b\"}]",
                },
            });
    }

    [Fact]
    public void GetSessionDeletionManifestResponse_omits_null_slots_inside_progress()
    {
        var snapshotSha = "aa86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
        var completedSteps = new HashSet<int> { 0 };

        AssertParity(
            new
            {
                success = true,
                mode = "summary",
                source = "stored",
                manifestId = "m-20260830-06",
                schemaHash = "sha256:ef02",
                snapshotSha256 = snapshotSha,
                estimatedRowCount = 0L,
                estimatedSnapshotBytes = 512L,
                preflightCounts = new Dictionary<string, int>(),
                sampleKeys = new Dictionary<string, List<object>>(StringComparer.Ordinal),
                progress = new
                {
                    SnapshotSha256 = snapshotSha,
                    completedStepOrders = completedSteps,
                    VerificationDone = false,
                    TombstoneStarted = false,
                    CompletedAt = (DateTime?)null,
                    aggregateDecrementsApplied = 0,
                    restoreReIncrementsApplied = 0,
                    LastFailureType = (string?)null,
                    LastFailureMessage = (string?)null,
                    LastObservedResidualCount = (int?)null,
                    LastResidualSampleJson = (string?)null,
                },
            },
            new GetSessionDeletionManifestResponse
            {
                Success = true,
                Mode = "summary",
                Source = "stored",
                ManifestId = "m-20260830-06",
                SchemaHash = "sha256:ef02",
                SnapshotSha256 = snapshotSha,
                EstimatedRowCount = 0,
                EstimatedSnapshotBytes = 512,
                PreflightCounts = new Dictionary<string, int>(),
                SampleKeys = new Dictionary<string, List<DeletionRowKeySample>>(StringComparer.Ordinal),
                Progress = new SessionDeletionProgressWire
                {
                    SnapshotSha256 = snapshotSha,
                    CompletedStepOrders = completedSteps,
                    VerificationDone = false,
                    TombstoneStarted = false,
                    CompletedAt = null,
                    AggregateDecrementsApplied = 0,
                    RestoreReIncrementsApplied = 0,
                    LastFailureType = null,
                    LastFailureMessage = null,
                    LastObservedResidualCount = null,
                    LastResidualSampleJson = null,
                },
            });
    }

    [Fact]
    public void GetSessionDeletionManifestResponse_omits_a_null_progress_object()
    {
        var snapshotSha = "bb86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
        // Old site: `progress = progress == null ? null : new { ... }` — the whole key vanishes.
        object? progress = null;

        AssertParity(
            new
            {
                success = true,
                mode = "summary",
                source = "stored",
                manifestId = "m-20260830-07",
                schemaHash = "sha256:ef03",
                snapshotSha256 = snapshotSha,
                estimatedRowCount = 0L,
                estimatedSnapshotBytes = 256L,
                preflightCounts = new Dictionary<string, int>(),
                sampleKeys = new Dictionary<string, List<object>>(StringComparer.Ordinal),
                progress,
            },
            new GetSessionDeletionManifestResponse
            {
                Success = true,
                Mode = "summary",
                Source = "stored",
                ManifestId = "m-20260830-07",
                SchemaHash = "sha256:ef03",
                SnapshotSha256 = snapshotSha,
                EstimatedRowCount = 0,
                EstimatedSnapshotBytes = 256,
                PreflightCounts = new Dictionary<string, int>(),
                SampleKeys = new Dictionary<string, List<DeletionRowKeySample>>(StringComparer.Ordinal),
                Progress = null,
            });
    }

    // ---- MarkSessionFailed / MarkSessionSucceeded ----------------------------------------

    [Fact]
    public void MarkSessionFailed_success_body_matches_SuccessMessageResponse()
    {
        var sessionId = "dee86277-0007-4b7c-a1d2-52f3aaaa0013";

        AssertParity(
            new
            {
                success = true,
                message = $"Session {sessionId} marked as failed"
            },
            new SuccessMessageResponse
            {
                Success = true,
                Message = $"Session {sessionId} marked as failed"
            });
    }

    [Fact]
    public void MarkSessionSucceeded_success_body_matches_SuccessMessageResponse()
    {
        var sessionId = "eff97388-0008-4b7c-a1d2-52f3aaaa0014";

        AssertParity(
            new
            {
                success = true,
                message = $"Session {sessionId} marked as succeeded"
            },
            new SuccessMessageResponse
            {
                Success = true,
                Message = $"Session {sessionId} marked as succeeded"
            });
    }

    // ---- QueueSessionAction --------------------------------------------------------------

    [Fact]
    public void QueueSessionActionResponse_matches_the_queue_acknowledgement_shape()
    {
        var actionType = "request_diagnostics";
        var queuedAt = new DateTime(2026, 8, 30, 13, 37, 0, DateTimeKind.Utc);

        AssertParity(
            new
            {
                success = true,
                message = $"Action '{actionType}' queued for delivery",
                queuedAt
            },
            new QueueSessionActionResponse
            {
                Success = true,
                Message = $"Action '{actionType}' queued for delivery",
                QueuedAt = queuedAt
            });
    }

    // ---- Delegated (MSP) MCP fleet aggregate: quota-excluded tenants ---------------------

    [Fact]
    public void SessionListResponse_carries_quotaExcludedTenants_on_a_narrowed_fleet_aggregate()
    {
        IReadOnlyList<SessionSummary> items = new List<SessionSummary> { SampleSummary("0b6f7a37-1111-4d61-9c93-0aa111111111") };
        string? nextLink = null;
        var quotaExcludedTenants = new[] { "7aa20c11-0002-4b7c-a1d2-52f3aaaa0002" };

        AssertParity(
            new
            {
                success = true,
                count = items.Count,
                sessions = items,
                nextLink,
                quotaExcludedTenants,
            },
            new SessionListResponse
            {
                Success = true,
                Count = items.Count,
                Sessions = items,
                NextLink = null,
                QuotaExcludedTenants = quotaExcludedTenants,
            });
    }

    [Fact]
    public void SessionStatsResponse_carries_quotaExcludedTenants_on_a_narrowed_fleet_aggregate()
    {
        var stats = new SessionStats { Days = 7, ComputedAt = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc) };
        var quotaExcludedTenants = new[] { "7aa20c11-0002-4b7c-a1d2-52f3aaaa0002", "7aa20c11-0002-4b7c-a1d2-52f3aaaa0003" };

        AssertParity(
            new { success = true, stats, quotaExcludedTenants },
            new SessionStatsResponse { Success = true, Stats = stats, QuotaExcludedTenants = quotaExcludedTenants });
    }

    private static void AssertParity(object anonymousLiteral, IApiResponse typed)
        => ApiResponseWireParityTests.AssertWireIdentical(anonymousLiteral, typed);
}

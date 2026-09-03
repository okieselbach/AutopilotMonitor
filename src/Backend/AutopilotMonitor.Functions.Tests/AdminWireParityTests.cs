using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Offboarding;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Wire-identity proof for the Functions/Admin folder (anonymous-object → typed-DTO
/// migration). Each fact serializes the OLD anonymous literal exactly as it stood at the
/// call site (copied from the pre-migration code, filled with realistic sample values)
/// against the NEW DTO carrying the same values, via
/// <see cref="ApiResponseWireParityTests.AssertWireIdentical"/> — key names, key order and
/// key presence/absence (WhenWritingNull) must match ordinally. Nullable slots additionally
/// get a null case proving the key vanishes identically on both sides.
/// </summary>
public class AdminWireParityTests
{
    private const string SampleTenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d";

    // ---- GetActiveUsers ------------------------------------------------------------------

    [Fact]
    public void GetActiveUsersResponse_matches_the_presence_listing_shape()
    {
        var lastSeen = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        var anonymousUsers = new[]
        {
            new
            {
                tenantId = SampleTenantId,
                upn = "admin@contoso.com",
                userRole = "GlobalAdmin",
                lastSeen,
                secondsAgo = 42
            },
        }.ToList();
        var typedUsers = new List<ActiveUserItem>
        {
            new ActiveUserItem
            {
                TenantId = SampleTenantId,
                Upn = "admin@contoso.com",
                UserRole = "GlobalAdmin",
                LastSeen = lastSeen,
                SecondsAgo = 42
            },
        };

        AssertParity(
            new
            {
                success = true,
                windowMinutes = 5,
                activeCount = anonymousUsers.Count,
                users = anonymousUsers
            },
            new GetActiveUsersResponse
            {
                Success = true,
                WindowMinutes = 5,
                ActiveCount = typedUsers.Count,
                Users = typedUsers
            });
    }

    // ---- GetAuditLogs / GetGlobalAuditLogs (non-paged + paged share one DTO) -------------

    private static AuditLogEntry SampleAuditLog() => new AuditLogEntry
    {
        Id = "20260830120000000_0001",
        TenantId = SampleTenantId,
        Action = "CREATE",
        EntityType = "DeviceBlock",
        EntityId = "SN-0042",
        PerformedBy = "admin@contoso.com",
        Timestamp = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc),
        Details = "{\"Action\":\"Block\"}",
    };

    [Fact]
    public void AuditLogListResponse_matches_the_non_paged_shape_without_a_nextLink_key()
    {
        var logs = new List<AuditLogEntry> { SampleAuditLog() };

        AssertParity(
            new { success = true, count = logs.Count, logs },
            new AuditLogListResponse
            {
                Success = true,
                Count = logs.Count,
                Logs = logs,
                // NextLink stays null → the key is absent, exactly like the old
                // non-paged literal that never declared it.
            });
    }

    [Fact]
    public void AuditLogListResponse_matches_the_paged_shape_with_a_nextLink()
    {
        IReadOnlyList<AuditLogEntry> items = new List<AuditLogEntry> { SampleAuditLog() };
        string? nextLink = "/api/audit/logs?pageSize=50&continuation=abc";

        AssertParity(
            new
            {
                success = true,
                count = items.Count,
                logs = items,
                nextLink,
            },
            new AuditLogListResponse
            {
                Success = true,
                Count = items.Count,
                Logs = items,
                NextLink = nextLink,
            });
    }

    [Fact]
    public void AuditLogListResponse_omits_a_null_nextLink_on_the_paged_path()
    {
        IReadOnlyList<AuditLogEntry> items = new List<AuditLogEntry>();
        string? nextLink = null;

        AssertParity(
            new
            {
                success = true,
                count = items.Count,
                logs = items,
                nextLink,
            },
            new AuditLogListResponse
            {
                Success = true,
                Count = items.Count,
                Logs = items,
                NextLink = null,
            });
    }

    // ---- GetOpsEvents (non-paged + paged share one DTO) ----------------------------------

    private static OpsEventEntry SampleOpsEvent() => new OpsEventEntry
    {
        Id = "20260830113000000_0007",
        Category = "Maintenance",
        EventType = "maintenance_completed",
        Severity = "Info",
        TenantId = SampleTenantId,
        UserId = "admin@contoso.com",
        Message = "Maintenance run completed",
        Details = "{\"durationMs\":1200}",
        Timestamp = new DateTime(2026, 8, 30, 11, 30, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void OpsEventListResponse_matches_the_non_paged_shape_without_a_nextLink_key()
    {
        var filtered = new List<OpsEventEntry> { SampleOpsEvent() };

        AssertParity(
            new { success = true, count = filtered.Count, events = filtered },
            new OpsEventListResponse
            {
                Success = true,
                Count = filtered.Count,
                Events = filtered,
            });
    }

    [Fact]
    public void OpsEventListResponse_matches_the_paged_shape_with_a_nextLink()
    {
        var pageItems = new List<OpsEventEntry> { SampleOpsEvent() };
        string? nextLink = "/api/global/ops-events?pageSize=100&continuation=tok";

        AssertParity(
            new
            {
                success = true,
                count = pageItems.Count,
                events = pageItems,
                nextLink,
            },
            new OpsEventListResponse
            {
                Success = true,
                Count = pageItems.Count,
                Events = pageItems,
                NextLink = nextLink,
            });
    }

    [Fact]
    public void OpsEventListResponse_omits_a_null_nextLink_on_the_paged_path()
    {
        var pageItems = new List<OpsEventEntry>();
        string? nextLink = null;

        AssertParity(
            new
            {
                success = true,
                count = pageItems.Count,
                events = pageItems,
                nextLink,
            },
            new OpsEventListResponse
            {
                Success = true,
                Count = pageItems.Count,
                Events = pageItems,
                NextLink = null,
            });
    }

    // ---- GetAutopilotDeviceValidationConsentUrl ------------------------------------------

    [Fact]
    public void AutopilotConsentUrlResponse_matches_the_consent_url_shape()
    {
        var consentUrl = "https://login.example/6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d/adminconsent?client_id=x";
        var funnel = true;

        AssertParity(
            new
            {
                consentUrl,
                willAutoFlipHoming = funnel
            },
            new AutopilotConsentUrlResponse
            {
                ConsentUrl = consentUrl,
                WillAutoFlipHoming = funnel
            });
    }

    // ---- GetAutopilotDeviceValidationConsentStatus ---------------------------------------

    [Fact]
    public void AutopilotConsentStatusResponse_matches_the_consent_status_shape()
    {
        string? message = "Consent verified";
        IReadOnlyList<string>? appHomingMissingRoles = null;

        AssertParity(
            new
            {
                isConsented = true,
                message,
                homingFlipped = false,
                appHomingPending = true,
                appHomingMissingRoles,
            },
            new AutopilotConsentStatusResponse
            {
                IsConsented = true,
                Message = message,
                HomingFlipped = false,
                AppHomingPending = true,
                AppHomingMissingRoles = null,
            });
    }

    [Fact]
    public void AutopilotConsentStatusResponse_omits_a_null_message()
    {
        string? message = null;
        IReadOnlyList<string>? appHomingMissingRoles = null;

        AssertParity(
            new
            {
                isConsented = false,
                message,
                homingFlipped = false,
                appHomingPending = false,
                appHomingMissingRoles,
            },
            new AutopilotConsentStatusResponse
            {
                IsConsented = false,
                Message = null,
                HomingFlipped = false,
                AppHomingPending = false,
                AppHomingMissingRoles = null,
            });
    }

    [Fact]
    public void AutopilotConsentStatusResponse_carries_the_blocking_add_on_roles()
    {
        string? message = "Consent verified";
        var roles = new[] { "DeviceManagementScripts.Read.All" };

        AssertParity(
            new
            {
                isConsented = true,
                message,
                homingFlipped = false,
                appHomingPending = false,
                appHomingMissingRoles = roles,
            },
            new AutopilotConsentStatusResponse
            {
                IsConsented = true,
                Message = message,
                HomingFlipped = false,
                AppHomingPending = false,
                AppHomingMissingRoles = roles,
            });
    }

    // ---- GetAutopilotDeviceValidationAccessCheck -----------------------------------------

    [Fact]
    public void AutopilotAccessCheckResponse_matches_the_access_check_shape()
    {
        IReadOnlyList<string>? appHomingMissingRoles = null;

        AssertParity(
            new
            {
                accessPresent = true,
                isTransient = false,
                requiredPermission = "DeviceManagementServiceConfig.Read.All",
                homingFlipped = true,
                appHomingPending = false,
                appHomingMissingRoles,
            },
            new AutopilotAccessCheckResponse
            {
                AccessPresent = true,
                IsTransient = false,
                RequiredPermission = "DeviceManagementServiceConfig.Read.All",
                HomingFlipped = true,
                AppHomingPending = false,
                AppHomingMissingRoles = null,
            });
    }

    [Fact]
    public void AutopilotAccessCheckResponse_carries_the_blocking_add_on_roles()
    {
        var roles = new[] { "CloudPC.Read.All", "DeviceManagementScripts.Read.All" };

        AssertParity(
            new
            {
                accessPresent = true,
                isTransient = false,
                requiredPermission = "DeviceManagementServiceConfig.Read.All",
                homingFlipped = false,
                appHomingPending = false,
                appHomingMissingRoles = roles,
            },
            new AutopilotAccessCheckResponse
            {
                AccessPresent = true,
                IsTransient = false,
                RequiredPermission = "DeviceManagementServiceConfig.Read.All",
                HomingFlipped = false,
                AppHomingPending = false,
                AppHomingMissingRoles = roles,
            });
    }

    // ---- BackfillOccurredUtc / ReclassifyLegacySessions (shared envelope) ----------------

    [Fact]
    public void BackfillJobRunResponse_matches_the_backfill_shape()
    {
        var result = new BackfillResult
        {
            Table = "audit",
            DryRun = true,
            RowsExamined = 250,
            WouldWrite = 12,
            Written = 0,
            SkippedAlreadySet = 230,
            SkippedUndecodable = 8,
            Errors = 0,
            NextContinuation = "token123",
        };
        var userEmail = "admin@contoso.com";
        var triggeredAt = new DateTime(2026, 8, 30, 13, 0, 0, DateTimeKind.Utc);

        AssertParity(
            new
            {
                success = true,
                result,
                triggeredBy = userEmail,
                triggeredAt,
            },
            new BackfillJobRunResponse
            {
                Success = true,
                Result = result,
                TriggeredBy = userEmail,
                TriggeredAt = triggeredAt,
            });
    }

    [Fact]
    public void ReclassifyJobRunResponse_matches_the_reclassify_shape()
    {
        var result = new ReclassificationResult
        {
            Mode = "legacy_timeouts",
            DryRun = false,
            TenantsExamined = 3,
            SessionsExamined = 120,
            WouldChange = 0,
            Changed = 7,
            ToSucceeded = 5,
            ToIncomplete = 2,
            KeptFailed = 110,
            Skipped = 3,
            Errors = 0,
            CapReached = false,
        };
        var userEmail = "admin@contoso.com";
        var triggeredAt = new DateTime(2026, 8, 30, 13, 5, 0, DateTimeKind.Utc);

        AssertParity(
            new
            {
                success = true,
                result,
                triggeredBy = userEmail,
                triggeredAt,
            },
            new ReclassifyJobRunResponse
            {
                Success = true,
                Result = result,
                TriggeredBy = userEmail,
                TriggeredAt = triggeredAt,
            });
    }

    // ---- TriggerMaintenance --------------------------------------------------------------

    [Fact]
    public void TriggerMaintenanceResponse_matches_the_manual_trigger_shape()
    {
        var result = new MaintenanceResult
        {
            Success = true,
            TriggeredBy = "admin@contoso.com",
            TriggeredAt = new DateTime(2026, 8, 30, 13, 10, 0, DateTimeKind.Utc),
            DurationMs = 1234,
            StalledSessionsChecked = true,
            MetricsAggregated = true,
            AggregatedDate = "2026-08-29",
            DataCleanupExecuted = false,
            PlatformStatsRecomputed = true,
            DevicesBlockedForExcessiveData = 0,
            ContactEmailsBackfilled = 2,
        };
        var userEmail = "admin@contoso.com";
        var triggeredAt = new DateTime(2026, 8, 30, 13, 10, 5, DateTimeKind.Utc);

        AssertParity(
            new
            {
                success = true,
                message = "Maintenance tasks completed",
                result = result,
                triggeredBy = userEmail,
                triggeredAt
            },
            new TriggerMaintenanceResponse
            {
                Success = true,
                Message = "Maintenance tasks completed",
                Result = result,
                TriggeredBy = userEmail,
                TriggeredAt = triggeredAt
            });
    }

    // ---- CustomsArchive (list runs / list entries / get entry / delete run) --------------
    // The run/entry item classes moved verbatim (same property names + declaration order)
    // from CustomsArchiveQueryFunction.RunSummary/EntrySummary into the Shared models, so
    // the same instances serve as the item payload on both sides of the envelope proof.

    [Fact]
    public void CustomsArchiveRunListResponse_matches_the_run_listing_shape()
    {
        var runs = new List<CustomsArchiveRunSummary>
        {
            new CustomsArchiveRunSummary
            {
                PartitionKey = $"{SampleTenantId}_20260829120000",
                TenantId = SampleTenantId,
                HistoryRowKey = "20260829120000",
                ArchivedAt = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc),
                GatherRulesCount = 4,
                AnalyzeRulesCount = 2,
                ImeLogPatternsCount = 1,
            },
        };

        AssertParity(
            new { success = true, count = runs.Count, runs },
            new CustomsArchiveRunListResponse { Success = true, Count = runs.Count, Runs = runs });
    }

    [Fact]
    public void CustomsArchiveEntryListResponse_matches_the_entry_listing_shape()
    {
        var items = new List<CustomsArchiveEntrySummary>
        {
            new CustomsArchiveEntrySummary
            {
                PartitionKey = $"{SampleTenantId}_20260829120000",
                RowKey = "GatherRules_R0FUSEVSLTE",
                OriginalTable = "GatherRules",
                OriginalRowKey = "GATHER-1",
                ArchivedAt = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc),
                EntityJsonPreview = "{\"RuleId\":\"GATHER-1\"}",
            },
        };

        AssertParity(
            new { success = true, count = items.Count, entries = items },
            new CustomsArchiveEntryListResponse { Success = true, Count = items.Count, Entries = items });
    }

    [Fact]
    public void CustomsArchiveEntryResponse_matches_the_full_entry_shape()
    {
        var entry = new TenantOffboardingCustomsArchiveEntry
        {
            PartitionKey = $"{SampleTenantId}_20260829120000",
            RowKey = "AnalyzeRules_QU5BTFlaRS0x",
            TenantId = SampleTenantId,
            OriginalTable = "AnalyzeRules",
            OriginalPartitionKey = SampleTenantId,
            OriginalRowKey = "ANALYZE-1",
            EntityJson = "{\"RuleId\":\"ANALYZE-1\",\"Name\":\"Sample\"}",
            HistoryRowKey = "20260829120000",
            ArchivedAt = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc),
        };

        AssertParity(
            new { success = true, entry },
            new CustomsArchiveEntryResponse { Success = true, Entry = entry });
    }

    [Fact]
    public void CustomsArchiveDeleteEntry_success_body_matches_SuccessOnlyResponse()
    {
        AssertParity(
            new { success = true },
            new SuccessOnlyResponse { Success = true });
    }

    [Fact]
    public void CustomsArchiveDeleteRunResponse_matches_the_bulk_delete_shape()
    {
        var deleted = 7;

        AssertParity(
            new { success = true, deleted },
            new CustomsArchiveDeleteRunResponse { Success = true, Deleted = deleted });
    }

    // ---- DelegatedAdminManagement --------------------------------------------------------

    private static DelegatedAdminEntry SampleDelegatedAdmin() => new DelegatedAdminEntry
    {
        Upn = "msp@fabrikam.com",
        TenantId = SampleTenantId,
        Role = "DelegatedReader",
        IsEnabled = true,
        Status = "Active",
        Source = "OperatorGranted",
        GrantedAt = new DateTime(2026, 8, 28, 9, 0, 0, DateTimeKind.Utc),
        GrantedBy = "admin@contoso.com",
    };

    [Fact]
    public void DelegatedAdminListResponse_matches_the_assignments_shape()
    {
        var assignments = new List<DelegatedAdminEntry> { SampleDelegatedAdmin() };

        AssertParity(
            new { assignments },
            new DelegatedAdminListResponse { Assignments = assignments });
    }

    [Fact]
    public void DelegatedAdminGrantResponse_matches_the_single_assignment_shape()
    {
        var entry = SampleDelegatedAdmin();

        AssertParity(
            new { assignment = entry },
            new DelegatedAdminGrantResponse { Assignment = entry });
    }

    // ---- DeviceBlock / GetAllBlockedDevices ----------------------------------------------

    [Fact]
    public void BlockedDeviceListResponse_matches_the_blocked_listing_shape()
    {
        var blocked = new List<BlockedDeviceEntry>
        {
            new BlockedDeviceEntry
            {
                TenantId = SampleTenantId,
                SerialNumber = "SN-0042",
                BlockedAt = new DateTime(2026, 8, 30, 8, 0, 0, DateTimeKind.Utc),
                UnblockAt = new DateTime(2026, 8, 30, 20, 0, 0, DateTimeKind.Utc),
                BlockedByEmail = "admin@contoso.com",
                DurationHours = 12,
                Reason = "Excessive data",
                Action = "Block",
                BlockedSessionIds = null,
            },
        };

        AssertParity(
            new { success = true, blocked },
            new BlockedDeviceListResponse { Success = true, Blocked = blocked });
    }

    [Fact]
    public void BlockDeviceResponse_matches_the_block_acknowledgement_shape()
    {
        var serialNumber = "SN-0042";
        var durationHours = 12;
        var isKill = false;
        var action = "Block";
        var unblockAt = new DateTime(2026, 8, 30, 20, 0, 0, DateTimeKind.Utc);

        AssertParity(
            new
            {
                success = true,
                message = isKill
                    ? $"Device {serialNumber} issued remote kill signal for {durationHours} hours."
                    : $"Device {serialNumber} blocked for {durationHours} hours.",
                unblockAt,
                action
            },
            new BlockDeviceResponse
            {
                Success = true,
                Message = $"Device {serialNumber} blocked for {durationHours} hours.",
                UnblockAt = unblockAt,
                Action = action
            });
    }

    [Fact]
    public void UnblockDevice_success_body_matches_SuccessMessageResponse()
    {
        var serialNumber = "SN-0042";

        AssertParity(
            new { success = true, message = $"Device {serialNumber} unblocked." },
            new SuccessMessageResponse { Success = true, Message = $"Device {serialNumber} unblocked." });
    }

    // ---- EmailTemplates ------------------------------------------------------------------

    [Fact]
    public void EmailTemplateResponse_matches_the_effective_template_shape_with_an_override()
    {
        string? updatedBy = "admin@contoso.com";
        DateTime? updatedUtc = new DateTime(2026, 8, 30, 10, 0, 0, DateTimeKind.Utc);

        AssertParity(
            new
            {
                kind = "welcome",
                subject = "Welcome to Autopilot Monitor",
                isOverridden = true,
                html = "<html>override</html>",
                builtInHtml = "<html>{{domainName}}</html>",
                updatedBy,
                updatedUtc,
                placeholder = "{{domainName}}",
                maxLength = 30_000,
            },
            new EmailTemplateResponse
            {
                Kind = "welcome",
                Subject = "Welcome to Autopilot Monitor",
                IsOverridden = true,
                Html = "<html>override</html>",
                BuiltInHtml = "<html>{{domainName}}</html>",
                UpdatedBy = updatedBy,
                UpdatedUtc = updatedUtc,
                Placeholder = "{{domainName}}",
                MaxLength = 30_000,
            });
    }

    [Fact]
    public void EmailTemplateResponse_omits_updatedBy_and_updatedUtc_without_an_override()
    {
        string? updatedBy = null;
        DateTime? updatedUtc = null;

        AssertParity(
            new
            {
                kind = "farewell",
                subject = "Farewell from Autopilot Monitor",
                isOverridden = false,
                html = "<html>{{domainName}}</html>",
                builtInHtml = "<html>{{domainName}}</html>",
                updatedBy,
                updatedUtc,
                placeholder = "{{domainName}}",
                maxLength = 30_000,
            },
            new EmailTemplateResponse
            {
                Kind = "farewell",
                Subject = "Farewell from Autopilot Monitor",
                IsOverridden = false,
                Html = "<html>{{domainName}}</html>",
                BuiltInHtml = "<html>{{domainName}}</html>",
                UpdatedBy = null,
                UpdatedUtc = null,
                Placeholder = "{{domainName}}",
                MaxLength = 30_000,
            });
    }

    [Fact]
    public void EmailTemplateSaveResponse_matches_the_save_acknowledgement_shape()
    {
        var updatedUtc = new DateTime(2026, 8, 30, 10, 5, 0, DateTimeKind.Utc);

        AssertParity(
            new { kind = "welcome", isOverridden = true, updatedBy = "admin@contoso.com", updatedUtc },
            new EmailTemplateSaveResponse { Kind = "welcome", IsOverridden = true, UpdatedBy = "admin@contoso.com", UpdatedUtc = updatedUtc });
    }

    [Fact]
    public void EmailTemplateResetResponse_matches_the_reset_acknowledgement_shape()
    {
        AssertParity(
            new { kind = "farewell", isOverridden = false },
            new EmailTemplateResetResponse { Kind = "farewell", IsOverridden = false });
    }

    [Fact]
    public void EmailTemplateTestSendResponse_matches_the_test_send_shape()
    {
        AssertParity(
            new { sentTo = "it@contoso.com", domainName = "contoso.com", draft = true },
            new EmailTemplateTestSendResponse { SentTo = "it@contoso.com", DomainName = "contoso.com", Draft = true });
    }

    // ---- IdentityBindingManagement -------------------------------------------------------

    private static AdminIdentityBinding SampleBinding() => new AdminIdentityBinding
    {
        Upn = "msp@fabrikam.com",
        TenantId = "0a0a35a2-30b2-4f2f-9a1b-6d9f1a2b3c01",
        ObjectId = "b1b1b1b1-1111-4222-8333-444455556666",
        BoundBy = "admin@contoso.com",
        BoundAt = new DateTime(2026, 8, 28, 9, 0, 0, DateTimeKind.Utc),
        ObjectIdPinnedAt = new DateTime(2026, 8, 28, 9, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void IdentityBindingListResponse_matches_the_bindings_shape()
    {
        var bindings = new List<AdminIdentityBinding> { SampleBinding() };

        AssertParity(
            new { bindings },
            new IdentityBindingListResponse { Bindings = bindings });
    }

    [Fact]
    public void IdentityBindingResponse_matches_the_single_binding_shape()
    {
        var binding = SampleBinding();

        AssertParity(
            new { binding },
            new IdentityBindingResponse { Binding = binding });
    }

    // ---- ReseedFromGitHub ----------------------------------------------------------------

    [Fact]
    public void ReseedFromGitHubResponse_matches_the_reseed_report_shape()
    {
        AssertParity(
            new
            {
                success = true,
                message = "Reseed from GitHub complete",
                gather = new { deleted = 10, written = 12, orphanStatesGcd = 1, sunsetSkipped = 0 },
                analyze = new { deleted = 8, written = 8, orphanStatesGcd = 0, sunsetSkipped = 1 },
                ime = new { deleted = 79, written = 79 },
                cpeCommunityMappings = new { deleted = 5, written = 6 },
                cpeSeedMappings = new { deleted = 100, written = 100 }
            },
            new ReseedFromGitHubResponse
            {
                Success = true,
                Message = "Reseed from GitHub complete",
                Gather = new ReseedRuleCountsNode { Deleted = 10, Written = 12, OrphanStatesGcd = 1, SunsetSkipped = 0 },
                Analyze = new ReseedRuleCountsNode { Deleted = 8, Written = 8, OrphanStatesGcd = 0, SunsetSkipped = 1 },
                Ime = new ReseedTableCountsNode { Deleted = 79, Written = 79 },
                CpeCommunityMappings = new ReseedTableCountsNode { Deleted = 5, Written = 6 },
                CpeSeedMappings = new ReseedTableCountsNode { Deleted = 100, Written = 100 }
            });
    }

    // ---- SubmitOffboardingFeedback -------------------------------------------------------

    [Fact]
    public void SubmitOffboardingFeedback_success_body_matches_SuccessOnlyResponse()
    {
        AssertParity(
            new { success = true },
            new SuccessOnlyResponse { Success = true });
    }

    // ---- TenantAdminManagement (AddTenantAdmin) ------------------------------------------
    // DELIBERATE wire change (2026-08-31 entity-hygiene pass): the row is TenantAdminRow, not
    // the raw TenantAdminEntity — partitionKey/rowKey/eTag/timestamp are gone from the wire.
    // These pins fix the NEW shape exactly (key names, order, table-key absence).

    [Fact]
    public void TenantAdminCreatedResponse_pins_the_created_member_shape()
    {
        var newAdmin = new TenantAdminRow
        {
            TenantId = SampleTenantId,
            Upn = "operator@contoso.com",
            IsEnabled = true,
            AddedDate = new DateTime(2026, 8, 30, 14, 0, 0, DateTimeKind.Utc),
            AddedBy = "admin@contoso.com",
            Role = "Operator",
        };

        Assert.Equal(
            "{\"admin\":{"
            + $"\"tenantId\":\"{SampleTenantId}\","
            + "\"upn\":\"operator@contoso.com\","
            + "\"isEnabled\":true,"
            + "\"addedDate\":\"2026-08-30T14:00:00Z\","
            + "\"addedBy\":\"admin@contoso.com\","
            + "\"role\":\"Operator\","
            + "\"canManageBootstrapTokens\":false}}",
            TestWire.Serialize(new TenantAdminCreatedResponse { Admin = newAdmin }));
    }

    [Fact]
    public void TenantAdminRow_omits_a_null_role_and_never_emits_table_keys()
    {
        // Legacy pre-role rows have Role = null → the key vanishes (WhenWritingNull), and the
        // ITableEntity keys of the storage entity must never reappear on the wire.
        var row = new TenantAdminRow
        {
            TenantId = SampleTenantId,
            Upn = "legacy@contoso.com",
            IsEnabled = true,
            AddedDate = new DateTime(2026, 8, 30, 14, 5, 0, DateTimeKind.Utc),
            AddedBy = "admin@contoso.com",
            Role = null,
        };

        var json = TestWire.Serialize(row);
        Assert.Equal(
            $"{{\"tenantId\":\"{SampleTenantId}\","
            + "\"upn\":\"legacy@contoso.com\","
            + "\"isEnabled\":true,"
            + "\"addedDate\":\"2026-08-30T14:05:00Z\","
            + "\"addedBy\":\"admin@contoso.com\","
            + "\"canManageBootstrapTokens\":false}",
            json);
        Assert.DoesNotContain("partitionKey", json);
        Assert.DoesNotContain("rowKey", json);
        Assert.DoesNotContain("eTag", json);
        Assert.DoesNotContain("timestamp", json);
    }

    // ---- TenantGroupManagement -----------------------------------------------------------

    [Fact]
    public void TenantGroupListResponse_matches_the_groups_shape()
    {
        var groups = new List<TenantGroup>
        {
            new TenantGroup
            {
                GroupId = "c2c2c2c2-2222-4333-8444-555566667777",
                Name = "EU Customers",
                CreatedBy = "admin@contoso.com",
                CreatedAt = new DateTime(2026, 8, 27, 8, 0, 0, DateTimeKind.Utc),
                TenantIds = new List<string> { SampleTenantId },
                AssigneeCount = 1,
                Assignees = new List<TenantGroupAssignment>
                {
                    new TenantGroupAssignment
                    {
                        Upn = "msp@fabrikam.com",
                        GroupId = "c2c2c2c2-2222-4333-8444-555566667777",
                        Role = "DelegatedReader",
                        IsEnabled = true,
                        AssignedBy = "admin@contoso.com",
                        AssignedAt = new DateTime(2026, 8, 27, 8, 5, 0, DateTimeKind.Utc),
                    },
                },
            },
        };

        AssertParity(
            new { groups },
            new TenantGroupListResponse { Groups = groups });
    }

    [Fact]
    public void CreateTenantGroupResponse_matches_the_created_group_shape()
    {
        var groupId = "c2c2c2c2-2222-4333-8444-555566667777";
        var name = "EU Customers";

        AssertParity(
            new { groupId, name },
            new CreateTenantGroupResponse { GroupId = groupId, Name = name });
    }

    // ---- VersionBlock --------------------------------------------------------------------

    [Fact]
    public void BlockedVersionListResponse_matches_the_rules_shape()
    {
        var rules = new List<BlockedVersionEntry>
        {
            new BlockedVersionEntry
            {
                VersionPattern = "1.4.*",
                Action = "Block",
                CreatedByEmail = "admin@contoso.com",
                CreatedAt = new DateTime(2026, 8, 30, 7, 0, 0, DateTimeKind.Utc),
                Reason = "Known crash loop",
            },
        };

        AssertParity(
            new { success = true, rules },
            new BlockedVersionListResponse { Success = true, Rules = rules });
    }

    [Fact]
    public void BlockVersionResponse_matches_the_block_acknowledgement_shape()
    {
        var versionPattern = "1.4.*";
        var normalizedAction = "Kill";

        AssertParity(
            new
            {
                success = true,
                message = $"Version pattern '{versionPattern}' set to {normalizedAction}.",
                versionPattern,
                action = normalizedAction
            },
            new BlockVersionResponse
            {
                Success = true,
                Message = $"Version pattern '{versionPattern}' set to {normalizedAction}.",
                VersionPattern = versionPattern,
                Action = normalizedAction
            });
    }

    [Fact]
    public void UnblockVersion_success_body_matches_SuccessMessageResponse()
    {
        var versionPattern = "1.4.*";

        AssertParity(
            new { success = true, message = $"Version pattern '{versionPattern}' unblocked." },
            new SuccessMessageResponse { Success = true, Message = $"Version pattern '{versionPattern}' unblocked." });
    }

    private static void AssertParity(object anonymousLiteral, IApiResponse typed)
        => ApiResponseWireParityTests.AssertWireIdentical(anonymousLiteral, typed);
}

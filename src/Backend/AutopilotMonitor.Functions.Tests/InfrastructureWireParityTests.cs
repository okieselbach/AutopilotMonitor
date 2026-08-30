using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Wire-identity proof for the Infrastructure / Global / Notifications / Bootstrap /
/// Diagnostics function folders plus McpQuotaEnforcementMiddleware (anonymous-object →
/// typed-DTO migration). Each fact serializes the OLD anonymous literal exactly as it stood
/// at the call site (copied from the pre-migration code, filled with realistic sample
/// values) against the NEW DTO carrying the same values, via
/// <see cref="ApiResponseWireParityTests.AssertWireIdentical"/> — key names, key order and
/// key presence/absence (WhenWritingNull) must match ordinally. Nullable slots additionally
/// get a null case proving the key vanishes identically on both sides.
/// </summary>
public class InfrastructureWireParityTests
{
    // ---- AuthFunction: IsGlobalAdmin -----------------------------------------------------

    [Fact]
    public void IsGlobalAdminResponse_matches_the_flag_shape()
    {
        var isAdmin = true;
        string? upn = "admin@contoso.com";

        AssertParity(
            new { isGlobalAdmin = isAdmin, upn },
            new IsGlobalAdminResponse { IsGlobalAdmin = isAdmin, Upn = upn });
    }

    [Fact]
    public void IsGlobalAdminResponse_omits_a_null_upn()
    {
        var isAdmin = false;
        string? upn = null;

        AssertParity(
            new { isGlobalAdmin = isAdmin, upn },
            new IsGlobalAdminResponse { IsGlobalAdmin = isAdmin, Upn = null });
    }

    // ---- AuthFunction: GetGlobalAdmins ---------------------------------------------------

    [Fact]
    public void GetGlobalAdminsResponse_matches_the_entity_listing_shape()
    {
        var admins = new List<GlobalAdminEntity>
        {
            new GlobalAdminEntity
            {
                RowKey = "admin@contoso.com",
                Timestamp = new DateTimeOffset(2026, 8, 30, 10, 0, 0, TimeSpan.Zero),
                Upn = "admin@contoso.com",
                IsEnabled = true,
                AddedDate = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc),
                AddedBy = "root@fabrikam.com",
                Role = "GlobalAdmin",
            },
            new GlobalAdminEntity
            {
                RowKey = "reader@fabrikam.com",
                Upn = "reader@fabrikam.com",
                IsEnabled = false,
                AddedDate = new DateTime(2026, 8, 2, 9, 0, 0, DateTimeKind.Utc),
                AddedBy = "admin@contoso.com",
                Role = "GlobalReader",
            },
        };

        AssertParity(
            new { admins },
            new GetGlobalAdminsResponse { Admins = admins });
    }

    // ---- AuthFunction: AddGlobalAdmin ----------------------------------------------------

    [Fact]
    public void AddGlobalAdminResponse_matches_the_created_entity_shape()
    {
        var newAdmin = new GlobalAdminEntity
        {
            RowKey = "new.admin@contoso.com",
            Upn = "new.admin@contoso.com",
            IsEnabled = true,
            AddedDate = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc),
            AddedBy = "admin@contoso.com",
            Role = "GlobalAdmin",
        };

        AssertParity(
            new { admin = newAdmin },
            new AddGlobalAdminResponse { Admin = newAdmin });
    }

    // ---- HealthCheckFunction: HealthCheck ------------------------------------------------

    [Fact]
    public void HealthCheckResponse_matches_the_liveness_shape()
    {
        var timestamp = new DateTime(2026, 8, 30, 13, 0, 0, DateTimeKind.Utc);
        var buildUtc = new DateTime(2026, 8, 29, 20, 15, 0, DateTimeKind.Utc);

        AssertParity(
            new
            {
                status = "healthy",
                service = "Autopilot Monitor API",
                timestamp,
                version = "1.2.3",
                commitHash = "abc1234",
                buildUtc
            },
            new HealthCheckResponse
            {
                Status = "healthy",
                Service = "Autopilot Monitor API",
                Timestamp = timestamp,
                Version = "1.2.3",
                CommitHash = "abc1234",
                BuildUtc = buildUtc
            });
    }

    // ---- HealthCheckFunction: DetailedHealthCheck ----------------------------------------

    [Fact]
    public void DetailedHealthCheckResponse_matches_the_report_shape()
    {
        var timestamp = new DateTime(2026, 8, 30, 13, 5, 0, DateTimeKind.Utc);
        var buildUtc = new DateTime(2026, 8, 29, 20, 15, 0, DateTimeKind.Utc);
        var visibleChecks = new List<HealthCheck>
        {
            new HealthCheck
            {
                Name = "Table Storage",
                Description = "Round-trip against the sessions table",
                Status = "healthy",
                Message = "OK",
                Details = new Dictionary<string, object> { ["latencyMs"] = 12 },
            },
            new HealthCheck
            {
                Name = "SignalR",
                Description = "Service reachability",
                Status = "degraded",
                Message = "Slow response",
                // Details stays null → the key vanishes inside the item on both sides.
            },
        };

        AssertParity(
            new
            {
                service = "Autopilot Monitor API",
                timestamp,
                overallStatus = "degraded",
                checks = visibleChecks,
                version = "1.2.3",
                commitHash = "abc1234",
                buildUtc
            },
            new DetailedHealthCheckResponse
            {
                Service = "Autopilot Monitor API",
                Timestamp = timestamp,
                OverallStatus = "degraded",
                Checks = visibleChecks,
                Version = "1.2.3",
                CommitHash = "abc1234",
                BuildUtc = buildUtc
            });
    }

    // ---- HealthCheckFunction: McpHealthCheck ---------------------------------------------

    [Fact]
    public void McpHealthCheckResponse_matches_the_probe_shape()
    {
        var timestamp = new DateTime(2026, 8, 30, 13, 10, 0, DateTimeKind.Utc);
        var check = new HealthCheck
        {
            Name = "MCP Server",
            Description = "Remote MCP server reachability",
            Status = "healthy",
            Message = "Responded in 480ms",
        };

        AssertParity(
            new
            {
                timestamp,
                check
            },
            new McpHealthCheckResponse
            {
                Timestamp = timestamp,
                Check = check
            });
    }

    // ---- McpUserFunction: GetMcpUsers ----------------------------------------------------

    [Fact]
    public void GetMcpUsersResponse_matches_the_whitelist_shape()
    {
        var users = new List<McpUserEntry>
        {
            new McpUserEntry
            {
                Upn = "analyst@contoso.com",
                IsEnabled = true,
                AddedAt = new DateTime(2026, 8, 10, 8, 0, 0, DateTimeKind.Utc),
                AddedBy = "admin@contoso.com",
                UsagePlan = "pro",
            },
            new McpUserEntry
            {
                Upn = "viewer@fabrikam.com",
                IsEnabled = false,
                AddedAt = new DateTime(2026, 8, 11, 8, 0, 0, DateTimeKind.Utc),
                AddedBy = "admin@contoso.com",
                // UsagePlan stays null → the key vanishes inside the item on both sides.
            },
        };

        AssertParity(
            new { policy = "Whitelist", users },
            new GetMcpUsersResponse { Policy = "Whitelist", Users = users });
    }

    // ---- McpUserFunction: AddMcpUser -----------------------------------------------------

    [Fact]
    public void AddMcpUserResponse_matches_the_created_entry_shape()
    {
        var user = new McpUserEntry
        {
            Upn = "new.analyst@contoso.com",
            IsEnabled = true,
            AddedAt = new DateTime(2026, 8, 30, 14, 0, 0, DateTimeKind.Utc),
            AddedBy = "admin@contoso.com",
        };

        AssertParity(
            new { user },
            new AddMcpUserResponse { User = user });
    }

    // ---- McpUserFunction: SetMcpUserUsagePlan --------------------------------------------

    [Fact]
    public void SetMcpUserUsagePlanResponse_matches_the_plan_shape()
    {
        var upn = "analyst@contoso.com";
        string? usagePlan = "pro";

        AssertParity(
            new { upn, usagePlan = usagePlan ?? "(inherit)" },
            new SetMcpUserUsagePlanResponse { Upn = upn, UsagePlan = usagePlan ?? "(inherit)" });
    }

    [Fact]
    public void SetMcpUserUsagePlanResponse_renders_the_inherit_fallback()
    {
        var upn = "analyst@contoso.com";
        string? usagePlan = null;

        AssertParity(
            new { upn, usagePlan = usagePlan ?? "(inherit)" },
            new SetMcpUserUsagePlanResponse { Upn = upn, UsagePlan = usagePlan ?? "(inherit)" });
    }

    // ---- SignalRNegotiateFunction --------------------------------------------------------

    [Fact]
    public void SignalRNegotiateResponse_matches_the_negotiate_protocol_shape()
    {
        var url = "https://signalr.service.example/client/?hub=notifications";
        var accessToken = "eyJhbGciOiJIUzI1NiJ9.sample.token";

        AssertParity(
            new { url, accessToken },
            new SignalRNegotiateResponse { Url = url, AccessToken = accessToken });
    }

    // ---- SignalRAddToGroup / SignalRRemoveFromGroup --------------------------------------

    [Fact]
    public void AddToGroup_success_body_matches_SuccessMessageResponse()
    {
        var groupName = "tenant-6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d";

        AssertParity(
            new
            {
                success = true,
                message = $"Added to group {groupName}"
            },
            new SuccessMessageResponse
            {
                Success = true,
                Message = $"Added to group {groupName}"
            });
    }

    [Fact]
    public void RemoveFromGroup_success_body_matches_SuccessMessageResponse()
    {
        var groupName = "session-6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d-0b6f7a37-1111-4d61-9c93-0aa111111111";

        AssertParity(
            new
            {
                success = true,
                message = $"Removed from group {groupName}"
            },
            new SuccessMessageResponse
            {
                Success = true,
                Message = $"Removed from group {groupName}"
            });
    }

    // ---- GlobalNotificationsFunction / TenantNotificationsFunction: list -----------------

    [Fact]
    public void NotificationListResponse_matches_the_active_notification_shape()
    {
        var notifications = new List<GlobalNotificationDto>
        {
            new GlobalNotificationDto
            {
                Id = "8f1c9d55-aaaa-4e0e-8888-52f3aaaa0001",
                Type = "preview_signup",
                Title = "New Tenant Signup",
                Message = "Tenant 6a6a35a2 (contoso.com), UPN: admin@contoso.com",
                Href = "/admin/tenants/management?tenantId=6a6a35a2",
                CreatedAt = new DateTime(2026, 8, 30, 9, 30, 0, DateTimeKind.Utc),
            },
            new GlobalNotificationDto
            {
                Id = "8f1c9d55-bbbb-4e0e-8888-52f3aaaa0002",
                Type = "info",
                Title = "Maintenance",
                Message = "Nightly cleanup completed",
                // Href stays null → the key vanishes inside the item on both sides.
                CreatedAt = new DateTime(2026, 8, 30, 4, 0, 0, DateTimeKind.Utc),
            },
        };

        AssertParity(
            new { success = true, notifications },
            new NotificationListResponse { Success = true, Notifications = notifications });
    }

    // ---- Global/Tenant notifications: dismiss single -------------------------------------

    [Fact]
    public void DismissNotification_success_body_matches_SuccessOnlyResponse()
    {
        AssertParity(
            new { success = true },
            new SuccessOnlyResponse { Success = true });
    }

    // ---- Global/Tenant notifications: dismiss-all ----------------------------------------

    [Fact]
    public void DismissAllNotificationsResponse_matches_the_count_shape()
    {
        var dismissedCount = 7;

        AssertParity(
            new { success = true, dismissedCount },
            new DismissAllNotificationsResponse { Success = true, DismissedCount = dismissedCount });
    }

    // ---- RevokeBootstrapSessionFunction --------------------------------------------------

    [Fact]
    public void RevokeBootstrapSession_success_body_matches_SuccessMessageResponse()
    {
        AssertParity(
            new { success = true, message = "Bootstrap session revoked" },
            new SuccessMessageResponse { Success = true, Message = "Bootstrap session revoked" });
    }

    // ---- DiagnosticsDownloadTicketFunction -----------------------------------------------

    [Fact]
    public void DiagnosticsDownloadTicketResponse_matches_the_ticket_shape()
    {
        var ticket = "dGVzdC10aWNrZXQ=.sig==";
        var expiresAt = new DateTime(2026, 8, 30, 15, 10, 0, DateTimeKind.Utc);
        var blobName = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d/0b6f7a37-1111-4d61-9c93-0aa111111111/diagnostics.zip";
        var destinationLabel = "Hosted";
        long? sizeBytes = 1048576;

        AssertParity(
            new
            {
                success = true,
                url = $"/api/diagnostics/download?t={Uri.EscapeDataString(ticket)}",
                expiresAt,
                blobName,
                destination = destinationLabel,
                sizeBytes,
            },
            new DiagnosticsDownloadTicketResponse
            {
                Success = true,
                Url = $"/api/diagnostics/download?t={Uri.EscapeDataString(ticket)}",
                ExpiresAt = expiresAt,
                BlobName = blobName,
                Destination = destinationLabel,
                SizeBytes = sizeBytes,
            });
    }

    [Fact]
    public void DiagnosticsDownloadTicketResponse_omits_a_null_sizeBytes()
    {
        var ticket = "dGVzdC10aWNrZXQy.sig==";
        var expiresAt = new DateTime(2026, 8, 30, 15, 20, 0, DateTimeKind.Utc);
        var blobName = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d/0b6f7a37-2222-4d61-9c93-0aa222222222/diagnostics.zip";
        var destinationLabel = "CustomerSas";
        long? sizeBytes = null;

        AssertParity(
            new
            {
                success = true,
                url = $"/api/diagnostics/download?t={Uri.EscapeDataString(ticket)}",
                expiresAt,
                blobName,
                destination = destinationLabel,
                sizeBytes,
            },
            new DiagnosticsDownloadTicketResponse
            {
                Success = true,
                Url = $"/api/diagnostics/download?t={Uri.EscapeDataString(ticket)}",
                ExpiresAt = expiresAt,
                BlobName = blobName,
                Destination = destinationLabel,
                SizeBytes = null,
            });
    }

    // ---- McpQuotaEnforcementMiddleware: 429 quota body -----------------------------------

    [Fact]
    public void McpQuotaExceededResponse_matches_the_daily_blocked_shape()
    {
        var decision = new McpQuotaDecision
        {
            Allowed = false,
            Plan = "community",
            Scope = "daily",
            DailyLimit = 200,
            MonthlyLimit = 2000,
            DailyUsed = 200,
            MonthlyUsed = 940,
            ResetUtc = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc),
        };

        AssertParity(
            new
            {
                quotaExceeded = true,
                plan = decision.Plan,
                scope = decision.Scope,
                limit = decision.Scope == "monthly" ? decision.MonthlyLimit : decision.DailyLimit,
                used = decision.Scope == "monthly" ? decision.MonthlyUsed : decision.DailyUsed,
                resetUtc = decision.ResetUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                message = $"MCP {decision.Scope} request quota exceeded for plan '{decision.Plan}'. Resets at {decision.ResetUtc:yyyy-MM-ddTHH:mm:ss}Z."
            },
            new McpQuotaExceededResponse
            {
                QuotaExceeded = true,
                Plan = decision.Plan,
                Scope = decision.Scope,
                Limit = decision.Scope == "monthly" ? decision.MonthlyLimit : decision.DailyLimit,
                Used = decision.Scope == "monthly" ? decision.MonthlyUsed : decision.DailyUsed,
                ResetUtc = decision.ResetUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Message = $"MCP {decision.Scope} request quota exceeded for plan '{decision.Plan}'. Resets at {decision.ResetUtc:yyyy-MM-ddTHH:mm:ss}Z."
            });
    }

    [Fact]
    public void McpQuotaExceededResponse_matches_the_monthly_blocked_shape()
    {
        var decision = new McpQuotaDecision
        {
            Allowed = false,
            Plan = "pro",
            Scope = "monthly",
            DailyLimit = 1000,
            MonthlyLimit = 10000,
            DailyUsed = 120,
            MonthlyUsed = 10000,
            ResetUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        AssertParity(
            new
            {
                quotaExceeded = true,
                plan = decision.Plan,
                scope = decision.Scope,
                limit = decision.Scope == "monthly" ? decision.MonthlyLimit : decision.DailyLimit,
                used = decision.Scope == "monthly" ? decision.MonthlyUsed : decision.DailyUsed,
                resetUtc = decision.ResetUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                message = $"MCP {decision.Scope} request quota exceeded for plan '{decision.Plan}'. Resets at {decision.ResetUtc:yyyy-MM-ddTHH:mm:ss}Z."
            },
            new McpQuotaExceededResponse
            {
                QuotaExceeded = true,
                Plan = decision.Plan,
                Scope = decision.Scope,
                Limit = decision.Scope == "monthly" ? decision.MonthlyLimit : decision.DailyLimit,
                Used = decision.Scope == "monthly" ? decision.MonthlyUsed : decision.DailyUsed,
                ResetUtc = decision.ResetUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Message = $"MCP {decision.Scope} request quota exceeded for plan '{decision.Plan}'. Resets at {decision.ResetUtc:yyyy-MM-ddTHH:mm:ss}Z."
            });
    }

    [Fact]
    public void McpQuotaExceededResponse_omits_a_null_scope()
    {
        // Unreachable at the 429 site (a blocked decision always names the exceeded window),
        // but the DTO slot is nullable — prove the key vanishes identically on both sides.
        string? scope = null;

        AssertParity(
            new
            {
                quotaExceeded = true,
                plan = "community",
                scope,
                limit = 200,
                used = 200L,
                resetUtc = "2026-08-31T00:00:00Z",
                message = "MCP  request quota exceeded for plan 'community'. Resets at 2026-08-31T00:00:00Z."
            },
            new McpQuotaExceededResponse
            {
                QuotaExceeded = true,
                Plan = "community",
                Scope = null,
                Limit = 200,
                Used = 200,
                ResetUtc = "2026-08-31T00:00:00Z",
                Message = "MCP  request quota exceeded for plan 'community'. Resets at 2026-08-31T00:00:00Z."
            });
    }

    private static void AssertParity(object anonymousLiteral, IApiResponse typed)
        => ApiResponseWireParityTests.AssertWireIdentical(anonymousLiteral, typed);
}

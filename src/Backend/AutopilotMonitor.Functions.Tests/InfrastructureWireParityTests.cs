using AutopilotMonitor.Functions.Middleware;
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
    // ---- AuthFunction: auth/me success body ----------------------------------------------
    // Left side: the exact anonymous literal BuildAuthResult produced before AuthMeResponse.

    [Fact]
    public void AuthMeResponse_matches_the_success_shape()
    {
        var tenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d";
        var upn = "admin@contoso.com";
        var displayName = "Admin User";
        var objectId = "0b6f7a37-1111-4d61-9c93-0aa111111111";
        var managedTenantIds = new[] { "7b7b46b3-40c3-4f2f-9a1b-6d9f1a2b3c4e" };

        AssertParity(
            new
            {
                tenantId,
                upn,
                displayName,
                objectId,
                isGlobalAdmin = false,
                isGlobalReader = false,
                isTenantAdmin = true,
                isDelegated = true,
                delegatedTenantIds = managedTenantIds,
                role = "Admin",
                canManageBootstrapTokens = true,
                hasMcpAccess = true,
                homedApp = "primary",
                bootstrapTokenEnabled = true,
                unrestrictedModeEnabled = false
            },
            new AuthMeResponse
            {
                TenantId = tenantId,
                Upn = upn,
                DisplayName = displayName,
                ObjectId = objectId,
                IsGlobalAdmin = false,
                IsGlobalReader = false,
                IsTenantAdmin = true,
                IsDelegated = true,
                DelegatedTenantIds = managedTenantIds,
                Role = "Admin",
                CanManageBootstrapTokens = true,
                HasMcpAccess = true,
                HomedApp = "primary",
                BootstrapTokenEnabled = true,
                UnrestrictedModeEnabled = false
            });
    }

    [Fact]
    public void AuthMeResponse_omits_a_null_role()
    {
        // A roleless caller (e.g. pure GlobalReader): role was null in the anonymous literal,
        // so the key vanished — the DTO must vanish it identically.
        var tenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d";
        var upn = "reader@contoso.com";
        var displayName = "Reader User";
        var objectId = "0b6f7a37-2222-4d61-9c93-0aa222222222";
        string? role = null;

        AssertParity(
            new
            {
                tenantId,
                upn,
                displayName,
                objectId,
                isGlobalAdmin = false,
                isGlobalReader = true,
                isTenantAdmin = false,
                isDelegated = false,
                delegatedTenantIds = Array.Empty<string>(),
                role,
                canManageBootstrapTokens = false,
                hasMcpAccess = true,
                homedApp = "legacy",
                bootstrapTokenEnabled = false,
                unrestrictedModeEnabled = false
            },
            new AuthMeResponse
            {
                TenantId = tenantId,
                Upn = upn,
                DisplayName = displayName,
                ObjectId = objectId,
                IsGlobalAdmin = false,
                IsGlobalReader = true,
                IsTenantAdmin = false,
                IsDelegated = false,
                DelegatedTenantIds = Array.Empty<string>(),
                Role = null,
                CanManageBootstrapTokens = false,
                HasMcpAccess = true,
                HomedApp = "legacy",
                BootstrapTokenEnabled = false,
                UnrestrictedModeEnabled = false
            });
    }

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

    // ---- McpUserFunction: CheckMcpAccess (auth/mcp) --------------------------------------
    // Left side: the exact Dictionary<string, object?> the site built before the DTO —
    // insertion order == wire order, conditional keys only present when the tier applies.

    [Fact]
    public void CheckMcpAccessResponse_matches_the_plain_user_shape()
    {
        var payload = new Dictionary<string, object?>
        {
            ["allowed"] = true,
            ["upn"] = "analyst@contoso.com",
            ["accessGrant"] = "whitelist",
            ["reason"] = "whitelisted",
        };

        AssertParity(
            payload,
            new CheckMcpAccessResponse
            {
                Allowed = true,
                Upn = "analyst@contoso.com",
                AccessGrant = "whitelist",
                Reason = "whitelisted",
            });
    }

    [Fact]
    public void CheckMcpAccessResponse_matches_the_platform_and_delegated_shape()
    {
        var delegatedTenantIds = new[] { "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d" };
        var payload = new Dictionary<string, object?>
        {
            ["allowed"] = true,
            ["upn"] = "admin@contoso.com",
            ["accessGrant"] = "global-admin",
            ["reason"] = "platform role",
            ["isGlobalAdmin"] = true,
            ["globalRole"] = "GlobalAdmin",
            ["delegatedTenantIds"] = delegatedTenantIds,
            ["delegatedRole"] = "DelegatedAdmin",
        };

        AssertParity(
            payload,
            new CheckMcpAccessResponse
            {
                Allowed = true,
                Upn = "admin@contoso.com",
                AccessGrant = "global-admin",
                Reason = "platform role",
                IsGlobalAdmin = true,
                GlobalRole = "GlobalAdmin",
                DelegatedTenantIds = delegatedTenantIds,
                DelegatedRole = "DelegatedAdmin",
            });
    }

    [Fact]
    public void CheckMcpAccessResponse_matches_the_denied_shape()
    {
        // 403 body: same shape, allowed=false, no conditional keys. isGlobalAdmin must never
        // appear as false — the DTO's null slot omits it exactly like the old dictionary did.
        var payload = new Dictionary<string, object?>
        {
            ["allowed"] = false,
            ["upn"] = "user@contoso.com",
            ["accessGrant"] = "",
            ["reason"] = "not whitelisted",
        };

        AssertParity(
            payload,
            new CheckMcpAccessResponse
            {
                Allowed = false,
                Upn = "user@contoso.com",
                AccessGrant = "",
                Reason = "not whitelisted",
                IsGlobalAdmin = null,
                GlobalRole = null,
                DelegatedTenantIds = null,
                DelegatedRole = null,
            });
    }

    // ---- AuthFunction: GetGlobalAdmins / AddGlobalAdmin ----------------------------------
    // DELIBERATE wire change (2026-08-31 entity-hygiene pass): rows are GlobalAdminRow, not the
    // raw GlobalAdminEntity — partitionKey/rowKey/eTag/timestamp are gone from the wire.
    // These pins fix the NEW shape exactly (key names, order, table-key absence).

    [Fact]
    public void GetGlobalAdminsResponse_pins_the_row_listing_shape()
    {
        var admins = new List<GlobalAdminRow>
        {
            new GlobalAdminRow
            {
                Upn = "admin@contoso.com",
                IsEnabled = true,
                AddedDate = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc),
                AddedBy = "root@fabrikam.com",
                Role = "GlobalAdmin",
            },
            new GlobalAdminRow
            {
                Upn = "reader@fabrikam.com",
                IsEnabled = false,
                AddedDate = new DateTime(2026, 8, 2, 9, 0, 0, DateTimeKind.Utc),
                AddedBy = "admin@contoso.com",
                Role = "GlobalReader",
            },
        };

        var json = TestWire.Serialize(new GetGlobalAdminsResponse { Admins = admins });
        Assert.Equal(
            "{\"admins\":["
            + "{\"upn\":\"admin@contoso.com\",\"isEnabled\":true,\"addedDate\":\"2026-08-01T09:00:00Z\",\"addedBy\":\"root@fabrikam.com\",\"role\":\"GlobalAdmin\"},"
            + "{\"upn\":\"reader@fabrikam.com\",\"isEnabled\":false,\"addedDate\":\"2026-08-02T09:00:00Z\",\"addedBy\":\"admin@contoso.com\",\"role\":\"GlobalReader\"}"
            + "]}",
            json);
        Assert.DoesNotContain("partitionKey", json);
        Assert.DoesNotContain("rowKey", json);
        Assert.DoesNotContain("eTag", json);
        Assert.DoesNotContain("timestamp", json);
    }

    [Fact]
    public void AddGlobalAdminResponse_pins_the_created_row_shape()
    {
        var newAdmin = new GlobalAdminRow
        {
            Upn = "new.admin@contoso.com",
            IsEnabled = true,
            AddedDate = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc),
            AddedBy = "admin@contoso.com",
            Role = "GlobalAdmin",
        };

        Assert.Equal(
            "{\"admin\":{\"upn\":\"new.admin@contoso.com\",\"isEnabled\":true,\"addedDate\":\"2026-08-30T12:00:00Z\",\"addedBy\":\"admin@contoso.com\",\"role\":\"GlobalAdmin\"}}",
            TestWire.Serialize(new AddGlobalAdminResponse { Admin = newAdmin }));
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
    // DELIBERATE wire change (error-envelope pass): the body now leads with the envelope prefix
    // error/code/correlationId (error carries the former `message`); quotaExceeded stays as the
    // MCP's discriminator. These literals pin the NEW shape (correlationId is stamped by the writer,
    // so the builder leaves it empty).

    [Fact]
    public void McpQuotaExceededResponse_matches_the_daily_blocked_shape()
    {
        // The caller's OWN daily window: level=user, limit/used from the user counters, plan named.
        var decision = McpQuotaService.BuildDecision(
            new McpPlanLimits("community", 200, 2000, "community", 600, 6000),
            dailyUsed: 200, monthlyUsed: 940, tenantDailyUsed: 250, tenantMonthlyUsed: 1200,
            new DateTime(2026, 8, 30, 15, 0, 0, DateTimeKind.Utc));

        AssertParity(
            new
            {
                error = "MCP daily request quota exceeded for plan 'community'. The Community plan is sized for occasional use; Pro raises your daily and monthly windows. Resets at 2026-08-31T00:00:00Z.",
                code = "QuotaExceeded",
                correlationId = "",
                quotaExceeded = true,
                plan = "community",
                scope = "daily",
                level = "user",
                limit = 200,
                used = 200L,
                resetUtc = "2026-08-31T00:00:00Z"
            },
            McpQuotaEnforcementMiddleware.BuildExceededResponse(decision));
    }

    [Fact]
    public void McpQuotaExceededResponse_matches_the_tenant_monthly_blocked_shape()
    {
        // The ORGANIZATION's monthly window while the caller is well inside their own plan: level=tenant,
        // limit/used from the tenant counters, the message names the tenant plan (not the user's override).
        var decision = McpQuotaService.BuildDecision(
            new McpPlanLimits("power", 10000, 100000, "pro", 3000, 60000),
            dailyUsed: 120, monthlyUsed: 5000, tenantDailyUsed: 900, tenantMonthlyUsed: 60000,
            new DateTime(2026, 8, 30, 15, 0, 0, DateTimeKind.Utc));

        AssertParity(
            new
            {
                error = "MCP monthly request quota of your organization exceeded (tenant plan 'pro', shared by all its members). Resets at 2026-09-01T00:00:00Z.",
                code = "QuotaExceeded",
                correlationId = "",
                quotaExceeded = true,
                plan = "power",
                scope = "monthly",
                level = "tenant",
                limit = 60000,
                used = 60000L,
                resetUtc = "2026-09-01T00:00:00Z"
            },
            McpQuotaEnforcementMiddleware.BuildExceededResponse(decision));
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
                error = "MCP  request quota exceeded for plan 'community'. Resets at 2026-08-31T00:00:00Z.",
                code = "QuotaExceeded",
                correlationId = "",
                quotaExceeded = true,
                plan = "community",
                scope,
                level = "user",
                limit = 200,
                used = 200L,
                resetUtc = "2026-08-31T00:00:00Z"
            },
            new McpQuotaExceededResponse
            {
                Error = "MCP  request quota exceeded for plan 'community'. Resets at 2026-08-31T00:00:00Z.",
                QuotaExceeded = true,
                Plan = "community",
                Scope = null,
                Level = "user",
                Limit = 200,
                Used = 200,
                ResetUtc = "2026-08-31T00:00:00Z",
            });
    }

    [Fact]
    public void McpQuotaExceededResponse_matches_the_managed_tenant_blocked_shape()
    {
        // A delegated (MSP) read charged to a MANAGED Community tenant whose organization window is spent:
        // level=tenant, the managed tenant is named (label + targetTenantId) and the upgrade path is spelled out.
        var decision = McpQuotaService.BuildDecision(
            new McpPlanLimits("pro", 1000, 20000, "community", 300, 9000),
            dailyUsed: 12, monthlyUsed: 340, tenantDailyUsed: 300, tenantMonthlyUsed: 2100,
            new DateTime(2026, 8, 30, 15, 0, 0, DateTimeKind.Utc),
            targetTenantId: "7aa20c11-0002-4b7c-a1d2-52f3aaaa0002");

        AssertParity(
            new
            {
                error = "MCP daily request quota of the managed tenant 'customer.example' exceeded (tenant plan 'community', shared by all its members and delegated admins). That tenant is on the Community plan, which is sized for occasional use; its own plan governs this window, not yours. Upgrading that tenant to Pro lifts its organization windows. Resets at 2026-08-31T00:00:00Z.",
                code = "QuotaExceeded",
                correlationId = "",
                quotaExceeded = true,
                plan = "pro",
                scope = "daily",
                level = "tenant",
                limit = 300,
                used = 300L,
                resetUtc = "2026-08-31T00:00:00Z",
                targetTenantId = "7aa20c11-0002-4b7c-a1d2-52f3aaaa0002"
            },
            McpQuotaEnforcementMiddleware.BuildExceededResponse(decision, "customer.example"));
    }

    [Fact]
    public void McpQuotaExceededResponse_matches_the_all_managed_tenants_exhausted_shape()
    {
        // The fleet aggregate where EVERY managed tenant is spent: no single target, the count is named.
        var decision = McpQuotaService.BuildDecision(
            new McpPlanLimits("pro", 1000, 20000, "community", 300, 9000),
            dailyUsed: 12, monthlyUsed: 340, tenantDailyUsed: 300, tenantMonthlyUsed: 2100,
            new DateTime(2026, 8, 30, 15, 0, 0, DateTimeKind.Utc),
            targetTenantId: "7aa20c11-0002-4b7c-a1d2-52f3aaaa0002");

        AssertParity(
            new
            {
                error = "MCP request quota exceeded for all 2 managed tenants in scope (each managed tenant's own plan governs its organization windows; upgrading a managed tenant to Pro lifts them). Earliest reset at 2026-08-31T00:00:00Z.",
                code = "QuotaExceeded",
                correlationId = "",
                quotaExceeded = true,
                plan = "pro",
                scope = "daily",
                level = "tenant",
                limit = 300,
                used = 300L,
                resetUtc = "2026-08-31T00:00:00Z"
            },
            McpQuotaEnforcementMiddleware.BuildExceededResponse(decision, exhaustedTenantCount: 2));
    }

    private static void AssertParity(object anonymousLiteral, IApiResponse typed)
        => ApiResponseWireParityTests.AssertWireIdentical(anonymousLiteral, typed);
}

using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Config;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Wire-identity proof for the Config function folder (anonymous-object → typed-DTO
/// migration). Each fact serializes the OLD anonymous literal exactly as it stood at the
/// call site (copied from the pre-migration code, filled with realistic sample values)
/// against the NEW DTO carrying the same values, via
/// <see cref="ApiResponseWireParityTests.AssertWireIdentical"/> — key names, key order and
/// key presence/absence (WhenWritingNull) must match ordinally. Nullable slots additionally
/// get a null case proving the key vanishes identically on both sides.
/// </summary>
public class ConfigWireParityTests
{
    // ---- GetAllTenantConfigurations (delegated one-shot page + GA paginated mode) --------

    [Fact]
    public void GetAllTenantConfigurationsResponse_matches_the_paginated_projection_shape()
    {
        // TenantConfigProjection.ProjectAll returns List<Dictionary<string, object?>>.
        var tenants = new List<Dictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["tenantId"] = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d",
                ["domainName"] = "contoso.com",
                ["planTier"] = "pro",
            },
        };
        string? nextLink = "/api/config/all?pageSize=1&continuation=tok";

        AssertParity(
            new
            {
                count = tenants.Count,
                tenants,
                nextLink,
            },
            new GetAllTenantConfigurationsResponse
            {
                Count = tenants.Count,
                Tenants = tenants,
                NextLink = nextLink,
            });
    }

    [Fact]
    public void GetAllTenantConfigurationsResponse_omits_the_null_nextLink_of_the_delegated_one_shot_page()
    {
        var projected = new List<Dictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["tenantId"] = "1b1b46b3-41c3-4a3a-8b2c-7e0a2b3c4d5e",
                ["domainName"] = "fabrikam.com",
            },
        };

        AssertParity(
            new
            {
                count = projected.Count,
                tenants = projected,
                nextLink = (string?)null,
            },
            new GetAllTenantConfigurationsResponse
            {
                Count = projected.Count,
                Tenants = projected,
                NextLink = null,
            });
    }

    // ---- UpdateTenantAppHoming -----------------------------------------------------------

    [Fact]
    public void UpdateTenantAppHomingResponse_matches_the_flip_result_shape()
    {
        var changed = true;
        string? homedAppClientId = "0f9e8d7c-6b5a-4433-2211-aabbccddeeff";
        string? lastAuthClientId = "0f9e8d7c-6b5a-4433-2211-aabbccddeeff";
        DateTime? lastAuthClientIdSince = new DateTime(2026, 8, 29, 6, 30, 0, DateTimeKind.Utc);

        AssertParity(
            new
            {
                success = true,
                changed,
                homedApp = "primary",
                homedAppClientId,
                lastAuthClientId,
                lastAuthClientIdSince,
                // Old ProbePayload helper built this anonymous shape.
                probe = new
                {
                    attempted = true,
                    succeeded = true,
                    isTransient = false,
                },
            },
            new UpdateTenantAppHomingResponse
            {
                Success = true,
                Changed = changed,
                HomedApp = "primary",
                HomedAppClientId = homedAppClientId,
                LastAuthClientId = lastAuthClientId,
                LastAuthClientIdSince = lastAuthClientIdSince,
                Probe = new AppHomingProbeWire
                {
                    Attempted = true,
                    Succeeded = true,
                    IsTransient = false,
                },
            });
    }

    [Fact]
    public void UpdateTenantAppHomingResponse_omits_null_homing_slots()
    {
        // Legacy-homed tenant that never authenticated: pin and last-auth fields are null.
        string? homedAppClientId = null;
        string? lastAuthClientId = null;
        DateTime? lastAuthClientIdSince = null;

        AssertParity(
            new
            {
                success = true,
                changed = false,
                homedApp = "legacy",
                homedAppClientId,
                lastAuthClientId,
                lastAuthClientIdSince,
                probe = new
                {
                    attempted = false,
                    succeeded = false,
                    isTransient = false,
                },
            },
            new UpdateTenantAppHomingResponse
            {
                Success = true,
                Changed = false,
                HomedApp = "legacy",
                HomedAppClientId = null,
                LastAuthClientId = null,
                LastAuthClientIdSince = null,
                Probe = new AppHomingProbeWire
                {
                    Attempted = false,
                    Succeeded = false,
                    IsTransient = false,
                },
            });
    }

    // ---- GetLatestVersions ---------------------------------------------------------------

    [Fact]
    public void GetLatestVersionsResponse_matches_the_versions_shape()
    {
        string? agentVersion = "2.14.3.0";
        string? bootstrapVersion = "1.9";
        string? sha256 = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
        DateTimeOffset? fetchedAtUtc = new DateTimeOffset(2026, 8, 30, 14, 0, 0, TimeSpan.Zero);

        AssertParity(
            new
            {
                latestAgentVersion = agentVersion,
                latestBootstrapScriptVersion = bootstrapVersion,
                latestAgentSha256 = sha256,
                fetchedAtUtc,
                source = "cache"
            },
            new GetLatestVersionsResponse
            {
                LatestAgentVersion = agentVersion,
                LatestBootstrapScriptVersion = bootstrapVersion,
                LatestAgentSha256 = sha256,
                FetchedAtUtc = fetchedAtUtc,
                Source = "cache"
            });
    }

    [Fact]
    public void GetLatestVersionsResponse_omits_all_null_slots_when_the_version_blob_was_unavailable()
    {
        // versions == null at the site: every ?. projection is null, source falls back to "blob".
        string? agentVersion = null;
        string? bootstrapVersion = null;
        string? sha256 = null;
        DateTimeOffset? fetchedAtUtc = null;

        AssertParity(
            new
            {
                latestAgentVersion = agentVersion,
                latestBootstrapScriptVersion = bootstrapVersion,
                latestAgentSha256 = sha256,
                fetchedAtUtc,
                source = "blob"
            },
            new GetLatestVersionsResponse
            {
                LatestAgentVersion = null,
                LatestBootstrapScriptVersion = null,
                LatestAgentSha256 = null,
                FetchedAtUtc = null,
                Source = "blob"
            });
    }

    // ---- GetTenantConfigFieldsSchema -----------------------------------------------------

    [Fact]
    public void GetTenantConfigFieldsSchemaResponse_matches_the_schema_shape()
    {
        IReadOnlyList<TenantConfigFieldSchema> schema = new List<TenantConfigFieldSchema>
        {
            new TenantConfigFieldSchema(
                Name: "contactEmail",
                Type: "string",
                Format: "email",
                Nullable: true,
                Writable: true,
                Reason: null,
                GaOnly: false,
                RevertProtected: false),
            new TenantConfigFieldSchema(
                Name: "homedAppClientId",
                Type: "string",
                Format: null,
                Nullable: true,
                Writable: false,
                Reason: "system-owned",
                GaOnly: true,
                RevertProtected: true),
        };

        AssertParity(
            new
            {
                count = schema.Count,
                writableCount = schema.Count(f => f.Writable),
                fields = schema,
            },
            new GetTenantConfigFieldsSchemaResponse
            {
                Count = schema.Count,
                WritableCount = schema.Count(f => f.Writable),
                Fields = schema,
            });
    }

    // ---- ListTenantConfigBackups ---------------------------------------------------------

    [Fact]
    public void ListTenantConfigBackupsResponse_matches_the_backup_listing_shape()
    {
        var tenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d";
        var backupId = "2519968812345678901-a1b2c3d4";
        var backupTakenAt = new DateTime(2026, 8, 30, 9, 45, 0, DateTimeKind.Utc);
        var changedBy = "admin@contoso.com";
        var source = "mcp-patch";
        string? reason = "enable webhook notifications";
        Dictionary<string, string>? diff = new Dictionary<string, string>
        {
            ["WebhookUrl"] = "(none) -> https://***MASKED***",
        };

        AssertParity(
            new
            {
                tenantId,
                backups = new List<object>
                {
                    new
                    {
                        backupId,
                        backupTakenAt,
                        changedBy,
                        source,
                        reason,
                        diff,
                    },
                },
            },
            new ListTenantConfigBackupsResponse
            {
                TenantId = tenantId,
                Backups = new List<TenantConfigBackupItem>
                {
                    new TenantConfigBackupItem
                    {
                        BackupId = backupId,
                        BackupTakenAt = backupTakenAt,
                        ChangedBy = changedBy,
                        Source = source,
                        Reason = reason,
                        Diff = diff,
                    },
                },
            });
    }

    [Fact]
    public void ListTenantConfigBackupsResponse_omits_null_reason_and_diff()
    {
        var tenantId = "1b1b46b3-41c3-4a3a-8b2c-7e0a2b3c4d5e";
        var backupId = "2519968800000000000-e5f6a7b8";
        var backupTakenAt = new DateTime(2026, 8, 29, 22, 10, 0, DateTimeKind.Utc);
        string? reason = null;
        Dictionary<string, string>? diff = null; // unparseable stored DiffJson → TryParseDiff returns null

        AssertParity(
            new
            {
                tenantId,
                backups = new List<object>
                {
                    new
                    {
                        backupId,
                        backupTakenAt,
                        changedBy = "system",
                        source = "portal-put",
                        reason,
                        diff,
                    },
                },
            },
            new ListTenantConfigBackupsResponse
            {
                TenantId = tenantId,
                Backups = new List<TenantConfigBackupItem>
                {
                    new TenantConfigBackupItem
                    {
                        BackupId = backupId,
                        BackupTakenAt = backupTakenAt,
                        ChangedBy = "system",
                        Source = "portal-put",
                        Reason = null,
                        Diff = null,
                    },
                },
            });
    }

    // ---- PatchTenantConfigurationFields / RevertTenantConfiguration (shared WriteOutcome) -

    [Fact]
    public void TenantConfigPatchOutcomeResponse_matches_the_applied_patch_shape()
    {
        IReadOnlyCollection<string> appliedFields = new List<string> { "ContactEmail", "NotificationsEnabled" };
        Dictionary<string, string>? diff = new Dictionary<string, string>
        {
            ["ContactEmail"] = "(none) -> it@contoso.com",
            ["NotificationsEnabled"] = "False -> True",
        };
        string? backupId = "2519968812345678901-a1b2c3d4";

        AssertParity(
            new
            {
                success = true,
                appliedFields,
                diff,
                backupId,
                noOp = appliedFields.Count == 0,
            },
            new TenantConfigPatchOutcomeResponse
            {
                Success = true,
                AppliedFields = appliedFields,
                Diff = diff,
                BackupId = backupId,
                NoOp = appliedFields.Count == 0,
            });
    }

    [Fact]
    public void TenantConfigPatchOutcomeResponse_omits_null_diff_and_backupId_on_a_noop()
    {
        IReadOnlyCollection<string> appliedFields = new List<string>();
        Dictionary<string, string>? diff = null;
        string? backupId = null;

        AssertParity(
            new
            {
                success = true,
                appliedFields,
                diff,
                backupId,
                noOp = appliedFields.Count == 0,
            },
            new TenantConfigPatchOutcomeResponse
            {
                Success = true,
                AppliedFields = appliedFields,
                Diff = null,
                BackupId = null,
                NoOp = true,
            });
    }

    // ---- SetTenantPlanTier ---------------------------------------------------------------

    [Fact]
    public void SetTenantPlanTierResponse_matches_the_plan_state_shape()
    {
        var tenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d";
        DateTime? trialExpiresUtc = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        DateTime? retentionGraceEndsUtc = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

        AssertParity(
            new
            {
                tenantId,
                planTier = "pro",
                trialExpiresUtc,
                trialConsumed = true,
                effectiveEdition = "pro",
                retentionGraceEndsUtc
            },
            new SetTenantPlanTierResponse
            {
                TenantId = tenantId,
                PlanTier = "pro",
                TrialExpiresUtc = trialExpiresUtc,
                TrialConsumed = true,
                EffectiveEdition = "pro",
                RetentionGraceEndsUtc = retentionGraceEndsUtc
            });
    }

    [Fact]
    public void SetTenantPlanTierResponse_omits_null_trial_and_grace_slots()
    {
        var tenantId = "1b1b46b3-41c3-4a3a-8b2c-7e0a2b3c4d5e";
        DateTime? trialExpiresUtc = null;
        DateTime? retentionGraceEndsUtc = null;

        AssertParity(
            new
            {
                tenantId,
                planTier = "community",
                trialExpiresUtc,
                trialConsumed = false,
                effectiveEdition = "community",
                retentionGraceEndsUtc
            },
            new SetTenantPlanTierResponse
            {
                TenantId = tenantId,
                PlanTier = "community",
                TrialExpiresUtc = null,
                TrialConsumed = false,
                EffectiveEdition = "community",
                RetentionGraceEndsUtc = null
            });
    }

    // ---- StartTenantTrial ----------------------------------------------------------------

    [Fact]
    public void StartTenantTrialResponse_matches_the_trial_start_shape()
    {
        var tenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d";
        DateTime? trialStartedUtc = new DateTime(2026, 8, 30, 15, 0, 0, DateTimeKind.Utc);
        DateTime? trialExpiresUtc = trialStartedUtc.Value.AddDays(30);

        AssertParity(
            new
            {
                tenantId,
                trialStartedUtc,
                trialExpiresUtc,
                effectiveEdition = "pro"
            },
            new StartTenantTrialResponse
            {
                TenantId = tenantId,
                TrialStartedUtc = trialStartedUtc,
                TrialExpiresUtc = trialExpiresUtc,
                EffectiveEdition = "pro"
            });
    }

    [Fact]
    public void StartTenantTrialResponse_omits_null_trial_slots()
    {
        // The site always sets both dates before responding — this pins that a null slot
        // would vanish identically on both sides anyway (compile-time type is DateTime?).
        var tenantId = "1b1b46b3-41c3-4a3a-8b2c-7e0a2b3c4d5e";
        DateTime? trialStartedUtc = null;
        DateTime? trialExpiresUtc = null;

        AssertParity(
            new
            {
                tenantId,
                trialStartedUtc,
                trialExpiresUtc,
                effectiveEdition = "pro"
            },
            new StartTenantTrialResponse
            {
                TenantId = tenantId,
                TrialStartedUtc = null,
                TrialExpiresUtc = null,
                EffectiveEdition = "pro"
            });
    }

    // ---- GetPlanTierDefinitions / SetPlanTierDefinitions ---------------------------------

    [Fact]
    public void PlanTierDefinitionsResponse_matches_the_tiers_shape()
    {
        var tiers = new List<PlanTierDefinition>
        {
            new PlanTierDefinition
            {
                Name = "community",
                DailyRequestLimit = 100,
                MonthlyRequestLimit = 3000,
                Description = "Free tier",
            },
            new PlanTierDefinition
            {
                Name = "pro",
                DailyRequestLimit = 1000,
                MonthlyRequestLimit = 30000,
                Description = "Paid tier",
            },
        };

        AssertParity(
            new { tiers },
            new PlanTierDefinitionsResponse { Tiers = tiers });
    }

    // ---- TestWebhookNotification (dual-purpose: success carries the delivery verdict) ----

    [Fact]
    public void TestWebhookNotificationResponse_matches_the_delivery_verdict_shape()
    {
        int? statusCode = 200;

        AssertParity(
            new
            {
                success = true,
                statusCode,
                message = "Test notification sent successfully"
            },
            new TestWebhookNotificationResponse
            {
                Success = true,
                StatusCode = statusCode,
                Message = "Test notification sent successfully"
            });
    }

    [Fact]
    public void TestWebhookNotificationResponse_omits_a_null_statusCode_when_the_send_got_no_response()
    {
        int? statusCode = null;

        AssertParity(
            new
            {
                success = false,
                statusCode,
                message = "The webhook endpoint could not be reached."
            },
            new TestWebhookNotificationResponse
            {
                Success = false,
                StatusCode = null,
                Message = "The webhook endpoint could not be reached."
            });
    }

    // ---- UpdateAdminConfiguration --------------------------------------------------------

    [Fact]
    public void UpdateAdminConfigurationResponse_matches_the_ack_plus_config_shape()
    {
        var config = new AdminConfiguration
        {
            PartitionKey = "GlobalConfig",
            RowKey = "config",
            UpdatedBy = "admin@contoso.com",
        };

        AssertParity(
            new
            {
                success = true,
                message = "Admin configuration updated successfully",
                config = config
            },
            new UpdateAdminConfigurationResponse
            {
                Success = true,
                Message = "Admin configuration updated successfully",
                Config = config
            });
    }

    // ---- UpdateTenantConfiguration -------------------------------------------------------

    [Fact]
    public void UpdateTenantConfigurationResponse_matches_the_ack_plus_config_shape()
    {
        var config = new TenantConfiguration
        {
            TenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d",
            DomainName = "contoso.com",
            UpdatedBy = "admin@contoso.com",
        };

        AssertParity(
            new
            {
                success = true,
                message = "Configuration updated successfully",
                config = config
            },
            new UpdateTenantConfigurationResponse
            {
                Success = true,
                Message = "Configuration updated successfully",
                Config = config
            });
    }

    private static void AssertParity(object anonymousLiteral, IApiResponse typed)
        => ApiResponseWireParityTests.AssertWireIdentical(anonymousLiteral, typed);
}

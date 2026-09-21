using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Backup;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Wire-identity proof for moving the backup job, row-restore and queued-run bodies off their
/// private <c>WriteJsonAsync(req, status, object)</c> wrappers onto <c>ResponseHelper</c>.
/// Two things changed at once and both are pinned here: the anonymous 202 bodies became DTOs,
/// and the typed bodies left <see cref="BackupManifestJson.SerializerOptions"/> for the worker
/// pipeline (<see cref="ApiJsonOptions"/>). The two option sets differ only in how they READ
/// enums (<c>allowIntegerValues</c>); the facts below keep the WRITE side honest if either set
/// ever gains a naming policy, a key policy or a converter.
/// </summary>
public class BackupWireParityTests
{
    private static readonly JsonSerializerOptions WireOptions = ApiJsonOptions.Create();

    private static JsonElement Json(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private static void AssertSameUnderBothOptionSets(object body)
    {
        var backupSurface = JsonSerializer.Serialize(body, body.GetType(), BackupManifestJson.SerializerOptions);
        var pipeline = JsonSerializer.Serialize(body, body.GetType(), WireOptions);
        Assert.Equal(backupSurface, pipeline);
    }

    [Fact]
    public void BackupJobStatus_serializes_identically_under_both_option_sets()
    {
        var t = new DateTime(2026, 9, 21, 4, 0, 0, DateTimeKind.Utc);

        AssertSameUnderBothOptionSets(new BackupJobStatus
        {
            JobId = "0f8fad5bd9cb469fa16570867728950e",
            Kind = BackupJobKind.RestoreTable,
            State = BackupJobState.BlockedTerminal,
            RequestedBy = "admin@contoso.example",
            QueuedAtUtc = t,
            StartedAtUtc = t.AddSeconds(3),
            CompletedAtUtc = t.AddMinutes(2),
            LastHeartbeatUtc = t.AddMinutes(2),
            BackupId = "20260921T040000Z_a1b2c3d4",
            SourceBackupId = "20260920T040000Z_e5f6a7b8",
            TableName = "TenantConfiguration",
            Strategy = "upsert-only",
            Progress = "{\"phase\":\"copy\",\"rows\":12}",
            Error = "IntegrityCheckFailed",
            BackupOutcome = BackupOutcome.Partial,
        });

        // Freshly queued: every nullable slot absent on both sides.
        AssertSameUnderBothOptionSets(new BackupJobStatus
        {
            JobId = "0f8fad5bd9cb469fa16570867728950e",
            Kind = BackupJobKind.Backup,
            State = BackupJobState.Queued,
            RequestedBy = "admin@contoso.example",
            QueuedAtUtc = t,
            LastHeartbeatUtc = t,
        });
    }

    [Fact]
    public void RestoreRowPreviewResponse_serializes_identically_under_both_option_sets()
    {
        var text = new RestoreRowPropertySnapshot { EdmType = "Edm.String", Value = Json("\"Pro\"") };
        var flag = new RestoreRowPropertySnapshot { EdmType = "Edm.Boolean", Value = Json("true") };
        var number = new RestoreRowPropertySnapshot { EdmType = "Edm.Int64", Value = Json("\"9007199254740993\"") };

        AssertSameUnderBothOptionSets(new RestoreRowPreviewResponse
        {
            BackupId = "20260921T040000Z_a1b2c3d4",
            TableName = "TenantConfiguration",
            PartitionKey = "tenant/with+odd%chars",
            RowKey = "config",
            // Case-faithful column names: a key policy on either side would rewrite them.
            BackupProperties = { ["PlanTier"] = text, ["IsEnabled"] = flag, ["lowerCaseColumn"] = number },
            CurrentProperties = new Dictionary<string, RestoreRowPropertySnapshot> { ["PlanTier"] = text },
            Diff =
            {
                new RestoreRowPropertyDiff { Name = "IsEnabled", Kind = RestoreRowDiffKind.Added, Backup = flag },
                new RestoreRowPropertyDiff { Name = "PlanTier", Kind = RestoreRowDiffKind.Unchanged, Backup = text, Current = text },
            },
            RowSha256 = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
            CurrentETag = "W/\"datetime'2026-09-21T04%3A00%3A00Z'\"",
            IsAuthTable = true,
        });

        // Live row absent: currentProperties and currentETag vanish on both sides.
        AssertSameUnderBothOptionSets(new RestoreRowPreviewResponse
        {
            BackupId = "20260921T040000Z_a1b2c3d4",
            TableName = "TenantConfiguration",
            PartitionKey = "pk",
            RowKey = "rk",
            BackupProperties = { ["PlanTier"] = text },
            Diff = { new RestoreRowPropertyDiff { Name = "PlanTier", Kind = RestoreRowDiffKind.Added, Backup = text } },
            RowSha256 = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
        });
    }

    [Fact]
    public void RestoreRowCommitResponse_serializes_identically_under_both_option_sets()
    {
        AssertSameUnderBothOptionSets(new RestoreRowCommitResponse
        {
            BackupId = "20260921T040000Z_a1b2c3d4",
            TableName = "TenantConfiguration",
            PartitionKey = "pk",
            RowKey = "rk",
            Outcome = RestoreRowCommitOutcome.Replaced,
        });
    }

    [Fact]
    public void BackupTriggerResponse_matches_the_anonymous_202_body()
    {
        const string jobId = "0f8fad5bd9cb469fa16570867728950e";
        var old = new { jobId, statusUrl = $"/api/global/backups/jobs/{jobId}" };

        Assert.Equal(
            JsonSerializer.Serialize(old, old.GetType(), BackupManifestJson.SerializerOptions),
            JsonSerializer.Serialize(
                new BackupTriggerResponse { JobId = jobId, StatusUrl = $"/api/global/backups/jobs/{jobId}" },
                WireOptions));
    }

    [Fact]
    public void SessionDeletionMaintenanceTriggerResponse_matches_the_anonymous_202_body()
    {
        var old = new { message = "Maintenance run queued.", triggeredBy = "admin@contoso.example" };

        // The old wrapper serialized with DEFAULT options; its keys were camelCase by hand.
        Assert.Equal(
            JsonSerializer.Serialize(old, old.GetType()),
            JsonSerializer.Serialize(
                new SessionDeletionMaintenanceTriggerResponse { Message = "Maintenance run queued.", TriggeredBy = "admin@contoso.example" },
                WireOptions));
    }

    [Fact]
    public void Delegation_acknowledgements_match_the_anonymous_message_body()
    {
        ApiResponseWireParityTests.AssertWireIdentical(
            new { message = "Delegation ended" },
            new MessageResponse { Message = "Delegation ended" });
    }
}

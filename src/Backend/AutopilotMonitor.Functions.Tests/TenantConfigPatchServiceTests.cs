using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Transactional config patch/revert: field gates, fail-closed backup, CAS retry,
/// exactly-these-fields verification with automatic rollback, and the phase-2
/// caller-tier deny-lists. The harness emulates real table CAS semantics (versioned
/// ETags, conditional replace) so the retry/rollback paths are exercised honestly.
/// </summary>
public class TenantConfigPatchServiceTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string Ga = "ga@operator.example";

    /// <summary>In-memory row with real CAS semantics + optional write-time corruption hook.</summary>
    private sealed class Harness
    {
        public Mock<IConfigRepository> Repo { get; } = new();
        public Mock<IConfigBackupRepository> Backup { get; } = new();
        public Mock<IMaintenanceRepository> Maintenance { get; } = new();
        public TenantConfigPatchService Sut { get; }

        public TenantConfiguration? Current;
        public int Version = 1;
        public List<ConfigBackupEntry> Backups { get; } = new();
        public int ReplaceCalls;
        /// <summary>Applied to the stored row on every successful replace — simulates serialization drift.</summary>
        public Func<TenantConfiguration, TenantConfiguration>? CorruptOnWrite;
        /// <summary>Bumps the version WITHOUT a replace before the Nth replace call — simulates a concurrent writer.</summary>
        public int? StealEtagBeforeReplaceCall;

        public Harness(TenantConfiguration? stored)
        {
            Current = stored;

            Repo.Setup(r => r.GetTenantConfigurationWithEtagAsync(TenantId))
                .ReturnsAsync(() => Current == null
                    ? ((TenantConfiguration, string)?)null
                    : (Clone(Current), $"etag-{Version}"));

            Repo.Setup(r => r.TryReplaceTenantConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string>()))
                .Returns<TenantConfiguration, string>((config, etag) =>
                {
                    ReplaceCalls++;
                    if (StealEtagBeforeReplaceCall == ReplaceCalls)
                    {
                        Version++; // the concurrent writer got there first
                        StealEtagBeforeReplaceCall = null;
                    }
                    if (etag != $"etag-{Version}")
                        return Task.FromResult(false);
                    Current = CorruptOnWrite != null ? CorruptOnWrite(Clone(config)) : Clone(config);
                    Version++;
                    return Task.FromResult(true);
                });

            Backup.Setup(b => b.UpsertAsync(It.IsAny<ConfigBackupEntry>(), It.IsAny<CancellationToken>()))
                .Callback<ConfigBackupEntry, CancellationToken>((e, _) => Backups.Add(e))
                .Returns(Task.CompletedTask);
            Backup.Setup(b => b.ListByPartitionAsync(TenantId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Backups.AsEnumerable().Reverse().ToList()); // newest first
            Backup.Setup(b => b.TryGetAsync(TenantId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, CancellationToken>((_, id, _) =>
                    Task.FromResult(Backups.FirstOrDefault(b => b.RowKey == id)));

            Maintenance.Setup(m => m.LogAuditEntryAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()))
                .ReturnsAsync(true);

            var configService = new TenantConfigurationService(
                Repo.Object, NullLogger<TenantConfigurationService>.Instance,
                new MemoryCache(new MemoryCacheOptions()));

            Sut = new TenantConfigPatchService(
                Repo.Object, Backup.Object, configService, Maintenance.Object,
                NullLogger<TenantConfigPatchService>.Instance);
        }

        private static TenantConfiguration Clone(TenantConfiguration c)
            => JsonConvert.DeserializeObject<TenantConfiguration>(JsonConvert.SerializeObject(c))!;
    }

    private static TenantConfiguration Stored()
    {
        var config = TenantConfiguration.CreateDefault(TenantId);
        config.DomainName = "contoso.com";
        config.UpdatedBy = "admin@contoso.com";
        config.DataRetentionDays = 30;
        config.TeamsWebhookUrl = "https://contoso.webhook.office.com/hook";
        return config;
    }

    private static JObject Fields(params (string Key, object? Value)[] fields)
    {
        var o = new JObject();
        foreach (var (key, value) in fields)
            o[key] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
        return o;
    }

    // ── Happy path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Patch_SingleField_Applies_BacksUp_Verifies_Audits()
    {
        var harness = new Harness(Stored());

        var outcome = await harness.Sut.ApplyFieldPatchAsync(
            TenantId, Fields(("dataRetentionDays", 90)), Ga, "mcp-patch", "retention bump");

        Assert.True(outcome.Success);
        Assert.Equal(new[] { "DataRetentionDays" }, outcome.AppliedFields);
        Assert.Equal(90, harness.Current!.DataRetentionDays);
        Assert.Equal(Ga, harness.Current.UpdatedBy);
        Assert.NotNull(outcome.BackupId);
        var backup = Assert.Single(harness.Backups);
        Assert.Equal("mcp-patch", backup.Source);
        Assert.Equal("retention bump", backup.Reason);
        Assert.Contains("\"DataRetentionDays\":30", backup.EntityJson); // snapshot = PRE-write state
        Assert.True(outcome.MaskedDiff!.ContainsKey("DataRetentionDays"));
        harness.Maintenance.Verify(m => m.LogAuditEntryAsync(
            TenantId, "PATCH", "TenantConfiguration", TenantId, Ga,
            It.Is<Dictionary<string, string>?>(d => d != null && d["BackupId"] == outcome.BackupId)), Times.Once);
    }

    [Fact]
    public async Task Patch_NullValue_ClearsNullableField()
    {
        var stored = Stored();
        stored.ContactEmail = "ops@contoso.com";
        var harness = new Harness(stored);

        var outcome = await harness.Sut.ApplyFieldPatchAsync(
            TenantId, Fields(("contactEmail", null)), Ga, "mcp-patch", null);

        Assert.True(outcome.Success);
        Assert.Null(harness.Current!.ContactEmail);
    }

    [Fact]
    public async Task Patch_NoOp_ValueEqualsStored_NoBackupNoWrite()
    {
        var harness = new Harness(Stored());

        var outcome = await harness.Sut.ApplyFieldPatchAsync(
            TenantId, Fields(("dataRetentionDays", 30)), Ga, "mcp-patch", null);

        Assert.True(outcome.Success);
        Assert.Empty(outcome.AppliedFields);
        Assert.Null(outcome.BackupId);
        Assert.Empty(harness.Backups);
        Assert.Equal(0, harness.ReplaceCalls);
    }

    // ── Field gate ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("planTier")]
    [InlineData("trialExpiresUtc")]
    [InlineData("homedAppClientId")]
    [InlineData("lastAuthClientId")]
    [InlineData("onboardedBy")]
    [InlineData("tenantId")]
    [InlineData("domainName")]
    [InlineData("lastUpdated")]
    [InlineData("updatedBy")]
    public async Task Patch_DeniedField_Returns400WithFieldName(string field)
    {
        var harness = new Harness(Stored());

        var outcome = await harness.Sut.ApplyFieldPatchAsync(
            TenantId, Fields((field, "x")), Ga, "mcp-patch", null);

        Assert.False(outcome.Success);
        Assert.Equal(PatchFailure.InvalidField, outcome.Failure);
        Assert.Contains(field, outcome.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Backups);
        Assert.Equal(0, harness.ReplaceCalls);
    }

    [Fact]
    public async Task Patch_UnknownField_Returns400WithFieldName()
    {
        var harness = new Harness(Stored());

        var outcome = await harness.Sut.ApplyFieldPatchAsync(
            TenantId, Fields(("definitelyNotAField", 1)), Ga, "mcp-patch", null);

        Assert.False(outcome.Success);
        Assert.Equal(PatchFailure.InvalidField, outcome.Failure);
        Assert.Contains("definitelyNotAField", outcome.Error);
    }

    [Fact]
    public async Task Patch_RedactedPlaceholderValue_Rejected()
    {
        var harness = new Harness(Stored());

        var outcome = await harness.Sut.ApplyFieldPatchAsync(
            TenantId, Fields(("webhookUrl", Constants.RedactedSecretPlaceholder)), Ga, "mcp-patch", null);

        Assert.False(outcome.Success);
        Assert.Equal(PatchFailure.InvalidField, outcome.Failure);
        Assert.Contains("redacted", outcome.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Patch_GaOnlyField_AllowedForGa_DeniedForTenantAdminTier()
    {
        var gaHarness = new Harness(Stored());
        var gaOutcome = await gaHarness.Sut.ApplyFieldPatchAsync(
            TenantId, Fields(("bootstrapTokenEnabled", true)), Ga, "mcp-patch", null,
            TenantConfigCallerTier.GlobalAdmin);
        Assert.True(gaOutcome.Success);

        foreach (var tier in new[] { TenantConfigCallerTier.TenantAdmin, TenantConfigCallerTier.DelegatedAdmin })
        {
            var harness = new Harness(Stored());
            var outcome = await harness.Sut.ApplyFieldPatchAsync(
                TenantId, Fields(("bootstrapTokenEnabled", true)), "admin@contoso.com", "api-patch", null, tier);
            Assert.False(outcome.Success);
            Assert.Equal(PatchFailure.InvalidField, outcome.Failure);
        }
    }

    // ── Validation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Patch_InvalidWebhookUrl_ValidationFails_NoWrite()
    {
        var harness = new Harness(Stored());

        var outcome = await harness.Sut.ApplyFieldPatchAsync(
            TenantId, Fields(("webhookUrl", "http://169.254.169.254/latest")), Ga, "mcp-patch", null);

        Assert.False(outcome.Success);
        Assert.Equal(PatchFailure.ValidationFailed, outcome.Failure);
        Assert.Equal(0, harness.ReplaceCalls);
        Assert.Empty(harness.Backups);
    }

    [Fact]
    public async Task Patch_BadNotificationChannelsJson_ValidationFails()
    {
        var harness = new Harness(Stored());

        var outcome = await harness.Sut.ApplyFieldPatchAsync(
            TenantId, Fields(("notificationChannelsJson", "{not json")), Ga, "mcp-patch", null);

        Assert.False(outcome.Success);
        Assert.Equal(PatchFailure.ValidationFailed, outcome.Failure);
    }

    [Fact]
    public async Task Patch_UnrestrictedModeWithoutGate_ExplicitError_NotSilentFlip()
    {
        var harness = new Harness(Stored()); // gate (UnrestrictedModeEnabled) is false by default

        var outcome = await harness.Sut.ApplyFieldPatchAsync(
            TenantId, Fields(("unrestrictedMode", true)), Ga, "mcp-patch", null);

        Assert.False(outcome.Success);
        Assert.Equal(PatchFailure.ValidationFailed, outcome.Failure);
        Assert.Contains("UnrestrictedModeEnabled", outcome.Error);
        Assert.Equal(0, harness.ReplaceCalls);
    }

    // ── Storage edges ───────────────────────────────────────────────────────

    [Fact]
    public async Task Patch_NoConfigRow_NotFound()
    {
        var harness = new Harness(stored: null);

        var outcome = await harness.Sut.ApplyFieldPatchAsync(
            TenantId, Fields(("dataRetentionDays", 90)), Ga, "mcp-patch", null);

        Assert.False(outcome.Success);
        Assert.Equal(PatchFailure.NotFound, outcome.Failure);
    }

    [Fact]
    public async Task Patch_BackupFailure_IsFailClosed_NoWrite()
    {
        var harness = new Harness(Stored());
        harness.Backup.Setup(b => b.UpsertAsync(It.IsAny<ConfigBackupEntry>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("backup table down"));

        var outcome = await harness.Sut.ApplyFieldPatchAsync(
            TenantId, Fields(("dataRetentionDays", 90)), Ga, "mcp-patch", null);

        Assert.False(outcome.Success);
        Assert.Equal(PatchFailure.BackupFailed, outcome.Failure);
        Assert.Equal(0, harness.ReplaceCalls);
        Assert.Equal(30, harness.Current!.DataRetentionDays); // untouched
    }

    [Fact]
    public async Task Patch_CasRace_RetriesAndSucceeds()
    {
        var harness = new Harness(Stored());
        harness.StealEtagBeforeReplaceCall = 1; // first replace loses, retry wins

        var outcome = await harness.Sut.ApplyFieldPatchAsync(
            TenantId, Fields(("dataRetentionDays", 90)), Ga, "mcp-patch", null);

        Assert.True(outcome.Success);
        Assert.Equal(2, harness.ReplaceCalls);
        Assert.Equal(90, harness.Current!.DataRetentionDays);
    }

    [Fact]
    public async Task Patch_CasRaceEveryAttempt_ReturnsConflict()
    {
        var harness = new Harness(Stored());
        harness.Repo.Setup(r => r.TryReplaceTenantConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string>()))
            .ReturnsAsync(false); // permanent race

        var outcome = await harness.Sut.ApplyFieldPatchAsync(
            TenantId, Fields(("dataRetentionDays", 90)), Ga, "mcp-patch", null);

        Assert.False(outcome.Success);
        Assert.Equal(PatchFailure.WriteConflict, outcome.Failure);
    }

    // ── Verify + rollback ───────────────────────────────────────────────────

    [Fact]
    public async Task Patch_DriftOnWrite_RollsBackToInitial_AndReportsDriftNames()
    {
        var harness = new Harness(Stored());
        // Simulated serialization drift: every write silently zeroes SessionTimeoutHours.
        // The verify must catch the unexpected field and roll the row back. (The rollback
        // write itself is also corrupted by the hook, but rollback success is judged by the
        // CAS result — clear the hook after the first corruption so the rollback restores.)
        harness.CorruptOnWrite = written =>
        {
            harness.CorruptOnWrite = null;
            written.SessionTimeoutHours = 0;
            return written;
        };

        var outcome = await harness.Sut.ApplyFieldPatchAsync(
            TenantId, Fields(("dataRetentionDays", 90)), Ga, "mcp-patch", null);

        Assert.False(outcome.Success);
        Assert.Equal(PatchFailure.DriftRolledBack, outcome.Failure);
        Assert.Contains(outcome.Drift!, d => d.Contains("SessionTimeoutHours"));
        Assert.NotNull(outcome.BackupId); // pre-write snapshot exists for manual inspection
        // The row is back at the initial state: retention 30, timeout restored.
        Assert.Equal(30, harness.Current!.DataRetentionDays);
        Assert.Equal(Stored().SessionTimeoutHours, harness.Current.SessionTimeoutHours);
        // No success audit for a rolled-back write.
        harness.Maintenance.Verify(m => m.LogAuditEntryAsync(
            It.IsAny<string>(), "PATCH", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<Dictionary<string, string>?>()), Times.Never);
    }

    [Fact]
    public async Task Patch_DriftAndRollbackLosesRace_ReportsManualRestoreWithBackupId()
    {
        var harness = new Harness(Stored());
        harness.CorruptOnWrite = written => { written.SessionTimeoutHours = 0; return written; };
        // Every write corrupts — including the rollback attempt — but the decisive part:
        // steal the ETag before the rollback replace so the rollback itself loses the race.
        harness.StealEtagBeforeReplaceCall = 2;

        var outcome = await harness.Sut.ApplyFieldPatchAsync(
            TenantId, Fields(("dataRetentionDays", 90)), Ga, "mcp-patch", null);

        Assert.False(outcome.Success);
        Assert.Equal(PatchFailure.DriftRollbackFailed, outcome.Failure);
        Assert.Contains(outcome.BackupId!, outcome.Error);
    }

    // ── Revert ──────────────────────────────────────────────────────────────

    private async Task<(Harness Harness, string BackupId)> PatchedHarnessAsync()
    {
        var harness = new Harness(Stored());
        var patch = await harness.Sut.ApplyFieldPatchAsync(
            TenantId, Fields(("dataRetentionDays", 90)), Ga, "mcp-patch", "bump");
        Assert.True(patch.Success);
        return (harness, patch.BackupId!);
    }

    [Fact]
    public async Task Revert_Latest_RestoresPatchedField_AndBacksUpCurrentFirst()
    {
        var (harness, _) = await PatchedHarnessAsync();
        Assert.Equal(90, harness.Current!.DataRetentionDays);

        var outcome = await harness.Sut.RevertAsync(
            TenantId, backupId: null, includeProtectedFields: false, Ga, "mcp-revert", null);

        Assert.True(outcome.Success);
        Assert.Equal(30, harness.Current!.DataRetentionDays); // back to the pre-patch value
        Assert.Equal(2, harness.Backups.Count); // revert snapshotted the 90-state first
        Assert.Contains("\"DataRetentionDays\":90", harness.Backups[1].EntityJson);
        harness.Maintenance.Verify(m => m.LogAuditEntryAsync(
            TenantId, "REVERT", "TenantConfiguration", TenantId, Ga,
            It.IsAny<Dictionary<string, string>?>()), Times.Once);
    }

    [Fact]
    public async Task Revert_ByBackupId_UsesThatSnapshot()
    {
        var (harness, backupId) = await PatchedHarnessAsync();

        var outcome = await harness.Sut.RevertAsync(
            TenantId, backupId, includeProtectedFields: false, Ga, "mcp-revert", null);

        Assert.True(outcome.Success);
        Assert.Equal(30, harness.Current!.DataRetentionDays);
    }

    [Fact]
    public async Task Revert_PreservesProtectedFields_ByDefault()
    {
        var (harness, backupId) = await PatchedHarnessAsync();
        // Plan + homing changed AFTER the snapshot (via their dedicated flows).
        harness.Current!.PlanTier = "enterprise";
        harness.Current.HomedAppClientId = "886ab5e2-6144-442c-80cc-9b28e0667731";

        var outcome = await harness.Sut.RevertAsync(
            TenantId, backupId, includeProtectedFields: false, Ga, "mcp-revert", null);

        Assert.True(outcome.Success);
        Assert.Equal(30, harness.Current!.DataRetentionDays);      // reverted
        Assert.Equal("enterprise", harness.Current.PlanTier);      // protected — kept current
        Assert.Equal("886ab5e2-6144-442c-80cc-9b28e0667731", harness.Current.HomedAppClientId);
    }

    [Fact]
    public async Task Revert_IncludeProtectedFields_RestoresThemToo()
    {
        var (harness, backupId) = await PatchedHarnessAsync();
        harness.Current!.PlanTier = "enterprise";

        var outcome = await harness.Sut.RevertAsync(
            TenantId, backupId, includeProtectedFields: true, Ga, "mcp-revert", null);

        Assert.True(outcome.Success);
        Assert.Equal(Stored().PlanTier, harness.Current!.PlanTier); // snapshot value restored
    }

    [Fact]
    public async Task Revert_NoBackups_NotFound()
    {
        var harness = new Harness(Stored());

        var outcome = await harness.Sut.RevertAsync(
            TenantId, backupId: null, includeProtectedFields: false, Ga, "mcp-revert", null);

        Assert.False(outcome.Success);
        Assert.Equal(PatchFailure.NotFound, outcome.Failure);
    }

    [Fact]
    public async Task Revert_UnknownBackupId_NotFound()
    {
        var (harness, _) = await PatchedHarnessAsync();

        var outcome = await harness.Sut.RevertAsync(
            TenantId, "does-not-exist", includeProtectedFields: false, Ga, "mcp-revert", null);

        Assert.False(outcome.Success);
        Assert.Equal(PatchFailure.NotFound, outcome.Failure);
    }

    // ── Rehydration roundtrip (serialization-drift canary) ─────────────────

    [Fact]
    public void RehydrateEntity_Roundtrip_ProducesIdenticalModel()
    {
        var original = Stored();
        original.ContactEmail = "ops@contoso.com";
        original.TrialExpiresUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        original.LastAuthClientIdSince = new DateTime(2026, 7, 31, 18, 3, 0, DateTimeKind.Utc);
        original.CustomRateLimitRequestsPerMinute = 42;

        // Snapshot exactly as the backup hook does, then rehydrate and re-map.
        var snapshot = TableConfigRepository.BuildBackupEntry(
            TableConfigRepository.ConvertToTenantTableEntity(original),
            TenantId, Ga, "test", null, null);
        var rehydrated = TableConfigRepository.ConvertFromTenantTableEntity(
            TenantConfigPatchService.RehydrateTenantConfigEntity(snapshot.EntityJson, TenantId));

        var diff = AutopilotMonitor.Functions.Helpers.ConfigPropertyComparer
            .GetChangedPropertyNames(original, rehydrated);
        Assert.True(diff.Count == 0, "Rehydration drift: " + string.Join(", ", diff));
    }

    [Fact]
    public void RehydrateEntity_DateLookingStringField_StaysString()
    {
        var original = Stored();
        original.DisabledReason = "2026-08-03T12:00:00+00:00"; // adversarial: a string that parses as a date

        var snapshot = TableConfigRepository.BuildBackupEntry(
            TableConfigRepository.ConvertToTenantTableEntity(original),
            TenantId, Ga, "test", null, null);
        var rehydrated = TableConfigRepository.ConvertFromTenantTableEntity(
            TenantConfigPatchService.RehydrateTenantConfigEntity(snapshot.EntityJson, TenantId));

        Assert.Equal(original.DisabledReason, rehydrated.DisabledReason);
    }
}

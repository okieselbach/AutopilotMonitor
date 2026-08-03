using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The universal pre-write snapshot hook in <see cref="TableConfigRepository"/>:
/// every overwriting save of a TenantConfiguration/AdminConfiguration row must first
/// snapshot the stored row into ConfigurationBackups — except row creations and
/// noise-only changes — and a backup-storage failure must never break the save (fail-soft).
/// </summary>
public class ConfigBackupHookTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    private sealed class Harness
    {
        public TableConfigRepository Sut { get; }
        public Mock<TableClient> Table { get; } = new();
        public Mock<IConfigBackupRepository> Backup { get; } = new();
        public List<TableEntity> Upserts { get; } = new();

        public Harness(TableEntity? stored)
        {
            if (stored == null)
            {
                Table.Setup(c => c.GetEntityAsync<TableEntity>(
                        It.IsAny<string>(), It.IsAny<string>(),
                        It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new RequestFailedException(404, "Not Found", "ResourceNotFound", null));
            }
            else
            {
                Table.Setup(c => c.GetEntityAsync<TableEntity>(
                        It.IsAny<string>(), It.IsAny<string>(),
                        It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Response.FromValue(stored, Mock.Of<Response>()));
            }

            Table.Setup(c => c.UpsertEntityAsync(
                    It.IsAny<TableEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, TableUpdateMode, CancellationToken>((e, _, _) =>
                {
                    Upserts.Add(e);
                    return Task.FromResult(Mock.Of<Response>());
                });

            var serviceClient = new Mock<TableServiceClient>();
            serviceClient.Setup(c => c.GetTableClient(It.IsAny<string>())).Returns(Table.Object);

            Sut = new TableConfigRepository(
                new TableStorageService(serviceClient.Object, NullLogger<TableStorageService>.Instance),
                Backup.Object,
                NullLogger<TableConfigRepository>.Instance);
        }
    }

    private static TenantConfiguration StoredConfig()
    {
        var config = TenantConfiguration.CreateDefault(TenantId);
        config.DomainName = "contoso.com";
        config.UpdatedBy = "admin@contoso.com";
        config.DataRetentionDays = 30;
        config.TeamsWebhookUrl = "https://contoso.webhook.office.com/hook";
        return config;
    }

    /// <summary>
    /// Mirrors production: every real writer round-trips the STORED row (read-modify-write),
    /// so time-stamped fields like OnboardedAt match the stored values exactly. Constructing
    /// a second CreateDefault instead would fabricate an OnboardedAt diff that cannot occur.
    /// </summary>
    private static TenantConfiguration RoundtripClone(TenantConfiguration stored)
        => TableConfigRepository.ConvertFromTenantTableEntity(
            TableConfigRepository.ConvertToTenantTableEntity(stored));

    [Fact]
    public async Task RealChange_SnapshotsStoredRow_ThenSaves_ThenPrunes()
    {
        var stored = StoredConfig();
        var harness = new Harness(TableConfigRepository.ConvertToTenantTableEntity(stored));

        var incoming = RoundtripClone(stored);
        incoming.DataRetentionDays = 90;
        incoming.UpdatedBy = "ga@operator.example";

        ConfigBackupEntry? snapshot = null;
        harness.Backup
            .Setup(b => b.UpsertAsync(It.IsAny<ConfigBackupEntry>(), It.IsAny<CancellationToken>()))
            .Callback<ConfigBackupEntry, CancellationToken>((e, _) => snapshot = e)
            .Returns(Task.CompletedTask);

        var saved = await harness.Sut.SaveTenantConfigurationAsync(incoming, "portal-put", "test change");

        Assert.True(saved);
        Assert.Single(harness.Upserts); // the config row itself
        Assert.NotNull(snapshot);
        Assert.Equal(TenantId, snapshot!.PartitionKey);
        Assert.Equal("portal-put", snapshot.Source);
        Assert.Equal("test change", snapshot.Reason);
        Assert.Equal("ga@operator.example", snapshot.ChangedBy); // identity about to write
        // The snapshot holds the STORED row (retention 30), not the incoming one.
        Assert.Contains("\"DataRetentionDays\":30", snapshot.EntityJson);
        // Raw secrets ARE in EntityJson (full-fidelity restore source)…
        Assert.Contains("contoso.webhook.office.com", snapshot.EntityJson);
        // …but the advisory diff masks them by property-name heuristics (ConfigDiffHelper).
        Assert.DoesNotContain("contoso.webhook.office.com", snapshot.DiffJson ?? string.Empty);
        harness.Backup.Verify(
            b => b.PruneAsync(TenantId, Constants.ConfigBackupKeepCount, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RowCreation_NoExistingRow_NoSnapshot()
    {
        var harness = new Harness(stored: null);

        var saved = await harness.Sut.SaveTenantConfigurationAsync(StoredConfig(), "portal-put", null);

        Assert.True(saved);
        Assert.Single(harness.Upserts);
        harness.Backup.Verify(
            b => b.UpsertAsync(It.IsAny<ConfigBackupEntry>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Backup.Verify(
            b => b.PruneAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NoiseOnlyChange_AuthClientIdFlip_NoSnapshot()
    {
        var stored = StoredConfig();
        var harness = new Harness(TableConfigRepository.ConvertToTenantTableEntity(stored));

        // The auth-side-effect write: only LastAuthClientId/Since + bookkeeping move.
        var incoming = RoundtripClone(stored);
        incoming.LastAuthClientId = "886ab5e2-6144-442c-80cc-9b28e0667731";
        incoming.LastAuthClientIdSince = DateTime.UtcNow;
        incoming.LastUpdated = DateTime.UtcNow;
        incoming.UpdatedBy = "System (auth)";

        var saved = await harness.Sut.SaveTenantConfigurationAsync(incoming, "auth", null);

        Assert.True(saved); // the save itself still goes through
        Assert.Single(harness.Upserts);
        harness.Backup.Verify(
            b => b.UpsertAsync(It.IsAny<ConfigBackupEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NoOpSave_IdenticalContent_NoSnapshot()
    {
        var stored = StoredConfig();
        var harness = new Harness(TableConfigRepository.ConvertToTenantTableEntity(stored));

        var saved = await harness.Sut.SaveTenantConfigurationAsync(RoundtripClone(stored), "portal-put", null);

        Assert.True(saved);
        harness.Backup.Verify(
            b => b.UpsertAsync(It.IsAny<ConfigBackupEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BackupStorageFailure_IsFailSoft_SaveStillSucceeds()
    {
        var stored = StoredConfig();
        var harness = new Harness(TableConfigRepository.ConvertToTenantTableEntity(stored));
        harness.Backup
            .Setup(b => b.UpsertAsync(It.IsAny<ConfigBackupEntry>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "backup table throttled"));

        var incoming = RoundtripClone(stored);
        incoming.DataRetentionDays = 90;

        var saved = await harness.Sut.SaveTenantConfigurationAsync(incoming, "portal-put", null);

        Assert.True(saved);
        Assert.Single(harness.Upserts); // the config write went through regardless
    }

    [Fact]
    public async Task LegacyOneArgOverload_StillSnapshots_WithUnknownSource()
    {
        var stored = StoredConfig();
        var harness = new Harness(TableConfigRepository.ConvertToTenantTableEntity(stored));

        ConfigBackupEntry? snapshot = null;
        harness.Backup
            .Setup(b => b.UpsertAsync(It.IsAny<ConfigBackupEntry>(), It.IsAny<CancellationToken>()))
            .Callback<ConfigBackupEntry, CancellationToken>((e, _) => snapshot = e)
            .Returns(Task.CompletedTask);

        var incoming = RoundtripClone(stored);
        incoming.DataRetentionDays = 90;
        await harness.Sut.SaveTenantConfigurationAsync(incoming);

        Assert.NotNull(snapshot);
        Assert.Equal("unknown", snapshot!.Source);
    }

    [Fact]
    public async Task AdminConfigSave_SnapshotsUnderGlobalConfigPartition()
    {
        var stored = new AdminConfiguration
        {
            UpdatedBy = "ga@operator.example",
            GlobalRateLimitRequestsPerMinute = 100,
        };
        var harness = new Harness(TableConfigRepository.ConvertToAdminTableEntity(stored));

        ConfigBackupEntry? snapshot = null;
        harness.Backup
            .Setup(b => b.UpsertAsync(It.IsAny<ConfigBackupEntry>(), It.IsAny<CancellationToken>()))
            .Callback<ConfigBackupEntry, CancellationToken>((e, _) => snapshot = e)
            .Returns(Task.CompletedTask);

        var incoming = new AdminConfiguration
        {
            UpdatedBy = "ga@operator.example",
            GlobalRateLimitRequestsPerMinute = 200,
        };

        var saved = await harness.Sut.SaveAdminConfigurationAsync(incoming);

        Assert.True(saved);
        Assert.NotNull(snapshot);
        Assert.Equal("GlobalConfig", snapshot!.PartitionKey);
        Assert.Equal("admin-config", snapshot.Source);
        harness.Backup.Verify(
            b => b.PruneAsync("GlobalConfig", Constants.ConfigBackupKeepCount, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}

using System;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Shared.Models.Backup;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pure Entity ↔ Domain roundtrip + unknown-string-rejection for
/// <see cref="BackupJobsRepository"/>. Enum refactorings on
/// <see cref="BackupJobState"/> / <see cref="BackupJobKind"/> / <see cref="BackupOutcome"/>
/// must NOT silently shift the persisted column — the mapper throws on unknown
/// values so the operator sees a clear "schema drift" error instead of an
/// unexplained state collapse.
/// </summary>
public class BackupJobsRepositoryMappingTests
{
    [Fact]
    public void Roundtrip_Completed_PartialOutcome_AllFieldsPreserved()
    {
        var now = new DateTime(2026, 5, 22, 4, 5, 6, DateTimeKind.Utc);
        var dto = new BackupJobStatus
        {
            JobId = "abc123",
            Kind = BackupJobKind.Backup,
            State = BackupJobState.Completed,
            RequestedBy = "alice@contoso.test",
            QueuedAtUtc = now,
            StartedAtUtc = now.AddSeconds(1),
            CompletedAtUtc = now.AddMinutes(3),
            LastHeartbeatUtc = now.AddMinutes(3),
            BackupId = "20260522T040000Z_aa",
            BackupOutcome = BackupOutcome.Partial,
        };

        var entity = BackupJobsRepository.MapToEntity(dto);
        var roundtripped = BackupJobsRepository.MapToDomain(entity);

        Assert.Equal(dto.JobId, roundtripped.JobId);
        Assert.Equal(BackupJobKind.Backup, roundtripped.Kind);
        Assert.Equal(BackupJobState.Completed, roundtripped.State);
        Assert.Equal(dto.RequestedBy, roundtripped.RequestedBy);
        Assert.Equal(dto.QueuedAtUtc, roundtripped.QueuedAtUtc);
        Assert.Equal(dto.StartedAtUtc, roundtripped.StartedAtUtc);
        Assert.Equal(dto.CompletedAtUtc, roundtripped.CompletedAtUtc);
        Assert.Equal(dto.LastHeartbeatUtc, roundtripped.LastHeartbeatUtc);
        Assert.Equal(dto.BackupId, roundtripped.BackupId);
        Assert.Equal(BackupOutcome.Partial, roundtripped.BackupOutcome);
    }

    [Fact]
    public void Roundtrip_RestoreTable_FieldsPreserved()
    {
        var dto = new BackupJobStatus
        {
            JobId = "restore01",
            Kind = BackupJobKind.RestoreTable,
            State = BackupJobState.Running,
            RequestedBy = "bob@contoso.test",
            QueuedAtUtc = DateTime.UtcNow,
            LastHeartbeatUtc = DateTime.UtcNow,
            SourceBackupId = "20260522T040000Z_aa",
            TableName = "AnalyzeRules",
            Strategy = "upsert-only",
            Progress = "{\"phase\":\"Upsert\"}",
        };

        var roundtripped = BackupJobsRepository.MapToDomain(BackupJobsRepository.MapToEntity(dto));

        Assert.Equal(BackupJobKind.RestoreTable, roundtripped.Kind);
        Assert.Equal(BackupJobState.Running, roundtripped.State);
        Assert.Equal(dto.SourceBackupId, roundtripped.SourceBackupId);
        Assert.Equal(dto.TableName, roundtripped.TableName);
        Assert.Equal(dto.Strategy, roundtripped.Strategy);
        Assert.Equal(dto.Progress, roundtripped.Progress);
        Assert.Null(roundtripped.BackupOutcome);
    }

    [Fact]
    public void Map_ThrowsOnUnknownStateString()
    {
        var entity = new BackupJobStatusEntity
        {
            RowKey = "x",
            Kind = "Backup",
            State = "FuturisticGhostState",   // unknown — refactor / hand-edit
            RequestedBy = "y",
        };
        Assert.Throws<InvalidOperationException>(() => BackupJobsRepository.MapToDomain(entity));
    }

    [Fact]
    public void Map_ThrowsOnUnknownKindString()
    {
        var entity = new BackupJobStatusEntity
        {
            RowKey = "x",
            Kind = "RestoreShard",   // unknown
            State = "Queued",
            RequestedBy = "y",
        };
        Assert.Throws<InvalidOperationException>(() => BackupJobsRepository.MapToDomain(entity));
    }

    [Fact]
    public void Map_ThrowsOnLegacyFailedBackupOutcome()
    {
        // Pre-Wave21 plans allowed BackupOutcome=Failed; the current schema restricts
        // BackupOutcome to {Success, Partial} (real failures live on JobState=Failed
        // with BackupOutcome=null). A legacy "Failed" row must surface loudly.
        var entity = new BackupJobStatusEntity
        {
            RowKey = "x",
            Kind = "Backup",
            State = "Completed",
            RequestedBy = "y",
            BackupOutcome = "Failed",   // legacy
        };
        Assert.Throws<InvalidOperationException>(() => BackupJobsRepository.MapToDomain(entity));
    }

    [Fact]
    public void Map_BackupOutcomeNull_RoundtripsAsNull()
    {
        var entity = new BackupJobStatusEntity
        {
            RowKey = "x",
            Kind = "Backup",
            State = "Failed",
            RequestedBy = "y",
            BackupOutcome = null,
        };
        var dto = BackupJobsRepository.MapToDomain(entity);
        Assert.Null(dto.BackupOutcome);
    }
}

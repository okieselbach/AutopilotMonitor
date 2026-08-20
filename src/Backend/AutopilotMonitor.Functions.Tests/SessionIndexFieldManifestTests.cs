using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Fragilitätsaudit P5.2: <see cref="SessionIndexFieldManifest"/> is the declared SessionsIndex
/// column set; builder, reader, and merge guard are all pinned against it here.
/// <list type="bullet">
///   <item>Builder ↔ manifest bidirectional: a field added to <c>BuildSessionIndexEntity</c> but
///         not the manifest fails, and vice versa.</item>
///   <item>Reader coverage: every manifest field must influence <c>MapToSessionSummary</c> output —
///         a projected-but-never-served column is dead weight; a served-but-not-projected one is
///         the drift bug.</item>
///   <item>Full-rebuild upsert is Replace-mode, so conditional fields the primary cleared do not
///         survive as stale index columns.</item>
/// </list>
/// </summary>
public class SessionIndexFieldManifestTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";
    private const string IndexRowKey = "2516000000000000000_" + SessionId;

    private static readonly DateTime StartedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] SystemKeys = { "PartitionKey", "RowKey", "Timestamp", "odata.etag" };

    /// <summary>
    /// One distinctly non-default sample value per manifest field. A new manifest field without
    /// an entry here fails the tests with a clear message — extend this map alongside the
    /// manifest. Values are typed the way the WRITER stores them (wire form).
    /// </summary>
    private static readonly Dictionary<string, object> SampleValues = new(StringComparer.Ordinal)
    {
        // SessionId sample deliberately differs from the RowKey suffix so the reader test can
        // detect that the stored property (not the RowKey fallback) is being served.
        ["SessionId"] = "33333333-3333-3333-3333-333333333333",
        ["SerialNumber"] = "SN-0001",
        ["DeviceName"] = "PC-0001",
        ["Manufacturer"] = "Contoso",
        ["Model"] = "Elite-1",
        ["StartedAt"] = new DateTimeOffset(StartedAt),
        ["Status"] = "Failed",
        ["CurrentPhase"] = 3,
        ["CurrentPhaseDetail"] = "phase-detail-1",
        ["EventCount"] = 42,
        ["EnrollmentType"] = "v2",
        ["IsPreProvisioned"] = true,
        ["IsHybridJoin"] = true,
        ["IsUserDriven"] = true,
        ["IsSelfDeployingProfile"] = true,
        ["IsCloudPc"] = true,
        ["AgentVersion"] = "2.0.1400",
        ["ImeAgentVersion"] = "1.2.3.4",
        ["OsName"] = "Windows 11",
        ["OsBuild"] = "26100.1000",
        ["OsDisplayVersion"] = "24H2",
        ["OsEdition"] = "Enterprise",
        ["OsLanguage"] = "de-DE",
        ["GeoCountry"] = "DE",
        ["GeoRegion"] = "HE",
        ["GeoCity"] = "Frankfurt",
        ["GeoLoc"] = "50.11,8.68",
        ["PlatformScriptCount"] = 2,
        ["RemediationScriptCount"] = 3,
        ["RebootCount"] = 4,
        ["ExcessiveEventsAlerted"] = true,
        ["ExcessiveEventsAutoActioned"] = true,
        ["CompletedAt"] = new DateTimeOffset(StartedAt.AddMinutes(45)),
        ["FailureReason"] = "esp timeout",
        ["FailureSource"] = "ime",
        ["ReconcileReason"] = "late completion",
        ["EspSoftFailure"] = true,
        ["CompletionSource"] = "esp_exit",
        ["ValidatedBy"] = "AutopilotV1",
        ["FailureSnapshotJson"] = "{\"x\":1}",
        ["AdminMarkedAction"] = "failed",
        ["DurationSeconds"] = 360,
        ["DiagnosticsBlobName"] = "diag-0001.zip",
        ["DiagnosticsBlobDestination"] = "Hosted",
        ["LastEventAt"] = new DateTimeOffset(StartedAt.AddMinutes(44)),
        ["LastIngestAt"] = new DateTimeOffset(StartedAt.AddMinutes(46)),
        ["ResumedAt"] = new DateTimeOffset(StartedAt.AddMinutes(10)),
        ["StalledAt"] = new DateTimeOffset(StartedAt.AddMinutes(20)),
        ["AvgApiLatencyMs"] = 513.5,
        ["ApiRequestCount"] = 100,
        ["ConnectionType"] = "Ethernet",
    };

    // ── Manifest shape ───────────────────────────────────────────────────────────

    [Fact]
    public void Manifest_IsInternallyConsistent()
    {
        Assert.Empty(SessionIndexFieldManifest.AlwaysProjected
            .Intersect(SessionIndexFieldManifest.ConditionallyProjected, StringComparer.Ordinal));
        Assert.Equal(SessionIndexFieldManifest.All.Length,
            SessionIndexFieldManifest.All.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(SessionIndexFieldManifest.PrimaryOnly
            .Intersect(SessionIndexFieldManifest.All, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryManifestField_HasASampleValueInThisTest()
    {
        var missing = SessionIndexFieldManifest.All
            .Where(f => !SampleValues.ContainsKey(f))
            .ToList();
        Assert.True(missing.Count == 0,
            "New manifest field(s) without a sample value in SessionIndexFieldManifestTests.SampleValues:\n  - "
            + string.Join("\n  - ", missing));
    }

    // ── Builder ↔ manifest (bidirectional) ───────────────────────────────────────

    [Fact]
    public void FullyPopulatedRow_BuilderOutputMatchesManifestExactly()
    {
        var idx = TableStorageService.BuildSessionIndexEntity(FullSessionRow(), IndexRowKey, StartedAt);
        var dataKeys = DataKeys(idx);

        var missingFromBuilder = SessionIndexFieldManifest.All.Except(dataKeys, StringComparer.Ordinal).ToList();
        var missingFromManifest = dataKeys.Except(SessionIndexFieldManifest.All, StringComparer.Ordinal).ToList();

        Assert.True(missingFromBuilder.Count == 0,
            "Manifest fields BuildSessionIndexEntity did not project from a fully-populated row:\n  - "
            + string.Join("\n  - ", missingFromBuilder));
        Assert.True(missingFromManifest.Count == 0,
            "BuildSessionIndexEntity projects fields missing from SessionIndexFieldManifest:\n  - "
            + string.Join("\n  - ", missingFromManifest));
    }

    [Fact]
    public void MinimalRow_BuilderOutputMatchesAlwaysProjectedExactly()
    {
        var minimal = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"] = new DateTimeOffset(StartedAt),
        };

        var idx = TableStorageService.BuildSessionIndexEntity(minimal, IndexRowKey, StartedAt);
        var dataKeys = DataKeys(idx);

        var missing = SessionIndexFieldManifest.AlwaysProjected.Except(dataKeys, StringComparer.Ordinal).ToList();
        var unexpected = dataKeys.Except(SessionIndexFieldManifest.AlwaysProjected, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0,
            "AlwaysProjected fields absent from a minimal-row rebuild (default missing in builder):\n  - "
            + string.Join("\n  - ", missing));
        Assert.True(unexpected.Count == 0,
            "Fields written for a minimal row although classified ConditionallyProjected (or unlisted):\n  - "
            + string.Join("\n  - ", unexpected));
    }

    // ── Reader coverage ──────────────────────────────────────────────────────────

    [Fact]
    public void EveryManifestField_InfluencesMappedSessionSummary()
    {
        var sut = NewMapperService();
        var baseline = Serialize(sut.MapIndexEntityToSessionSummary(MinimalIndexEntity()));

        var silent = new List<string>();
        foreach (var field in SessionIndexFieldManifest.All)
        {
            var entity = MinimalIndexEntity();
            entity[field] = SampleValues[field];
            var mapped = Serialize(sut.MapIndexEntityToSessionSummary(entity));
            if (mapped == baseline)
                silent.Add(field);
        }

        Assert.True(silent.Count == 0,
            "Manifest fields whose value does NOT surface in MapToSessionSummary — either the reader "
            + "ignores the column (dead projection) or the sample value needs adjusting:\n  - "
            + string.Join("\n  - ", silent));
    }

    // ── Merge guard ──────────────────────────────────────────────────────────────

    [Fact]
    public void FindNonManifestKeys_FlagsOnlyForeignDataKeys()
    {
        var merge = new TableEntity(TenantId, IndexRowKey)
        {
            ["Status"] = "Failed",
            ["RebootCount"] = 2,
            ["BrandNewField"] = "x",
        };

        var offenders = SessionIndexFieldManifest.FindNonManifestKeys(merge);

        Assert.Equal(new[] { "BrandNewField" }, offenders);
    }

    [Fact]
    public void FindNonManifestKeys_EmptyForPureManifestMerge()
    {
        var merge = new TableEntity(TenantId, IndexRowKey)
        {
            ["EventCount"] = 7,
            ["LastEventAt"] = new DateTimeOffset(StartedAt),
        };

        Assert.Empty(SessionIndexFieldManifest.FindNonManifestKeys(merge));
    }

    // ── Full-rebuild upsert semantics ────────────────────────────────────────────

    [Fact]
    public async Task UpsertSessionIndex_UsesReplaceMode()
    {
        // Replace (not merge) is what heals stale conditional columns after the primary
        // cleared them — merge-mode upsert cannot remove a column.
        var harness = new UpsertHarness();

        await harness.Sut.UpsertSessionIndexAsync(FullSessionRow(), StartedAt);

        harness.Index.Verify(t => t.UpsertEntityAsync(
            It.Is<TableEntity>(e => e.RowKey.EndsWith("_" + SessionId, StringComparison.Ordinal)),
            TableUpdateMode.Replace,
            It.IsAny<CancellationToken>()),
            Times.Once);
        // IndexRowKey written back onto the Sessions row (merge).
        harness.Sessions.Verify(t => t.UpdateEntityAsync(
            It.Is<ITableEntity>(e => e.PartitionKey == TenantId && e.RowKey == SessionId),
            It.IsAny<ETag>(),
            TableUpdateMode.Merge,
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpsertSessionIndex_DeletesStaleRowKey_WhenStartedAtShifted()
    {
        var staleRowKey = "9999999999999999999_" + SessionId;
        var row = FullSessionRow();
        row["IndexRowKey"] = staleRowKey;

        var harness = new UpsertHarness();

        await harness.Sut.UpsertSessionIndexAsync(row, StartedAt);

        harness.Index.Verify(t => t.DeleteEntityAsync(
            TenantId, staleRowKey, It.IsAny<ETag>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A Sessions row carrying every known field. Deliberately built from SampleValues (the
    /// test-local list), NOT from the manifest — deriving the input from the manifest would
    /// make the bidirectional check circular: a field dropped from the manifest would also
    /// vanish from the input row and the drift would pass unnoticed.
    /// </summary>
    private static TableEntity FullSessionRow()
    {
        var row = new TableEntity(TenantId, SessionId);
        foreach (var (field, value) in SampleValues)
        {
            // SessionId comes from the RowKey; the builder never reads a SessionId property.
            if (field == "SessionId")
                continue;
            row[field] = value;
        }
        return row;
    }

    private static TableEntity MinimalIndexEntity() => new(TenantId, IndexRowKey);

    private static List<string> DataKeys(TableEntity entity)
        => entity.Keys.Except(SystemKeys, StringComparer.Ordinal).ToList();

    private static string Serialize(object o) => JsonSerializer.Serialize(o);

    private static TableStorageService NewMapperService()
        => new(Mock.Of<TableServiceClient>(), NullLogger<TableStorageService>.Instance);

    private sealed class UpsertHarness
    {
        public Mock<TableClient> Sessions { get; } = new();
        public Mock<TableClient> Index { get; } = new();
        public TableStorageService Sut { get; }

        public UpsertHarness()
        {
            Index
                .Setup(t => t.UpsertEntityAsync(It.IsAny<TableEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<Response>());
            Index
                .Setup(t => t.DeleteEntityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<Response>());
            Sessions
                .Setup(t => t.UpdateEntityAsync(It.IsAny<ITableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<Response>());

            var serviceClient = new Mock<TableServiceClient>();
            serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.Sessions)).Returns(Sessions.Object);
            serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.SessionsIndex)).Returns(Index.Object);
            Sut = new TableStorageService(serviceClient.Object, NullLogger<TableStorageService>.Instance);
        }
    }
}

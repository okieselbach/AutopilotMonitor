using System.Text.Json;
using AutopilotMonitor.Functions.Services.Offboarding;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Offboarding;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests.Offboarding;

/// <summary>
/// PR3.B §3.4-§3.5 — invariants the offboarding-customs-archive design guarantees:
/// snapshot serialization, partition-key namespacing per offboarding run, and the
/// counter-from-archive recomputation that makes the History fields crash-resume safe.
/// <para>
/// Source-table archive-then-wipe orchestration (3-iteration cap + customs_arrival_race
/// fail-closed) is a structural property of the loop in
/// <see cref="TenantOffboardingHandler.ArchiveAndWipeRulesTableAsync"/> — manual smoketest
/// on a dev tenant covers that contract; full mock-TableClient integration test is a
/// followup once we have a TableClient abstraction in the codebase.
/// </para>
/// </summary>
public sealed class TenantCustomsArchiveContractTests
{
    private const string TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string HistoryRowKey = "20260518091523123_aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    private static TenantOffboardingHandler BuildHandler(ITenantCustomsArchiveRepository archive)
    {
        // We only need the handler instance for its internal BuildArchiveEntry helper; that
        // method does not call into any of the other collaborators, so we can pass null! for
        // them. Tests exercise pure-method behaviour only.
        return new TenantOffboardingHandler(
            auditRepo: null!,
            enumerator: null!,
            cascadeEnqueuer: null!,
            expectations: null!,
            drainProbe: null!,
            safeWipe: null!,
            storage: null!,
            maintenance: null!,
            reEnqueuer: null!,
            opsEvents: null!,
            customsArchive: archive,
            logger: NullLogger<TenantOffboardingHandler>.Instance);
    }

    [Fact]
    public void BuildArchiveEntry_PartitionKey_IsTenantUnderscoreHistoryRowKey()
    {
        var handler = BuildHandler(new FakeCustomsArchiveRepository());
        var source = new TableEntity(TenantId, "rule-id-1") { ["DisplayName"] = "anything" };

        var entry = handler.BuildArchiveEntry(source, TenantId, Constants.TableNames.GatherRules, HistoryRowKey);

        Assert.Equal($"{TenantId}_{HistoryRowKey}", entry.PartitionKey);
    }

    [Fact]
    public void BuildArchiveEntry_RowKey_PrefixedWithOriginalTable_AndBase64Encoded()
    {
        var handler = BuildHandler(new FakeCustomsArchiveRepository());
        var source = new TableEntity(TenantId, "rule/with/slash") { ["DisplayName"] = "x" };

        var entry = handler.BuildArchiveEntry(source, TenantId, Constants.TableNames.AnalyzeRules, HistoryRowKey);

        Assert.StartsWith("AnalyzeRules_", entry.RowKey);
        Assert.DoesNotContain('/', entry.RowKey); // base64url replaces / with _
    }

    [Fact]
    public void BuildArchiveEntry_StripsSystemProperties_FromJson()
    {
        var handler = BuildHandler(new FakeCustomsArchiveRepository());
        var source = new TableEntity(TenantId, "rule")
        {
            ["DisplayName"] = "MyRule",
            ["Category"] = "preset",
            ["Timestamp"] = System.DateTimeOffset.UtcNow,
            ["odata.etag"] = "\"0x123\"",
        };

        var entry = handler.BuildArchiveEntry(source, TenantId, Constants.TableNames.GatherRules, HistoryRowKey);

        var parsed = JsonDocument.Parse(entry.EntityJson);
        Assert.True(parsed.RootElement.TryGetProperty("DisplayName", out var dn));
        Assert.Equal("MyRule", dn.GetString());
        Assert.True(parsed.RootElement.TryGetProperty("Category", out _));

        // System properties must NOT appear in the snapshot.
        Assert.False(parsed.RootElement.TryGetProperty("Timestamp", out _));
        Assert.False(parsed.RootElement.TryGetProperty("odata.etag", out _));
    }

    [Fact]
    public void BuildArchiveEntry_PopulatesOriginalKeysAndArchivedBy()
    {
        var handler = BuildHandler(new FakeCustomsArchiveRepository());
        var source = new TableEntity(TenantId, "my-rule-id") { ["X"] = "Y" };

        var entry = handler.BuildArchiveEntry(source, TenantId, Constants.TableNames.ImeLogPatterns, HistoryRowKey);

        Assert.Equal(TenantId, entry.TenantId);
        Assert.Equal(Constants.TableNames.ImeLogPatterns, entry.OriginalTable);
        Assert.Equal(TenantId, entry.OriginalPartitionKey);
        Assert.Equal("my-rule-id", entry.OriginalRowKey);
        Assert.Equal(HistoryRowKey, entry.HistoryRowKey);
        Assert.Equal("TenantOffboardingHandler", entry.ArchivedBy);
        Assert.NotEqual(default, entry.ArchivedAt);
    }

    [Fact]
    public async Task ReReOffboard_TwoRuns_CoExistInArchive_AsDistinctPartitions()
    {
        // PR3.B §3.5: each offboarding run gets its own PartitionKey
        // ({tenantId}_{historyRowKey}), so a tenant offboarded twice produces two immutable
        // archive partitions side-by-side.
        var repo = new FakeCustomsArchiveRepository();

        var run1 = "20260101000000000_" + TenantId;
        var run2 = "20260518091523123_" + TenantId;

        await repo.UpsertAsync(new TenantOffboardingCustomsArchiveEntry
        {
            PartitionKey = $"{TenantId}_{run1}",
            RowKey = "GatherRules_aaa",
            TenantId = TenantId,
            OriginalTable = Constants.TableNames.GatherRules,
            OriginalRowKey = "aaa",
            HistoryRowKey = run1,
        });

        await repo.UpsertAsync(new TenantOffboardingCustomsArchiveEntry
        {
            PartitionKey = $"{TenantId}_{run2}",
            RowKey = "GatherRules_aaa",
            TenantId = TenantId,
            OriginalTable = Constants.TableNames.GatherRules,
            OriginalRowKey = "aaa",
            HistoryRowKey = run2,
        });

        var count1 = await repo.CountByRunAndTableAsync(TenantId, run1, Constants.TableNames.GatherRules);
        var count2 = await repo.CountByRunAndTableAsync(TenantId, run2, Constants.TableNames.GatherRules);

        Assert.Equal(1, count1);
        Assert.Equal(1, count2);
        Assert.Equal(2, repo.Store.Count);
    }

    [Fact]
    public async Task CountByRunAndTable_FiltersByOriginalTable()
    {
        var repo = new FakeCustomsArchiveRepository();
        var run = "20260518091523123_" + TenantId;
        var pk = $"{TenantId}_{run}";

        await repo.UpsertAsync(new TenantOffboardingCustomsArchiveEntry { PartitionKey = pk, RowKey = "GatherRules_a", TenantId = TenantId, OriginalTable = "GatherRules", HistoryRowKey = run });
        await repo.UpsertAsync(new TenantOffboardingCustomsArchiveEntry { PartitionKey = pk, RowKey = "GatherRules_b", TenantId = TenantId, OriginalTable = "GatherRules", HistoryRowKey = run });
        await repo.UpsertAsync(new TenantOffboardingCustomsArchiveEntry { PartitionKey = pk, RowKey = "AnalyzeRules_a", TenantId = TenantId, OriginalTable = "AnalyzeRules", HistoryRowKey = run });

        Assert.Equal(2, await repo.CountByRunAndTableAsync(TenantId, run, "GatherRules"));
        Assert.Equal(1, await repo.CountByRunAndTableAsync(TenantId, run, "AnalyzeRules"));
        Assert.Equal(0, await repo.CountByRunAndTableAsync(TenantId, run, "ImeLogPatterns"));
    }

    [Fact]
    public async Task DeleteRunAsync_RemovesEveryEntryInPartition_LeavesOtherRunsAlone()
    {
        var repo = new FakeCustomsArchiveRepository();
        var run1 = "20260101000000000_" + TenantId;
        var run2 = "20260518091523123_" + TenantId;

        await repo.UpsertAsync(new() { PartitionKey = $"{TenantId}_{run1}", RowKey = "GatherRules_a", TenantId = TenantId, OriginalTable = "GatherRules" });
        await repo.UpsertAsync(new() { PartitionKey = $"{TenantId}_{run1}", RowKey = "GatherRules_b", TenantId = TenantId, OriginalTable = "GatherRules" });
        await repo.UpsertAsync(new() { PartitionKey = $"{TenantId}_{run2}", RowKey = "GatherRules_a", TenantId = TenantId, OriginalTable = "GatherRules" });

        var deleted = await repo.DeleteRunAsync(TenantId, run1);

        Assert.Equal(2, deleted);
        Assert.Single(repo.Store);
        // The remaining row belongs to run2.
        Assert.Single(repo.Store.Values, e => e.PartitionKey == $"{TenantId}_{run2}");
    }
}

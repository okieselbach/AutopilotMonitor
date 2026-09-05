using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// RuleStats after D-199: the writer folds a session into one partition read plus one CAS
/// transaction per scope; readers are PartitionKey ranges per scope (plus the legacy query
/// while legacy rows can exist); the cleanup deletes per scope and never mistakes a tenant
/// GUID that sorts inside a date range for a legacy row.
/// </summary>
public class RuleStatsRepositoryTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Tenant2 = "22222222-2222-2222-2222-222222222222";
    private const string Date = "2026-09-05";

    private sealed class Harness
    {
        public Mock<TableClient> Table { get; } = new();
        public ConcurrentBag<string> Filters { get; } = new();
        public List<List<TableTransactionAction>> Submitted { get; } = new();
        public List<(string Pk, string Rk)> Deleted { get; } = new();
        public Func<string, TableEntity[]> RowsFor { get; set; } = _ => Array.Empty<TableEntity>();
        public Queue<Func<Response<IReadOnlyList<Response>>>> SubmitBehaviors { get; } = new();
        public TableStorageService Sut { get; }

        public Harness()
        {
            Table.Setup(c => c.QueryAsync<TableEntity>(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .Returns((string filter, int? _, IEnumerable<string> _, CancellationToken _) =>
                {
                    Filters.Add(filter);
                    var page = Page<TableEntity>.FromValues(RowsFor(filter), null, Mock.Of<Response>());
                    return AsyncPageable<TableEntity>.FromPages(new[] { page });
                });
            Table.Setup(c => c.SubmitTransactionAsync(It.IsAny<IEnumerable<TableTransactionAction>>(), It.IsAny<CancellationToken>()))
                .Returns<IEnumerable<TableTransactionAction>, CancellationToken>((actions, _) =>
                {
                    var snapshot = actions.ToList();
                    Submitted.Add(snapshot);
                    if (SubmitBehaviors.Count > 0) return Task.FromResult(SubmitBehaviors.Dequeue()());
                    var inner = (IReadOnlyList<Response>)snapshot.Select(_ => Mock.Of<Response>()).ToList();
                    return Task.FromResult(Response.FromValue(inner, Mock.Of<Response>()));
                });
            Table.Setup(c => c.DeleteEntityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, ETag, CancellationToken>((pk, rk, _, _) =>
                {
                    Deleted.Add((pk, rk));
                    return Task.FromResult(Mock.Of<Response>());
                });

            var service = new Mock<TableServiceClient>();
            service.Setup(s => s.GetTableClient(Constants.TableNames.RuleStats)).Returns(Table.Object);
            Sut = new TableStorageService(service.Object, NullLogger<TableStorageService>.Instance);
        }
    }

    private static TableEntity ExistingRow(string pk, string ruleId, int fires, int evals, long confSum, string etag)
    {
        var e = new TableEntity(pk, ruleId)
        {
            ["RuleId"] = ruleId, ["RuleType"] = "analyze", ["RuleTitle"] = "t", ["Category"] = "c", ["Severity"] = "s",
            ["FireCount"] = fires, ["EvaluationCount"] = evals, ["SessionsEvaluated"] = evals,
            ["ConfidenceScoreSum"] = confSum, ["AvgConfidenceScore"] = fires > 0 ? (double)confSum / fires : 0.0,
        };
        e.ETag = new ETag(etag);
        return e;
    }

    /// <summary>A row in the pre-D-199 layout: PartitionKey = date, RowKey = "{scope}_{ruleId}", RuleId column = ruleId.</summary>
    private static TableEntity LegacyRow(string date, string scope, string ruleId)
    {
        var e = ExistingRow(date, ruleId, fires: 0, evals: 1, confSum: 0, etag: "e");
        return new TableEntity(date, $"{scope}_{ruleId}") { ["RuleId"] = ruleId, ["RuleType"] = e.GetString("RuleType"), ["EvaluationCount"] = 1 };
    }

    // ---------- writer ----------

    [Fact]
    public async Task Writer_reads_the_partition_once_and_submits_one_transaction_with_etag_updates_and_adds()
    {
        var pk = $"{Tenant}_{Date}";
        var h = new Harness { RowsFor = f => f == $"PartitionKey eq '{pk}'" ? new[] { ExistingRow(pk, "A", fires: 2, evals: 5, confSum: 160, etag: "eA") } : Array.Empty<TableEntity>() };

        await h.Sut.RecordRuleStatsAsync(Date, Tenant, new[]
        {
            new RuleStatIncrement("A", "analyze", "A title", "apps", "warning", fired: true, confidenceScore: 80),
            new RuleStatIncrement("B", "analyze", "B title", "net", "info", fired: false, confidenceScore: null),
        });

        Assert.Single(h.Filters);
        var batch = Assert.Single(h.Submitted);
        Assert.Equal(2, batch.Count);

        var update = batch.Single(a => a.ActionType == TableTransactionActionType.UpdateReplace);
        Assert.Equal(new ETag("eA"), update.ETag);
        var a = (TableEntity)update.Entity;
        Assert.Equal(3, a.GetInt32("FireCount"));
        Assert.Equal(6, a.GetInt32("EvaluationCount"));
        Assert.Equal(240L, a.GetInt64("ConfidenceScoreSum"));
        Assert.Equal(80.0, a.GetDouble("AvgConfidenceScore"));
        Assert.Equal("A title", a.GetString("RuleTitle"));

        var add = batch.Single(x => x.ActionType == TableTransactionActionType.Add);
        var b = (TableEntity)add.Entity;
        Assert.Equal(pk, b.PartitionKey);
        Assert.Equal("B", b.RowKey);
        Assert.Equal(0, b.GetInt32("FireCount"));
        Assert.Equal(1, b.GetInt32("EvaluationCount"));
        Assert.Equal("analyze", b.GetString("RuleType"));
    }

    [Fact]
    public async Task Writer_re_reads_and_retries_after_a_precondition_failure()
    {
        var pk = $"{Tenant}_{Date}";
        var reads = 0;
        var h = new Harness();
        h.RowsFor = _ => new[] { ExistingRow(pk, "A", fires: 0, evals: reads++, confSum: 0, etag: $"e{reads}") };
        h.SubmitBehaviors.Enqueue(() => throw new RequestFailedException(412, "precondition failed"));

        await h.Sut.RecordRuleStatsAsync(Date, Tenant, new[] { new RuleStatIncrement("A", "analyze", "t", "c", "s", fired: false, confidenceScore: null) });

        Assert.Equal(2, h.Filters.Count);
        Assert.Equal(2, h.Submitted.Count);
        Assert.Equal(new ETag("e2"), h.Submitted[1].Single().ETag);
    }

    [Fact]
    public async Task Writer_with_nothing_to_record_touches_storage_not_at_all()
    {
        var h = new Harness();
        await h.Sut.RecordRuleStatsAsync(Date, Tenant, Array.Empty<RuleStatIncrement>());
        Assert.Empty(h.Filters);
        Assert.Empty(h.Submitted);
    }

    // ---------- readers ----------

    [Fact]
    public async Task Reader_maps_both_layouts_and_queries_one_partition_range_per_scope()
    {
        var h = new Harness
        {
            RowsFor = f =>
                f.StartsWith($"PartitionKey ge '{Tenant}_")
                    ? new[] { ExistingRow($"{Tenant}_2026-09-03", "ANALYZE-X", 1, 1, 50, "e") }
                    : f.Contains($"RowKey ge '{Tenant}_'")
                        ? new[] { LegacyRow("2026-09-02", Tenant, "ANALYZE-Y") }
                        : Array.Empty<TableEntity>()
        };

        var entries = await h.Sut.GetRuleStatsAsync(Tenant, "2026-09-01", "2026-09-05");

        Assert.Contains($"PartitionKey ge '{Tenant}_2026-09-01' and PartitionKey le '{Tenant}_2026-09-05'", h.Filters);
        Assert.Contains($"PartitionKey ge '2026-09-01' and PartitionKey le '2026-09-05' and RowKey ge '{Tenant}_' and RowKey lt '{Tenant}_~'", h.Filters);

        var x = Assert.Single(entries, e => e.RuleId == "ANALYZE-X");
        Assert.Equal((Tenant, "2026-09-03"), (x.TenantId, x.Date));
        var y = Assert.Single(entries, e => e.RuleId == "ANALYZE-Y");
        Assert.Equal((Tenant, "2026-09-02"), (y.TenantId, y.Date));
    }

    [Fact]
    public async Task Reader_for_many_tenants_fans_out_per_tenant_and_skips_global()
    {
        var h = new Harness();

        await h.Sut.GetRuleStatsForTenantsAsync(new[] { Tenant, "global", Tenant2 }, "2026-09-01", "2026-09-05", "analyze");

        Assert.Contains(h.Filters, f => f.StartsWith($"PartitionKey ge '{Tenant}_2026-09-01'") && f.EndsWith("RuleType eq 'analyze'"));
        Assert.Contains(h.Filters, f => f.StartsWith($"PartitionKey ge '{Tenant2}_2026-09-01'"));
        Assert.DoesNotContain(h.Filters, f => f.StartsWith("PartitionKey ge 'global_"));
    }

    [Fact]
    public async Task Reader_without_a_scope_is_a_caller_bug()
    {
        var h = new Harness();
        await Assert.ThrowsAsync<ArgumentException>(() => h.Sut.GetRuleStatsAsync(string.Empty));
    }

    // ---------- cleanup ----------

    [Fact]
    public async Task Cleanup_deletes_per_scope_and_only_bare_date_rows_from_the_legacy_query()
    {
        var h = new Harness
        {
            RowsFor = f => f.StartsWith("PartitionKey ge '2000-01-01'")
                ? new[]
                {
                    new TableEntity("2026-05-01", $"{Tenant}_ANALYZE-A"),                     // legacy → delete
                    new TableEntity("2020a3f1-0000-0000-0000-000000000000_2026-05-01", "ANALYZE-A"), // GUID inside the date range → keep
                }
                : f.StartsWith($"PartitionKey ge '{Tenant}_'")
                    ? new[] { new TableEntity($"{Tenant}_2026-05-30", "ANALYZE-B") }
                    : Array.Empty<TableEntity>()
        };

        var deleted = await h.Sut.DeleteRuleStatsOlderThanAsync(new DateTime(2026, 6, 7, 0, 0, 0, DateTimeKind.Utc), new[] { Tenant });

        Assert.Contains($"PartitionKey ge '{Tenant}_' and PartitionKey lt '{Tenant}_2026-06-07'", h.Filters);
        Assert.Contains("PartitionKey ge 'global_' and PartitionKey lt 'global_2026-06-07'", h.Filters);
        Assert.Equal(2, deleted);
        Assert.Contains(("2026-05-01", $"{Tenant}_ANALYZE-A"), h.Deleted);
        Assert.Contains(($"{Tenant}_2026-05-30", "ANALYZE-B"), h.Deleted);
        Assert.DoesNotContain(h.Deleted, d => d.Pk.StartsWith("2020a3f1"));
    }

    // ---------- platform stats (D-198) ----------

    [Fact]
    public async Task Platform_stat_increment_merges_only_the_field_with_the_read_etag()
    {
        var table = new Mock<TableClient>();
        var row = new TableEntity("global", "current") { ["IssuesDetected"] = 5L, ["TotalEnrollments"] = 100L };
        row.ETag = new ETag("e9");
        table.Setup(t => t.GetEntityAsync<TableEntity>("global", "current", It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(row, Mock.Of<Response>()));
        (TableEntity Entity, ETag ETag, TableUpdateMode Mode)? update = null;
        table.Setup(t => t.UpdateEntityAsync(It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .Returns<TableEntity, ETag, TableUpdateMode, CancellationToken>((e, tag, mode, _) => { update = (e, tag, mode); return Task.FromResult(Mock.Of<Response>()); });
        var service = new Mock<TableServiceClient>();
        service.Setup(s => s.GetTableClient(Constants.TableNames.PlatformStats)).Returns(table.Object);
        var sut = new TableStorageService(service.Object, NullLogger<TableStorageService>.Instance);

        await sut.IncrementPlatformStatAsync("IssuesDetected", 3);

        Assert.NotNull(update);
        Assert.Equal(new ETag("e9"), update!.Value.ETag);
        Assert.Equal(TableUpdateMode.Merge, update.Value.Mode);
        Assert.Equal(8L, update.Value.Entity.GetInt64("IssuesDetected"));
        Assert.False(update.Value.Entity.ContainsKey("TotalEnrollments"), "the patch must not carry fields it did not change");
    }
}

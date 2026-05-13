using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Per-class behaviour tests for <see cref="CascadeVerificationService"/>. Verifies that each
/// step class drives the right inventory-reader call shape (filter for scan-based classes,
/// direct GetEntity for manifest-key-only classes), that AGGREGATE + FINAL are skipped, and
/// that the residual-sample cap is honoured.
/// </summary>
public class CascadeVerificationServiceTests
{
    private const string TenantId  = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task Verify_returns_clean_when_all_tables_empty()
    {
        var reader = new FakeReader();
        var sut = new CascadeVerificationService(reader, NullLogger<CascadeVerificationService>.Instance);
        var manifest = BuildManifestWithAllClasses();

        var result = await sut.VerifyAsync(manifest);

        Assert.True(result.IsClean);
        Assert.Empty(result.Residuals);
    }

    [Fact]
    public async Task PkBySession_uses_partition_filter()
    {
        var reader = new FakeReader();
        var sut = new CascadeVerificationService(reader, NullLogger<CascadeVerificationService>.Instance);
        var manifest = new DeletionManifest
        {
            TenantId = TenantId,
            SessionId = SessionId,
            Steps = new List<DeletionStep>
            {
                new DeletionStep { Order = 1, Table = "Events", Class = DeletionStepClass.PkBySession, RowCount = 0 },
            },
        };

        await sut.VerifyAsync(manifest);

        var call = Assert.Single(reader.QueryCalls);
        Assert.Equal("Events", call.Table);
        Assert.Equal($"PartitionKey eq '{TenantId}_{SessionId}'", call.Filter);
    }

    [Fact]
    public async Task PropTenantPk_uses_partition_and_sessionid_filter()
    {
        var reader = new FakeReader();
        var sut = new CascadeVerificationService(reader, NullLogger<CascadeVerificationService>.Instance);
        var manifest = new DeletionManifest
        {
            TenantId = TenantId,
            SessionId = SessionId,
            Steps = new List<DeletionStep>
            {
                new DeletionStep { Order = 3, Table = "AppInstallSummaries", Class = DeletionStepClass.PropTenantPk, RowCount = 0 },
            },
        };

        await sut.VerifyAsync(manifest);

        var call = Assert.Single(reader.QueryCalls);
        Assert.Equal($"PartitionKey eq '{TenantId}' and SessionId eq '{SessionId}'", call.Filter);
    }

    [Fact]
    public async Task PkRkExact_uses_direct_GetEntity_per_manifest_row()
    {
        var reader = new FakeReader();
        var sut = new CascadeVerificationService(reader, NullLogger<CascadeVerificationService>.Instance);
        var manifest = new DeletionManifest
        {
            TenantId = TenantId,
            SessionId = SessionId,
            Steps = new List<DeletionStep>
            {
                new DeletionStep
                {
                    Order = 5, Table = "DeviceSnapshot", Class = DeletionStepClass.PkRkExact, RowCount = 1,
                    Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = TenantId, Rk = SessionId } },
                },
            },
        };

        await sut.VerifyAsync(manifest);

        Assert.Empty(reader.QueryCalls);
        var get = Assert.Single(reader.GetCalls);
        Assert.Equal("DeviceSnapshot", get.Table);
        Assert.Equal(TenantId, get.Pk);
        Assert.Equal(SessionId, get.Rk);
    }

    [Fact]
    public async Task DiscriminatorPkRkExact_uses_manifest_key_GetEntity_not_live_scan()
    {
        // Plan §12-Q8: CveIndex is the deliberate exception. Manifest-key only — verifier MUST NOT
        // do a tenant-prefix scan on this class.
        var reader = new FakeReader();
        var sut = new CascadeVerificationService(reader, NullLogger<CascadeVerificationService>.Instance);
        var manifest = new DeletionManifest
        {
            TenantId = TenantId,
            SessionId = SessionId,
            Steps = new List<DeletionStep>
            {
                new DeletionStep
                {
                    Order = 10, Table = "CveIndex", Class = DeletionStepClass.DiscriminatorPkRkExact, RowCount = 2,
                    Rows = new List<DeletionRowDump>
                    {
                        new DeletionRowDump { Pk = $"{TenantId}_CVE-2024-0001", Rk = SessionId },
                        new DeletionRowDump { Pk = $"{TenantId}_CVE-2024-0002", Rk = SessionId },
                    },
                },
            },
        };

        await sut.VerifyAsync(manifest);

        Assert.Empty(reader.QueryCalls); // no tenant-prefix scan
        Assert.Equal(2, reader.GetCalls.Count);
        Assert.All(reader.GetCalls, c => Assert.Equal("CveIndex", c.Table));
    }

    [Fact]
    public async Task DiscriminatorPkRkSuffix_uses_tenant_prefix_scan_with_client_side_suffix_filter()
    {
        var reader = new FakeReader();
        // Seed with a row matching the suffix and one that doesn't — client-side filter must drop the non-match.
        reader.QueryResults["EventTypeIndex"] = new List<TableEntity>
        {
            BuildEntity($"{TenantId}_install_failed", $"6299999999999999_{SessionId}"), // matches
            BuildEntity($"{TenantId}_install_failed", "6299999999999999_other-session"), // does not match
        };
        var sut = new CascadeVerificationService(reader, NullLogger<CascadeVerificationService>.Instance);
        var manifest = new DeletionManifest
        {
            TenantId = TenantId,
            SessionId = SessionId,
            Steps = new List<DeletionStep>
            {
                new DeletionStep { Order = 9, Table = "EventTypeIndex", Class = DeletionStepClass.DiscriminatorPkRkSuffix, RowCount = 0 },
            },
        };

        var result = await sut.VerifyAsync(manifest);

        var call = Assert.Single(reader.QueryCalls);
        Assert.Contains($"PartitionKey ge '{TenantId}_'", call.Filter);
        Assert.Contains($"PartitionKey lt '{TenantId}_~'", call.Filter);
        // Suffix-matching row is the only residual reported.
        Assert.False(result.IsClean);
        Assert.Single(result.Residuals);
        Assert.EndsWith($"_{SessionId}", result.Residuals[0].Rk);
    }

    [Fact]
    public async Task DiscriminatorPkProp_uses_tenant_prefix_and_sessionid_filter()
    {
        var reader = new FakeReader();
        var sut = new CascadeVerificationService(reader, NullLogger<CascadeVerificationService>.Instance);
        var manifest = new DeletionManifest
        {
            TenantId = TenantId,
            SessionId = SessionId,
            Steps = new List<DeletionStep>
            {
                new DeletionStep { Order = 11, Table = "SessionsByTerminal", Class = DeletionStepClass.DiscriminatorPkProp, RowCount = 0 },
            },
        };

        await sut.VerifyAsync(manifest);

        var call = Assert.Single(reader.QueryCalls);
        Assert.Contains($"PartitionKey ge '{TenantId}_'", call.Filter);
        Assert.Contains($"PartitionKey lt '{TenantId}_~'", call.Filter);
        Assert.Contains($"SessionId eq '{SessionId}'", call.Filter);
    }

    [Fact]
    public async Task Aggregate_step_is_skipped_without_verification_call()
    {
        var reader = new FakeReader();
        var sut = new CascadeVerificationService(reader, NullLogger<CascadeVerificationService>.Instance);
        var manifest = new DeletionManifest
        {
            TenantId = TenantId,
            SessionId = SessionId,
            Steps = new List<DeletionStep>
            {
                new DeletionStep
                {
                    Order = 16, Step = DeletionStepNames.SoftwareInventoryDecrement,
                    Class = DeletionStepClass.Aggregate, RowCount = 3,
                    Decrements = new List<DeletionDecrementKey>
                    {
                        new DeletionDecrementKey { Vendor = "Microsoft", Name = "Office", Version = "16.0" },
                    },
                },
            },
        };

        var result = await sut.VerifyAsync(manifest);

        Assert.True(result.IsClean);
        Assert.Empty(reader.QueryCalls);
        Assert.Empty(reader.GetCalls);
    }

    [Fact]
    public async Task Final_step_is_skipped_without_verification_call()
    {
        var reader = new FakeReader();
        var sut = new CascadeVerificationService(reader, NullLogger<CascadeVerificationService>.Instance);
        var manifest = new DeletionManifest
        {
            TenantId = TenantId,
            SessionId = SessionId,
            Steps = new List<DeletionStep>
            {
                new DeletionStep
                {
                    Order = 18, Step = DeletionStepNames.Tombstone, Class = DeletionStepClass.Final, RowCount = 2,
                    Rows = new List<DeletionRowDump>
                    {
                        new DeletionRowDump { Pk = TenantId, Rk = $"6299999999999999_{SessionId}" },
                        new DeletionRowDump { Pk = TenantId, Rk = SessionId },
                    },
                },
            },
        };

        var result = await sut.VerifyAsync(manifest);

        Assert.True(result.IsClean);
        Assert.Empty(reader.QueryCalls);
        Assert.Empty(reader.GetCalls);
    }

    [Fact]
    public async Task Single_ghost_row_in_PkBySession_returns_not_clean_with_residual()
    {
        var reader = new FakeReader();
        reader.QueryResults["Events"] = new List<TableEntity>
        {
            BuildEntity($"{TenantId}_{SessionId}", "ghost-event-rk"),
        };
        var sut = new CascadeVerificationService(reader, NullLogger<CascadeVerificationService>.Instance);
        var manifest = new DeletionManifest
        {
            TenantId = TenantId,
            SessionId = SessionId,
            Steps = new List<DeletionStep>
            {
                new DeletionStep { Order = 1, Table = "Events", Class = DeletionStepClass.PkBySession, RowCount = 0 },
            },
        };

        var result = await sut.VerifyAsync(manifest);

        Assert.False(result.IsClean);
        var residual = Assert.Single(result.Residuals);
        Assert.Equal("Events", residual.Table);
        Assert.Equal($"{TenantId}_{SessionId}", residual.Pk);
        Assert.Equal("ghost-event-rk", residual.Rk);
    }

    [Fact]
    public async Task Residual_sample_is_capped_at_MaxResidualSampleSize()
    {
        var reader = new FakeReader();
        reader.QueryResults["Events"] = Enumerable.Range(0, 200)
            .Select(i => BuildEntity($"{TenantId}_{SessionId}", $"rk-{i:D3}"))
            .ToList();
        var sut = new CascadeVerificationService(reader, NullLogger<CascadeVerificationService>.Instance);
        var manifest = new DeletionManifest
        {
            TenantId = TenantId,
            SessionId = SessionId,
            Steps = new List<DeletionStep>
            {
                new DeletionStep { Order = 1, Table = "Events", Class = DeletionStepClass.PkBySession, RowCount = 0 },
            },
        };

        var result = await sut.VerifyAsync(manifest);

        Assert.False(result.IsClean);
        Assert.Equal(CascadeVerificationService.MaxResidualSampleSize, result.Residuals.Count);
    }

    [Fact]
    public async Task First_table_failure_short_circuits_remaining_steps()
    {
        var reader = new FakeReader();
        reader.QueryResults["Events"] = new List<TableEntity>
        {
            BuildEntity($"{TenantId}_{SessionId}", "ghost-rk"),
        };
        // RuleResults would have ghosts too, but the verifier must NOT enumerate it after the
        // first-table failure — the short-circuit keeps verification cost bounded.
        reader.QueryResults["RuleResults"] = new List<TableEntity>
        {
            BuildEntity($"{TenantId}_{SessionId}", "should-not-be-seen"),
        };
        var sut = new CascadeVerificationService(reader, NullLogger<CascadeVerificationService>.Instance);
        var manifest = new DeletionManifest
        {
            TenantId = TenantId,
            SessionId = SessionId,
            Steps = new List<DeletionStep>
            {
                new DeletionStep { Order = 1, Table = "Events", Class = DeletionStepClass.PkBySession, RowCount = 0 },
                new DeletionStep { Order = 2, Table = "RuleResults", Class = DeletionStepClass.PkBySession, RowCount = 0 },
            },
        };

        var result = await sut.VerifyAsync(manifest);

        Assert.False(result.IsClean);
        // Only Events was queried; RuleResults short-circuited.
        Assert.Single(reader.QueryCalls);
        Assert.Equal("Events", reader.QueryCalls[0].Table);
    }

    // ---------------------------------------------------------------- Helpers ----

    private static TableEntity BuildEntity(string pk, string rk) => new TableEntity(pk, rk);

    private static DeletionManifest BuildManifestWithAllClasses() => new DeletionManifest
    {
        TenantId = TenantId,
        SessionId = SessionId,
        Steps = new List<DeletionStep>
        {
            new DeletionStep { Order = 1, Table = "Events",                       Class = DeletionStepClass.PkBySession,             RowCount = 0 },
            new DeletionStep { Order = 3, Table = "AppInstallSummaries",          Class = DeletionStepClass.PropTenantPk,            RowCount = 0 },
            new DeletionStep { Order = 5, Table = "DeviceSnapshot",               Class = DeletionStepClass.PkRkExact,               RowCount = 0 },
            new DeletionStep { Order = 9, Table = "EventTypeIndex",               Class = DeletionStepClass.DiscriminatorPkRkSuffix, RowCount = 0 },
            new DeletionStep { Order = 10, Table = "CveIndex",                    Class = DeletionStepClass.DiscriminatorPkRkExact,  RowCount = 0 },
            new DeletionStep { Order = 11, Table = "SessionsByTerminal",          Class = DeletionStepClass.DiscriminatorPkProp,     RowCount = 0 },
            new DeletionStep { Order = 16, Step = DeletionStepNames.SoftwareInventoryDecrement, Class = DeletionStepClass.Aggregate, RowCount = 0 },
            new DeletionStep { Order = 18, Step = DeletionStepNames.Tombstone,    Class = DeletionStepClass.Final,                   RowCount = 0 },
        },
    };

    private sealed class FakeReader : ISessionDeletionInventoryReader
    {
        public List<(string Table, string Filter)> QueryCalls { get; } = new List<(string, string)>();
        public List<(string Table, string Pk, string Rk)> GetCalls { get; } = new List<(string, string, string)>();
        public Dictionary<string, List<TableEntity>> QueryResults { get; } = new Dictionary<string, List<TableEntity>>(StringComparer.Ordinal);
        public Dictionary<(string Table, string Pk, string Rk), TableEntity> GetResults { get; }
            = new Dictionary<(string, string, string), TableEntity>();

        public Task<TableEntity?> GetSessionRowAsync(string tenantId, string sessionId, CancellationToken ct = default)
            => Task.FromResult<TableEntity?>(null);

        public Task<TableEntity?> GetSessionsIndexRowAsync(string tenantId, string indexRowKey, CancellationToken ct = default)
            => Task.FromResult<TableEntity?>(null);

        public async IAsyncEnumerable<TableEntity> QueryAsync(
            string tableName, string filter, [EnumeratorCancellation] CancellationToken ct = default)
        {
            QueryCalls.Add((tableName, filter));
            if (QueryResults.TryGetValue(tableName, out var rows))
            {
                foreach (var row in rows)
                {
                    await Task.Yield();
                    yield return row;
                }
            }
        }

        public Task<TableEntity?> GetEntityOrNullAsync(string tableName, string partitionKey, string rowKey, CancellationToken ct = default)
        {
            GetCalls.Add((tableName, partitionKey, rowKey));
            GetResults.TryGetValue((tableName, partitionKey, rowKey), out var entity);
            return Task.FromResult<TableEntity?>(entity);
        }

        public Task<TableEntity?> GetActiveSessionTombstoneAsync(string tenantId, string sessionId, CancellationToken ct = default)
            => Task.FromResult<TableEntity?>(null);
    }
}

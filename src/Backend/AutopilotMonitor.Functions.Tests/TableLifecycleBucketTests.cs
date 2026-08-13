using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Functions.Services.Offboarding;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Lifecycle-completeness net for <see cref="Constants.TableNames"/> (Fragilitätsaudit P5.1).
/// Every table must be covered by at least one deletion lifecycle — tenant-offboarding wipe
/// (read via reflection from <see cref="TenantOffboardingHandler"/>'s bucket arrays), the
/// per-session deletion cascade (derived from a real <see cref="DeletionManifestBuilder"/>
/// manifest), or the explicit <see cref="KeptByDesign"/> list below. A new table that lands
/// in no bucket fails here with instructions; a kept-by-design table that someone starts
/// wiping (or vice versa) fails too.
///
/// NOT covered mechanically: whether a bucket's KEY SHAPE matches the table's writer
/// (the UsageMetrics bug this package fixed — PK=date/RK=tenantId sat in the exact-PK
/// bucket and the wipe matched 0 rows). When classifying a new table, read its writer's
/// <c>new TableEntity(pk, rk)</c> call before picking the bucket.
/// </summary>
public class TableLifecycleBucketTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    /// <summary>
    /// Tables that intentionally survive both tenant offboarding and session deletion.
    /// Each entry needs a reason — this list is the reviewed exception set, not a dumping
    /// ground for "forgot to classify".
    /// </summary>
    private static readonly Dictionary<string, string> KeptByDesign = new(StringComparer.Ordinal)
    {
        [Constants.TableNames.AdminConfiguration] = "single platform 'GlobalConfig' partition, no tenant rows",
        [Constants.TableNames.GlobalAdmins] = "platform operator identities",
        [Constants.TableNames.McpUsers] = "platform MCP identities (PK='McpUsers', RK=UPN)",
        [Constants.TableNames.TenantGroupAssignments] = "per-UPN rows without tenant anchor; offboarding never touches it (see Constants doc)",
        [Constants.TableNames.PreviewConfig] = "global TelegramBot config row",
        [Constants.TableNames.Feedback] = "product feedback deliberately survives offboarding (TableFeedbackRepositoryTests contract)",
        [Constants.TableNames.BlockedVersions] = "platform-wide agent version blocks",
        [Constants.TableNames.GlobalNotifications] = "GA notifications; time-retention only",
        [Constants.TableNames.VulnerabilityCache] = "CPE/CVE cache without tenant dimension",
        [Constants.TableNames.OpsEvents] = "platform ops stream; time-retention only",
        [Constants.TableNames.ImeVersionHistory] = "permanent platform archive by design",
        [Constants.TableNames.RuleStats] = "per-rule daily counters; time-retention only",
        [Constants.TableNames.OffboardingAudit] = "the audit trail OF the offboarding itself",
        [Constants.TableNames.TenantOffboardingCustomsArchive] = "operator-driven cleanup by design",
        [Constants.TableNames.BackupJobs] = "platform backup job log (PK='BackupJobs'); 365d retention",
    };

    /// <summary>
    /// Wipes the offboarding handler performs OUTSIDE its bucket arrays (one-off calls in
    /// HandleAsync). Reflection over the arrays cannot see these; keep in sync with the
    /// handler's phase code when adding one.
    /// </summary>
    private static readonly string[] OneOffOffboardingWipes =
    {
        // Phase 2.F-final: deleted LAST via SafeWipe after everything else completed.
        Constants.TableNames.TenantConfiguration,
    };

    /// <summary>
    /// Tables allowed in MORE than one offboarding bucket. BootstrapSessions has main rows
    /// under PK=tenantId AND CodeLookup rows under the discriminator PK.
    /// </summary>
    private static readonly string[] AllowedMultiBucket =
    {
        Constants.TableNames.BootstrapSessions,
    };

    [Fact]
    public void TableNamesConstants_SetEquals_TableNamesAll()
    {
        var constants = ConstStrings(typeof(Constants.TableNames));
        var all = Constants.TableNames.All;

        var missingFromAll = constants.Except(all, StringComparer.Ordinal).ToList();
        var orphansInAll = all.Except(constants, StringComparer.Ordinal).ToList();

        Assert.True(missingFromAll.Count == 0,
            "TableNames constants missing from TableNames.All (table will never be created at startup):\n  - "
            + string.Join("\n  - ", missingFromAll));
        Assert.True(orphansInAll.Count == 0,
            "TableNames.All entries without a matching constant:\n  - " + string.Join("\n  - ", orphansInAll));
    }

    [Fact]
    public async Task EveryTable_HasALifecycleClassification()
    {
        var allTables = ConstStrings(typeof(Constants.TableNames)).ToHashSet(StringComparer.Ordinal);
        var offboardWiped = OffboardingBucketUnion();
        var perSession = await PerSessionCascadeTablesAsync();

        var unclassified = allTables
            .Except(offboardWiped, StringComparer.Ordinal)
            .Except(perSession, StringComparer.Ordinal)
            .Except(KeptByDesign.Keys, StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.True(unclassified.Count == 0,
            "Tables without any lifecycle classification. Either add them to an offboarding wipe bucket "
            + "(TenantOffboardingHandler — check the writer's PK/RK shape first!), to the per-session "
            + "deletion manifest (DeletionManifestBuilder), or to KeptByDesign in this test WITH a reason:\n  - "
            + string.Join("\n  - ", unclassified));
    }

    [Fact]
    public async Task KeptByDesign_IsDisjointFromEveryWipePath()
    {
        var offboardWiped = OffboardingBucketUnion();
        var perSession = await PerSessionCascadeTablesAsync();

        var conflicts = KeptByDesign.Keys
            .Where(t => offboardWiped.Contains(t) || perSession.Contains(t))
            .ToList();

        Assert.True(conflicts.Count == 0,
            "Tables listed as kept-by-design but ALSO wiped by offboarding or the session cascade — "
            + "one of the two claims is wrong:\n  - " + string.Join("\n  - ", conflicts));
    }

    [Fact]
    public void EveryOffboardingBucketEntry_IsAKnownTableName()
    {
        var known = ConstStrings(typeof(Constants.TableNames)).ToHashSet(StringComparer.Ordinal);
        var orphans = OffboardingBuckets()
            .SelectMany(b => b.Tables.Select(t => $"{b.Name}: {t}"))
            .Where(entry => !known.Contains(entry.Split(": ", 2)[1]))
            .ToList();

        Assert.True(orphans.Count == 0,
            "Offboarding bucket entries that are not TableNames constants:\n  - " + string.Join("\n  - ", orphans));
    }

    [Fact]
    public void OffboardingBuckets_AreDisjoint_ExceptDocumentedMultiMembership()
    {
        var buckets = OffboardingBuckets().ToList();
        var violations = new List<string>();

        for (var i = 0; i < buckets.Count; i++)
        {
            for (var j = i + 1; j < buckets.Count; j++)
            {
                var overlap = buckets[i].Tables
                    .Intersect(buckets[j].Tables, StringComparer.Ordinal)
                    .Except(AllowedMultiBucket, StringComparer.Ordinal)
                    .ToList();
                violations.AddRange(overlap.Select(t => $"{t} (in {buckets[i].Name} AND {buckets[j].Name})"));
            }
        }

        Assert.True(violations.Count == 0,
            "Tables in more than one offboarding wipe bucket without a documented exception — "
            + "a double wipe with different key anchors usually means one of the two is wrong:\n  - "
            + string.Join("\n  - ", violations));
    }

    // ── Bucket derivation ────────────────────────────────────────────────────────

    private sealed record Bucket(string Name, IReadOnlyList<string> Tables);

    private static IEnumerable<Bucket> OffboardingBuckets()
    {
        yield return new Bucket("TenantPartitionTables", HandlerArray("TenantPartitionTables"));
        yield return new Bucket("CompositePartitionTables", HandlerArray("CompositePartitionTables"));
        yield return new Bucket("RowKeyTables", HandlerArray("RowKeyTables"));
        yield return new Bucket("PropertyOnlyTables", HandlerArray("PropertyOnlyTables"));
        yield return new Bucket("DiscriminatorTables",
            HandlerTupleArray<(string Table, string Discriminator)>("DiscriminatorTables").Select(t => t.Table).ToList());
        yield return new Bucket("ArchivedRuleTables",
            HandlerTupleArray<(string Table, string Field)>("ArchivedRuleTables").Select(t => t.Table).ToList());
        yield return new Bucket("OneOffOffboardingWipes", OneOffOffboardingWipes);
    }

    private static HashSet<string> OffboardingBucketUnion()
        => OffboardingBuckets().SelectMany(b => b.Tables).ToHashSet(StringComparer.Ordinal);

    private static string[] HandlerArray(string fieldName)
        => (string[])HandlerField(fieldName).GetValue(null)!;

    private static T[] HandlerTupleArray<T>(string fieldName)
        => (T[])HandlerField(fieldName).GetValue(null)!;

    private static FieldInfo HandlerField(string fieldName)
        => typeof(TenantOffboardingHandler).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException(
               $"TenantOffboardingHandler.{fieldName} not found — bucket array renamed? Update this test's derivation.");

    /// <summary>
    /// Derives the per-session cascade table set by building a REAL deletion manifest against
    /// an empty-inventory fake, so new builder steps are picked up automatically. Synthetic
    /// steps map back to their tables: Tombstone → Sessions + SessionsIndex,
    /// SoftwareInventoryDecrement → SoftwareInventory.
    /// </summary>
    private static async Task<HashSet<string>> PerSessionCascadeTablesAsync()
    {
        var builder = new DeletionManifestBuilder(new EmptyInventoryReader(), NullLogger<DeletionManifestBuilder>.Instance);
        var manifest = await builder.BuildAsync(
            TenantId, SessionId, "lifecycle_bucket_test",
            new DeletionActor { Type = "admin", Actor = "alice@contoso.com" },
            new DeletionRetentionContext { TenantRetentionDays = 90 });

        var tables = manifest.Steps
            .Where(s => !string.IsNullOrEmpty(s.Table))
            .Select(s => s.Table!)
            .ToHashSet(StringComparer.Ordinal);

        if (manifest.Steps.Any(s => s.Step == DeletionStepNames.Tombstone))
        {
            tables.Add(Constants.TableNames.SessionsIndex);
            tables.Add(Constants.TableNames.Sessions);
        }
        if (manifest.Steps.Any(s => s.Step == DeletionStepNames.SoftwareInventoryDecrement))
        {
            tables.Add(Constants.TableNames.SoftwareInventory);
        }

        // The inventory pair (steps 20/21) is only emitted when a contributions side-row
        // exists; the fake provides one so the derivation sees the complete step list.
        Assert.Contains(Constants.TableNames.SessionInventoryContributions, tables);

        return tables;
    }

    private static string[] ConstStrings(Type constClass)
        => constClass.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

    // ── Fake inventory reader ────────────────────────────────────────────────────

    private sealed class EmptyInventoryReader : ISessionDeletionInventoryReader
    {
        private const string IndexRowKey = "0000000000000000001_" + SessionId;

        public Task<TableEntity?> GetSessionRowAsync(string tenantId, string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<TableEntity?>(new TableEntity(tenantId, sessionId) { ["IndexRowKey"] = IndexRowKey });

        public Task<TableEntity?> GetSessionsIndexRowAsync(string tenantId, string indexRowKey, CancellationToken cancellationToken = default)
            => Task.FromResult<TableEntity?>(new TableEntity(tenantId, indexRowKey) { ["SessionId"] = SessionId });

        public async IAsyncEnumerable<TableEntity> QueryAsync(string tableName, string filter, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<TableEntity?> GetEntityOrNullAsync(string tableName, string partitionKey, string rowKey, CancellationToken cancellationToken = default)
        {
            // Provide the SessionInventoryContributions side-row so steps 20/21 are emitted.
            if (tableName == Constants.TableNames.SessionInventoryContributions)
            {
                return Task.FromResult<TableEntity?>(new TableEntity(partitionKey, rowKey));
            }
            return Task.FromResult<TableEntity?>(null);
        }

        public Task<TableEntity?> GetActiveSessionTombstoneAsync(string tenantId, string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<TableEntity?>(null);
    }
}

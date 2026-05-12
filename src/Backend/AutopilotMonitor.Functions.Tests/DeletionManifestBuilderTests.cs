using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Behaviour tests for <see cref="DeletionManifestBuilder"/>. PR1 is read-only — the builder
/// must (a) enumerate every cascade table per the §3 taxonomy, (b) only emit the
/// SoftwareInventoryDecrement / SessionInventoryContributions pair when the side-row exists,
/// and (c) order the FINAL tombstone step SessionsIndex-then-Sessions. All tests are
/// Moq-based against the new <see cref="ISessionDeletionInventoryReader"/> seam — no Azurite,
/// matching repo convention.
/// </summary>
public class DeletionManifestBuilderTests
{
    private const string TenantId  = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task Build_returns_correct_counts_for_full_session()
    {
        var reader = NewReader(seed => seed
            .WithSessionsRow()
            .WithSessionsIndexRow()
            .WithEvents(7)
            .WithRuleResults(2)
            .WithAppInstallSummaries(3)
            .WithVulnerabilityReport()
            .WithDeviceSnapshot()
            .WithEventSessionIndex()
            .WithSignals(15)
            .WithDecisionTransitions(4)
            .WithEventTypeIndex(5)
            .WithCveIndex(2)
            .WithSessionsByTerminal(1)
            .WithSessionsByStage(3)
            .WithDeadEndsByReason(0)
            .WithClassifierVerdictsByIdLevel(2)
            .WithSignalsByKind(11));

        var manifest = await NewBuilder(reader).BuildAsync(
            TenantId, SessionId, "admin_delete",
            new DeletionActor { Type = "admin", Actor = "alice@example.com" },
            new DeletionRetentionContext { TenantRetentionDays = 90 });

        var c = manifest.PreflightCounts;
        Assert.Equal(7,  c["events"]);
        Assert.Equal(2,  c["ruleResults"]);
        Assert.Equal(3,  c["appInstallSummaries"]);
        Assert.Equal(1,  c["vulnerabilityReports"]);
        Assert.Equal(1,  c["deviceSnapshot"]);
        Assert.Equal(1,  c["eventSessionIndex"]);
        Assert.Equal(15, c["signals"]);
        Assert.Equal(4,  c["decisionTransitions"]);
        Assert.Equal(5,  c["eventTypeIndex"]);
        Assert.Equal(2,  c["cveIndex"]);
        Assert.Equal(1,  c["sessionsByTerminal"]);
        Assert.Equal(3,  c["sessionsByStage"]);
        Assert.Equal(0,  c["deadEndsByReason"]);
        Assert.Equal(2,  c["classifierVerdictsByIdLevel"]);
        Assert.Equal(11, c["signalsByKind"]);

        // 15 cascade tables + tombstone = 16 steps; no inventory steps when no contributions row.
        Assert.Equal(16, manifest.Steps.Count);
        Assert.Equal(DeletionStepClass.Final, manifest.Steps.Last().Class);
    }

    [Fact]
    public async Task Build_returns_full_row_dumps_for_all_classes()
    {
        // Seed one row in each non-inventory table with a recognisable property so we can
        // assert that the dump path captures its value.
        var reader = NewReader(seed => seed
            .WithSessionsRow()
            .WithSessionsIndexRow()
            .WithEvents(1, propValue: "events_marker")
            .WithRuleResults(1, propValue: "rules_marker")
            .WithAppInstallSummaries(1, propValue: "appinstalls_marker")
            .WithVulnerabilityReport(propValue: "vuln_marker")
            .WithDeviceSnapshot(propValue: "device_marker")
            .WithEventSessionIndex(propValue: "esi_marker")
            .WithSignals(1, propValue: "signals_marker")
            .WithDecisionTransitions(1, propValue: "transitions_marker")
            .WithEventTypeIndex(1, propValue: "eti_marker")
            .WithCveIndex(1, propValue: "cve_marker")
            .WithSessionsByTerminal(1, propValue: "terminal_marker")
            .WithSessionsByStage(1, propValue: "stage_marker")
            .WithDeadEndsByReason(1, propValue: "deadend_marker")
            .WithClassifierVerdictsByIdLevel(1, propValue: "classifier_marker")
            .WithSignalsByKind(1, propValue: "signalkind_marker"));

        var manifest = await NewBuilder(reader).BuildAsync(
            TenantId, SessionId, "admin_delete",
            new DeletionActor { Type = "admin", Actor = "alice@example.com" },
            new DeletionRetentionContext { TenantRetentionDays = 90 });

        foreach (var step in manifest.Steps)
        {
            // For the FINAL tombstone we expect 2 rows (SessionsIndex + Sessions); other steps each have 1.
            if (step.Class == DeletionStepClass.Final)
            {
                Assert.Equal(2, step.Rows.Count);
                continue;
            }
            Assert.Equal(step.RowCount, step.Rows.Count);
            Assert.Single(step.Rows);

            var dump = step.Rows[0];
            Assert.False(string.IsNullOrEmpty(dump.Pk));
            Assert.False(string.IsNullOrEmpty(dump.Rk));
            Assert.False(string.IsNullOrEmpty(dump.Etag));
            Assert.True(dump.Props.ContainsKey("Marker"),
                $"Step {step.Order} ({step.Table ?? step.Step}) row dump is missing the Marker prop.");
            Assert.Equal(DeletionPropEdmType.String, dump.Props["Marker"].EdmType);
            Assert.Equal(JsonValueKind.String, dump.Props["Marker"].Value.ValueKind);
            // The seed marker round-trips faithfully through the property → DeletionPropValue convertor.
            Assert.False(string.IsNullOrEmpty(dump.Props["Marker"].Value.GetString()));
        }
    }

    [Fact]
    public async Task Build_returns_empty_rows_for_tables_with_no_session_data()
    {
        // No data anywhere — the builder still emits one step per cascade table, all with RowCount=0.
        var reader = NewReader(seed => seed.WithSessionsRow().WithSessionsIndexRow());

        var manifest = await NewBuilder(reader).BuildAsync(
            TenantId, SessionId, "admin_delete",
            new DeletionActor { Type = "admin", Actor = "alice@example.com" },
            new DeletionRetentionContext { TenantRetentionDays = 90 });

        var nonTombstoneSteps = manifest.Steps.Where(s => s.Class != DeletionStepClass.Final).ToList();
        // Without a contributions row, neither inventory step is emitted → exactly 15 cascade-table steps.
        Assert.Equal(15, nonTombstoneSteps.Count);
        foreach (var step in nonTombstoneSteps)
        {
            Assert.Equal(0, step.RowCount);
            Assert.Empty(step.Rows);
        }
    }

    [Fact]
    public async Task Build_emits_inventory_steps_when_side_row_present()
    {
        var sideRowKeys = new List<DeletionDecrementKey>
        {
            new DeletionDecrementKey { Vendor = "Contoso",  Name = "Widget",  Version = "1.0" },
            new DeletionDecrementKey { Vendor = "Fabrikam", Name = "Gadget",  Version = "2.0" },
            new DeletionDecrementKey { Vendor = "Tailspin", Name = "Service", Version = "3.0" },
        };
        var sideRow = MakeContributionsRow(sideRowKeys, compressed: false);

        var reader = NewReader(seed => seed
            .WithSessionsRow()
            .WithSessionsIndexRow()
            .WithSessionInventoryContributions(sideRow));

        var manifest = await NewBuilder(reader).BuildAsync(
            TenantId, SessionId, "admin_delete",
            new DeletionActor { Type = "admin", Actor = "alice@example.com" },
            new DeletionRetentionContext { TenantRetentionDays = 90 });

        // Expect: 15 cascade + 2 inventory + 1 tombstone = 18 steps.
        Assert.Equal(18, manifest.Steps.Count);

        var aggregate = manifest.Steps.Single(s => s.Class == DeletionStepClass.Aggregate);
        Assert.Equal(DeletionStepNames.SoftwareInventoryDecrement, aggregate.Step);
        Assert.NotNull(aggregate.Decrements);
        Assert.Equal(3, aggregate.Decrements!.Count);
        // Builder sorts deterministically by (Vendor, Name, Version).
        Assert.Equal("Contoso",  aggregate.Decrements[0].Vendor);
        Assert.Equal("Fabrikam", aggregate.Decrements[1].Vendor);
        Assert.Equal("Tailspin", aggregate.Decrements[2].Vendor);
        Assert.Empty(aggregate.Rows);

        var contribStep = manifest.Steps.Single(s => s.Table == Constants.TableNames.SessionInventoryContributions);
        Assert.Equal(DeletionStepClass.PkRkExact, contribStep.Class);
        Assert.Single(contribStep.Rows);
        Assert.Equal(TenantId, contribStep.Rows[0].Pk);
        Assert.Equal(SessionId, contribStep.Rows[0].Rk);

        Assert.Equal(3, manifest.PreflightCounts["softwareInventoryDecrement"]);
        Assert.Equal(1, manifest.PreflightCounts["sessionInventoryContributions"]);
    }

    [Fact]
    public async Task Build_emits_inventory_steps_when_side_row_is_gzip_compressed()
    {
        var sideRowKeys = new List<DeletionDecrementKey>
        {
            new DeletionDecrementKey { Vendor = "Contoso", Name = "WidgetSuite", Version = "10.5" },
            new DeletionDecrementKey { Vendor = "Tailspin", Name = "ToolKit",     Version = "0.9" },
        };
        var sideRow = MakeContributionsRow(sideRowKeys, compressed: true);

        var reader = NewReader(seed => seed
            .WithSessionsRow()
            .WithSessionsIndexRow()
            .WithSessionInventoryContributions(sideRow));

        var manifest = await NewBuilder(reader).BuildAsync(
            TenantId, SessionId, "admin_delete",
            new DeletionActor { Type = "admin", Actor = "alice@example.com" },
            new DeletionRetentionContext { TenantRetentionDays = 90 });

        var aggregate = manifest.Steps.Single(s => s.Class == DeletionStepClass.Aggregate);
        Assert.Equal(2, aggregate.Decrements!.Count);
        Assert.Equal("Contoso",  aggregate.Decrements[0].Vendor);
        Assert.Equal("Tailspin", aggregate.Decrements[1].Vendor);
    }

    [Fact]
    public async Task Build_omits_inventory_steps_when_no_side_row()
    {
        // The default reader returns no inventory row → 15 cascade + 1 tombstone, no inventory steps.
        var reader = NewReader(seed => seed.WithSessionsRow().WithSessionsIndexRow());

        var manifest = await NewBuilder(reader).BuildAsync(
            TenantId, SessionId, "retention_cutoff",
            new DeletionActor { Type = "maintenance", Actor = "system" },
            new DeletionRetentionContext { TenantRetentionDays = 90 });

        Assert.DoesNotContain(manifest.Steps, s => s.Class == DeletionStepClass.Aggregate);
        Assert.DoesNotContain(manifest.Steps, s => s.Table == Constants.TableNames.SessionInventoryContributions);
        Assert.False(manifest.PreflightCounts.ContainsKey("softwareInventoryDecrement"));
        Assert.False(manifest.PreflightCounts.ContainsKey("sessionInventoryContributions"));
    }

    [Fact]
    public async Task Build_records_diagnosticsBlobName_when_present_but_no_delete_step_emitted()
    {
        const string blobName = "diagnostics-uploads/tenant/session.zip";
        var reader = NewReader(seed => seed
            .WithSessionsRow(diagnosticsBlobName: blobName)
            .WithSessionsIndexRow());

        var manifest = await NewBuilder(reader).BuildAsync(
            TenantId, SessionId, "admin_delete",
            new DeletionActor { Type = "admin", Actor = "alice@example.com" },
            new DeletionRetentionContext { TenantRetentionDays = 90 });

        Assert.Equal(blobName, manifest.DiagnosticsBlobName);

        // No cascade step targets the diagnostics blob — customer storage, customer cleans up
        // (plan §1 P10 / §3 / §11). The blob name is captured for forensic traceability only.
        // It IS expected to appear as a property value on the Sessions row dump in the Tombstone
        // step (the row is being deleted with all its columns intact); that's a row-content
        // property, not a "the cascade will delete this blob" signal.
        foreach (var step in manifest.Steps)
        {
            Assert.NotEqual("diagnostics", step.Table);
            Assert.NotEqual("DiagnosticsUploads", step.Table);
        }
    }

    [Fact]
    public async Task Build_tombstone_step_orders_sessionsIndex_before_sessions()
    {
        var reader = NewReader(seed => seed.WithSessionsRow().WithSessionsIndexRow());

        var manifest = await NewBuilder(reader).BuildAsync(
            TenantId, SessionId, "admin_delete",
            new DeletionActor { Type = "admin", Actor = "alice@example.com" },
            new DeletionRetentionContext { TenantRetentionDays = 90 });

        var tombstone = manifest.Steps.Last();
        Assert.Equal(DeletionStepClass.Final, tombstone.Class);
        Assert.Equal(DeletionStepNames.Tombstone, tombstone.Step);
        Assert.Equal(2, tombstone.Rows.Count);

        // Plan §5 PR4: SessionsIndex is row[0], Sessions is row[1] — UI listings drop the session before the canonical row goes.
        // We seeded the Sessions row at (TenantId, SessionId) and the SessionsIndex row at (TenantId, "<index>{SessionId}").
        Assert.NotEqual(SessionId, tombstone.Rows[0].Rk);                          // SessionsIndex first
        Assert.Contains(SessionId, tombstone.Rows[0].Rk, StringComparison.Ordinal); // RK still references the session
        Assert.Equal(SessionId, tombstone.Rows[1].Rk);                              // Sessions row second
    }

    [Fact]
    public async Task Build_produces_identical_steps_and_counts_across_runs_with_identical_data()
    {
        // Determinism guarantee per plan §18.6: the builder is read-only and produces the same
        // manifest given the same input. Volatile fields (ManifestId, CreatedAt, SchemaHash —
        // which is computed over those volatile fields) intentionally differ.
        var reader = NewReader(seed => seed
            .WithSessionsRow()
            .WithSessionsIndexRow()
            .WithEvents(3)
            .WithSignals(5)
            .WithSessionsByTerminal(1));

        var first  = await NewBuilder(reader).BuildAsync(TenantId, SessionId, "admin_delete",
            new DeletionActor { Type = "admin", Actor = "alice@example.com" },
            new DeletionRetentionContext { TenantRetentionDays = 90 });
        var second = await NewBuilder(reader).BuildAsync(TenantId, SessionId, "admin_delete",
            new DeletionActor { Type = "admin", Actor = "alice@example.com" },
            new DeletionRetentionContext { TenantRetentionDays = 90 });

        Assert.NotEqual(first.ManifestId, second.ManifestId);
        Assert.Equal(first.PreflightCounts, second.PreflightCounts);
        Assert.Equal(first.Steps.Count, second.Steps.Count);
        for (int i = 0; i < first.Steps.Count; i++)
        {
            Assert.Equal(first.Steps[i].Order, second.Steps[i].Order);
            Assert.Equal(first.Steps[i].Class, second.Steps[i].Class);
            Assert.Equal(first.Steps[i].Table, second.Steps[i].Table);
            Assert.Equal(first.Steps[i].Step, second.Steps[i].Step);
            Assert.Equal(first.Steps[i].RowCount, second.Steps[i].RowCount);
            Assert.Equal(first.Steps[i].Rows.Count, second.Steps[i].Rows.Count);
        }
    }

    [Fact]
    public async Task Restore_from_manifest_round_trips_session()
    {
        // PR1 ships the manifest as a backup format. The Azure-side re-insertion is exercised by
        // PR4's worker tests; here we verify the manifest is sufficient AS the backup —
        // serialization round-trip preserves every (PK, RK, props, ETag) needed to replay.
        var reader = NewReader(seed => seed
            .WithSessionsRow()
            .WithSessionsIndexRow()
            .WithEvents(2, propValue: "events_check")
            .WithRuleResults(1, propValue: "rules_check")
            .WithSessionsByTerminal(1, propValue: "terminal_check")
            .WithSessionInventoryContributions(MakeContributionsRow(new List<DeletionDecrementKey>
            {
                new DeletionDecrementKey { Vendor = "Contoso", Name = "App", Version = "1.0" },
            }, compressed: false)));

        var manifest = await NewBuilder(reader).BuildAsync(
            TenantId, SessionId, "admin_delete",
            new DeletionActor { Type = "admin", Actor = "alice@example.com" },
            new DeletionRetentionContext { TenantRetentionDays = 90 });

        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, DeletionManifestJson.SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<DeletionManifest>(json, DeletionManifestJson.SerializerOptions);

        Assert.NotNull(roundTripped);
        Assert.Equal(manifest.ManifestId, roundTripped!.ManifestId);
        Assert.Equal(manifest.SchemaHash, roundTripped.SchemaHash);
        Assert.Equal(manifest.Steps.Count, roundTripped.Steps.Count);

        // For each step's row dump, confirm we have everything a restore needs to replay.
        foreach (var step in roundTripped.Steps)
        {
            foreach (var row in step.Rows)
            {
                Assert.False(string.IsNullOrEmpty(row.Pk));
                Assert.False(string.IsNullOrEmpty(row.Rk));
                Assert.NotNull(row.Props);
            }
            if (step.Class == DeletionStepClass.Aggregate)
            {
                Assert.NotNull(step.Decrements);
                foreach (var k in step.Decrements!)
                {
                    Assert.False(string.IsNullOrEmpty(k.Vendor));
                    Assert.False(string.IsNullOrEmpty(k.Name));
                }
            }
        }

        // Spot-check: the Marker we seeded into Events survives the round-trip end-to-end.
        var eventsStep = roundTripped.Steps.Single(s => s.Table == Constants.TableNames.Events);
        Assert.Equal(2, eventsStep.Rows.Count);
        Assert.Equal(DeletionPropEdmType.String, eventsStep.Rows[0].Props["Marker"].EdmType);
        Assert.Equal("events_check", eventsStep.Rows[0].Props["Marker"].Value.GetString());
    }

    [Fact]
    public async Task Build_preserves_per_column_edm_types_for_restore()
    {
        // Restore-faithfulness: every Azure Tables type Azure SDK exposes must surface in the dump
        // with the correct EdmType tag so a future restore can re-Insert with the same column type
        // instead of collapsing everything to plain strings.
        var richRow = new TableEntity(TenantId, "rich")
        {
            ["StringProp"]   = "hello",
            ["IntProp"]      = 42,
            ["LongProp"]     = 123456789012L,
            ["DoubleProp"]   = 3.14,
            ["BoolProp"]     = true,
            ["DateProp"]     = new DateTime(2026, 5, 12, 10, 0, 0, DateTimeKind.Utc),
            ["GuidProp"]     = new Guid("33333333-3333-3333-3333-333333333333"),
            ["BinaryProp"]   = new byte[] { 0x01, 0x02, 0x03 },
        };
        richRow.ETag = new ETag("0xRICH");

        var reader = new Mock<ISessionDeletionInventoryReader>();
        reader.Setup(r => r.GetSessionRowAsync(TenantId, SessionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(richRow);
        reader.Setup(r => r.GetSessionsIndexRowAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((TableEntity?)null);
        reader.Setup(r => r.GetEntityOrNullAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((TableEntity?)null);
        reader.Setup(r => r.QueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(EmptyAsyncEnumerable());

        var manifest = await NewBuilder(reader.Object).BuildAsync(
            TenantId, SessionId, "admin_delete",
            new DeletionActor { Type = "admin", Actor = "alice@example.com" },
            new DeletionRetentionContext { TenantRetentionDays = 90 });

        // The rich row was placed on the Sessions row → it ends up in the FINAL tombstone step.
        var tombstone = manifest.Steps.Last();
        Assert.Equal(DeletionStepClass.Final, tombstone.Class);
        var dump = tombstone.Rows[0];

        Assert.Equal(DeletionPropEdmType.String,   dump.Props["StringProp"].EdmType);
        Assert.Equal(DeletionPropEdmType.Int32,    dump.Props["IntProp"].EdmType);
        Assert.Equal(DeletionPropEdmType.Int64,    dump.Props["LongProp"].EdmType);
        Assert.Equal(DeletionPropEdmType.Double,   dump.Props["DoubleProp"].EdmType);
        Assert.Equal(DeletionPropEdmType.Boolean,  dump.Props["BoolProp"].EdmType);
        Assert.Equal(DeletionPropEdmType.DateTime, dump.Props["DateProp"].EdmType);
        Assert.Equal(DeletionPropEdmType.Guid,     dump.Props["GuidProp"].EdmType);
        Assert.Equal(DeletionPropEdmType.Binary,   dump.Props["BinaryProp"].EdmType);

        // Values round-trip through System.Text.Json with the encoding the restore expects.
        Assert.Equal("hello", dump.Props["StringProp"].Value.GetString());
        Assert.Equal(42, dump.Props["IntProp"].Value.GetInt32());
        Assert.Equal(123456789012L, dump.Props["LongProp"].Value.GetInt64());
        Assert.Equal(3.14, dump.Props["DoubleProp"].Value.GetDouble(), 5);
        Assert.True(dump.Props["BoolProp"].Value.GetBoolean());

        // DateTime → ISO-8601 string; restore parses with DateTime.Parse(round-trip kind).
        var dateStr = dump.Props["DateProp"].Value.GetString();
        Assert.NotNull(dateStr);
        var parsed = DateTime.Parse(dateStr!, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(new DateTime(2026, 5, 12, 10, 0, 0, DateTimeKind.Utc), parsed.ToUniversalTime());

        // Guid → string "33333333-3333-3333-3333-333333333333".
        Assert.Equal("33333333-3333-3333-3333-333333333333", dump.Props["GuidProp"].Value.GetString());

        // Binary → Base64 of the original bytes.
        var b64 = dump.Props["BinaryProp"].Value.GetString();
        Assert.Equal(Convert.ToBase64String(new byte[] { 0x01, 0x02, 0x03 }), b64);
    }

    [Fact]
    public async Task Build_falls_back_to_sessionsIndex_partition_scan_when_indexrowkey_missing()
    {
        // Edge: Sessions row exists but its IndexRowKey property is empty / corrupted. Without a
        // fallback the cascade would leave a SessionsIndex orphan after tombstone. The builder
        // must scan SessionsIndex by (PartitionKey, SessionId) to find the row anyway.
        var sessionRow = new TableEntity(TenantId, SessionId)
        {
            ["IndexRowKey"]         = string.Empty,
            ["DiagnosticsBlobName"] = string.Empty,
            ["Status"]              = "Succeeded",
        };
        sessionRow.ETag = new ETag("0xSESS");

        var orphanedIndexRow = new TableEntity(TenantId, "FALLBACK_" + SessionId)
        {
            ["SessionId"] = SessionId,
            ["Status"]    = "Succeeded",
        };
        orphanedIndexRow.ETag = new ETag("0xIDX");

        var reader = new Mock<ISessionDeletionInventoryReader>();
        reader.Setup(r => r.GetSessionRowAsync(TenantId, SessionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(sessionRow);
        // Direct lookup with empty key intentionally NOT set up — builder must skip it and scan instead.
        reader.Setup(r => r.GetSessionsIndexRowAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((TableEntity?)null);
        reader.Setup(r => r.GetEntityOrNullAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((TableEntity?)null);
        reader.Setup(r => r.QueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns<string, string, CancellationToken>((tableName, filter, _) =>
                  tableName == Constants.TableNames.SessionsIndex
                      && filter.Contains($"SessionId eq '{SessionId}'", StringComparison.Ordinal)
                          ? AsyncFrom(new[] { orphanedIndexRow })
                          : EmptyAsyncEnumerable());

        var manifest = await NewBuilder(reader.Object).BuildAsync(
            TenantId, SessionId, "admin_delete",
            new DeletionActor { Type = "admin", Actor = "alice@example.com" },
            new DeletionRetentionContext { TenantRetentionDays = 90 });

        var tombstone = manifest.Steps.Last();
        Assert.Equal(DeletionStepClass.Final, tombstone.Class);
        Assert.Equal(2, tombstone.Rows.Count);
        // SessionsIndex first (recovered via fallback scan), then Sessions.
        Assert.Equal("FALLBACK_" + SessionId, tombstone.Rows[0].Rk);
        Assert.Equal(SessionId, tombstone.Rows[1].Rk);
    }

    [Fact]
    public async Task Build_falls_back_to_sessionsIndex_partition_scan_when_direct_lookup_returns_null()
    {
        // Edge: Sessions.IndexRowKey points at a SessionsIndex row that has already been deleted
        // (or never existed). The direct lookup returns 404 → builder must still scan to catch any
        // OTHER index row that happens to reference this SessionId. Defensive: handles the case
        // where IndexRowKey drifted between Sessions write and cascade build.
        const string staleIndexRowKey = "STALE_KEY";
        var sessionRow = new TableEntity(TenantId, SessionId)
        {
            ["IndexRowKey"] = staleIndexRowKey,
            ["Status"]      = "Succeeded",
        };
        sessionRow.ETag = new ETag("0xSESS");

        var recoveredIndexRow = new TableEntity(TenantId, "RECOVERED_" + SessionId)
        {
            ["SessionId"] = SessionId,
            ["Status"]    = "Succeeded",
        };
        recoveredIndexRow.ETag = new ETag("0xIDX");

        var reader = new Mock<ISessionDeletionInventoryReader>();
        reader.Setup(r => r.GetSessionRowAsync(TenantId, SessionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(sessionRow);
        reader.Setup(r => r.GetSessionsIndexRowAsync(TenantId, staleIndexRowKey, It.IsAny<CancellationToken>()))
              .ReturnsAsync((TableEntity?)null); // stale key → 404
        reader.Setup(r => r.GetEntityOrNullAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((TableEntity?)null);
        reader.Setup(r => r.QueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns<string, string, CancellationToken>((tableName, filter, _) =>
                  tableName == Constants.TableNames.SessionsIndex
                      && filter.Contains($"SessionId eq '{SessionId}'", StringComparison.Ordinal)
                          ? AsyncFrom(new[] { recoveredIndexRow })
                          : EmptyAsyncEnumerable());

        var manifest = await NewBuilder(reader.Object).BuildAsync(
            TenantId, SessionId, "admin_delete",
            new DeletionActor { Type = "admin", Actor = "alice@example.com" },
            new DeletionRetentionContext { TenantRetentionDays = 90 });

        var tombstone = manifest.Steps.Last();
        Assert.Equal(2, tombstone.Rows.Count);
        Assert.Equal("RECOVERED_" + SessionId, tombstone.Rows[0].Rk);
    }

    [Fact]
    public async Task Build_handles_missing_sessions_row_without_throwing()
    {
        // Edge: cascade preflight fired against an already-deleted session. Builder must not throw;
        // the tombstone step just carries no rows.
        var reader = new Mock<ISessionDeletionInventoryReader>();
        reader.Setup(r => r.GetSessionRowAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((TableEntity?)null);
        reader.Setup(r => r.GetSessionsIndexRowAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((TableEntity?)null);
        reader.Setup(r => r.GetEntityOrNullAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((TableEntity?)null);
        reader.Setup(r => r.QueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(EmptyAsyncEnumerable());

        var manifest = await NewBuilder(reader.Object).BuildAsync(
            TenantId, SessionId, "admin_delete",
            new DeletionActor { Type = "admin", Actor = "alice@example.com" },
            new DeletionRetentionContext { TenantRetentionDays = 90 });

        var tombstone = manifest.Steps.Last();
        Assert.Equal(DeletionStepClass.Final, tombstone.Class);
        Assert.Equal(0, tombstone.RowCount);
        Assert.Empty(tombstone.Rows);
    }

    // ---------------------------------------------------------------- Test-fixture plumbing ----

    private static DeletionManifestBuilder NewBuilder(ISessionDeletionInventoryReader reader)
        => new DeletionManifestBuilder(reader, NullLogger<DeletionManifestBuilder>.Instance);

    private static ISessionDeletionInventoryReader NewReader(Action<ReaderSeed> seedAction)
    {
        var seed = new ReaderSeed();
        seedAction(seed);
        return seed.Build();
    }

    private static TableEntity MakeContributionsRow(List<DeletionDecrementKey> keys, bool compressed)
    {
        var json = JsonSerializer.Serialize(keys, DeletionManifestJson.SerializerOptions);
        string stored;
        if (compressed)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                gzip.Write(bytes, 0, bytes.Length);
            }
            stored = Convert.ToBase64String(output.ToArray());
        }
        else
        {
            stored = json;
        }

        return new TableEntity(TenantId, SessionId)
        {
            ["SoftwareKeysJson"] = stored,
            ["IsCompressed"]     = compressed,
            ["CountedAt"]        = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            ["LastUpdatedAt"]    = new DateTime(2026, 5, 12, 9, 0, 0, DateTimeKind.Utc),
            ["KeyCount"]         = keys.Count,
        };
    }

    private static async IAsyncEnumerable<TableEntity> EmptyAsyncEnumerable()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<TableEntity> AsyncFrom(IEnumerable<TableEntity> source)
    {
        foreach (var entity in source)
        {
            yield return entity;
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// Fluent test-data seed. Every <c>WithXxx</c> registers Moq setups for the corresponding
    /// table; tables not seeded return empty / null on the reader.
    /// </summary>
    private class ReaderSeed
    {
        private readonly Dictionary<string, List<TableEntity>> _query = new(StringComparer.Ordinal);
        private TableEntity? _sessionsRow;
        private TableEntity? _sessionsIndexRow;
        private TableEntity? _vulnerabilityReport;
        private TableEntity? _deviceSnapshot;
        private TableEntity? _eventSessionIndex;
        private TableEntity? _sessionInventoryContributions;
        private string _sessionsIndexRowKey = "INDEX_" + SessionId;

        public ReaderSeed WithSessionsRow(string? diagnosticsBlobName = null)
        {
            _sessionsRow = new TableEntity(TenantId, SessionId)
            {
                ["IndexRowKey"]         = _sessionsIndexRowKey,
                ["DiagnosticsBlobName"] = diagnosticsBlobName ?? string.Empty,
                ["Status"]              = "Succeeded",
            };
            // Mark with a stable ETag so dumps look real.
            _sessionsRow.ETag = new ETag("0xSESS");
            return this;
        }

        public ReaderSeed WithSessionsIndexRow()
        {
            _sessionsIndexRow = new TableEntity(TenantId, _sessionsIndexRowKey)
            {
                ["SessionId"] = SessionId,
                ["Status"]    = "Succeeded",
            };
            _sessionsIndexRow.ETag = new ETag("0xIDX");
            return this;
        }

        public ReaderSeed WithEvents(int n, string propValue = "evt") => SeedSessionPartition(Constants.TableNames.Events, n, propValue);
        public ReaderSeed WithRuleResults(int n, string propValue = "rule") => SeedSessionPartition(Constants.TableNames.RuleResults, n, propValue);
        public ReaderSeed WithSignals(int n, string propValue = "sig") => SeedSessionPartition(Constants.TableNames.Signals, n, propValue);
        public ReaderSeed WithDecisionTransitions(int n, string propValue = "tx") => SeedSessionPartition(Constants.TableNames.DecisionTransitions, n, propValue);

        public ReaderSeed WithAppInstallSummaries(int n, string propValue = "app")
        {
            var rows = new List<TableEntity>(n);
            for (int i = 0; i < n; i++)
            {
                var e = new TableEntity(TenantId, $"app{i}")
                {
                    ["SessionId"] = SessionId,
                    ["Marker"]    = propValue,
                };
                e.ETag = new ETag("0xAPP" + i);
                rows.Add(e);
            }
            _query[Constants.TableNames.AppInstallSummaries] = rows;
            return this;
        }

        public ReaderSeed WithVulnerabilityReport(string propValue = "vuln")
        {
            _vulnerabilityReport = new TableEntity($"{TenantId}_{SessionId}", "report")
            {
                ["Marker"] = propValue,
            };
            _vulnerabilityReport.ETag = new ETag("0xVULN");
            return this;
        }

        public ReaderSeed WithDeviceSnapshot(string propValue = "dev")
        {
            _deviceSnapshot = new TableEntity(TenantId, SessionId)
            {
                ["Marker"] = propValue,
            };
            _deviceSnapshot.ETag = new ETag("0xDEV");
            return this;
        }

        public ReaderSeed WithEventSessionIndex(string propValue = "esi")
        {
            _eventSessionIndex = new TableEntity(TenantId, SessionId)
            {
                ["Marker"] = propValue,
            };
            _eventSessionIndex.ETag = new ETag("0xESI");
            return this;
        }

        public ReaderSeed WithEventTypeIndex(int n, string propValue = "eti")
        {
            // PK = {tenantId}_{eventType}, RK ends with _{sessionId}.
            var rows = new List<TableEntity>(n);
            for (int i = 0; i < n; i++)
            {
                var e = new TableEntity($"{TenantId}_evt{i}", $"00000000000000{i:D5}_{SessionId}")
                {
                    ["Marker"] = propValue,
                    ["SessionId"] = SessionId,
                };
                e.ETag = new ETag("0xETI" + i);
                rows.Add(e);
            }
            _query[Constants.TableNames.EventTypeIndex] = rows;
            return this;
        }

        public ReaderSeed WithCveIndex(int n, string propValue = "cve")
        {
            // PK = {tenantId}_{cveId}, RK = sessionId.
            var rows = new List<TableEntity>(n);
            for (int i = 0; i < n; i++)
            {
                var e = new TableEntity($"{TenantId}_CVE-2026-{i:D4}", SessionId)
                {
                    ["Marker"] = propValue,
                    ["SessionId"] = SessionId,
                };
                e.ETag = new ETag("0xCVE" + i);
                rows.Add(e);
            }
            _query[Constants.TableNames.CveIndex] = rows;
            return this;
        }

        public ReaderSeed WithSessionsByTerminal(int n, string propValue = "term") => SeedDiscriminatorPkProp(Constants.TableNames.SessionsByTerminal, n, propValue, "Failed");
        public ReaderSeed WithSessionsByStage(int n, string propValue = "stg")    => SeedDiscriminatorPkProp(Constants.TableNames.SessionsByStage, n, propValue, "AccountSetup");
        public ReaderSeed WithDeadEndsByReason(int n, string propValue = "de")    => SeedDiscriminatorPkProp(Constants.TableNames.DeadEndsByReason, n, propValue, "Timeout");
        public ReaderSeed WithClassifierVerdictsByIdLevel(int n, string propValue = "cls") => SeedDiscriminatorPkProp(Constants.TableNames.ClassifierVerdictsByIdLevel, n, propValue, "EspApps_High");
        public ReaderSeed WithSignalsByKind(int n, string propValue = "sk")        => SeedDiscriminatorPkProp(Constants.TableNames.SignalsByKind, n, propValue, "EspPhaseChanged");

        public ReaderSeed WithSessionInventoryContributions(TableEntity row)
        {
            _sessionInventoryContributions = row;
            _sessionInventoryContributions.ETag = new ETag("0xCONTRIB");
            return this;
        }

        public ISessionDeletionInventoryReader Build()
        {
            var reader = new Mock<ISessionDeletionInventoryReader>();

            reader.Setup(r => r.GetSessionRowAsync(TenantId, SessionId, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(_sessionsRow);
            reader.Setup(r => r.GetSessionsIndexRowAsync(TenantId, _sessionsIndexRowKey, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(_sessionsIndexRow);

            // PK_RK_EXACT lookups
            reader.Setup(r => r.GetEntityOrNullAsync(Constants.TableNames.VulnerabilityReports, It.IsAny<string>(), "report", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(_vulnerabilityReport);
            reader.Setup(r => r.GetEntityOrNullAsync(Constants.TableNames.DeviceSnapshot, TenantId, SessionId, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(_deviceSnapshot);
            reader.Setup(r => r.GetEntityOrNullAsync(Constants.TableNames.EventSessionIndex, TenantId, SessionId, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(_eventSessionIndex);
            reader.Setup(r => r.GetEntityOrNullAsync(Constants.TableNames.SessionInventoryContributions, TenantId, SessionId, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(_sessionInventoryContributions);
            // Default null for any other GetEntityOrNullAsync.
            reader.Setup(r => r.GetEntityOrNullAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync((TableEntity?)null);
            // Re-register the specific ones AFTER the default catch-all (Moq honors the most recent setup).
            reader.Setup(r => r.GetEntityOrNullAsync(Constants.TableNames.VulnerabilityReports, It.IsAny<string>(), "report", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(_vulnerabilityReport);
            reader.Setup(r => r.GetEntityOrNullAsync(Constants.TableNames.DeviceSnapshot, TenantId, SessionId, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(_deviceSnapshot);
            reader.Setup(r => r.GetEntityOrNullAsync(Constants.TableNames.EventSessionIndex, TenantId, SessionId, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(_eventSessionIndex);
            reader.Setup(r => r.GetEntityOrNullAsync(Constants.TableNames.SessionInventoryContributions, TenantId, SessionId, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(_sessionInventoryContributions);

            // QueryAsync: per-table seeded list, otherwise empty.
            reader.Setup(r => r.QueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Returns<string, string, CancellationToken>((tableName, _, _) =>
                      _query.TryGetValue(tableName, out var rows)
                          ? AsyncFrom(rows)
                          : EmptyAsyncEnumerable());

            return reader.Object;
        }

        private ReaderSeed SeedSessionPartition(string tableName, int n, string propValue)
        {
            var partitionKey = $"{TenantId}_{SessionId}";
            var rows = new List<TableEntity>(n);
            for (int i = 0; i < n; i++)
            {
                var e = new TableEntity(partitionKey, $"{tableName}_{i:D5}")
                {
                    ["Marker"] = propValue,
                    ["SessionId"] = SessionId,
                };
                e.ETag = new ETag("0x" + tableName + i);
                rows.Add(e);
            }
            _query[tableName] = rows;
            return this;
        }

        private ReaderSeed SeedDiscriminatorPkProp(string tableName, int n, string propValue, string discriminator)
        {
            // PK = {tenantId}_{discriminator}, server-side filter on PK prefix + SessionId == sessionId.
            var rows = new List<TableEntity>(n);
            for (int i = 0; i < n; i++)
            {
                var e = new TableEntity($"{TenantId}_{discriminator}", $"{tableName}_{i:D5}")
                {
                    ["Marker"] = propValue,
                    ["SessionId"] = SessionId,
                };
                e.ETag = new ETag("0x" + tableName + i);
                rows.Add(e);
            }
            _query[tableName] = rows;
            return this;
        }
    }
}

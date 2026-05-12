using System;
using System.Collections.Generic;
using System.Text.Json;
using AutopilotMonitor.Shared.Models.Deletion;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pure JSON serialization round-trip tests. The cascade plan §3 schema is the wire contract;
/// both producer + worker depend on byte-faithful round-trip of the manifest types.
/// </summary>
public class DeletionManifestSerializationTests
{
    [Fact]
    public void Manifest_round_trips_through_system_text_json_with_camel_case()
    {
        var original = BuildSampleManifest();

        var json = JsonSerializer.Serialize(original, DeletionManifestJson.SerializerOptions);

        // Camel-case casing per plan §3 (top-level fields).
        Assert.Contains("\"manifestId\":", json);
        Assert.Contains("\"tenantId\":", json);
        Assert.Contains("\"preflightCounts\":", json);
        Assert.Contains("\"steps\":", json);
        Assert.Contains("\"schemaHash\":", json);
        // PascalCase shouldn't leak.
        Assert.DoesNotContain("\"ManifestId\":", json);
        Assert.DoesNotContain("\"Steps\":", json);

        var decoded = JsonSerializer.Deserialize<DeletionManifest>(json, DeletionManifestJson.SerializerOptions);

        Assert.NotNull(decoded);
        Assert.Equal(original.ManifestId, decoded!.ManifestId);
        Assert.Equal(original.TenantId, decoded.TenantId);
        Assert.Equal(original.SessionId, decoded.SessionId);
        Assert.Equal(original.Reason, decoded.Reason);
        Assert.Equal(original.SchemaHash, decoded.SchemaHash);
        Assert.Equal(original.PreflightCounts.Count, decoded.PreflightCounts.Count);
        Assert.Equal(original.Steps.Count, decoded.Steps.Count);
    }

    [Fact]
    public void DeletionStep_AGGREGATE_carries_decrements_but_no_rows()
    {
        var step = new DeletionStep
        {
            Order = 16,
            Step = DeletionStepNames.SoftwareInventoryDecrement,
            Class = DeletionStepClass.Aggregate,
            RowCount = 2,
            Rows = new List<DeletionRowDump>(),
            Decrements = new List<DeletionDecrementKey>
            {
                new DeletionDecrementKey { Vendor = "Contoso", Name = "Widget", Version = "1.0" },
                new DeletionDecrementKey { Vendor = "Fabrikam", Name = "Gadget", Version = "2.0" },
            },
        };

        var json = JsonSerializer.Serialize(step, DeletionManifestJson.SerializerOptions);
        var decoded = JsonSerializer.Deserialize<DeletionStep>(json, DeletionManifestJson.SerializerOptions);

        Assert.NotNull(decoded);
        Assert.Equal(DeletionStepClass.Aggregate, decoded!.Class);
        Assert.Empty(decoded.Rows);
        Assert.NotNull(decoded.Decrements);
        Assert.Equal(2, decoded.Decrements!.Count);
        Assert.Equal("Contoso", decoded.Decrements[0].Vendor);
        Assert.Equal("Widget", decoded.Decrements[0].Name);
        // Plan §3: AGGREGATE steps have decrements but no Rows entries.
        Assert.True(decoded.Rows.Count == 0);
    }

    [Fact]
    public void DeletionStep_FINAL_carries_two_rows_in_order()
    {
        // Plan §5 PR4: tombstone deletes SessionsIndex FIRST, then Sessions.
        var step = new DeletionStep
        {
            Order = 18,
            Step = DeletionStepNames.Tombstone,
            Class = DeletionStepClass.Final,
            RowCount = 2,
            Rows = new List<DeletionRowDump>
            {
                new DeletionRowDump { Pk = "tenant", Rk = "indexRowKey", Etag = "0x1" },
                new DeletionRowDump { Pk = "tenant", Rk = "sessionId",   Etag = "0x2" },
            },
        };

        var json = JsonSerializer.Serialize(step, DeletionManifestJson.SerializerOptions);
        var decoded = JsonSerializer.Deserialize<DeletionStep>(json, DeletionManifestJson.SerializerOptions);

        Assert.NotNull(decoded);
        Assert.Equal(DeletionStepClass.Final, decoded!.Class);
        Assert.Equal(2, decoded.Rows.Count);
        Assert.Equal("indexRowKey", decoded.Rows[0].Rk);
        Assert.Equal("sessionId", decoded.Rows[1].Rk);
    }

    [Fact]
    public void DeletionRowDump_typed_props_round_trip()
    {
        var props = new Dictionary<string, DeletionPropValue>(StringComparer.Ordinal)
        {
            ["StringProp"] = new DeletionPropValue { EdmType = DeletionPropEdmType.String,  Value = ToElement("\"hello\"") },
            ["IntProp"]    = new DeletionPropValue { EdmType = DeletionPropEdmType.Int32,   Value = ToElement("42") },
            ["BoolProp"]   = new DeletionPropValue { EdmType = DeletionPropEdmType.Boolean, Value = ToElement("true") },
            ["NumberProp"] = new DeletionPropValue { EdmType = DeletionPropEdmType.Double,  Value = ToElement("3.14") },
        };
        var dump = new DeletionRowDump { Pk = "pk", Rk = "rk", Etag = "0x1", Props = props };

        var json = JsonSerializer.Serialize(dump, DeletionManifestJson.SerializerOptions);
        var decoded = JsonSerializer.Deserialize<DeletionRowDump>(json, DeletionManifestJson.SerializerOptions);

        Assert.NotNull(decoded);
        Assert.Equal(DeletionPropEdmType.String,  decoded!.Props["StringProp"].EdmType);
        Assert.Equal(DeletionPropEdmType.Int32,   decoded.Props["IntProp"].EdmType);
        Assert.Equal(DeletionPropEdmType.Boolean, decoded.Props["BoolProp"].EdmType);
        Assert.Equal(DeletionPropEdmType.Double,  decoded.Props["NumberProp"].EdmType);

        Assert.Equal("hello", decoded.Props["StringProp"].Value.GetString());
        Assert.Equal(42,      decoded.Props["IntProp"].Value.GetInt32());
        Assert.True(decoded.Props["BoolProp"].Value.GetBoolean());
        Assert.Equal(3.14,    decoded.Props["NumberProp"].Value.GetDouble(), 5);
    }

    [Fact]
    public void DeletionProgress_round_trips_under_500_bytes()
    {
        // Plan §3 R9: progress blob is intentionally minimal so an operator can open it during incident response.
        var progress = new DeletionProgress
        {
            SnapshotSha256 = new string('a', 64),
            CompletedSteps = new HashSet<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17 },
            VerificationDone = true,
            CompletedAt = new DateTime(2026, 5, 12, 10, 0, 0, DateTimeKind.Utc),
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(progress, DeletionManifestJson.SerializerOptions);

        Assert.True(json.Length < 500, $"Progress blob serialized to {json.Length} bytes — expected <500 per §3 R9.");

        var decoded = JsonSerializer.Deserialize<DeletionProgress>(json, DeletionManifestJson.SerializerOptions);
        Assert.NotNull(decoded);
        Assert.Equal(progress.SnapshotSha256, decoded!.SnapshotSha256);
        Assert.Equal(progress.CompletedSteps, decoded.CompletedSteps);
        Assert.True(decoded.VerificationDone);
        Assert.Equal(progress.CompletedAt, decoded.CompletedAt);
    }

    private static DeletionManifest BuildSampleManifest() => new DeletionManifest
    {
        ManifestId = "0123456789ABCDEF_FEDCBA9876543210",
        ManifestVersion = 1,
        TenantId = "11111111-1111-1111-1111-111111111111",
        SessionId = "22222222-2222-2222-2222-222222222222",
        CreatedAt = new DateTime(2026, 5, 12, 10, 0, 0, DateTimeKind.Utc),
        CreatedBy = new DeletionActor { Type = "admin", Actor = "alice@example.com" },
        Reason = "admin_delete",
        RetentionContext = new DeletionRetentionContext { TenantRetentionDays = 90 },
        PreflightCounts = new Dictionary<string, int> { ["events"] = 5, ["ruleResults"] = 1 },
        DiagnosticsBlobName = "diag.zip",
        Steps = new List<DeletionStep>
        {
            new DeletionStep
            {
                Order = 1,
                Table = "Events",
                Class = DeletionStepClass.PkBySession,
                RowCount = 1,
                Rows = new List<DeletionRowDump>
                {
                    new DeletionRowDump { Pk = "t_s", Rk = "evt", Etag = "0x1" },
                },
            },
        },
        SchemaHash = "sha256:abc",
    };

    private static JsonElement ToElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

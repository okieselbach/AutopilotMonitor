using System;
using System.Collections.Generic;
using System.Text.Json;
using AutopilotMonitor.Shared.Models.Backup;
using AutopilotMonitor.Shared.Models.Deletion;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Locks the JSON shape produced by <see cref="BackupManifestJson.SerializerOptions"/>
/// so the manifest blob format stays implementation-stable across enum refactors.
/// </summary>
public class BackupManifestJsonTests
{
    [Fact]
    public void Enums_Serialize_AsStrings_NotNumbers()
    {
        var manifest = new CriticalTableBackupManifest
        {
            BackupId = "test",
            Outcome = BackupOutcome.Partial,
            Tables = { new CriticalTableBackupTableEntry { TableName = "X", Status = TableBackupStatus.Skipped } },
        };

        var json = JsonSerializer.Serialize(manifest, BackupManifestJson.SerializerOptions);

        Assert.Contains("\"outcome\":\"Partial\"", json);
        Assert.Contains("\"status\":\"Skipped\"", json);
        Assert.DoesNotContain("\"outcome\":1", json);
        Assert.DoesNotContain("\"status\":2", json);
    }

    [Fact]
    public void Deserialize_RejectsNumericEnumValues()
    {
        var jsonWithNumber = "{\"backupId\":\"x\",\"outcome\":1,\"tables\":[]}";
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<CriticalTableBackupManifest>(jsonWithNumber, BackupManifestJson.SerializerOptions));
    }

    [Fact]
    public void DictionaryKeyPolicy_Unset_PreservesAzureTableColumnNames()
    {
        // DeletionRowDump.Props uses Azure Table column names as dictionary keys; camelcasing them
        // would manufacture new columns on restore. The serializer-options block in
        // BackupManifestJson deliberately leaves DictionaryKeyPolicy unset for this reason.
        var dump = new DeletionRowDump
        {
            Pk = "p", Rk = "r",
            Props = new Dictionary<string, DeletionPropValue>
            {
                ["IsEnabled"] = new() { EdmType = DeletionPropEdmType.Boolean, Value = ParseJson("true") },
                ["EventType"] = new() { EdmType = DeletionPropEdmType.String, Value = ParseJson("\"x\"") },
            },
        };

        var json = JsonSerializer.Serialize(dump, BackupManifestJson.SerializerOptions);

        Assert.Contains("\"IsEnabled\"", json);
        Assert.Contains("\"EventType\"", json);
        // Negative — camelcased variants would silently break restore against the live table.
        Assert.DoesNotContain("\"isEnabled\":", json);
        Assert.DoesNotContain("\"eventType\":", json);
    }

    [Fact]
    public void Roundtrip_ManifestFieldsPreserved()
    {
        var startedAt = new DateTime(2026, 5, 22, 4, 0, 0, DateTimeKind.Utc);
        var manifest = new CriticalTableBackupManifest
        {
            SchemaVersion = 1,
            BackupId = "20260522T040000Z_abcdef01",
            StartedAtUtc = startedAt,
            CompletedAtUtc = startedAt.AddMinutes(5),
            TriggeredBy = "alice@contoso.test",
            Outcome = BackupOutcome.Success,
            Tables =
            {
                new CriticalTableBackupTableEntry
                {
                    TableName = "AdminConfiguration",
                    Status = TableBackupStatus.Ok,
                    RowCount = 12,
                    ByteSize = 4096,
                    Sha256Hex = "ABCDEF",
                    BlobName = "20260522T040000Z_abcdef01/AdminConfiguration.ndjson",
                },
            },
        };

        var json = JsonSerializer.Serialize(manifest, BackupManifestJson.SerializerOptions);
        var roundtripped = JsonSerializer.Deserialize<CriticalTableBackupManifest>(json, BackupManifestJson.SerializerOptions);

        Assert.NotNull(roundtripped);
        Assert.Equal(manifest.BackupId, roundtripped!.BackupId);
        Assert.Equal(manifest.TriggeredBy, roundtripped.TriggeredBy);
        Assert.Equal(BackupOutcome.Success, roundtripped.Outcome);
        Assert.Single(roundtripped.Tables);
        Assert.Equal("AdminConfiguration", roundtripped.Tables[0].TableName);
        Assert.Equal(TableBackupStatus.Ok, roundtripped.Tables[0].Status);
        Assert.Equal(12, roundtripped.Tables[0].RowCount);
    }

    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

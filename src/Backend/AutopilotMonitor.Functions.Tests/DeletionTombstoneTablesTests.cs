using System.Text.Json;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Deletion;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the FINAL-step table resolution: an explicit <see cref="DeletionRowDump.Table"/> wins
/// unconditionally; only legacy manifests (written before the field existed) fall back to the
/// RowKey-shape heuristic. The adversarial rows deliberately contradict the heuristic so a
/// regression to Contains('_') routing flips the assertion.
/// </summary>
public class DeletionTombstoneTablesTests
{
    [Fact]
    public void Explicit_table_wins_over_contradicting_rowkey_shape()
    {
        // RK contains '_' (heuristic would say SessionsIndex) but Table says Sessions — and vice versa.
        var sessionsRow = new DeletionRowDump { Pk = "t", Rk = "id_with_underscores", Table = Constants.TableNames.Sessions };
        var indexRow = new DeletionRowDump { Pk = "t", Rk = "plainkey", Table = Constants.TableNames.SessionsIndex };

        Assert.Equal(Constants.TableNames.Sessions, DeletionTombstoneTables.Resolve(sessionsRow));
        Assert.Equal(Constants.TableNames.SessionsIndex, DeletionTombstoneTables.Resolve(indexRow));
    }

    [Fact]
    public void Legacy_rows_without_table_fall_back_to_rowkey_shape()
    {
        // Pre-field manifests: Sessions RK is the bare session GUID, index RK is composed.
        var legacySessions = new DeletionRowDump { Pk = "t", Rk = "22222222-2222-2222-2222-222222222222" };
        var legacyIndex = new DeletionRowDump { Pk = "t", Rk = "6299999999999999999_22222222-2222-2222-2222-222222222222" };

        Assert.Equal(Constants.TableNames.Sessions, DeletionTombstoneTables.Resolve(legacySessions));
        Assert.Equal(Constants.TableNames.SessionsIndex, DeletionTombstoneTables.Resolve(legacyIndex));
    }

    [Fact]
    public void Table_field_round_trips_through_manifest_json()
    {
        var dump = new DeletionRowDump { Pk = "t", Rk = "rk", Table = Constants.TableNames.SessionsIndex };
        var json = JsonSerializer.Serialize(dump, DeletionManifestJson.SerializerOptions);
        Assert.Contains("\"table\":", json);

        var roundTripped = JsonSerializer.Deserialize<DeletionRowDump>(json, DeletionManifestJson.SerializerOptions);
        Assert.Equal(Constants.TableNames.SessionsIndex, roundTripped!.Table);
    }

    [Fact]
    public void Legacy_manifest_json_without_table_deserializes_to_null_and_is_omitted_on_write()
    {
        // WhenWritingNull keeps rows of table-targeted steps (Table always null there) byte-
        // identical to the pre-field format — SchemaHash of those steps is unaffected.
        var legacyJson = "{\"pk\":\"t\",\"rk\":\"rk\",\"props\":{}}";
        var dump = JsonSerializer.Deserialize<DeletionRowDump>(legacyJson, DeletionManifestJson.SerializerOptions);
        Assert.Null(dump!.Table);

        var rewritten = JsonSerializer.Serialize(dump, DeletionManifestJson.SerializerOptions);
        Assert.DoesNotContain("table", rewritten);
    }
}

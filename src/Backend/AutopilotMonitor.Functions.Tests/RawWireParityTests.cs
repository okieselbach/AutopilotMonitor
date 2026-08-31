using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Wire-identity proof for the Functions/Raw folder (anonymous-object → typed-DTO migration).
/// Each case serializes the OLD anonymous literal exactly as it stood in the pre-migration
/// code (keys, order, compile-time value types) against the NEW DTO with the same values.
/// Raw rows are PascalCase-verbatim table columns carried as dictionaries — the production
/// options set PropertyNamingPolicy (camelCase) but NO DictionaryKeyPolicy, so dictionary
/// keys must survive untouched on both sides; one case asserts that explicitly.
/// </summary>
public class RawWireParityTests
{
    private static readonly JsonSerializerOptions WireOptions = ApiJsonOptions.Create();

    private static List<IReadOnlyDictionary<string, object?>> SampleEventRows() =>
        new()
        {
            new Dictionary<string, object?>
            {
                ["PartitionKey"] = "d3b7f8a2-1c4e-4f5a-9b6d-0e2f3a4b5c6d_8f1e2d3c-4b5a-6789-abcd-ef0123456789",
                ["RowKey"] = "0000000042",
                ["Timestamp"] = new DateTimeOffset(2026, 8, 30, 12, 34, 56, TimeSpan.Zero),
                ["EventType"] = "esp_phase_change",
                ["Severity"] = 1,
                ["Source"] = "EspWatcher",
                ["Sequence"] = 42L,
                ["DataJson"] = "{\"phase\":\"DeviceSetup\"}",
            },
        };

    // ── QueryRawEventsFunction — GET /api/raw/events + /api/global/raw/events ────────────
    // Old literal (single-session site):
    //   new { tenantId, count = filtered.Count,
    //         events = RawEntityProjection.Project(filtered, fields), nextLink = singleNextLink }
    // Old literal (cross-session site) has the identical shape:
    //   new { tenantId, count = events.Count,
    //         events = RawEntityProjection.Project(events, fields), nextLink }

    [Fact]
    public void QueryRawEvents_populated_matches_old_anonymous_shape()
    {
        var tenantId = "11111111-2222-3333-4444-555555555555";
        var events = SampleEventRows();
        var nextLink = "/api/raw/events?pageSize=50&continuation=abc123";

        ApiResponseWireParityTests.AssertWireIdentical(
            new { tenantId, count = events.Count, events, nextLink },
            new QueryRawEventsResponse { TenantId = tenantId, Count = events.Count, Events = events, NextLink = nextLink });
    }

    [Fact]
    public void QueryRawEvents_null_tenantId_and_nextLink_keys_vanish_identically()
    {
        // Global scope without tenantId filter (cross-tenant) and last page: both keys
        // disappear under WhenWritingNull — before and after the migration.
        var tenantId = (string?)null;
        var events = new List<IReadOnlyDictionary<string, object?>>();
        var nextLink = (string?)null;

        ApiResponseWireParityTests.AssertWireIdentical(
            new { tenantId, count = events.Count, events, nextLink },
            new QueryRawEventsResponse { TenantId = tenantId, Count = events.Count, Events = events, NextLink = nextLink });
    }

    [Fact]
    public void QueryRawEvents_dictionary_row_keys_stay_pascalcase_verbatim()
    {
        var events = SampleEventRows();
        var json = JsonSerializer.Serialize(
            new QueryRawEventsResponse { TenantId = "t", Count = events.Count, Events = events, NextLink = null },
            WireOptions);

        // Envelope properties are camelCased by PropertyNamingPolicy...
        Assert.Contains("\"tenantId\"", json);
        Assert.Contains("\"events\"", json);
        // ...but the raw row keys are dictionary keys: no DictionaryKeyPolicy is set, so
        // they must remain PascalCase-verbatim stored column names.
        Assert.Contains("\"PartitionKey\"", json);
        Assert.Contains("\"RowKey\"", json);
        Assert.Contains("\"EventType\"", json);
        Assert.Contains("\"DataJson\"", json);
        Assert.DoesNotContain("\"partitionKey\"", json);
        Assert.DoesNotContain("\"eventType\"", json);
    }

    // ── QueryRawSessionsFunction — GET /api/raw/sessions + /api/global/raw/sessions ──────
    // Old literal:
    //   new { tenantId, count = page.Items.Count, sessions = sessionsPayload, nextLink }

    [Fact]
    public void QueryRawSessions_populated_matches_old_anonymous_shape()
    {
        var tenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var sessions = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["PartitionKey"] = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                ["RowKey"] = "9c8b7a65-4321-0fed-cba9-876543210fed",
                ["Status"] = "InProgress",
                ["SerialNumber"] = "VMware-42 00 11 22 33 44 55 66",
                ["AgentVersion"] = "2.14.0.0",
                ["DeviceName"] = "DESKTOP-CONTOSO1",
            },
        };
        var nextLink = "/api/raw/sessions?pageSize=25&continuation=xyz789";

        ApiResponseWireParityTests.AssertWireIdentical(
            new { tenantId, count = sessions.Count, sessions, nextLink },
            new QueryRawSessionsResponse { TenantId = tenantId, Count = sessions.Count, Sessions = sessions, NextLink = nextLink });
    }

    [Fact]
    public void QueryRawSessions_null_tenantId_and_nextLink_keys_vanish_identically()
    {
        var tenantId = (string?)null;
        var sessions = new List<IReadOnlyDictionary<string, object?>>();
        var nextLink = (string?)null;

        ApiResponseWireParityTests.AssertWireIdentical(
            new { tenantId, count = sessions.Count, sessions, nextLink },
            new QueryRawSessionsResponse { TenantId = tenantId, Count = sessions.Count, Sessions = sessions, NextLink = nextLink });
    }

    // ── TableQueryFunction.ListTables — GET /api/global/raw/tables ───────────────────────
    // Old literal: new { count = tables.Count, tables }

    [Fact]
    public void ListRawTables_matches_old_anonymous_shape()
    {
        var tables = new List<string> { "Sessions", "SessionsIndex", "TenantConfiguration" };

        ApiResponseWireParityTests.AssertWireIdentical(
            new { count = tables.Count, tables },
            new ListRawTablesResponse { Count = tables.Count, Tables = tables });
    }

    // ── TableQueryFunction.QueryTable — GET /api/global/raw/tables/{tableName} ───────────
    // Old literal: new { table = actualTableName, count = entities.Count, entities, nextLink }
    // (entities is List<Dictionary<string, object?>> at the call site.)

    [Fact]
    public void QueryRawTable_populated_matches_old_anonymous_shape()
    {
        var table = "SessionsIndex";
        var entities = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["PartitionKey"] = "11111111-2222-3333-4444-555555555555",
                ["RowKey"] = "8f1e2d3c-4b5a-6789-abcd-ef0123456789",
                ["Timestamp"] = new DateTimeOffset(2026, 8, 30, 8, 0, 0, TimeSpan.Zero),
                ["Status"] = "Succeeded",
                ["RebootCount"] = 2,
            },
        };
        var nextLink = "/api/global/raw/tables/SessionsIndex?pageSize=100&continuation=tok456";

        ApiResponseWireParityTests.AssertWireIdentical(
            new { table, count = entities.Count, entities, nextLink },
            new QueryRawTableResponse { Table = table, Count = entities.Count, Entities = entities, NextLink = nextLink });
    }

    [Fact]
    public void QueryRawTable_null_nextLink_key_vanishes_identically()
    {
        var table = "OpsEvents";
        var entities = new List<Dictionary<string, object?>>();
        var nextLink = (string?)null;

        ApiResponseWireParityTests.AssertWireIdentical(
            new { table, count = entities.Count, entities, nextLink },
            new QueryRawTableResponse { Table = table, Count = entities.Count, Entities = entities, NextLink = nextLink });
    }
}

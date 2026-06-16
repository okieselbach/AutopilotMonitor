using System.Collections.Generic;
using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Stj = System.Text.Json.JsonSerializer;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Regression tests for the V2 ingest data-corruption bug: the telemetry payload parser used to
/// skip the Newtonsoft-JToken → native normalization the V1 NDJSON path applied, so nested
/// arrays/objects (e.g. hardware_spec.disks) reached System.Text.Json downstream as JObject/JArray
/// and serialized to corrupt nested-empty-array output. Also covers DetectHasSSD, which used an
/// exact mediaType compare that never matched the agent's composite values ("NVMe SSD").
/// </summary>
public class EventDataNormalizationTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    private static TelemetryItemDto EventDto(string payloadJson)
        => new TelemetryItemDto
        {
            Kind            = "Event",
            PartitionKey    = $"{TenantId}_{SessionId}",
            RowKey          = "0000000001",
            TelemetryItemId = 1,
            PayloadJson     = payloadJson,
            EnqueuedAtUtc   = new System.DateTime(2026, 4, 21, 10, 0, 0, System.DateTimeKind.Utc),
        };

    // hardware_spec shape the agent actually emits — a nested object array (disks) and a nested
    // object array (gpus), plus flat scalars.
    private const string HardwareSpecPayload =
        "{\"EventType\":\"hardware_spec\",\"Data\":{" +
            "\"systemModel\":\"HP EliteBook 840 G8\"," +
            "\"ramTotalGB\":7.7," +
            "\"disks\":[{\"model\":\"KIOXIA\",\"sizeGB\":238,\"mediaType\":\"NVMe SSD\",\"busType\":\"NVMe\"}]," +
            "\"gpus\":[{\"name\":\"Intel Iris Xe\",\"adapterRAMGB\":2}]" +
        "}}";

    [Fact]
    public void ParseEvent_normalizes_nested_objects_to_native_types()
    {
        var evt = TelemetryPayloadParser.ParseEvent(EventDto(HardwareSpecPayload), TenantId, SessionId);

        Assert.NotNull(evt);
        // Nested array must be a native List<object> of Dictionary<string,object>, NOT a JArray/JObject.
        var disks = Assert.IsType<List<object>>(evt!.Data["disks"]);
        var disk = Assert.IsType<Dictionary<string, object>>(disks[0]);
        Assert.Equal("NVMe SSD", disk["mediaType"]?.ToString());
    }

    [Fact]
    public void ParseEvent_output_is_SystemTextJson_safe()
    {
        // The actual corruption manifests when the downstream DeviceSnapshot writer re-serializes
        // Data with System.Text.Json. Before the fix this produced "disks":[[[[]],...]]; now the
        // real fields must survive a round-trip.
        var evt = TelemetryPayloadParser.ParseEvent(EventDto(HardwareSpecPayload), TenantId, SessionId);
        Assert.NotNull(evt);

        var json = Stj.Serialize(evt!.Data);

        Assert.Contains("\"mediaType\":\"NVMe SSD\"", json);
        Assert.Contains("\"model\":\"KIOXIA\"", json);
        Assert.Contains("\"name\":\"Intel Iris Xe\"", json);
        // The corruption signature: nested empty arrays where objects should be.
        Assert.DoesNotContain("[[[", json);
    }

    [Fact]
    public void NormalizeMap_converts_newtonsoft_tokens_from_stored_json()
    {
        // Mirrors the read path: storage JSON deserialized by Newtonsoft into object values.
        var deserialized = JsonConvert.DeserializeObject<Dictionary<string, object>>(
            "{\"disks\":[{\"mediaType\":\"NVMe SSD\"}],\"flat\":\"x\"}");

        var normalized = EventDataNormalizer.NormalizeMap(deserialized);

        Assert.IsType<List<object>>(normalized["disks"]);
        Assert.Equal("x", normalized["flat"]?.ToString());
        Assert.DoesNotContain("[[[", Stj.Serialize(normalized));
    }

    [Fact]
    public void NormalizeMap_returns_empty_for_null()
        => Assert.Empty(EventDataNormalizer.NormalizeMap(null));

    // ============================================================ DetectHasSSD

    private static Dictionary<string, object> DisksWith(params string[] mediaTypes)
    {
        var disks = new List<object>();
        foreach (var mt in mediaTypes)
            disks.Add(new Dictionary<string, object> { ["mediaType"] = mt });
        return new Dictionary<string, object> { ["disks"] = disks };
    }

    [Theory]
    [InlineData("NVMe SSD", true)]   // composite value the exact-equals bug missed
    [InlineData("NVMe", true)]
    [InlineData("SSD", true)]
    [InlineData("nvme ssd", true)]   // case-insensitive
    [InlineData("HDD", false)]
    [InlineData("Unknown", false)]
    public void DetectHasSSD_matches_composite_and_case_insensitive(string mediaType, bool expected)
        => Assert.Equal(expected, TableStorageService.DetectHasSSD(DisksWith(mediaType)));

    [Fact]
    public void DetectHasSSD_true_when_any_disk_is_solid_state()
        => Assert.Equal(true, TableStorageService.DetectHasSSD(DisksWith("Unknown", "NVMe SSD")));

    [Fact]
    public void DetectHasSSD_null_when_no_disks_key()
        => Assert.Null(TableStorageService.DetectHasSSD(new Dictionary<string, object>()));
}

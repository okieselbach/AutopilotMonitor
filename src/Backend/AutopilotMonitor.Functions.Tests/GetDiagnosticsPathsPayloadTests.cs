using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Diagnostics;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Wire shape of <c>GET /api/diagnostics/paths</c> (<see cref="GetDiagnosticsPathsFunction.BuildPayload"/>).
/// The route is MemberRead, so the field set is locked: the payload must stay "what is
/// collected" — never grow admin-only config.
/// </summary>
public class GetDiagnosticsPathsPayloadTests
{
    // Mirrors the worker's serializer (Program.cs: PropertyNamingPolicy = CamelCase) so the
    // nested DiagnosticsLogPath class serializes the way the portal actually receives it.
    private static readonly JsonSerializerOptions WorkerJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static JsonElement Serialize(AdminConfiguration config)
    {
        var json = JsonSerializer.Serialize(GetDiagnosticsPathsFunction.BuildPayload(config), WorkerJson);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void Payload_HasExactlyBuiltInAndGlobalPaths()
    {
        var element = Serialize(new AdminConfiguration());

        var fieldNames = element.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "builtIn", "globalPaths" }, fieldNames);
        Assert.Equal(JsonValueKind.Array, element.GetProperty("globalPaths").ValueKind);
        Assert.Equal(0, element.GetProperty("globalPaths").GetArrayLength());
    }

    [Fact]
    public void BuiltIn_MirrorsTheCatalog_WithConditionAsName()
    {
        var builtIn = Serialize(new AdminConfiguration()).GetProperty("builtIn");

        Assert.Equal(DiagnosticsBuiltInSections.All.Count, builtIn.GetArrayLength());
        var first = builtIn[0];
        Assert.Equal("AgentLogs", first.GetProperty("id").GetString());
        Assert.Equal("AgentLogs", first.GetProperty("zipFolder").GetString());
        Assert.Equal(AutopilotMonitor.Shared.Constants.LogDirectory, first.GetProperty("sourceFolder").GetString());
        Assert.Equal(DiagnosticsBuiltInSections.LogFilePatterns.Length, first.GetProperty("patterns").GetArrayLength());
        Assert.False(first.GetProperty("includeSubfolders").GetBoolean());
        Assert.Equal("Always", first.GetProperty("condition").GetString());

        var conditions = builtIn.EnumerateArray().Select(s => s.GetProperty("condition").GetString()).ToArray();
        Assert.Contains("RealmJoinWatcher", conditions);
        Assert.Contains("DevicePreparation", conditions);
        Assert.All(conditions, c => Assert.False(int.TryParse(c, out _), "condition must serialize as the enum name"));
    }

    [Fact]
    public void GlobalPaths_RoundTripTheStoredJson()
    {
        var config = new AdminConfiguration
        {
            DiagnosticsGlobalLogPathsJson =
                "[{\"path\":\"C:\\\\Windows\\\\Panther\\\\setupact.log\",\"description\":\"Setup\",\"isBuiltIn\":true,\"includeSubfolders\":false}]",
        };

        var globalPaths = Serialize(config).GetProperty("globalPaths");

        Assert.Equal(1, globalPaths.GetArrayLength());
        Assert.Equal(@"C:\Windows\Panther\setupact.log", globalPaths[0].GetProperty("path").GetString());
        Assert.Equal("Setup", globalPaths[0].GetProperty("description").GetString());
    }
}

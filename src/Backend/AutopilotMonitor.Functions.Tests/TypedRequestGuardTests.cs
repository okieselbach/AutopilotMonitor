using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Request-side twin of <see cref="TypedResponseGuardTests"/> (D-207). Every HTTP request body is
/// read through <see cref="RequestBody"/> into a Shared <see cref="IApiRequest"/> type, which the
/// manifest exports to TypeScript — so the web and the MCP server compile against the same
/// shape the backend deserialises. Three guards:
///   A: no raw deserializer on a request body inside <c>Functions/</c> (source scan, EMPTY
///      baseline except the permanent exemptions below),
///   B: the Newtonsoft branch (<c>ReadNewtonsoftAsync</c>) only where the downstream is
///      Newtonsoft-bound — a frozen per-file baseline that may only shrink,
///   C: no request DTO declared in the Functions assembly (reflection).
/// </summary>
public class TypedRequestGuardTests
{
    /// <summary>Raw body deserializers a handler must not call — RequestBody owns them.</summary>
    private static readonly Regex RawDeserializer = new(
        @"\b(ReadFromJsonAsync<|JsonConvert\.DeserializeObject<|JsonConvert\.DeserializeAnonymousType\(|JsonSerializer\.Deserialize(Async)?<|JObject\.Parse\(|JsonDocument\.Parse\()",
        RegexOptions.Compiled);

    private static readonly Regex NewtonsoftBranch = new(@"\bReadNewtonsoftAsync<", RegexOptions.Compiled);

    /// <summary>
    /// Files where a raw parser is legitimate. Each entry names WHY; a new entry is a decision,
    /// not a convenience.
    /// </summary>
    private static readonly Dictionary<string, string> RawParserExemptions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Agent-facing routes: wire frozen by D-200 fixtures; their reader is not this package's concern.
        ["Functions/Sessions/RegisterSessionFunction.cs"] = "agent route (D-200)",
        ["Functions/Bootstrap/BootstrapRegisterSessionFunction.cs"] = "agent route (D-200)",
        ["Functions/Ingest/IngestTelemetryFunction.cs"] = "agent route, streamed Newtonsoft (D-200)",
        ["Functions/Ingest/ReportDistressFunction.cs"] = "agent route (pre-auth)",
        ["Functions/Ingest/ReportAgentErrorFunction.cs"] = "agent route",
        ["Functions/Diagnostics/GetDiagnosticsUploadUrlFunction.cs"] = "agent route (D-200)",
        // Foreign schema: Azure Monitor common alert schema, not our contract.
        ["Functions/Infrastructure/AzureMonitorAlertWebhookFunction.cs"] = "Azure Monitor alert schema",
        // Parses the UPSTREAM Kusto response, not the request body.
        ["Functions/Raw/AppInsightsQueryFunction.cs"] = "upstream response parse",
        // Not request bodies at all: telemetry item payloads (agent wire, D-200) and stored table columns.
        ["Functions/Ingest/TelemetryPayloadParser.cs"] = "telemetry item PayloadJson (agent wire, D-200)",
        ["Functions/Config/ListTenantConfigBackupsFunction.cs"] = "stored backup diff column",
        ["Functions/Vulnerability/GetCpeMappingsFunction.cs"] = "stored pattern-list columns",
    };

    /// <summary>
    /// The Newtonsoft-bound bodies: tenant/admin configuration (redaction restore + patch service
    /// merge via JsonConvert.PopulateObject) and the rule documents. Porting them is a
    /// roundtrip-proven change, tracked in the backlog; until then this list only shrinks.
    /// </summary>
    private static readonly Dictionary<string, int> NewtonsoftBaseline = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Functions/Config/PatchTenantConfigurationFieldsFunction.cs"] = 1,
        ["Functions/Config/UpdateAdminConfigurationFunction.cs"] = 1,
        ["Functions/Config/UpdateTenantConfigurationFunction.cs"] = 1,
        ["Functions/Rules/AnalyzeRulesFunction.cs"] = 2,
        ["Functions/Rules/DryRunAnalyzeRuleFunction.cs"] = 1,
        ["Functions/Rules/GatherRulesFunction.cs"] = 2,
        ["Functions/Rules/GlobalRulesFunction.cs"] = 1,
        ["Functions/Rules/ImeLogPatternsFunction.cs"] = 1,
    };

    [Fact]
    public void A_NoRawBodyDeserializerInHandlers()
    {
        var violations = new List<string>();
        foreach (var (relative, text) in HandlerSources())
        {
            if (RawParserExemptions.ContainsKey(relative)) continue;
            var count = RawDeserializer.Matches(text).Count;
            if (count > 0)
                violations.Add($"{relative}: {count} raw body deserializer(s) — read the body through RequestBody.ReadAsync<T> into a Shared IApiRequest type.");
        }

        Assert.True(violations.Count == 0, "Raw request-body parsing in handlers:\n  " + string.Join("\n  ", violations));

        // Every exemption must still exist and still parse something, or it is stale.
        var stale = RawParserExemptions.Keys
            .Where(rel => !HandlerSources().Any(s => s.Relative.Equals(rel, StringComparison.OrdinalIgnoreCase) && RawDeserializer.IsMatch(s.Text)))
            .ToList();
        Assert.True(stale.Count == 0, "Stale raw-parser exemptions (remove them):\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void B_NewtonsoftBranchOnlyWhereBaselined()
    {
        var actual = HandlerSources()
            .Select(s => (s.Relative, Count: NewtonsoftBranch.Matches(s.Text).Count))
            .Where(x => x.Count > 0)
            .ToDictionary(x => x.Relative, x => x.Count, StringComparer.OrdinalIgnoreCase);

        var violations = new List<string>();
        foreach (var (file, count) in actual.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!NewtonsoftBaseline.TryGetValue(file, out var allowed))
                violations.Add($"{file}: {count} NEW ReadNewtonsoftAsync site(s) — use ReadAsync<T> (System.Text.Json); the Newtonsoft branch is frozen.");
            else if (count > allowed)
                violations.Add($"{file}: {count} ReadNewtonsoftAsync site(s), baseline allows {allowed}.");
        }
        foreach (var (file, allowed) in NewtonsoftBaseline.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            actual.TryGetValue(file, out var count);
            if (count < allowed)
                violations.Add($"{file}: baseline says {allowed} but only {count} remain — lower/remove its entry (ratchet down).");
        }

        Assert.True(violations.Count == 0, "Newtonsoft body-reader baseline violated:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void C_NoRequestTypeDeclaredInFunctionsAssembly()
    {
        var functions = typeof(RequestBody).Assembly;

        var implementers = functions.GetTypes()
            .Where(t => typeof(IApiRequest).IsAssignableFrom(t) && !t.IsInterface)
            .Select(t => t.FullName)
            .ToList();
        Assert.True(implementers.Count == 0,
            "IApiRequest implementers must live in AutopilotMonitor.Shared (manifest export):\n  " + string.Join("\n  ", implementers));

        // Name-suffix sweep: a body DTO that forgot the marker would still be a `*Request` class here.
        var suspicious = functions.GetTypes()
            .Where(t => t.IsClass && t.Name.EndsWith("Request", StringComparison.Ordinal))
            .Where(t => !t.Name.EndsWith("FilterRequest", StringComparison.Ordinal))   // query-string parsing helpers
            .Where(t => t.Name != "MandrillSendRequest")                                // outbound Mandrill payload
            .Select(t => t.FullName)
            .ToList();
        Assert.True(suspicious.Count == 0,
            "Request-shaped classes in the Functions assembly — move them to Shared as IApiRequest, or exempt with a reason:\n  " + string.Join("\n  ", suspicious));
    }

    [Fact]
    public void D_EveryRequestTypeIsFlatAndInShared()
    {
        // Mirrors the response-side rule: flat (no base class outside object), declared in Shared,
        // no untyped slot except the one field map, so the TypeScript twin is exact.
        var shared = typeof(IApiRequest).Assembly;
        var offenders = new List<string>();
        foreach (var t in shared.GetTypes().Where(t => typeof(IApiRequest).IsAssignableFrom(t) && t.IsClass))
        {
            if (t.BaseType != typeof(object))
                offenders.Add($"{t.Name}: base type {t.BaseType?.Name} (request DTOs stay flat)");
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var slot = p.PropertyType;
                var isObjectMap = slot.IsGenericType && slot.GetGenericTypeDefinition() == typeof(Dictionary<,>) && slot.GetGenericArguments()[1] == typeof(object);
                if (slot == typeof(object))
                    offenders.Add($"{t.Name}.{p.Name}: object slot (TypeScript gets unknown)");
                else if (isObjectMap && !(t == typeof(PatchTenantConfigurationFieldsRequest) && p.Name == nameof(PatchTenantConfigurationFieldsRequest.Fields)))
                    offenders.Add($"{t.Name}.{p.Name}: untyped map (only the tenant-config field patch carries one by design)");
            }
        }
        Assert.True(offenders.Count == 0, "Request DTO shape violations:\n  " + string.Join("\n  ", offenders));
    }

    private static IEnumerable<(string Relative, string Text)> HandlerSources()
    {
        var functionsRoot = Path.Combine(FindRepoRoot(), "src", "Backend", "AutopilotMonitor.Functions");
        var handlers = Path.Combine(functionsRoot, "Functions");
        foreach (var file in Directory.EnumerateFiles(handlers, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(functionsRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase) || relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
                continue;
            yield return (relative, File.ReadAllText(file));
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

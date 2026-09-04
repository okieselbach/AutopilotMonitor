using System.Reflection;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Rules;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Cross-language parity anchor: reflects the wire-relevant catalogs out of
/// AutopilotMonitor.Shared (models, enums, string vocabularies) into a canonical JSON
/// manifest committed at <c>src/Web/autopilot-monitor-web/utils/shared-manifests.json</c>.
/// The web's vitest suite and compile-time checks compare their hand-written TS mirrors
/// against that manifest — so a C# change that would silently drift a TS mirror first
/// fails HERE, and the TS side fails until its mirror follows.
///
/// Regenerate after intentional changes:
///   AM_WRITE_SHARED_MANIFESTS=1 dotnet test --filter SharedManifestParityTests
/// then run the web codegen: node scripts/generate-shared-manifest-types.js
/// </summary>
public sealed class SharedManifestParityTests
{
    private const string ManifestRepoPath = "src/Web/autopilot-monitor-web/utils/shared-manifests.json";
    private const string WriteEnvVar = "AM_WRITE_SHARED_MANIFESTS";

    [Fact]
    public void Committed_manifest_matches_shared_assembly()
    {
        var expected = BuildManifestJson();
        var path = Path.Combine(FindRepoRoot(), ManifestRepoPath.Replace('/', Path.DirectorySeparatorChar));

        if (Environment.GetEnvironmentVariable(WriteEnvVar) == "1")
        {
            File.WriteAllText(path, expected);
            return;
        }

        Assert.True(File.Exists(path),
            $"Manifest missing at {ManifestRepoPath}. Regenerate: {WriteEnvVar}=1 dotnet test --filter SharedManifestParityTests");

        var committed = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.True(string.Equals(expected, committed, StringComparison.Ordinal),
            $"shared-manifests.json is out of date with AutopilotMonitor.Shared. " +
            $"Regenerate: {WriteEnvVar}=1 dotnet test --filter SharedManifestParityTests " +
            $"(then: node scripts/generate-shared-manifest-types.js in the web folder).");
    }

    // ── manifest construction ───────────────────────────────────────────────

    private static string BuildManifestJson()
    {
        var manifest = new Dictionary<string, object?>
        {
            ["$comment"] = "GENERATED from AutopilotMonitor.Shared by SharedManifestParityTests — do not edit by hand. " +
                           "Regenerate: AM_WRITE_SHARED_MANIFESTS=1 dotnet test --filter SharedManifestParityTests, " +
                           "then node scripts/generate-shared-manifest-types.js.",
            // v2: adds the "types" section (full wire-type graph) and drops v1's
            // "sessionSummary" (it had no TS consumer; SessionSummary now rides in "types").
            ["schemaVersion"] = 2,
            ["adminConfiguration"] = new Dictionary<string, object?>
            {
                ["fields"] = WireFieldNames(typeof(AdminConfiguration)),
            },
            ["tenantConfiguration"] = new Dictionary<string, object?>
            {
                ["fields"] = WireFieldNames(typeof(TenantConfiguration)),
            },
            // Enum member order is load-bearing (append-only ordinals) — declaration order kept.
            ["sessionStatuses"] = Enum.GetNames(typeof(SessionStatus)),
            ["enrollmentPhases"] = EnumMap<EnrollmentPhase>(),
            ["eventSeverities"] = EnumMap<EventSeverity>(),
            ["webhookProviderTypes"] = EnumMap<WebhookProviderType>(),
            ["annotationLanes"] = AnnotationLanes.All,
            ["annotationVerdicts"] = AnnotationVerdicts.All,
            // Ops vocabularies. Declaration order kept for severities — it IS the ladder
            // (Info < Warning < Error < Critical), and the MCP renders it in that order.
            ["opsEventCategories"] = ConstStrings(typeof(OpsEventCategory)),
            ["opsEventSeverities"] = ConstStrings(typeof(OpsEventSeverity)),
            // Declaration order (grouped by category) — the MCP renders this catalog verbatim.
            ["opsEventTypes"] = OpsEventTypes.All,
            // Telemetry stores behind the operator KQL proxy (query_backend_logs `source`).
            ["logSources"] = LogQuerySources.All,
            // Every machine-readable `code` an error envelope (IApiErrorResponse) can carry: the
            // generic catalog first, then the domain code classes. Declaration order kept.
            ["apiErrorCodes"] = ConstStrings(typeof(Constants.ApiErrorCodes))
                .Concat(ConstStrings(typeof(Constants.DelegationCodes)))
                .Concat(ConstStrings(typeof(Constants.DelegatedSlots)))
                .ToArray(),
            ["tenantRoles"] = ConstStrings(typeof(Constants.TenantRoles)),
            ["globalRoles"] = ConstStrings(typeof(Constants.GlobalRoles)),
            ["delegatedRoles"] = ConstStrings(typeof(Constants.DelegatedRoles)),
            ["analyzeRuleSources"] = DryRunAnalyzeRuleFunction.KnownSources.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            ["analyzeRuleOperators"] = DryRunAnalyzeRuleFunction.KnownOperators.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            ["eventTypes"] = ConstStrings(typeof(Constants.EventTypes)).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            // Declaration order kept: the web derives a union type from this list, order is cosmetic
            // but a stable order keeps the generated file diff-minimal.
            ["signalRMessages"] = ConstStrings(typeof(Constants.SignalRMessages)),
            // Every IApiResponse implementer + [WireContract] type, transitively closed —
            // the source of utils/wire-types.generated.ts. See WireTypeManifestBuilder.
            ["types"] = WireTypeManifestBuilder.BuildTypesSection(),
        };

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        // Normalized to LF + trailing newline so the file is byte-stable across OSes.
        return json.Replace("\r\n", "\n") + "\n";
    }

    /// <summary>Wire (camelCase) names of the public instance properties, declaration order.</summary>
    private static string[] WireFieldNames(Type type)
        => WireProperties(type).Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).ToArray();

    private static IEnumerable<PropertyInfo> WireProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod != null && p.GetIndexParameters().Length == 0);

    private static Dictionary<string, int> EnumMap<TEnum>() where TEnum : struct, Enum
        => Enum.GetValues<TEnum>()
            .ToDictionary(v => v.ToString(), v => Convert.ToInt32(v));

    private static string[] ConstStrings(Type constClass)
        => constClass.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

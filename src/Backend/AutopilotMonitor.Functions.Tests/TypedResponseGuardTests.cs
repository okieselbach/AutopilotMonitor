using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Ratchet against anonymous-object API SUCCESS responses (successor of the retired
/// OkAsyncBaselineGuardTests, same mechanics, wider net). Anonymous success bodies ship a shape
/// no type checks, no manifest exports, and the web mirrors by hand — the drift class the
/// 2026-08-13 fragility audit found. Two frozen per-file baselines that may only SHRINK:
///   - Regex A: <c>OkAsync/CreatedAsync/JsonAsync(new { ... })</c> through ResponseHelper
///     (44 sites frozen 2026-08-13; typed initializers <c>OkAsync(new SomeResponse { ... })</c>
///     do not match),
///   - Regex B: raw success <c>WriteAsJsonAsync(new { ... })</c> (134 sites frozen 2026-08-30).
///     Error bodies — first property <c>error</c>/<c>message</c>, or literal
///     <c>success = false</c> — are tolerated and stay anonymous by design (one shape).
/// A new anonymous success site (new file, or count above baseline) fails; converting a site
/// fails too until its baseline entry is lowered/removed, so the debt stays visible in the diff.
/// New/changed endpoints return typed DTOs implementing <see cref="IApiResponse"/>
/// (AutopilotMonitor.Shared.Models, exported to TypeScript by SharedManifestParityTests).
/// </summary>
public class TypedResponseGuardTests
{
    // Lookbehind keeps 'JsonAsync(' from matching as the suffix of 'WriteAsJsonAsync('.
    private static readonly Regex AnonymousHelperCall =
        new(@"(?<![A-Za-z])(OkAsync|CreatedAsync|JsonAsync)\(\s*new\s*\{", RegexOptions.Compiled);

    private static readonly Regex AnonymousWriteAsJson =
        new(@"WriteAsJsonAsync\(\s*new\s*\{", RegexOptions.Compiled);

    /// <summary>
    /// Frozen 2026-08-13 baseline (44 sites / 34 files) for Regex A, relative to the Functions
    /// project. Only ever lower a count or delete an entry — never raise or add.
    /// </summary>
    private static readonly Dictionary<string, int> HelperBaseline = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Functions/Admin/GetActiveUsersFunction.cs"] = 1,
        ["Functions/Admin/GetAuditLogsFunction.cs"] = 2,
        ["Functions/Admin/GetGlobalAuditLogsFunction.cs"] = 2,
        ["Functions/Admin/GetOpsEventsFunction.cs"] = 2,
        ["Functions/Annotations/GetSessionAnnotationsFunction.cs"] = 1,
        ["Functions/Annotations/ListSessionAnnotationsFunction.cs"] = 1,
        ["Functions/Annotations/ListTenantSessionAnnotationsFunction.cs"] = 1,
        ["Functions/Annotations/UpsertSessionAnnotationFunction.cs"] = 2,
        ["Functions/Config/GetAllTenantConfigurationsFunction.cs"] = 2,
        ["Functions/Graph/GetGraphPermissionsStatusFunction.cs"] = 1,
        ["Functions/Graph/GetScriptDisplayNamesFunction.cs"] = 3,
        ["Functions/Graph/RefreshGraphPermissionsFunction.cs"] = 1,
        ["Functions/Raw/QueryRawEventsFunction.cs"] = 2,
        ["Functions/Raw/QueryRawSessionsFunction.cs"] = 1,
        ["Functions/Reports/GetDeviceNotRegisteredFunction.cs"] = 1,
        ["Functions/Reports/GetHardwareRejectedFunction.cs"] = 1,
        ["Functions/Reports/GetSessionReportsFunction.cs"] = 2,
        ["Functions/Reports/GetTpmPssUnsupportedFunction.cs"] = 1,
        ["Functions/Sessions/GetAllSessionStatsFunction.cs"] = 1,
        ["Functions/Sessions/GetAllSessionsFunction.cs"] = 1,
        ["Functions/Sessions/GetSessionDecisionGraphFunction.cs"] = 1,
        ["Functions/Sessions/GetSessionDeletionsListFunction.cs"] = 1,
        ["Functions/Sessions/GetSessionEventsFunction.cs"] = 2,
        ["Functions/Sessions/GetSessionFunction.cs"] = 1,
        ["Functions/Sessions/GetSessionReducerVerificationFunction.cs"] = 1,
        ["Functions/Sessions/GetSessionSignalsFunction.cs"] = 1,
        ["Functions/Sessions/GetSessionStatsFunction.cs"] = 1,
        ["Functions/Sessions/GetSessionsFunction.cs"] = 1,
        ["Functions/Sessions/GetTenantDeletionManifestsFunction.cs"] = 1,
        ["Functions/Sessions/GetTenantsWithDeletionManifestsFunction.cs"] = 1,
        ["Functions/Sessions/QuickSearchSessionsFunction.cs"] = 1,
        ["Functions/Sessions/SearchSessionsByCveFunction.cs"] = 1,
        ["Functions/Sessions/SearchSessionsByEventFunction.cs"] = 1,
        ["Functions/Sessions/SearchSessionsFunction.cs"] = 1,
    };

    /// <summary>
    /// Frozen 2026-08-30 baseline for Regex B success sites (error-shaped bodies excluded by
    /// <see cref="IsErrorShape"/>). Only ever lower a count or delete an entry.
    /// </summary>
    private static readonly Dictionary<string, int> WriteBaseline = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Functions/Admin/AutopilotDeviceValidationConsentFunction.cs"] = 3,
        ["Functions/Admin/BackfillOccurredUtcFunction.cs"] = 1,
        ["Functions/Admin/CustomsArchiveQueryFunction.cs"] = 5,
        ["Functions/Admin/DelegatedAdminManagementFunction.cs"] = 2,
        ["Functions/Admin/DeviceBlockFunction.cs"] = 3,
        ["Functions/Admin/EmailTemplatesFunction.cs"] = 4,
        ["Functions/Admin/GetAllBlockedDevicesFunction.cs"] = 1,
        ["Functions/Admin/IdentityBindingManagementFunction.cs"] = 2,
        ["Functions/Admin/ReclassifyLegacySessionsFunction.cs"] = 1,
        ["Functions/Admin/ReseedFromGitHubFunction.cs"] = 1,
        ["Functions/Admin/SubmitOffboardingFeedbackFunction.cs"] = 1,
        ["Functions/Admin/TenantAdminManagementFunction.cs"] = 1,
        ["Functions/Admin/TenantGroupManagementFunction.cs"] = 2,
        ["Functions/Admin/TriggerMaintenanceFunction.cs"] = 1,
        ["Functions/Admin/VersionBlockFunction.cs"] = 3,
        ["Functions/Bootstrap/RevokeBootstrapSessionFunction.cs"] = 1,
        ["Functions/Config/AppHomingFunction.cs"] = 1,
        ["Functions/Config/GetLatestVersionsFunction.cs"] = 1,
        ["Functions/Config/GetTenantConfigFieldsSchemaFunction.cs"] = 1,
        ["Functions/Config/ListTenantConfigBackupsFunction.cs"] = 1,
        ["Functions/Config/PatchTenantConfigurationFieldsFunction.cs"] = 1,
        ["Functions/Config/PlanManagementFunction.cs"] = 4,
        ["Functions/Config/TestWebhookNotificationFunction.cs"] = 1,
        ["Functions/Config/UpdateAdminConfigurationFunction.cs"] = 1,
        ["Functions/Config/UpdateTenantConfigurationFunction.cs"] = 1,
        ["Functions/Diagnostics/DiagnosticsDownloadTicketFunction.cs"] = 1,
        ["Functions/Global/GlobalNotificationsFunction.cs"] = 3,
        ["Functions/Infrastructure/AuthFunction.cs"] = 3,
        ["Functions/Infrastructure/HealthCheckFunction.cs"] = 3,
        ["Functions/Infrastructure/McpUserFunction.cs"] = 3,
        ["Functions/Infrastructure/SignalRAddToGroupFunction.cs"] = 1,
        ["Functions/Infrastructure/SignalRNegotiateFunction.cs"] = 1,
        ["Functions/Infrastructure/SignalRRemoveFromGroupFunction.cs"] = 1,
        ["Functions/Metrics/GetDeviceJourneyFunctions.cs"] = 1,
        ["Functions/Metrics/GetGeographicLocationSessionsFunction.cs"] = 2,
        ["Functions/Metrics/GetGlobalGeographicLocationSessionsFunction.cs"] = 2,
        ["Functions/Metrics/GetPlatformStatsFunction.cs"] = 2,
        ["Functions/Metrics/GetTimeAttributionFunctions.cs"] = 1,
        ["Functions/Metrics/McpUsageMetricsFunction.cs"] = 4,
        ["Functions/Metrics/MetricsSummaryFunction.cs"] = 2,
        ["Functions/Metrics/RuleHitSessionsFunction.cs"] = 1,
        ["Functions/Notifications/TenantNotificationsFunction.cs"] = 3,
        ["Functions/Progress/ProgressPortalFunction.cs"] = 2,
        ["Functions/Raw/TableQueryFunction.cs"] = 2,
        ["Functions/Reports/GetDistressReportsFunction.cs"] = 1,
        ["Functions/Reports/GetSessionReportDownloadUrlFunction.cs"] = 1,
        ["Functions/Reports/UpdateSessionReportNoteFunction.cs"] = 1,
        ["Functions/Rules/AnalyzeRulesFunction.cs"] = 5,
        ["Functions/Rules/DryRunAnalyzeRuleFunction.cs"] = 1,
        ["Functions/Rules/GatherRulesFunction.cs"] = 4,
        ["Functions/Rules/GetRuleResultsFunction.cs"] = 1,
        ["Functions/Rules/GlobalRulesFunction.cs"] = 6,
        ["Functions/Rules/ImeLogPatternsFunction.cs"] = 3,
        ["Functions/Rules/PreviewWhitelistFunction.cs"] = 3,
        ["Functions/Rules/TestLogPatternFunction.cs"] = 1,
        ["Functions/Sessions/GetSessionDeletePreviewFunction.cs"] = 1,
        ["Functions/Sessions/GetSessionDeletionManifestFunction.cs"] = 1,
        ["Functions/Sessions/MarkSessionFailedFunction.cs"] = 1,
        ["Functions/Sessions/MarkSessionSucceededFunction.cs"] = 1,
        ["Functions/Sessions/QueueSessionActionFunction.cs"] = 1,
        ["Functions/Vulnerability/AutoResolveCpeMappingFunction.cs"] = 1,
        ["Functions/Vulnerability/DeleteCustomCpeMappingFunction.cs"] = 1,
        ["Functions/Vulnerability/GetCpeMappingsFunction.cs"] = 1,
        ["Functions/Vulnerability/GetSoftwareInventoryFunction.cs"] = 1,
        ["Functions/Vulnerability/GetTenantSoftwareInventoryFunction.cs"] = 1,
        ["Functions/Vulnerability/GetUnmatchedSoftwareFunction.cs"] = 1,
        ["Functions/Vulnerability/GetVulnerabilityReportFunction.cs"] = 4,
        ["Functions/Vulnerability/GetVulnerabilitySyncStatusFunction.cs"] = 1,
        ["Functions/Vulnerability/IgnoreSoftwareFunction.cs"] = 3,
        ["Functions/Vulnerability/SaveCustomCpeMappingFunction.cs"] = 1,
        ["Functions/Vulnerability/TriggerEpssSyncFunction.cs"] = 1,
        ["Functions/Vulnerability/TriggerMsrcSyncFunction.cs"] = 1,
        ["Functions/Vulnerability/TriggerNvdCacheRefreshFunction.cs"] = 1,
        ["Functions/Vulnerability/TriggerVulnerabilityDataSyncFunction.cs"] = 1,
        ["Middleware/McpQuotaEnforcementMiddleware.cs"] = 1,
    };

    [Fact]
    public void Anonymous_helper_success_bodies_never_exceed_the_frozen_baseline()
    {
        AssertRatchet(
            file => AnonymousHelperCall.Matches(file).Count,
            HelperBaseline,
            "anonymous OkAsync/CreatedAsync/JsonAsync(new { ... })");
    }

    [Fact]
    public void Anonymous_WriteAsJsonAsync_success_bodies_never_exceed_the_frozen_baseline()
    {
        AssertRatchet(
            text => AnonymousWriteAsJson.Matches(text)
                .Count(m => !IsErrorShape(text, m.Index + m.Length)),
            WriteBaseline,
            "anonymous success WriteAsJsonAsync(new { ... })");
    }

    /// <summary>
    /// Wire DTOs must be flat: System.Text.Json serializes derived properties BEFORE base
    /// properties, so any base class would silently reorder JSON keys — and key order is part
    /// of the wire contract (MCP hands raw JSON to an LLM). Declaration order == wire order.
    /// DTOs also must live in the Shared assembly, where SharedManifestParityTests exports them.
    /// </summary>
    [Fact]
    public void Every_IApiResponse_implementer_is_flat_and_lives_in_Shared()
    {
        var sharedAssembly = typeof(IApiResponse).Assembly;
        var functionsAssembly = typeof(AutopilotMonitor.Functions.Helpers.ResponseHelper).Assembly;

        var strays = functionsAssembly.GetTypes()
            .Where(t => typeof(IApiResponse).IsAssignableFrom(t) && !t.IsInterface)
            .Select(t => t.FullName)
            .ToList();
        Assert.True(strays.Count == 0,
            "IApiResponse implementers must live in AutopilotMonitor.Shared (manifest export):\n  "
            + string.Join("\n  ", strays));

        var nonFlat = sharedAssembly.GetTypes()
            .Where(t => typeof(IApiResponse).IsAssignableFrom(t) && !t.IsInterface)
            .Where(t => t.BaseType != typeof(object))
            .Select(t => $"{t.FullName} : {t.BaseType?.FullName}")
            .ToList();
        Assert.True(nonFlat.Count == 0,
            "IApiResponse implementers must derive directly from object (key-order protection):\n  "
            + string.Join("\n  ", nonFlat));
    }

    /// <summary>
    /// Error shape = first property of the anonymous object is <c>error</c> or <c>message</c>
    /// (assigned or C# shorthand), or the literal <c>success = false</c>. Everything else —
    /// including <c>success = someExpression</c> (dual success/failure sites) — counts as a
    /// success body that must become a typed DTO.
    /// </summary>
    private static bool IsErrorShape(string text, int afterBraceIndex)
    {
        var i = SkipTrivia(text, afterBraceIndex);
        var start = i;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
            i++;
        var identifier = text.Substring(start, i - start);

        if (identifier is "error" or "message")
            return true;

        if (identifier == "success")
        {
            i = SkipTrivia(text, i);
            if (i < text.Length && text[i] == '=' && (i + 1 >= text.Length || text[i + 1] != '='))
            {
                i = SkipTrivia(text, i + 1);
                if (string.CompareOrdinal(text, i, "false", 0, 5) == 0 &&
                    (i + 5 >= text.Length || !char.IsLetterOrDigit(text[i + 5])))
                    return true;
            }
        }

        return false;
    }

    private static int SkipTrivia(string text, int i)
    {
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
            }
            else
            {
                break;
            }
        }
        return i;
    }

    private static void AssertRatchet(
        Func<string, int> countSites, Dictionary<string, int> baseline, string label)
    {
        var functionsRoot = Path.Combine(FindRepoRoot(), "src", "Backend", "AutopilotMonitor.Functions");
        var actual = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(functionsRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(functionsRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
                continue;

            var count = countSites(File.ReadAllText(file));
            if (count > 0)
                actual[relative] = count;
        }

        var violations = new List<string>();

        foreach (var (file, count) in actual.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!baseline.TryGetValue(file, out var allowed))
                violations.Add($"{file}: {count} NEW {label} site(s) — return a typed response DTO instead.");
            else if (count > allowed)
                violations.Add($"{file}: {count} {label} site(s), baseline allows {allowed} — return a typed response DTO for the new one(s).");
        }

        // Ratchet: a converted site must also lower its baseline entry, so the frozen debt
        // list stays truthful (and its shrink is visible in the diff).
        foreach (var (file, allowed) in baseline.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            actual.TryGetValue(file, out var count);
            if (count < allowed)
                violations.Add($"{file}: baseline says {allowed} but only {count} remain — lower/remove its entry in {nameof(TypedResponseGuardTests)} (ratchet down).");
        }

        Assert.True(violations.Count == 0,
            $"Anonymous {label} baseline violated:\n  " + string.Join("\n  ", violations));
    }

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

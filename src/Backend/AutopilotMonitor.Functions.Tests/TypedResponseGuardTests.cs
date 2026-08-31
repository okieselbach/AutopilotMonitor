using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Regression guard against anonymous-object API SUCCESS responses (successor of the retired
/// OkAsyncBaselineGuardTests). Anonymous success bodies ship a shape no type checks, no
/// manifest exports, and the web mirrors by hand — the drift class the 2026-08-13 fragility
/// audit found across 44 helper + 134 raw sites. That debt was migrated to typed DTOs in
/// 2026-08-31 (feat/typed-api-contract), so both per-file baselines are now EMPTY and any
/// match is a straight failure:
///   - Regex A: <c>OkAsync/CreatedAsync/JsonAsync(new { ... })</c> through ResponseHelper
///     (typed initializers <c>OkAsync(new SomeResponse { ... })</c> do not match),
///   - Regex B: raw success <c>WriteAsJsonAsync(new { ... })</c>. Error bodies — first
///     property <c>error</c>/<c>message</c>, or literal <c>success = false</c> — are
///     tolerated and stay anonymous by design (one shape).
/// Endpoints return typed DTOs implementing <see cref="IApiResponse"/>
/// (AutopilotMonitor.Shared.Models, exported to TypeScript by SharedManifestParityTests),
/// and each conversion carries an ordinal old-vs-new proof in the *WireParityTests files.
/// </summary>
public class TypedResponseGuardTests
{
    // Lookbehind keeps 'JsonAsync(' from matching as the suffix of 'WriteAsJsonAsync('.
    private static readonly Regex AnonymousHelperCall =
        new(@"(?<![A-Za-z])(OkAsync|CreatedAsync|JsonAsync)\(\s*new\s*\{", RegexOptions.Compiled);

    private static readonly Regex AnonymousWriteAsJson =
        new(@"WriteAsJsonAsync\(\s*new\s*\{", RegexOptions.Compiled);

    /// <summary>
    /// EMPTY since 2026-08-31 — every anonymous helper success body is typed (the migration
    /// started from the frozen 2026-08-13 baseline of 44 sites / 34 files). Any entry that
    /// would need to be ADDED here is a regression: return a typed DTO instead.
    /// </summary>
    private static readonly Dictionary<string, int> HelperBaseline = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// EMPTY since 2026-08-31 — every anonymous success WriteAsJsonAsync body is typed (the
    /// migration started from the frozen 2026-08-30 baseline of 134 sites / 76 files). Any
    /// entry that would need to be ADDED here is a regression: return a typed DTO instead.
    /// </summary>
    private static readonly Dictionary<string, int> WriteBaseline = new(StringComparer.OrdinalIgnoreCase);

    // ── Bypass-shape guards (closed 2026-08-31) ─────────────────────────────────────────
    // Regexes A/B only see the INLINE literal `...(new { ... })`. Three bypass shapes let a
    // response ship untyped anyway and are ratcheted here with EMPTY baselines:
    //   C: the literal parked in a variable first (`var result = new { ... };` →
    //      `WriteAsJsonAsync(result)`), which is how rule-stats escaped the 08-31 migration,
    //   D: hand-serialized bodies (`WriteStringAsync(JsonSerializer.Serialize(new { ... }))`),
    //   E: builder methods declared `object` / `Task<object>` returning `new { ... }`
    //      (verdict-calibration's Build). A fourth shape — local `WriteJson(req, object)`
    //      wrappers — is closed structurally: wrappers take a `T : IApiResponse` generic.

    /// <summary>Identifier passed to WriteAsJsonAsync — flagged when the SAME file assigns that identifier an anonymous object.</summary>
    private static readonly Regex WriteAsJsonIdentifier =
        new(@"WriteAsJsonAsync\(\s*([A-Za-z_]\w*)\s*[,)]", RegexOptions.Compiled);

    private static readonly Regex SerializeAnonymous =
        new(@"JsonSerializer\.Serialize(?:<[^>]+>)?\(\s*new\s*\{", RegexOptions.Compiled);

    private static readonly Regex ObjectReturningBuilder =
        new(@"\b(?:object|Task<object>)\s+\w*(?:Build|Compute|Payload|Response)\w*\s*\(", RegexOptions.Compiled);

    /// <summary>EMPTY — an anonymous object smuggled through a local variable is the same regression as an inline one.</summary>
    private static readonly Dictionary<string, int> IdentifierBaseline = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// JsonSerializer.Serialize(new { ... }) sites that are NOT responses. The single entry is
    /// an outgoing HTTP CLIENT request body (App Insights query POST) — not response debt.
    /// Everything else must be a typed DTO (error shapes are tolerated by the shape check).
    /// </summary>
    private static readonly Dictionary<string, int> SerializeBaseline = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Functions/Raw/AppInsightsQueryFunction.cs"] = 1,
    };

    /// <summary>
    /// A Build*/Compute*/*Payload/*Response method declared object hides its wire shape from
    /// every type check. The single tolerated site is BuildV2ResponseBody: its SUCCESS arm is
    /// the typed <c>SessionDeletionQueuedResponse</c>, but the method returns a union with the
    /// anonymous ERROR arms (tolerated by design), so its declared type must stay object.
    /// </summary>
    private static readonly Dictionary<string, int> ObjectBuilderBaseline = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Functions/Sessions/DeleteSessionFunction.cs"] = 1,
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

    [Fact]
    public void Anonymous_objects_smuggled_through_a_variable_into_WriteAsJsonAsync_are_flagged()
    {
        AssertRatchet(
            text => WriteAsJsonIdentifier.Matches(text).Count(m =>
            {
                var ident = m.Groups[1].Value;
                if (ident == "new") return false;
                var assign = new Regex(@"(?:var\s+)?" + Regex.Escape(ident) + @"\s*=\s*new\s*\{");
                var a = assign.Match(text);
                // Tolerate error-shaped variables the same way inline literals are tolerated.
                return a.Success && !IsErrorShape(text, a.Index + a.Length);
            }),
            IdentifierBaseline,
            "WriteAsJsonAsync(<variable holding an anonymous object>)");
    }

    [Fact]
    public void Anonymous_JsonSerializer_Serialize_success_bodies_never_exceed_the_frozen_baseline()
    {
        AssertRatchet(
            text => SerializeAnonymous.Matches(text)
                .Count(m => !IsErrorShape(text, m.Index + m.Length)),
            SerializeBaseline,
            "anonymous success JsonSerializer.Serialize(new { ... })");
    }

    [Fact]
    public void Object_returning_response_builders_are_flagged()
    {
        AssertRatchet(
            text => ObjectReturningBuilder.Matches(text).Count,
            ObjectBuilderBaseline,
            "object/Task<object>-returning Build*/Compute*/*Payload/*Response method");
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

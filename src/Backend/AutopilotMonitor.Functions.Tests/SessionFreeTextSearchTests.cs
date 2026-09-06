using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Server-side free-text search (<c>q=</c>) for the dashboard search box. The predicate is the
/// search grammar of <see cref="SessionSearchQuery"/> over the web dashboard's client-side
/// searchable field set (minus derived-only tokens). Parity with the web parser is pinned by
/// the shared case file <c>src/Web/autopilot-monitor-web/utils/session-search-syntax.cases.json</c>,
/// which vitest runs against <c>app/dashboard/utils/sessionSearchQuery.ts</c>.
/// </summary>
public class SessionFreeTextSearchTests
{
    private const string CasesRepoPath = "src/Web/autopilot-monitor-web/utils/session-search-syntax.cases.json";

    private static SessionSummary Sample() => new()
    {
        SessionId = "22222222-2222-2222-2222-222222222222",
        TenantId = "11111111-1111-1111-1111-111111111111",
        DeviceName = "DESKTOP-A1B2C3",
        SerialNumber = "5CG1234XYZ",
        Manufacturer = "Contoso",
        Model = "EliteBook 840",
        Status = SessionStatus.Failed,
        GeoCountry = "DE",
        GeoRegion = "Hessen",
        GeoCity = "Frankfurt",
        AgentVersion = "2.0.1400",
        OsName = "Windows 11",
        OsBuild = "26100.1000",
        OsDisplayVersion = "24H2",
        OsEdition = "Enterprise",
        OsLanguage = "de-DE",
    };

    [Theory]
    [InlineData("a1b2")]        // device name, case-insensitive substring
    [InlineData("5cg1234")]     // serial
    [InlineData("contoso")]     // manufacturer
    [InlineData("elitebook")]   // model
    [InlineData("failed")]      // status text
    [InlineData("2222-2222")]   // sessionId fragment
    [InlineData("frankfurt")]   // geo city
    [InlineData("2.0.1400")]    // agent version
    [InlineData("26100")]       // os build
    [InlineData("24h2")]        // os display version
    public void MatchesFreeText_FindsSubstringAcrossDashboardFields(string q)
    {
        Assert.True(TableStorageService.MatchesFreeText(Sample(), q));
    }

    [Theory]
    [InlineData("tailspin")]
    [InlineData("succeeded")]
    [InlineData("99999")]
    public void MatchesFreeText_RejectsNonMatches(string q)
    {
        Assert.False(TableStorageService.MatchesFreeText(Sample(), q));
    }

    [Fact]
    public void MatchesFreeText_EmptyQuery_MatchesEverything()
    {
        Assert.True(TableStorageService.MatchesFreeText(Sample(), null));
        Assert.True(TableStorageService.MatchesFreeText(Sample(), string.Empty));
    }

    // ---- Parity with the web parser (shared case file) ----

    public static IEnumerable<object[]> SharedCases()
    {
        var doc = LoadCases();
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            // `extra` cases exercise client-derived tokens the server never sees.
            if (c.TryGetProperty("extra", out _)) continue;
            yield return new object[]
            {
                c.GetProperty("query").GetString()!,
                c.GetProperty("expected").GetBoolean(),
                c.GetProperty("note").GetString()!,
            };
        }
    }

    [Theory]
    [MemberData(nameof(SharedCases))]
    public void SharedCase_MatchesLikeTheWebParser(string query, bool expected, string note)
    {
        var session = SessionFromCaseFile();
        Assert.True(expected == SessionSearchQuery.Parse(query).Matches(session), $"{query} — {note}");
    }

    [Fact]
    public void SharedCaseFile_SampleSession_IsTheOneTheTestsUse()
    {
        // The case file's session drives both sides; the local Sample() mirrors it so the
        // InlineData theories above and the shared cases talk about the same rows.
        var fromFile = SessionFromCaseFile();
        var local = Sample();
        foreach (var field in SessionSearchQuery.Fields)
            Assert.Equal(field.Value(local), field.Value(fromFile));
    }

    [Fact]
    public void SharedCaseFile_CoversEveryQualifier_AndEveryFieldHasAValue()
    {
        var doc = LoadCases();
        var queries = string.Join("\n", doc.RootElement.GetProperty("cases").EnumerateArray()
            .Select(c => c.GetProperty("query").GetString()!.ToLowerInvariant()));
        var session = SessionFromCaseFile();
        foreach (var field in SessionSearchQuery.Fields)
        {
            Assert.True(queries.Contains(field.Key + "=") || queries.Contains(field.Key + ":"),
                $"no shared case uses {field.Key}=");
            Assert.False(string.IsNullOrEmpty(field.Value(session)), $"sample carries no {field.Key}");
        }
        Assert.Equal(SessionSearchQuery.Fields.Count, SessionSearchQuery.Fields.Select(f => f.Key).Distinct().Count());
    }

    [Fact]
    public void Parse_ResolvesKnownQualifiers_AndKeepsUnknownOnesLiteral()
    {
        var q = SessionSearchQuery.Parse("Model=Surface -status:failed 14:30 foo=bar");
        Assert.Equal(new[]
        {
            new SessionSearchQuery.Term("Surface", "model"),
            new SessionSearchQuery.Term("14:30", null),
            new SessionSearchQuery.Term("foo=bar", null),
        }, q.Include);
        Assert.Equal(new[] { new SessionSearchQuery.Term("failed", "status") }, q.Exclude);
    }

    [Fact]
    public void Parse_LoneMinusOrBareQualifier_IsEmpty()
    {
        Assert.True(SessionSearchQuery.Parse(" - model= ").IsEmpty);
        Assert.True(SessionSearchQuery.Parse(null).IsEmpty);
    }

    [Fact]
    public void Fingerprint_BindsQ_SoContinuationCannotCrossQueries()
    {
        var withQ = new SessionSearchFilter { Q = "desktop" };
        var otherQ = new SessionSearchFilter { Q = "laptop" };
        var noQ = new SessionSearchFilter();
        const string caller = "11111111-1111-1111-1111-111111111111";

        var fp1 = SearchSessionsPagination.Fingerprint("search:tenant", caller, null, withQ);
        var fp2 = SearchSessionsPagination.Fingerprint("search:tenant", caller, null, otherQ);
        var fp3 = SearchSessionsPagination.Fingerprint("search:tenant", caller, null, noQ);

        Assert.NotEqual(fp1, fp2);
        Assert.NotEqual(fp1, fp3);
    }

    private static JsonDocument LoadCases()
    {
        var path = Path.Combine(FindRepoRoot(), CasesRepoPath.Replace('/', Path.DirectorySeparatorChar));
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static SessionSummary SessionFromCaseFile()
    {
        var s = LoadCases().RootElement.GetProperty("session");
        string F(string name) => s.GetProperty(name).GetString()!;
        return new SessionSummary
        {
            DeviceName = F("deviceName"),
            SerialNumber = F("serialNumber"),
            Manufacturer = F("manufacturer"),
            Model = F("model"),
            Status = Enum.Parse<SessionStatus>(F("status")),
            SessionId = F("sessionId"),
            GeoCountry = F("geoCountry"),
            GeoRegion = F("geoRegion"),
            GeoCity = F("geoCity"),
            AgentVersion = F("agentVersion"),
            OsName = F("osName"),
            OsBuild = F("osBuild"),
            OsDisplayVersion = F("osDisplayVersion"),
            OsEdition = F("osEdition"),
            OsLanguage = F("osLanguage"),
        };
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

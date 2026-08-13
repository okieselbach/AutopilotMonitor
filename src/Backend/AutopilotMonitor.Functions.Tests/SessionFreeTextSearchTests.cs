using System;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Fragilitätsaudit P6.2: server-side free-text search (<c>q=</c>) for the dashboard search
/// box, replacing the client's unbounded loadAll() sweep. The predicate must mirror the web
/// dashboard's client-side searchable field set (minus derived-only tokens) — see the doc on
/// <c>TableStorageService.MatchesFreeText</c>.
/// </summary>
public class SessionFreeTextSearchTests
{
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
}

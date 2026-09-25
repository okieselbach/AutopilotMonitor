using AutopilotMonitor.Functions.Functions.Metrics;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Guards the geographic drilldown fixes:
///  - FilterSessionsByFields matches actual Geo* fields (the robust replacement
///    for the opaque-key path that returned 0 for country/region drilldowns).
///  - ComputeGeographicMetrics blanks finer-than-group structured fields so a
///    country/region row is not mislabeled with an arbitrary sample city.
/// </summary>
public class GeographicDrilldownFilterTests
{
    private static SessionSummary Geo(string id, string country, string region, string city,
        SessionStatus status = SessionStatus.Succeeded) => new SessionSummary
        {
            SessionId = id,
            GeoCountry = country,
            GeoRegion = region,
            GeoCity = city,
            GeoLoc = "47.0,8.0",
            Status = status,
        };

    private static List<SessionSummary> Sample() => new()
    {
        Geo("us-dc",  "US", "District of Columbia", "Washington"),
        Geo("us-nc",  "US", "North Carolina",       "Raleigh"),
        Geo("de-sax", "DE", "Saxony",               "Falkenstein"),
        Geo("de-bav", "DE", "Bavaria",              "Munich"),
        new SessionSummary { SessionId = "no-geo", GeoCountry = "" }, // dropped: no geo data
    };

    [Fact]
    public void FilterSessionsByFields_CountryOnly_ReturnsEveryCountrySession()
    {
        // The exact case the opaque-key path produced 0 results for.
        var result = GetGeographicLocationSessionsFunction.FilterSessionsByFields(Sample(), "US", null, null);

        Assert.Equal(2, result.Count);
        Assert.All(result, s => Assert.Equal("US", s.GeoCountry));
    }

    [Fact]
    public void FilterSessionsByFields_CountryAndRegion_Narrows()
    {
        var result = GetGeographicLocationSessionsFunction.FilterSessionsByFields(Sample(), "DE", "Saxony", null);

        Assert.Single(result);
        Assert.Equal("de-sax", result[0].SessionId);
    }

    [Fact]
    public void FilterSessionsByFields_CountryRegionCity_PinpointsOneLocation()
    {
        var result = GetGeographicLocationSessionsFunction.FilterSessionsByFields(Sample(), "DE", "Saxony", "Falkenstein");

        Assert.Single(result);
        Assert.Equal("de-sax", result[0].SessionId);
    }

    [Theory]
    [InlineData("us")]
    [InlineData("US")]
    public void FilterSessionsByFields_IsCaseInsensitive(string country)
    {
        var result = GetGeographicLocationSessionsFunction.FilterSessionsByFields(Sample(), country, "north carolina", null);

        Assert.Single(result);
        Assert.Equal("us-nc", result[0].SessionId);
    }

    [Fact]
    public void FilterSessionsByFields_NeverMatchesSessionsWithoutGeo()
    {
        var result = GetGeographicLocationSessionsFunction.FilterSessionsByFields(Sample(), "", null, null);

        Assert.Empty(result);
    }

    [Fact]
    public void ComputeGeographicMetrics_CountryGrouping_BlanksRegionAndCity()
    {
        var result = GetGeographicMetricsFunction.ComputeGeographicMetrics(
            Sample(), new List<AppInstallSummary>(), "country");

        var us = Assert.Single(result.Locations, l => l.LocationKey == "US");
        Assert.Equal("US", us.Country);
        Assert.Equal(string.Empty, us.Region); // not "District of Columbia" from the first session
        Assert.Equal(string.Empty, us.City);   // not "Washington"
        Assert.Equal(2, us.SessionCount);
    }

    [Fact]
    public void ComputeGeographicMetrics_RegionGrouping_BlanksCityOnly()
    {
        var result = GetGeographicMetricsFunction.ComputeGeographicMetrics(
            Sample(), new List<AppInstallSummary>(), "region");

        var sax = Assert.Single(result.Locations, l => l.LocationKey == "Saxony, DE");
        Assert.Equal("DE", sax.Country);
        Assert.Equal("Saxony", sax.Region);
        Assert.Equal(string.Empty, sax.City);
    }

    [Fact]
    public void ComputeGeographicMetrics_CityGrouping_KeepsAllStructuredFields()
    {
        var result = GetGeographicMetricsFunction.ComputeGeographicMetrics(
            Sample(), new List<AppInstallSummary>(), "city");

        var fal = Assert.Single(result.Locations, l => l.LocationKey == "Falkenstein, Saxony, DE");
        Assert.Equal("DE", fal.Country);
        Assert.Equal("Saxony", fal.Region);
        Assert.Equal("Falkenstein", fal.City);
    }

    [Theory]
    [InlineData("city")]
    [InlineData("region")]
    [InlineData("country")]
    public void CountryOnlySession_KeyIsTheCountryAndDrillsDownToItself(string groupBy)
    {
        // The agent's last-resort geo provider returns the country only.
        var countryOnly = Geo("de-only", "DE", "", "");
        var sessions = new List<SessionSummary> { countryOnly, Geo("de-bav", "DE", "Bavaria", "Munich") };

        var key = GetGeographicMetricsFunction.GetLocationKey(countryOnly, groupBy);
        var drilled = GetGeographicLocationSessionsFunction.FilterSessionsByLocation(sessions, key, groupBy);

        Assert.Equal("DE", key); // never ", DE" or ", , DE"
        Assert.Contains(drilled, s => s.SessionId == "de-only");
    }
}

using System.Globalization;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Typed reads of event data: stored values arrive as native types or as the strings the agent
/// sends, and no read may depend on the server's culture.
/// </summary>
public class EventDataReaderTests
{
    private static EnrollmentEvent Evt(Dictionary<string, object> data) => new() { EventId = "e1", EventType = "x", Data = data };

    [Fact]
    public void Text_is_culture_free_for_numbers_dates_and_bools()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var data = new Dictionary<string, object>
            {
                ["minutes"] = 15.5,
                ["at"] = new DateTime(2026, 10, 13, 14, 19, 46, DateTimeKind.Utc),
                ["flag"] = true,
            };
            Assert.Equal("15.5", EventDataReader.String(data, "minutes"));
            Assert.Equal("2026-10-13T14:19:46.0000000Z", EventDataReader.String(data, "at"));
            Assert.Equal("true", EventDataReader.String(data, "flag"));
            Assert.Null(EventDataReader.String(data, "absent"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData("True", true)]
    [InlineData(" false ", false)]
    [InlineData("yes", null)]
    [InlineData(1L, null)]
    public void Bool_accepts_a_bool_or_its_string(object raw, bool? expected)
        => Assert.Equal(expected, EventDataReader.Bool(Evt(new() { ["v"] = raw }), "v"));

    [Theory]
    [InlineData(40L, 40L)]
    [InlineData(40, 40L)]
    [InlineData(40.0, 40L)]
    [InlineData(40.5, null)]
    [InlineData("60", 60L)]
    [InlineData("0x3c", null)]
    public void Long_reads_whole_numbers_only(object raw, long? expected)
        => Assert.Equal(expected, EventDataReader.Long(new Dictionary<string, object> { ["v"] = raw }, "v"));

    [Fact]
    public void A_plain_string_is_no_list_and_an_absent_list_stays_null()
    {
        var data = new Dictionary<string, object>
        {
            ["names"] = new List<object> { " Contoso Notes ", "", "Contoso VPN" },
            ["typed"] = new[] { "a", "b" },
            ["joined"] = "Contoso Notes, Contoso VPN",
        };
        Assert.Equal(new[] { "Contoso Notes", "Contoso VPN" }, EventDataReader.StringList(data, "names"));
        Assert.Equal(new[] { "a", "b" }, EventDataReader.StringList(data, "typed"));
        Assert.Null(EventDataReader.StringList(data, "joined"));
        Assert.Null(EventDataReader.StringList(data, "absent"));
    }

    [Fact]
    public void Maps_read_stored_and_typed_objects_and_skip_the_rest()
    {
        var data = new Dictionary<string, object>
        {
            ["apps"] = new List<object>
            {
                new Dictionary<string, object> { ["appName"] = "Contoso Notes" },
                new Dictionary<string, string> { ["appName"] = "Contoso VPN" },
                "not an object",
            },
            ["nested"] = new Dictionary<string, object> { ["state"] = "failed" },
        };
        Assert.Equal(new[] { "Contoso Notes", "Contoso VPN" }, EventDataReader.Maps(data, "apps").Select(m => EventDataReader.String(m, "appName")));
        Assert.Equal("failed", EventDataReader.String(EventDataReader.Map(data, "nested"), "state"));
        Assert.Empty(EventDataReader.Maps(data, "nested"));
    }
}

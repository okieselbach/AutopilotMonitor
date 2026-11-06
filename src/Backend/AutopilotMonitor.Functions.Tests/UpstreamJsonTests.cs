using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The contract of <see cref="UpstreamJson"/> (D-420), the reader under every Graph and Entra token-endpoint
/// parser: one test group per member, then the culture matrix. The parser-level contract is in
/// <see cref="GraphResponseParsingTests"/>.
/// </summary>
public class UpstreamJsonTests
{
    private static JsonElement Obj(string json) => UpstreamJson.ParseObjectOrNull(json)!.Value;

    // ── ParseObjectOrNull ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    [InlineData("null")]
    [InlineData(" null ")]
    [InlineData("/*c*/ null")]
    public void ParseObjectOrNull_reads_a_blank_body_and_a_null_root_as_no_object(string? body)
        => Assert.Null(UpstreamJson.ParseObjectOrNull(body));

    [Theory]
    [InlineData("{}")]
    [InlineData(" { \"a\" : 1 } ")]
    [InlineData("{\"a\":1,}")]
    [InlineData("{/*c*/\"a\":[1,2,],// line\n}")]
    [InlineData("// leading\n{\"a\":1}")]
    public void ParseObjectOrNull_reads_one_object_with_comments_and_trailing_commas(string body)
        => Assert.Equal(JsonValueKind.Object, UpstreamJson.ParseObjectOrNull(body)!.Value.ValueKind);

    [Fact]
    public void ParseObjectOrNull_returns_an_element_that_outlives_the_parse()
    {
        // The document is disposed inside the call; only a cloned root stays readable.
        var json = Obj("""{"a":"b","list":[{"c":"d"}]}""");
        GC.Collect();
        Assert.Equal("b", json.GetProperty("a").GetString());
        Assert.Equal("d", json.GetProperty("list")[0].GetProperty("c").GetString());
    }

    // Everything that is not exactly one object throws JsonException, the type the callers catch. The syntax
    // only Newtonsoft tolerated (single quotes, bare names, NaN, hex, undefined, raw line breaks in strings)
    // is malformed too; Graph and Entra never send it.
    [Theory]
    [InlineData("[]")]
    [InlineData("[{}]")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("true")]
    [InlineData("not json")]
    [InlineData("<html>busy</html>")]
    [InlineData("{")]
    [InlineData("{\"a\":}")]
    [InlineData("{} x")]
    [InlineData("{}{}")]
    [InlineData("{} /* unterminated")]
    [InlineData("{'a':'b'}")]
    [InlineData("{a:1}")]
    [InlineData("{\"a\":NaN}")]
    [InlineData("{\"a\":0x1F}")]
    [InlineData("{\"a\":undefined}")]
    [InlineData("{\"a\":\"line\nbreak\"}")]
    [InlineData("{\"a\":1,,}")]
    public void ParseObjectOrNull_throws_JsonException_for_anything_but_one_object(string body)
        => Assert.ThrowsAny<JsonException>(() => UpstreamJson.ParseObjectOrNull(body));

    [Fact]
    public void ParseObjectOrNull_throws_JsonException_for_nesting_beyond_the_depth_limit()
        => Assert.ThrowsAny<JsonException>(() => UpstreamJson.ParseObjectOrNull(
            "{\"a\":" + new string('[', 100) + new string(']', 100) + "}"));

    // ── GetString ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"a":"value"}""", "value")]
    [InlineData("""{"a":""}""", "")]
    [InlineData("""{"a":"  padded  "}""", "  padded  ")]
    [InlineData("""{"a":"quote\" back\\ slash\n tab\t é 😀"}""", "quote\" back\\ slash\n tab\t é 😀")]
    [InlineData("""{"a":"2026-01-01T00:00:00Z"}""", "2026-01-01T00:00:00Z")]
    [InlineData("""{"a":"/Date(1713096021000)/"}""", "/Date(1713096021000)/")]
    [InlineData("""{"a":"42"}""", "42")]
    [InlineData("""{"a":"first","a":"last"}""", "last")]
    public void GetString_reads_text_as_sent(string json, string expected)
        => Assert.Equal(expected, UpstreamJson.GetString(Obj(json), "a"));

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"a":null}""")]
    [InlineData("""{"a":42}""")]
    [InlineData("""{"a":4.2}""")]
    [InlineData("""{"a":true}""")]
    [InlineData("""{"a":false}""")]
    [InlineData("""{"a":{}}""")]
    [InlineData("""{"a":[]}""")]
    [InlineData("""{"a":["x"]}""")]
    [InlineData("""{"A":"other case"}""")]
    [InlineData("""{"a ":"other name"}""")]
    public void GetString_reads_anything_but_text_under_the_exact_name_as_absent(string json)
        => Assert.Null(UpstreamJson.GetString(Obj(json), "a"));

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("[]")]
    public void GetString_on_a_list_entry_that_is_no_object_throws_InvalidOperationException(string entry)
    {
        // The validators rely on this: a non-object entry before the match keeps ending the lookup as transient.
        var item = Obj($$"""{"value":[{{entry}}]}""").GetProperty("value")[0];
        Assert.Throws<InvalidOperationException>(() => UpstreamJson.GetString(item, "a"));
    }

    [Theory]
    [InlineData("""{"a":"x\ud800y"}""")]
    [InlineData("""{"a":"\udc00"}""")]
    [InlineData("""{"a":"\ud800A"}""")]
    public void GetString_reads_text_with_a_broken_surrogate_escape_as_absent(string json)
        => Assert.Null(UpstreamJson.GetString(Obj(json), "a"));

    // ── Items ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Items_yields_every_array_entry_in_order_whatever_its_kind()
        => Assert.Equal(
            new[] { JsonValueKind.Object, JsonValueKind.Null, JsonValueKind.Number, JsonValueKind.String, JsonValueKind.Array },
            UpstreamJson.Items(Obj("""{"value":[{},null,1,"x",[]]}"""), "value").Select(i => i.ValueKind));

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"value":null}""")]
    [InlineData("""{"value":{}}""")]
    [InlineData("""{"value":"oops"}""")]
    [InlineData("""{"value":42}""")]
    [InlineData("""{"value":[]}""")]
    [InlineData("""{"Value":[1]}""")]
    public void Items_is_empty_unless_the_property_is_a_non_empty_array(string json)
        => Assert.Empty(UpstreamJson.Items(Obj(json), "value"));

    // ── GetNextLink ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{}""", null)]
    [InlineData("""{"@odata.nextLink":null}""", null)]
    [InlineData("""{"@odata.nextLink":""}""", "")]
    [InlineData("""{"@odata.nextLink":"https://graph.microsoft.com/beta/x?$skiptoken=a%2Fb"}""", "https://graph.microsoft.com/beta/x?$skiptoken=a%2Fb")]
    [InlineData("""{"@odata.nextlink":"https://graph.microsoft.com/beta/other-case"}""", null)]
    public void GetNextLink_reads_the_link_or_its_absence(string json, string? expected)
        => Assert.Equal(expected, UpstreamJson.GetNextLink(Obj(json)));

    [Theory]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[\"https://graph.microsoft.com/beta/x\"]")]
    [InlineData("\"https://graph.microsoft.com/beta/x\\ud800\"")]
    public void GetNextLink_throws_for_a_link_that_is_not_text(string link)
        => Assert.ThrowsAny<JsonException>(() => UpstreamJson.GetNextLink(Obj($$"""{"@odata.nextLink":{{link}}}""")));

    // ── GetInt64 ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("3599", 3599L)]
    [InlineData("0", 0L)]
    [InlineData("-5", -5L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("\"3599\"", 3599L)]
    [InlineData("\" 3599 \"", 3599L)]
    [InlineData("\"+3599\"", 3599L)]
    [InlineData("\"-1\"", -1L)]
    public void GetInt64_reads_a_whole_number_or_its_text(string value, long expected)
        => Assert.Equal(expected, UpstreamJson.GetInt64(Obj($$"""{"n":{{value}}}"""), "n"));

    [Theory]
    [InlineData("3599.5")]
    [InlineData("3599.0")]
    [InlineData("7e2")]
    [InlineData("9223372036854775808")]
    [InlineData("99999999999999999999")]
    [InlineData("\"3599.0\"")]
    [InlineData("\"3,599\"")]
    [InlineData("\"3.599\"")]
    [InlineData("\"0x10\"")]
    [InlineData("\"\"")]
    [InlineData("\"abc\"")]
    [InlineData("\"\u0661\u0662\u0663\"")]
    [InlineData("\"\\ud800\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void GetInt64_reads_anything_but_a_whole_number_as_absent(string value)
        => Assert.Null(UpstreamJson.GetInt64(Obj($$"""{"n":{{value}}}"""), "n"));

    [Fact]
    public void GetInt64_reads_a_missing_property_as_absent()
        => Assert.Null(UpstreamJson.GetInt64(Obj("{}"), "n"));

    // ── GetBoolean ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", null)]
    [InlineData("\"true\"", null)]
    [InlineData("\"false\"", null)]
    [InlineData("1", null)]
    [InlineData("0", null)]
    [InlineData("{}", null)]
    public void GetBoolean_reads_only_true_and_false(string value, bool? expected)
        => Assert.Equal(expected, UpstreamJson.GetBoolean(Obj($$"""{"b":{{value}}}"""), "b"));

    [Fact]
    public void GetBoolean_reads_a_missing_property_as_absent()
        => Assert.Null(UpstreamJson.GetBoolean(Obj("{}"), "b"));

    // ── GetUtcDateTime ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-05-08T12:16:49Z", 0L)]
    [InlineData("2026-05-08T12:16:49.1234567Z", 1234567L)]
    [InlineData("2026-05-08T12:16:49.5Z", 5000000L)]
    [InlineData("2026-05-08T14:16:49+02:00", 0L)]
    [InlineData("2026-05-08T07:16:49.1234567-05:00", 1234567L)]
    [InlineData("2026-05-08T12:16:49", 0L)]
    public void GetUtcDateTime_reads_the_instant_in_utc_with_full_precision(string value, long fractionTicks)
    {
        var date = UpstreamJson.GetUtcDateTime(Obj($$"""{"d":"{{value}}"}"""), "d");

        Assert.Equal(new DateTimeOffset(2026, 5, 8, 12, 16, 49, TimeSpan.Zero).AddTicks(fractionTicks), date);
        Assert.Equal(TimeSpan.Zero, date!.Value.Offset);
    }

    [Fact]
    public void GetUtcDateTime_reads_a_date_without_time_as_its_utc_midnight()
        => Assert.Equal(new DateTimeOffset(2026, 5, 8, 0, 0, 0, TimeSpan.Zero), UpstreamJson.GetUtcDateTime(Obj("""{"d":"2026-05-08"}"""), "d"));

    // Graph sends 0001-01-01 for a date it does not have: absence, not a date two thousand years ago.
    [Theory]
    [InlineData("\"0001-01-01T00:00:00Z\"")]
    [InlineData("\"0001-01-01T00:00:00\"")]
    [InlineData("\"0001-01-01\"")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\"not-a-date\"")]
    [InlineData("\"2026-13-01T00:00:00Z\"")]
    [InlineData("\"/Date(1713096021000)/\"")]
    [InlineData("null")]
    [InlineData("20260508")]
    [InlineData("true")]
    [InlineData("{}")]
    public void GetUtcDateTime_reads_an_unset_or_unusable_value_as_absent(string value)
        => Assert.Null(UpstreamJson.GetUtcDateTime(Obj($$"""{"d":{{value}}}"""), "d"));

    [Fact]
    public void GetUtcDateTime_reads_a_missing_property_as_absent()
        => Assert.Null(UpstreamJson.GetUtcDateTime(Obj("{}"), "d"));

    // ── Culture matrix ──────────────────────────────────────────────────────────

    // No member may depend on the host culture: day-first, Thai Buddhist calendar and the en-US default all
    // read the same body the same way.
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("en-GB")]
    [InlineData("th-TH")]
    public void Every_member_reads_the_same_in_any_culture(string culture)
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            var json = Obj("""{"d":"2026-05-08T12:16:49.1234567Z","day":"2026-05-08","n":"3599","t":"2026-05-08T12:16:49Z","f":"1.5"}""");

            Assert.Equal(new DateTimeOffset(2026, 5, 8, 12, 16, 49, TimeSpan.Zero).AddTicks(1234567), UpstreamJson.GetUtcDateTime(json, "d"));
            Assert.Equal(new DateTimeOffset(2026, 5, 8, 0, 0, 0, TimeSpan.Zero), UpstreamJson.GetUtcDateTime(json, "day"));
            Assert.Equal(3599L, UpstreamJson.GetInt64(json, "n"));
            Assert.Equal("2026-05-08T12:16:49Z", UpstreamJson.GetString(json, "t"));
            Assert.Null(UpstreamJson.GetInt64(json, "f"));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}

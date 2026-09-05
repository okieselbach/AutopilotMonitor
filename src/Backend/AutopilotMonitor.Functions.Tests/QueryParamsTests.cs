using System.Globalization;
using AutopilotMonitor.Functions.Helpers;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The one query-parameter parser (<see cref="QueryParams"/>). The lenient theories carry the
/// cases the former per-function copies pinned (RuleHitSessions, DeviceJourney, VerdictCalibration,
/// the Apps reject-to-default variant) so their behaviour is decided once, here: garbage and
/// below-minimum mean default, above-maximum clamps. The strict theories pin the 400 messages
/// the paginators and admin routes return.
/// </summary>
public class QueryParamsTests
{
    private static readonly DateTime Occurred = new(2026, 8, 29, 20, 11, 0, DateTimeKind.Utc);

    // ── Lenient ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, 14)]
    [InlineData("", 14)]
    [InlineData("  ", 14)]
    [InlineData("garbage", 14)]
    [InlineData("14", 14)]
    [InlineData("1", 1)]
    [InlineData("0", 14)]        // below min ⇒ default (the old ClampDays copies answered 1)
    [InlineData("-5", 14)]
    [InlineData("90", 90)]
    [InlineData("365", 90)]      // above max ⇒ clamped (the old Apps copies answered the default)
    [InlineData("2147483648", 14)] // overflow ⇒ default
    public void Int_defaults_below_min_and_clamps_above_max(string? raw, int expected)
    {
        Assert.Equal(expected, QueryParams.Int(raw, @default: 14, min: 1, max: 90));
    }

    [Fact]
    public void Int_reads_the_named_parameter_from_a_query_collection()
    {
        var query = System.Web.HttpUtility.ParseQueryString("days=7&limit=abc");
        Assert.Equal(7, QueryParams.Int(query, "days", 30, 1, 365));
        Assert.Equal(100, QueryParams.Int(query, "limit", 100, 1, 2000));
        Assert.Equal(30, QueryParams.Int((System.Collections.Specialized.NameValueCollection?)null, "days", 30, 1, 365));
    }

    [Fact]
    public void Int_is_culture_invariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(1000, QueryParams.Int("1000", 1, 1, 5000));
            Assert.Equal(1, QueryParams.Int("1.000", 1, 1, 5000)); // thousands separator is not a number
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("x", null)]
    [InlineData("-3", -3)]
    [InlineData("12", 12)]
    public void IntOrNull_has_no_range(string? raw, int? expected)
    {
        Assert.Equal(expected, QueryParams.IntOrNull(raw));
    }

    [Fact]
    public void UtcInstant_is_null_for_absent_or_garbage_and_utc_otherwise()
    {
        Assert.Null(QueryParams.UtcInstant(null));
        Assert.Null(QueryParams.UtcInstant(" "));
        Assert.Null(QueryParams.UtcInstant("yesterday"));
        var parsed = QueryParams.UtcInstant("2026-08-29T22:11:00+02:00");
        Assert.Equal(Occurred, parsed);
        Assert.Equal(DateTimeKind.Utc, parsed!.Value.Kind);
    }

    // ── Strict ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryInt_absent_is_null_and_ok(string? raw)
    {
        Assert.True(QueryParams.TryInt(raw, "days", 1, 365, out var value, out var error));
        Assert.Null(value);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("banana", "days must be an integer")]
    [InlineData("1.5", "days must be an integer")]
    [InlineData("0", "days must be between 1 and 365")]
    [InlineData("-1", "days must be between 1 and 365")]
    [InlineData("366", "days must be between 1 and 365")]
    public void TryInt_rejects_garbage_and_out_of_range_with_a_named_message(string raw, string expectedError)
    {
        Assert.False(QueryParams.TryInt(raw, "days", 1, 365, out var value, out var error));
        Assert.Null(value);
        Assert.Equal(expectedError, error);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("365", 365)]
    public void TryInt_accepts_the_range_ends(string raw, int expected)
    {
        Assert.True(QueryParams.TryInt(raw, "days", 1, 365, out var value, out var error));
        Assert.Equal(expected, value);
        Assert.Null(error);
    }

    [Fact]
    public void TryInt_without_an_upper_bound_names_only_the_minimum()
    {
        Assert.False(QueryParams.TryInt("-1", "skip", 0, int.MaxValue, out _, out var error));
        Assert.Equal("skip must be an integer of at least 0", error);
        Assert.True(QueryParams.TryInt("2147483647", "skip", 0, int.MaxValue, out var max, out _));
        Assert.Equal(int.MaxValue, max);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("1", 1, null)]
    [InlineData("1000", 1000, null)]
    [InlineData("1001", null, "pageSize must be between 1 and 1000")]
    [InlineData("0", null, "pageSize must be between 1 and 1000")]
    [InlineData("ten", null, "pageSize must be an integer")]
    public void TryPageSize_is_strict_within_one_to_MaxPageSize(string? raw, int? expected, string? expectedError)
    {
        var ok = QueryParams.TryPageSize(raw, out var pageSize, out var error);
        Assert.Equal(expectedError == null, ok);
        Assert.Equal(expected, pageSize);
        Assert.Equal(expectedError, error);
    }

    // ── Instants ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryUtcInstant_reads_offset_bare_and_date_only_values_as_utc()
    {
        Assert.True(QueryParams.TryUtcInstant("2026-08-29T20:11:00Z", "d", out var z, out _));
        Assert.True(QueryParams.TryUtcInstant("2026-08-29T22:11:00+02:00", "d", out var offset, out _));
        Assert.True(QueryParams.TryUtcInstant("2026-08-29T20:11:00", "d", out var bare, out _));
        Assert.True(QueryParams.TryUtcInstant("2026-08-29", "d", out var dateOnly, out _));

        Assert.Equal(Occurred, z);
        Assert.Equal(Occurred, offset);
        Assert.Equal(Occurred, bare);
        Assert.Equal(Occurred.Date, dateOnly);
        Assert.Equal(DateTimeKind.Utc, z!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, bare!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, dateOnly!.Value.Kind);
    }

    [Fact]
    public void TryUtcInstant_treats_absent_and_whitespace_as_no_filter_and_garbage_as_error()
    {
        Assert.True(QueryParams.TryUtcInstant(null, "dateFrom", out var none, out var noneError));
        Assert.Null(none);
        Assert.Null(noneError);
        Assert.True(QueryParams.TryUtcInstant("  ", "dateFrom", out var blank, out _));
        Assert.Null(blank);
        Assert.False(QueryParams.TryUtcInstant("yesterday", "dateFrom", out var bad, out var error));
        Assert.Null(bad);
        Assert.Equal("dateFrom must be an ISO 8601 date or date-time", error);
    }

    [Fact]
    public void TryUtcInstant_is_culture_invariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // de-DE would read "08/29/2026" day-first and fail; invariant parsing does not.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.True(QueryParams.TryUtcInstant("08/29/2026 20:11", "d", out var parsed, out _));
            Assert.Equal(Occurred, parsed);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}

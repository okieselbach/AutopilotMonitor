using System;
using AutopilotMonitor.Functions.Helpers;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the single inverted-tick RowKey encoder every table-key builder now routes through.
/// The encodings are PERSISTED — a change here reorders live tables against their existing
/// rows, so the exact widths and boundary literals are asserted, not just round-trips.
/// </summary>
public class RowKeyCodecTests
{
    [Fact]
    public void InvertedTicks_boundary_literals_are_pinned()
    {
        // rev(MaxValue) = 0 and rev(MinValue) = MaxValue.Ticks — exact strings, no derivation.
        Assert.Equal("0000000000000000000", RowKeyCodec.InvertedTicks(DateTime.MaxValue));
        Assert.Equal("3155378975999999999", RowKeyCodec.InvertedTicks(DateTime.MinValue));
    }

    [Fact]
    public void InvertedTicks_is_fixed_width_19_digits()
    {
        foreach (var utc in SampleInstants())
        {
            var key = RowKeyCodec.InvertedTicks(utc);
            Assert.Equal(19, key.Length);
            Assert.All(key, c => Assert.InRange(c, '0', '9'));
        }
    }

    [Fact]
    public void InvertedTicksD20_is_the_same_value_one_zero_wider()
    {
        // UserActivity legacy width: identical rev value, padded to 20 digits. Existing D20
        // rows pin this — a D19 key would sort after every D20 key in that table.
        foreach (var utc in SampleInstants())
        {
            var d20 = RowKeyCodec.InvertedTicksD20(utc);
            Assert.Equal(20, d20.Length);
            Assert.Equal("0" + RowKeyCodec.InvertedTicks(utc), d20);
        }
    }

    [Fact]
    public void Later_instants_sort_lexicographically_first()
    {
        var earlier = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var later = earlier.AddSeconds(1);
        Assert.True(string.CompareOrdinal(RowKeyCodec.InvertedTicks(later), RowKeyCodec.InvertedTicks(earlier)) < 0);
    }

    [Fact]
    public void Decode_round_trips_encode_exactly()
    {
        foreach (var utc in SampleInstants())
        {
            Assert.True(RowKeyCodec.TryDecodeInvertedTicks(RowKeyCodec.InvertedTicks(utc), out var decoded));
            Assert.Equal(utc.Ticks, decoded.Ticks);
            Assert.Equal(DateTimeKind.Utc, decoded.Kind);
        }
    }

    [Theory]
    [InlineData("")]                        // empty
    [InlineData("123456789012345678x")]     // non-digit
    [InlineData("!155378975999999999")]     // prefix leaked into the digit run
    [InlineData("9999999999999999999")]     // rev > MaxValue.Ticks → negative tick value
    public void Decode_rejects_foreign_input(string digits)
    {
        Assert.False(RowKeyCodec.TryDecodeInvertedTicks(digits, out _));
    }

    private static DateTime[] SampleInstants() => new[]
    {
        new DateTime(2026, 8, 13, 18, 30, 12, DateTimeKind.Utc),
        new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc).AddTicks(7),
        DateTime.MinValue,
        DateTime.MaxValue,
    };
}

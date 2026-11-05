using System.Diagnostics;
using System.Text.RegularExpressions;
using AutopilotMonitor.Functions.Security;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The hardware whitelist matches tenant wildcards against device headers on every agent request
/// without building a Regex (D-411): same results as the former regex construction for every shape
/// an entry can take, linear work for the worst entry a tenant can write, and a device value longer
/// than any real hardware string only meets "*".
/// </summary>
public class HardwareWhitelistValidatorTests
{
    /// <summary>The construction this replaced, kept as the oracle: '*' → '.*', '?' → '.', the rest escaped.</summary>
    private static bool FormerRegexMatch(string text, string pattern)
    {
        if (pattern == "*")
            return true;
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("Dell Inc.", "Dell*")]
    [InlineData("dell inc.", "DELL*")]
    [InlineData("Hewlett-Packard", "HP*")]
    [InlineData("HP", "HP*")]
    [InlineData("LENOVO", "Lenovo*")]
    [InlineData("Microsoft Corporation", "Microsoft Corporation")]
    [InlineData("Microsoft Corporation ", "Microsoft Corporation")]
    [InlineData("Microsoft Corp", "Microsoft Corporation")]
    [InlineData("ThinkPad T14 Gen 6", "*T14*")]
    [InlineData("ThinkPad T14 Gen 6", "*Gen ?")]
    [InlineData("ThinkPad T14 Gen 16", "*Gen ?")]
    [InlineData("ThinkPad T14 Gen 6", "ThinkPad*Gen*")]
    [InlineData("VMware20,1", "VMware??,?")]
    [InlineData("VMware20,1", "VMware?,?")]
    [InlineData("a.b", "a.b")]
    [InlineData("axb", "a.b")]
    [InlineData("f(x)", "f(x)")]
    [InlineData("f(x)", "f(*)")]
    [InlineData("a\\b", "a\\b")]
    [InlineData("a\\xyzb", "a\\*b")]
    [InlineData("a*b", "a\\*b")]
    [InlineData("[abc]", "[abc]")]
    [InlineData("a", "[abc]")]
    [InlineData("x+y", "x+y")]
    [InlineData("xxy", "x+y")]
    [InlineData("$1.00", "$1.00")]
    [InlineData("^start", "^start")]
    [InlineData("abc", "**")]
    [InlineData("abc", "*?*")]
    [InlineData("ab", "???")]
    [InlineData("abc", "???")]
    [InlineData("abcc", "*c")]
    [InlineData("ÄÖÜ GmbH", "äöü*")]
    [InlineData("straße", "STRASSE")]
    public void Matches_like_the_former_regex_construction(string value, string pattern)
    {
        Assert.Equal(FormerRegexMatch(value, pattern), HardwareWhitelistValidator.MatchesWildcard(value, pattern));
    }

    [Fact]
    public void Case_folding_is_ordinal_the_one_deliberate_difference()
    {
        // The regex engine folded Turkish dotted 'İ' to 'i'; ordinal folding does not. SMBIOS strings are ASCII.
        Assert.True(FormerRegexMatch("İstanbul", "istanbul*"));
        Assert.False(HardwareWhitelistValidator.MatchesWildcard("İstanbul", "istanbul*"));
    }

    [Fact]
    public void The_worst_whitelist_a_tenant_can_write_costs_milliseconds_per_request()
    {
        // 200 entries of 128 characters, each a chain of wildcards that never matches, against the
        // longest value still compared (the cap) and a 32 KB header (past the cap).
        var entry = string.Concat(Enumerable.Repeat("*a", 63)) + "*b";
        var whitelist = Enumerable.Repeat(entry, 200).ToArray();
        var atCap = new string('a', HardwareWhitelistValidator.MaxMatchedValueLength);
        var huge = new string('a', 32 * 1024);

        var clock = Stopwatch.StartNew();
        var atCapResult = HardwareWhitelistValidator.ValidateHardware(atCap, "Model", whitelist, new[] { "*" });
        var hugeResult = HardwareWhitelistValidator.ValidateHardware(huge, "Model", whitelist, new[] { "*" });
        clock.Stop();

        Assert.False(atCapResult.IsValid);
        Assert.False(hugeResult.IsValid);
        // The former construction spent up to 200 × 100 ms per list on this.
        Assert.True(clock.ElapsedMilliseconds < 500, $"took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void A_value_longer_than_any_hardware_string_only_meets_the_star()
    {
        var longValue = "Dell" + new string('x', HardwareWhitelistValidator.MaxMatchedValueLength);

        Assert.False(HardwareWhitelistValidator.MatchesWildcard(longValue, "Dell*"));
        Assert.True(HardwareWhitelistValidator.MatchesWildcard(longValue, "*"));

        var rejected = HardwareWhitelistValidator.ValidateHardware(longValue, "Model", new[] { "Dell*" }, new[] { "*" });
        Assert.False(rejected.IsValid);
        Assert.True(rejected.ErrorMessage!.Length < HardwareWhitelistValidator.MaxMatchedValueLength + 64, "the rejection message carries a shortened value");
    }
}

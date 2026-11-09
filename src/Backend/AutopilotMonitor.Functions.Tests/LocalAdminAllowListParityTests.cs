using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <c>tests/fixtures/local-admin-allow-list/cases.json</c> is the one definition of the allowed-list entry
/// grammar; the agent tests run its parse cases against the netstandard2.0 build, the portal runs the file
/// against <c>allowedAccounts.ts</c>. Plus the list-level write rules of
/// <see cref="TenantConfigValidation.ValidateLocalAdminAllowedAccounts"/>.
/// </summary>
public class LocalAdminAllowListParityTests
{
    private static readonly Lazy<JObject> Fixture = new(() =>
        JObject.Parse(File.ReadAllText(Path.Combine(FindRepoRoot(), "tests", "fixtures", "local-admin-allow-list", "cases.json"))));

    public static IEnumerable<object[]> ParseEntries() => Fixture.Value["parse"]!.Select(c => new object[] { c.Value<string>("entry")! });

    public static IEnumerable<object[]> WriteEntries() => Fixture.Value["write"]!.Select(c => new object[] { c.Value<string>("entry")! });

    [Theory]
    [MemberData(nameof(ParseEntries))]
    public void Parse_matches_the_shared_case(string entry)
    {
        var c = Fixture.Value["parse"]!.First(x => x.Value<string>("entry") == entry);

        var parsed = LocalAdminAllowList.TryParseEntry(entry, out var pattern, out var role);

        if (c.Value<bool?>("ignored") == true)
        {
            Assert.False(parsed);
            return;
        }
        Assert.True(parsed);
        Assert.Equal(c.Value<string>("pattern"), pattern);
        Assert.Equal(c.Value<string>("role") == "user" ? LocalAccountRole.StandardUser : LocalAccountRole.Any, role);
    }

    [Theory]
    [MemberData(nameof(WriteEntries))]
    public void New_entry_validation_matches_the_shared_case(string entry)
    {
        var c = Fixture.Value["write"]!.First(x => x.Value<string>("entry") == entry);

        var error = LocalAdminAllowList.ValidateNewEntry(entry);

        if (c.Value<bool>("valid"))
            Assert.Null(error);
        else
            Assert.NotNull(error);
    }

    [Fact]
    public void Shared_cases_are_present()
    {
        Assert.True(Fixture.Value["parse"]!.Count() >= 15, "cases.json lost parse cases.");
        Assert.True(Fixture.Value["write"]!.Count() >= 25, "cases.json lost write cases.");
    }

    [Fact]
    public void Builtin_catalog_keeps_the_four_rid_accounts_and_the_standard_user_system_accounts()
    {
        var rid = LocalAdminAllowList.BuiltIns.Where(b => b.Match == LocalAdminBuiltInMatch.Rid).ToDictionary(b => b.Rid!.Value, b => b.Role);
        Assert.Equal(LocalAccountRole.Any, rid[500]);
        Assert.Equal(LocalAccountRole.StandardUser, rid[501]);
        Assert.Equal(LocalAccountRole.StandardUser, rid[503]);
        Assert.Equal(LocalAccountRole.StandardUser, rid[504]);

        string RoleOf(string name) => LocalAdminAllowList.BuiltIns
            .Single(b => b.Match == LocalAdminBuiltInMatch.AccountName && b.Name == name).Role.ToString();
        Assert.Equal("StandardUser", RoleOf("WsiAccount"));
        Assert.Equal("StandardUser", RoleOf("kioskUser0"));
        Assert.Equal("StandardUser", RoleOf("Administrator"));
        Assert.Equal("Any", RoleOf("defaultuser0"));
    }

    // ── list-level write rules ───────────────────────────────────────────────

    [Theory]
    [InlineData(null, "[\"adm-*\",\"kiosk|user\"]")]
    [InlineData("[\"adm-*\"]", "[\"adm-*\",\"kiosk|user\"]")]
    [InlineData("[\"adm-*\"]", "[]")]
    [InlineData("[\"adm-*\"]", "")]
    [InlineData("[\"*\"]", "[\"*\"]")]
    public void Valid_or_unchanged_lists_pass(string? stored, string candidate)
    {
        Assert.Null(TenantConfigValidation.ValidateLocalAdminAllowedAccounts(candidate, stored));
    }

    [Fact]
    public void A_stored_entry_is_never_rechecked_when_another_entry_is_added()
    {
        // Lists written before the grammar existed (a bare "*", an Entra SID) stay saveable.
        Assert.Null(TenantConfigValidation.ValidateLocalAdminAllowedAccounts(
            "[\"*\",\"S-1-12-1-1-2-3-4\",\"WsiAccount\",\"kiosk|user\"]",
            "[\"*\",\"S-1-12-1-1-2-3-4\",\"WsiAccount\"]"));
    }

    [Theory]
    [InlineData("{\"a\":1}", "JSON array")]
    [InlineData("[1,2]", "JSON array")]
    [InlineData("not json", "JSON array")]
    [InlineData("[\"*\"]", "too broad")]
    [InlineData("[\"kiosk|admin\"]", "is not valid")]
    [InlineData("[\"kiosk\",\"kiosk|user\"]", "listed twice")]
    [InlineData("[\"Kiosk\",\"kiosk\"]", "listed twice")]
    public void Invalid_new_entries_are_rejected(string candidate, string expected)
    {
        var error = TenantConfigValidation.ValidateLocalAdminAllowedAccounts(candidate, null);

        Assert.NotNull(error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void A_new_entry_repeating_a_stored_pattern_is_rejected()
    {
        var error = TenantConfigValidation.ValidateLocalAdminAllowedAccounts("[\"WsiAccount\",\"WsiAccount|user\"]", "[\"WsiAccount\"]");

        Assert.NotNull(error);
        Assert.Contains("listed twice", error);
    }

    [Fact]
    public void The_entry_count_is_capped()
    {
        var entries = Enumerable.Range(0, LocalAdminAllowList.MaxEntries + 1).Select(i => $"\"svc{i:D3}\"");

        var error = TenantConfigValidation.ValidateLocalAdminAllowedAccounts("[" + string.Join(",", entries) + "]", null);

        Assert.NotNull(error);
        Assert.Contains($"at most {LocalAdminAllowList.MaxEntries}", error);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "AutopilotMonitor.sln")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Could not locate repo root (AutopilotMonitor.sln).");
    }
}

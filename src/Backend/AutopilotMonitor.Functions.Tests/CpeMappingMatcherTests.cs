using System;
using System.Collections.Generic;
using AutopilotMonitor.Functions.Services.Vulnerability;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Rules of <see cref="CpeMappingMatcher"/>. The negative cases are names that the former
/// substring matching filed under a foreign product.
/// </summary>
public class CpeMappingMatcherTests
{
    private static CpeMapping Map(
        string vendor, string product, string cpe,
        string[]? patterns = null, string[]? publishers = null, string[]? excludes = null,
        CpeMappingSource source = CpeMappingSource.Seed)
        => new()
        {
            PartitionKey = "test",
            RowKey = $"{vendor}:{product}",
            Source = source,
            Vendor = vendor,
            Product = product,
            CpeUri = cpe,
            ProductKey = CpeName.ProductKeyOf(cpe),
            DisplayNamePatterns = patterns ?? Array.Empty<string>(),
            PublisherPatterns = publishers ?? Array.Empty<string>(),
            ExcludePatterns = excludes ?? Array.Empty<string>(),
        };

    private static readonly CpeMapping Chrome = Map("google", "google chrome", "cpe:2.3:a:google:chrome",
        new[] { "google chrome", "chrome" }, new[] { "google", "google llc" });
    private static readonly CpeMapping Edge = Map("microsoft", "microsoft edge", "cpe:2.3:a:microsoft:edge_chromium",
        new[] { "microsoft edge", "microsoft edge webview2", "webview2 runtime" }, new[] { "microsoft" }, new[] { "edge update" });
    private static readonly CpeMapping EdgeUpdate = Map("microsoft", "microsoft edge update", "cpe:2.3:a:microsoft:edge_update",
        new[] { "microsoft edge update" }, new[] { "microsoft" });
    private static readonly CpeMapping Rust = Map("rust-lang", "rust", "cpe:2.3:a:rust-lang:rust",
        new[] { "rust" }, new[] { "the rust project developers" });
    private static readonly CpeMapping Redis = Map("redis", "redis", "cpe:2.3:a:redis:redis",
        new[] { "redis" }, new[] { "redis", "redis labs" });
    private static readonly CpeMapping RLanguage = Map("r-project", "r", "cpe:2.3:a:r-project:r",
        new[] { "r for windows" }, new[] { "r core team" });
    private static readonly CpeMapping Opera = Map("opera", "opera", "cpe:2.3:a:opera:opera_browser",
        new[] { "opera stable", "opera" }, new[] { "opera software" });
    private static readonly CpeMapping Visio = Map("microsoft", "microsoft visio", "cpe:2.3:a:microsoft:visio",
        new[] { "microsoft visio", "visio" }, new[] { "microsoft" });
    private static readonly CpeMapping OracleJdk = Map("oracle", "java se development kit", "cpe:2.3:a:oracle:jdk",
        new[] { "java se development kit", "jdk" }, new[] { "oracle", "oracle corporation" });
    private static readonly CpeMapping VisualStudio = Map("microsoft", "visual studio", "cpe:2.3:a:microsoft:visual_studio",
        new[] { "visual studio" }, new[] { "microsoft" });
    private static readonly CpeMapping VsCode = Map("microsoft", "visual studio code", "cpe:2.3:a:microsoft:visual_studio_code",
        new[] { "microsoft visual studio code", "visual studio code" }, new[] { "microsoft" });
    private static readonly CpeMapping GoLanguage = Map("golang", "go", "cpe:2.3:a:golang:go",
        new[] { "go programming language" }, new[] { "google", "the go authors" });
    private static readonly CpeMapping Access = Map("microsoft", "microsoft access", "cpe:2.3:a:microsoft:access",
        new[] { "microsoft access" }, new[] { "microsoft" });
    private static readonly CpeMapping Office = Map("microsoft", "microsoft office", "cpe:2.3:a:microsoft:office",
        new[] { "microsoft office" }, new[] { "microsoft" });
    private static readonly CpeMapping Teams = Map("microsoft", "microsoft teams", "cpe:2.3:a:microsoft:teams",
        new[] { "microsoft teams", "msteams", "teams" }, new[] { "microsoft" }, new[] { "add-in", "addin" });
    private static readonly CpeMapping VisualCpp = Map("microsoft", "visual c++", @"cpe:2.3:a:microsoft:visual_c\+\+",
        new[] { "microsoft visual c++" }, new[] { "microsoft" });
    private static readonly CpeMapping NotepadPp = Map("notepad++", "notepad++", @"cpe:2.3:a:notepad-plus-plus:notepad\+\+",
        new[] { "notepad++" }, new[] { "notepad++ team" });
    private static readonly CpeMapping DotNet = Map("microsoft", ".net", "cpe:2.3:a:microsoft:.net",
        new[] { "microsoft .net runtime", ".net runtime" }, new[] { "microsoft" });
    private static readonly CpeMapping NodeJs = Map("openjs foundation", "node.js", "cpe:2.3:a:nodejs:node.js",
        new[] { "node.js" }, new[] { "node.js foundation", "openjs foundation" });

    private static readonly CpeMappingMatcher Matcher = new(new[]
    {
        Chrome, Edge, EdgeUpdate, Rust, Redis, RLanguage, Opera, Visio, OracleJdk, VisualStudio, VsCode,
        GoLanguage, Access, Office, Teams, VisualCpp, NotepadPp, DotNet, NodeJs,
    });

    private static string? CpeOf(string vendor, string name, string display, string publisher = "")
        => Matcher.Match(vendor, name, display, publisher)?.Mapping.CpeUri;

    // ---------------------------------------------------------------- key match

    [Fact]
    public void KeyMatch_VendorAndCanonicalName_IsHighConfidence()
    {
        var match = Matcher.Match("google", "google chrome", "Google Chrome", "Google LLC");

        Assert.Equal("cpe:2.3:a:google:chrome", match!.Value.Mapping.CpeUri);
        Assert.Equal("high", match.Value.Confidence);
    }

    [Fact]
    public void KeyMatch_UsesTheDisplayNameNotTheAgentKey()
    {
        // The agent's normalized name can be mangled; the shown name carries the product.
        var match = Matcher.Match("notepad++", "notepad++x64)", "Notepad++ (64-bit x64)", "Notepad++ Team");

        Assert.Equal(@"cpe:2.3:a:notepad-plus-plus:notepad\+\+", match!.Value.Mapping.CpeUri);
        Assert.Equal("high", match.Value.Confidence);
    }

    [Fact]
    public void KeyMatch_HigherSourceWinsForTheSameKey()
    {
        var seed = Map("contoso", "contoso widget", "cpe:2.3:a:contoso:widget_old");
        var custom = Map("contoso", "Contoso Widget 4.2 (x64)", "cpe:2.3:a:contoso:widget", source: CpeMappingSource.Custom);
        var auto = Map("contoso", "contoso widget", "cpe:2.3:a:contoso:widget_guess", source: CpeMappingSource.Auto);

        foreach (var order in new[] { new[] { seed, custom, auto }, new[] { auto, custom, seed }, new[] { custom, auto, seed } })
        {
            var match = new CpeMappingMatcher(order).Match("contoso", "widget", "Contoso Widget 5.0", "Contoso Ltd");
            Assert.Equal("cpe:2.3:a:contoso:widget", match!.Value.Mapping.CpeUri);
        }
    }

    [Fact]
    public void KeyMatch_UnknownVendorPlaceholderEqualsNoVendor()
    {
        var custom = Map("unknown", "Microsoft Edge WebView2 Runtime", "cpe:2.3:a:microsoft:edge_chromium", source: CpeMappingSource.Custom);

        var match = new CpeMappingMatcher(new[] { custom }).Match("", "microsoft edge webview2 runtime", "Microsoft Edge WebView2 Runtime", "");

        Assert.Equal("high", match!.Value.Confidence);
    }

    [Fact]
    public void KeyMatch_AnotherVendorWithTheSameNameIsNotMatched()
    {
        var custom = Map("contoso", "Agent", "cpe:2.3:a:contoso:agent", source: CpeMappingSource.Custom);

        Assert.Null(new CpeMappingMatcher(new[] { custom }).Match("fabrikam", "agent", "Agent 2.1", "Fabrikam Inc"));
    }

    // ---------------------------------------------------------------- patterns

    [Fact]
    public void Pattern_WholeWordsInOrder_IsMediumConfidence()
    {
        var match = Matcher.Match("", "microsoft edge webview2 runtime", "Microsoft Edge WebView2 Runtime", "");

        Assert.Equal("cpe:2.3:a:microsoft:edge_chromium", match!.Value.Mapping.CpeUri);
        Assert.Equal("medium", match.Value.Confidence);
    }

    [Theory]
    [InlineData("beyondtrust", "BeyondTrust Privilege Management for Windows (x64) 24.5.220.0", "BeyondTrust Corporation")] // "rust"
    [InlineData("microsoft", "Microsoft Visual C++ 2013 Redistributable (x86) - 12.0.40664", "Microsoft Corporation")]        // "redis"
    [InlineData("microsoft", "Microsoft Interoperability Forms Redistributable Package 2.0a", "Microsoft Corporation")]      // "opera"
    [InlineData("contoso", "Envision Viewer 3.1", "Contoso Ltd")]                                                           // "visio"
    [InlineData("google", "Google Earth Pro", "Google LLC")]                                                               // "go"
    [InlineData("microsoft", "Global Secure Access Client", "Microsoft Corporation")]                                      // "access"
    public void Pattern_NeverMatchesInsideALongerWordOrByVendorAlone(string vendor, string display, string publisher)
    {
        var cpe = CpeOf(vendor, display.ToLowerInvariant(), display, publisher);

        Assert.True(cpe is null or @"cpe:2.3:a:microsoft:visual_c\+\+", $"'{display}' was filed under {cpe}");
    }

    [Fact]
    public void Pattern_LanguageSuffixDoesNotTriggerAOneLetterProduct()
        => Assert.Null(CpeOf("microsoft", "microsoft onenote - fr-fr", "Microsoft OneNote - fr-fr", "Microsoft Corporation"));

    [Fact]
    public void Pattern_MostWordsWins()
    {
        Assert.Equal("cpe:2.3:a:microsoft:visual_studio_code",
            CpeOf("microsoft", "microsoft visual studio code (user)", "Microsoft Visual Studio Code (User)", "Microsoft Corporation"));
        Assert.Equal("cpe:2.3:a:microsoft:visual_studio",
            CpeOf("microsoft", "visual studio professional 2022", "Visual Studio Professional 2022", "Microsoft Corporation"));
        Assert.Equal("cpe:2.3:a:microsoft:edge_update",
            CpeOf("", "microsoft edge update", "Microsoft Edge Update", ""));
    }

    [Fact]
    public void Pattern_AfterForIsAnAddOnNotTheProduct()
    {
        Assert.Null(CpeOf("contoso", "remote desktop host for google chrome", "Remote Desktop Host for Google Chrome", "Contoso Ltd"));
        Assert.Null(CpeOf("contoso", "druckertreiber für microsoft office", "Druckertreiber für Microsoft Office", "Contoso GmbH"));
    }

    [Fact]
    public void Pattern_ExcludeWordsBlockTheMapping()
    {
        // The add-in is not Teams, and what follows "for" is not Office either.
        Assert.Null(CpeOf("microsoft", "microsoft teams meeting add-in for microsoft office",
            "Microsoft Teams Meeting Add-in for Microsoft Office", "Microsoft"));
        Assert.Equal("cpe:2.3:a:microsoft:teams",
            CpeOf("microsoft", "teams machine-wide installer", "Teams Machine-Wide Installer", "Microsoft Corporation"));
    }

    [Fact]
    public void OneWordPattern_NeedsTheVendorOrTheWholeName()
    {
        // Vendor agrees.
        Assert.Equal("cpe:2.3:a:google:chrome", CpeOf("google", "chrome beta", "Chrome Beta 131.0", "Google LLC"));
        // Vendor does not: a helper that carries the browser's name.
        Assert.Null(CpeOf("contoso", "contoso chrome helper", "Contoso Chrome Helper 3.0", "Contoso Ltd"));
        // The word is the whole name: an AppX package has no publisher to compare.
        Assert.Equal("cpe:2.3:a:microsoft:teams", CpeOf("msteams", "msteams", "MSTeams", "MSTeams"));
    }

    [Fact]
    public void OneWordPattern_VendorAgreesUpToLegalFormAndTrailingWords()
    {
        Assert.Equal("cpe:2.3:a:oracle:jdk", CpeOf("oracle", "jdk 21", "JDK 21.0.2", "Oracle Corporation"));
        Assert.Equal("cpe:2.3:a:google:chrome", CpeOf("google llc", "chrome", "Chrome Canary", "Google LLC"));
        // Another vendor's JDK is not Oracle's.
        Assert.Null(CpeOf("azul", "azul zulu jdk", "Azul Zulu JDK 17.0.9 (64-bit)", "Azul Systems, Inc."));
    }

    [Theory]
    [InlineData("microsoft", "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33135", @"cpe:2.3:a:microsoft:visual_c\+\+")]
    [InlineData("contoso", "Notepad++ Portable Launcher", null)] // one word, foreign vendor
    [InlineData("notepad++ team", "Notepad++ Portable Launcher", @"cpe:2.3:a:notepad-plus-plus:notepad\+\+")]
    [InlineData("microsoft", "Microsoft .NET Runtime - 8.0.11 (x64)", "cpe:2.3:a:microsoft:.net")]
    [InlineData("node.js foundation", "Node.js 20.11.0", "cpe:2.3:a:nodejs:node.js")]
    public void Tokens_KeepPlusSignsAndSplitOnDots(string vendor, string display, string? expectedCpe)
        => Assert.Equal(expectedCpe, CpeOf(vendor, display.ToLowerInvariant(), display, vendor));

    [Fact]
    public void Tokenize_SplitsOnPunctuationAndKeepsPlusAndHash()
    {
        Assert.Equal(new[] { "microsoft", "visual", "c++", "2015", "2022" }, CpeMappingMatcher.Tokenize("Microsoft Visual C++ 2015-2022"));
        Assert.Equal(new[] { "f#", "tools" }, CpeMappingMatcher.Tokenize("F# Tools"));
        Assert.Equal(new[] { "intel", "wireless" }, CpeMappingMatcher.Tokenize("Intel(R) Wireless™"));
        Assert.Empty(CpeMappingMatcher.Tokenize("   "));
    }

    [Fact]
    public void Match_NothingKnown_IsNull()
    {
        Assert.Null(Matcher.Match("contoso", "widget", "Contoso Widget 4.2", "Contoso Ltd"));
        Assert.Null(Matcher.Match(null, null, null, null));
    }

    // ---------------------------------------------------------------- keys

    [Theory]
    [InlineData("Google", "Google Chrome 131.0.6778.86", "google|google chrome")]
    [InlineData("google llc", "Google Chrome", "google llc|google chrome")]
    [InlineData("unknown", "7-Zip 24.08 (x64)", "|7-zip")]
    [InlineData(null, "7-Zip", "|7-zip")]
    [InlineData("contoso", "", "")]
    public void SoftwareKey_IsVendorAndCanonicalName(string? vendor, string name, string expected)
        => Assert.Equal(expected, CpeMappingMatcher.SoftwareKey(vendor, name));
}

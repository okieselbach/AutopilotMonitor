using AutopilotMonitor.Functions.Services.Vulnerability;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="CpeDictionary"/> answers one question: is this program name an NVD product name?
/// A name resolves only when its words are the product's words and the vendor agrees, or when
/// they are the vendor's words followed by the product's, and only when exactly one product fits.
/// There is no similarity search: a near miss stays unresolved.
/// </summary>
public class CpeDictionaryTests
{
    private static CpeDictionary Dictionary(params string[] productKeys) => new(
        productKeys.ToDictionary(key => key, key => new CpeDictionaryProduct { T = $"Title of {key}" }, StringComparer.Ordinal),
        "2026-11-11T03:00:00.0000000Z");

    private static string? Resolved(CpeDictionary dictionary, string name, string? vendor = null, string? publisher = null) =>
        dictionary.Resolve(name, vendor, publisher)?.CpeUri;

    [Theory]
    [InlineData("contoso", null)]
    [InlineData(null, "Contoso")]
    [InlineData("contoso inc", "")]
    [InlineData("", "Contoso Ltd.")]
    [InlineData("fabrikam", "The Contoso Company")] // either of the program's two vendor forms may agree
    [InlineData("contoso software", null)]           // a trailing word after the vendor's name
    public void Resolve_NameThatIsTheProductName_ResolvesWhenTheProgramsVendorAgrees(string? vendor, string? publisher)
    {
        var match = Dictionary("contoso:widget").Resolve("widget", vendor, publisher);

        Assert.NotNull(match);
        Assert.Equal("contoso", match!.Value.Vendor);
        Assert.Equal("widget", match.Value.Product);
        Assert.Equal("cpe:2.3:a:contoso:widget", match.Value.CpeUri);
        Assert.Equal("Title of contoso:widget", match.Value.Title);
    }

    [Theory]
    [InlineData("fabrikam", "Fabrikam Inc")]
    [InlineData("contosoft", null)]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("inc", "Ltd")] // nothing is left of a vendor that is only a legal form
    public void Resolve_NameThatIsTheProductName_DoesNotResolveWithoutAnAgreeingVendor(string? vendor, string? publisher) =>
        Assert.Null(Dictionary("contoso:widget").Resolve("widget", vendor, publisher));

    [Fact]
    public void Resolve_SameProductNameUnderTwoVendors_TakesTheOneWhoseVendorAgrees()
    {
        var dictionary = Dictionary("contoso:reader", "fabrikam:reader");

        Assert.Equal("cpe:2.3:a:fabrikam:reader", Resolved(dictionary, "reader", "fabrikam"));
        Assert.Equal("cpe:2.3:a:contoso:reader", Resolved(dictionary, "reader", publisher: "Contoso Ltd"));
        Assert.Null(Resolved(dictionary, "reader"));
        // Vendor and publisher each name one of the two: the name is not decided.
        Assert.Null(Resolved(dictionary, "reader", "contoso", "Fabrikam Inc"));
    }

    [Fact]
    public void Resolve_VendorWordsFollowedByProductWords_ResolveWithoutAVendor()
    {
        var dictionary = Dictionary("contoso:widget", "northwind_traders:tool_box");

        Assert.Equal("cpe:2.3:a:contoso:widget", Resolved(dictionary, "contoso widget"));
        Assert.Equal("cpe:2.3:a:northwind_traders:tool_box", Resolved(dictionary, "northwind traders tool box"));
        // The product's words first is another name.
        Assert.Null(Resolved(dictionary, "widget contoso"));
    }

    [Fact]
    public void Resolve_TwoProductsThatFit_ResolveToNothing()
    {
        var sameWordsSplitDifferently = Dictionary("contoso:widget_pro", "contoso_widget:pro");
        var productNameAndVendorPlusProduct = Dictionary("adatum:contoso_widget", "contoso:widget");

        Assert.Null(Resolved(sameWordsSplitDifferently, "contoso widget pro"));
        // Adatum's "contoso widget" and Contoso's "widget" both read "contoso widget".
        Assert.Null(Resolved(productNameAndVendorPlusProduct, "contoso widget", "adatum"));
        Assert.Equal("cpe:2.3:a:contoso:widget", Resolved(productNameAndVendorPlusProduct, "contoso widget"));
    }

    [Theory]
    [InlineData("widget", "contoso")]              // one word short
    [InlineData("widget pro max", "contoso")]      // one word more
    [InlineData("pro widget", "contoso")]          // other order
    [InlineData("contoso widget", null)]
    [InlineData("contoso widget pro 2", null)]
    [InlineData("contoso widgets pro", null)]      // nearly the same word
    public void Resolve_NameWithAWordMoreOrLess_DoesNotResolve(string name, string? vendor)
    {
        var dictionary = Dictionary("contoso:widget_pro");

        Assert.Null(Resolved(dictionary, name, vendor));
        Assert.Equal("cpe:2.3:a:contoso:widget_pro", Resolved(dictionary, "widget pro", "contoso"));
        Assert.Equal("cpe:2.3:a:contoso:widget_pro", Resolved(dictionary, "contoso widget pro"));
    }

    [Fact]
    public void Resolve_CpeEscapesAndUnderscores_AreReadAsTheWordsTheyStandFor()
    {
        var dictionary = Dictionary(@"fabrikam:notepad\+\+", "contoso:visual_studio_code", "contoso:node.js", "northwind_traders:x-ray");

        var escaped = dictionary.Resolve("notepad++", "fabrikam", null);
        Assert.Equal(@"notepad\+\+", escaped!.Value.Product);
        Assert.Equal(@"cpe:2.3:a:fabrikam:notepad\+\+", escaped.Value.CpeUri);
        Assert.Equal(@"cpe:2.3:a:fabrikam:notepad\+\+", Resolved(dictionary, "fabrikam notepad++"));
        // The plus signs are part of the word: "notepad" alone is another name.
        Assert.Null(Resolved(dictionary, "notepad", "fabrikam"));

        Assert.Equal("cpe:2.3:a:contoso:visual_studio_code", Resolved(dictionary, "visual studio code", "contoso"));
        Assert.Equal("cpe:2.3:a:contoso:visual_studio_code", Resolved(dictionary, "Contoso Visual Studio Code"));
        Assert.Null(Resolved(dictionary, "visual studio", "contoso"));

        Assert.Equal("cpe:2.3:a:contoso:node.js", Resolved(dictionary, "node.js", "contoso"));
        Assert.Equal("cpe:2.3:a:northwind_traders:x-ray", Resolved(dictionary, "X-Ray", publisher: "Northwind Traders Ltd"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--")]
    public void Resolve_NameWithoutAWord_IsNothing(string name) =>
        Assert.Null(Dictionary("contoso:widget").Resolve(name, "contoso", "Contoso Ltd"));

    [Fact]
    public void Entries_WithoutAVendorOrAProduct_AreNeverResolved_AndDoNotGetInTheWayOfTheOthers()
    {
        var dictionary = Dictionary("widget", ":widget", "contoso:", "contoso:-", "contoso:reader");

        Assert.Equal("2026-11-11T03:00:00.0000000Z", dictionary.LastRefresh);
        Assert.Null(Resolved(dictionary, "widget", "contoso"));
        Assert.Null(Resolved(dictionary, "contoso"));
        Assert.Equal("cpe:2.3:a:contoso:reader", Resolved(dictionary, "reader", "contoso"));
    }
}

using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Functions.Services.Vulnerability;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The canonical name is the key for manual mappings, the ignore list and the admin's unmatched
/// list. Every version, architecture and language of one product must land on one key, and two
/// products must not.
/// </summary>
public class SoftwareNameCanonicalizerTests
{
    public static IEnumerable<object[]> Names() => new[]
    {
        // versions, architectures, editions in parentheses
        new object[] { "7-Zip 24.08 (x64)", "7-zip" },
        new object[] { "7-Zip 26.03 (x64 edition)", "7-zip" },
        new object[] { "7-Zip", "7-zip" },
        new object[] { "Zoom Workplace (64-bit)", "zoom workplace" },
        new object[] { "Python 3.12.1 (64-bit)", "python" },
        new object[] { "WinRAR 7.01 (64-bit)", "winrar" },
        new object[] { "Notepad++ (64-bit x64)", "notepad++" },
        new object[] { "Foo Tool v2.5", "foo tool" },
        new object[] { "Foo Tool version 2.5-beta", "foo tool" },

        // language tags
        new object[] { "Mozilla Firefox (x64 de)", "mozilla firefox" },
        new object[] { "Mozilla Firefox (x64 en-US)", "mozilla firefox" },
        new object[] { "Mozilla Firefox ESR (x64 en-US)", "mozilla firefox esr" },
        new object[] { "Microsoft 365 Apps for enterprise - de-de", "microsoft 365 apps for enterprise" },
        new object[] { "Microsoft OneNote - fr-fr", "microsoft onenote" },

        // marks and separators
        new object[] { "Intel(R) Wireless Bluetooth(R)", "intel wireless bluetooth" },
        new object[] { "Java(TM) SE Development Kit 17.0.9 (64-bit)", "java se development kit" },
        new object[] { "HP Wolf Security - Console", "hp wolf security console" },
        new object[] { "Google Chrome", "google chrome" },

        // counters that change with every patch or build
        new object[] { "Java 8 Update 401 (64-bit)", "java 8" },
        new object[] { "Update for Windows 10 for x64-based Systems (KB5001716)", "update for windows 10 for x64-based systems" },
        new object[] { "Citrix Workspace 2402", "citrix workspace" },
        new object[] { "Microsoft .NET Runtime - 8.0.11 (x64)", "microsoft .net runtime" },
        new object[] { "Contoso Agent {5A3B9C10-1D2E-4F60-8A7B-0C9D8E7F6A5B}", "contoso agent" },
        new object[] { "Windows Driver Package - Contoso (ctsdrv) System  (06/12/2020 2028.15.0.1688)", "windows driver package contoso ctsdrv system" },

        // a year is part of the name, a release range too
        new object[] { "Microsoft Visual Studio Professional 2022", "microsoft visual studio professional 2022" },
        new object[] { "Microsoft Office Professional Plus 2016", "microsoft office professional plus 2016" },
        new object[] { "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33135", "microsoft visual c++ 2015-2022 redistributable" },

        // a small number is part of the name
        new object[] { "Office 16 Click-to-Run Licensing Component", "office 16 click-to-run licensing component" },
        new object[] { "Node.js", "node.js" },

        new object[] { "", "" },
        new object[] { "   ", "" },
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void Canonicalize_KeepsOnlyWhatNamesTheProduct(string displayName, string expected)
        => Assert.Equal(expected, SoftwareNameCanonicalizer.Canonicalize(displayName));

    [Fact]
    public void Canonicalize_Null_IsEmpty()
        => Assert.Equal(string.Empty, SoftwareNameCanonicalizer.Canonicalize(null));

    /// <summary>Stored keys are canonicalized again when loaded, so a second pass must not move them.</summary>
    [Fact]
    public void Canonicalize_IsStableOnItsOwnOutput()
    {
        foreach (var name in Names().Select(row => (string)row[0]))
        {
            var once = SoftwareNameCanonicalizer.Canonicalize(name);
            Assert.Equal(once, SoftwareNameCanonicalizer.Canonicalize(once));
        }
    }

    [Fact]
    public void Canonicalize_KeepsDifferentProductsApart()
    {
        var keys = new[]
        {
            "Microsoft Visual Studio Professional 2019", "Microsoft Visual Studio Professional 2022",
            "Microsoft Visual Studio Code", "Mozilla Firefox", "Mozilla Firefox ESR",
            "Microsoft Edge", "Microsoft Edge Update", "Microsoft Edge WebView2 Runtime",
        }.Select(SoftwareNameCanonicalizer.Canonicalize).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}

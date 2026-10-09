using System.Text.RegularExpressions;
using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Tests;

public class TableKeySanitizerTests
{
    [Fact]
    public void Sanitize_RemovesControlCharacters()
    {
        // C0 and C1 controls: Table Storage rejects them in keys, and a point read carries the key
        // in its URL, where the HTTP layer refuses them with "400 Invalid URL".
        var input = $"a{(char)0}b\tc\r\nd{(char)0x7F}e{(char)0x85}f";
        Assert.Equal("abcdef", TableKeySanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_ReplacesTheCharactersTableStorageRejectsInKeys()
    {
        Assert.Equal("a_b_c_d_e", TableKeySanitizer.Sanitize("a/b\\c#d?e"));
    }

    [Fact]
    public void Sanitize_NulPaddedInventoryTriple_YieldsTheCleanKey()
    {
        var padded = "contoso\0\0\0\0\0:widget\0\0\0\0\0:1.0";
        Assert.Equal("contoso:widget:1.0", TableKeySanitizer.Sanitize(padded));
    }

    [Theory]
    [InlineData("contoso:widget:1.0")]
    [InlineData(":microsoft edge update:1.3.265.7")]
    [InlineData("müller gmbh:übersicht:2.3")]
    [InlineData("cve_cpe:2.3:a:contoso:widget:*:*:*:*:*:*:*:*")]
    public void Sanitize_KeyWithoutControlOrIllegalCharacters_IsUnchanged(string key)
    {
        // Existing rows must stay reachable: the rule only changes keys that could never be read.
        Assert.Equal(key, TableKeySanitizer.Sanitize(key));
    }

    [Fact]
    public void Sanitize_IsIdempotent()
    {
        var once = TableKeySanitizer.Sanitize("a/b\0#c?\td");
        Assert.Equal(once, TableKeySanitizer.Sanitize(once));
    }

    [Fact]
    public void RemoveControlCharacters_KeepsKeyIllegalCharacters()
    {
        Assert.Equal("a/b#c", TableKeySanitizer.RemoveControlCharacters("a/b\0#\u0001c"));
    }

    [Fact]
    public void No_key_builder_keeps_its_own_copy_of_the_rule()
    {
        // The rule existed ten times, and only one copy removed control characters. A new inline
        // copy would map the same text to a different key than every other writer of that table.
        var functionsDir = Path.Combine(RepoRoot(), "src", "Backend", "AutopilotMonitor.Functions");
        var inlineCopy = new Regex(@"Replace\(\s*[""']#[""']\s*,\s*[""']_[""']\s*\)|static\s+string\s+SanitizeTableKey\s*\(");
        var offenders = Directory
            .EnumerateFiles(functionsDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => inlineCopy.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(functionsDir, f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Build table keys from free text through TableKeySanitizer.Sanitize, not a local copy: "
            + string.Join(", ", offenders));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

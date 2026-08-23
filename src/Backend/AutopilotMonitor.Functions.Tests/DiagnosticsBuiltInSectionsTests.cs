using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Invariants of the built-in diagnostics catalog (<see cref="DiagnosticsBuiltInSections"/>).
/// Shared has no test project, so the catalog is pinned here: the agent iterates it to build the
/// archive and the backend serves it to the portal, so a malformed entry would either break
/// collection on every device or mislead every administrator reading the list.
/// </summary>
public class DiagnosticsBuiltInSectionsTests
{
    [Fact]
    public void SectionIds_AreUnique()
    {
        var ids = DiagnosticsBuiltInSections.All.Select(s => s.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ZipFolderAndSourceFolderPairs_AreUnique()
    {
        // Two sections may share a ZIP folder (ImeLogs + the bootstrapper evtx) only when they
        // read from different source folders — the same (zip, source) pair would double-add
        // every file under duplicate entry names.
        var pairs = DiagnosticsBuiltInSections.All
            .Select(s => $"{s.ZipFolder}|{s.SourceFolder}".ToLowerInvariant())
            .ToArray();
        Assert.Equal(pairs.Length, pairs.Distinct().Count());
    }

    [Fact]
    public void AlwaysSections_KeepHistoricalArchiveLayoutAndOrder()
    {
        // Archive-layout lock: these five sections (and their order) are what every diagnostics
        // ZIP has carried since the PR1-B forensics work; tooling and support habits rely on it.
        var always = DiagnosticsBuiltInSections.All
            .Where(s => s.Condition == DiagnosticsSectionCondition.Always)
            .Select(s => s.Id)
            .ToArray();
        Assert.Equal(new[] { "AgentLogs", "ImeLogs", "AgentState", "AgentSpool", "AgentMarkers" }, always);
    }

    [Fact]
    public void ConditionalSections_CarryTheExpectedGate()
    {
        foreach (var section in DiagnosticsBuiltInSections.All)
        {
            if (section.Id.StartsWith("RealmJoin", StringComparison.Ordinal))
                Assert.Equal(DiagnosticsSectionCondition.RealmJoinWatcher, section.Condition);
            if (section.Id == "ImeBootstrapperEventLog")
                Assert.Equal(DiagnosticsSectionCondition.DevicePreparation, section.Condition);
        }
        Assert.Contains(DiagnosticsBuiltInSections.All, s => s.Id == "ImeBootstrapperEventLog");
        Assert.Equal(5, DiagnosticsBuiltInSections.All.Count(s => s.Condition == DiagnosticsSectionCondition.RealmJoinWatcher));
    }

    [Fact]
    public void UserProfileTokenSections_AreNeverAlwaysOn()
    {
        // The token resolves only once an interactive user exists; an Always section reading
        // from the user profile would be skipped on every pre-desktop package and mislead the
        // portal's "collected by every agent" wording.
        var tokenSections = DiagnosticsBuiltInSections.All
            .Where(s => s.SourceFolder.Contains(DiagnosticsBuiltInSections.UserProfileToken, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(tokenSections);
        Assert.All(tokenSections, s => Assert.NotEqual(DiagnosticsSectionCondition.Always, s.Condition));
    }

    [Fact]
    public void Patterns_AreFileNamesWithoutPathSeparators()
    {
        Assert.All(DiagnosticsBuiltInSections.All, s =>
        {
            Assert.NotEmpty(s.Patterns);
            Assert.All(s.Patterns, p =>
            {
                Assert.False(string.IsNullOrWhiteSpace(p));
                Assert.DoesNotContain('\\', p);
                Assert.DoesNotContain('/', p);
            });
            Assert.False(string.IsNullOrWhiteSpace(s.Description));
            Assert.False(s.ZipFolder.StartsWith("/") || s.ZipFolder.EndsWith("/"));
            Assert.DoesNotContain('\\', s.ZipFolder);
        });
    }

    [Fact]
    public void NoBuiltInSection_UsesTheConfiguredPathsZipFolder()
    {
        // AdditionalLogs/<folder> is reserved for admin-configured entries.
        Assert.All(DiagnosticsBuiltInSections.All,
            s => Assert.False(s.ZipFolder.StartsWith("AdditionalLogs", StringComparison.OrdinalIgnoreCase)));
    }
}

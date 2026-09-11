using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The IME pattern self-seed behind the newest-source-wins gate (<see cref="RuleCatalogSeedGate"/>):
/// a GitHub reseed newer than the build owns the table (nothing written, nothing deleted), an
/// older one hands it back to the binary (missing ids written, retired ids sunset), an empty
/// table is always seeded, and the code reseed stamps <c>embedded</c>. The retired id stands in
/// for PS-SCRIPT-EXITCODE, which the deployed binary re-inserted on 2026-09-11 after the reseed
/// had deleted it.
/// </summary>
public class ImeLogPatternServiceSeedTests
{
    private const string RetiredId = "IME-RETIRED-TEST";
    private static readonly DateTime Build = new(2026, 9, 10, 19, 19, 57, DateTimeKind.Utc);
    private static readonly BackendBuildInfo BuildInfo = new("1.5.1205", "e3b2113", Build);

    private static RuleCatalogStamp GitHubStamp(DateTime at) => new()
    {
        Kind = RuleCatalogStamp.KindIme, Source = RuleCatalogStamp.SourceGitHub, StampedAt = at, Count = 82,
    };

    /// <summary>Global partition = the embedded catalog minus its first id plus one retired built-in row.</summary>
    private static (List<ImeLogPattern> table, string missingId) DriftedTable()
    {
        var embedded = BuiltInImeLogPatterns.GetAll();
        var missing = embedded[0].PatternId;
        var table = embedded.Skip(1).Select(p => p.Clone()).ToList();
        table.Add(new ImeLogPattern
        {
            PatternId = RetiredId, Category = "always", Pattern = "^retired", Action = "scriptExitCode",
            Parameters = new Dictionary<string, string>(), Enabled = true, Description = "retired", IsBuiltIn = true,
        });
        return (table, missing);
    }

    private sealed class Rig
    {
        public readonly Mock<IRuleRepository> Repo = new();
        public readonly List<string> Stored = new();
        public readonly List<string> Deleted = new();

        public Rig(List<ImeLogPattern> global, RuleCatalogStamp? stamp)
        {
            Repo.Setup(r => r.GetImeLogPatternsAsync("global")).ReturnsAsync(() => global.Select(p => p.Clone()).ToList());
            Repo.Setup(r => r.GetImeLogPatternsAsync(It.Is<string>(k => k != "global"))).ReturnsAsync(new List<ImeLogPattern>());
            Repo.Setup(r => r.GetRuleCatalogStampAsync(RuleCatalogStamp.KindIme)).ReturnsAsync(stamp);
            Repo.Setup(r => r.StoreImeLogPatternAsync(It.IsAny<ImeLogPattern>(), "global"))
                .Callback<ImeLogPattern, string>((p, _) => Stored.Add(p.PatternId)).ReturnsAsync(true);
            Repo.Setup(r => r.DeleteImeLogPatternAsync("global", It.IsAny<string>()))
                .Callback<string, string>((_, id) => Deleted.Add(id)).ReturnsAsync(true);
            Repo.Setup(r => r.SetRuleCatalogStampAsync(It.IsAny<RuleCatalogStamp>())).ReturnsAsync(true);
        }
    }

    [Fact]
    public async Task GitHub_reseed_newer_than_the_build_owns_the_table()
    {
        var (table, missing) = DriftedTable();
        var rig = new Rig(table, GitHubStamp(Build.AddHours(6)));
        var service = new ImeLogPatternService(rig.Repo.Object, NullLogger<ImeLogPatternService>.Instance, BuildInfo);

        var catalog = await service.GetGlobalCatalogAsync();

        Assert.Empty(rig.Stored);
        Assert.Empty(rig.Deleted);
        Assert.Contains(catalog, p => p.PatternId == RetiredId);       // the reseed's table, untouched
        Assert.DoesNotContain(catalog, p => p.PatternId == missing);
    }

    [Fact]
    public async Task GitHub_reseed_older_than_the_build_hands_the_table_to_the_binary()
    {
        var (table, missing) = DriftedTable();
        var rig = new Rig(table, GitHubStamp(Build.AddHours(-1)));
        var service = new ImeLogPatternService(rig.Repo.Object, NullLogger<ImeLogPatternService>.Instance, BuildInfo);

        await service.GetGlobalCatalogAsync();

        Assert.Contains(missing, rig.Stored);      // added from the embedded catalog
        Assert.Equal(new[] { RetiredId }, rig.Deleted); // sunset: no longer shipped
    }

    [Fact]
    public async Task Without_build_info_the_gate_is_open_and_the_stamp_is_never_read()
    {
        var (table, missing) = DriftedTable();
        var rig = new Rig(table, GitHubStamp(Build.AddHours(6)));
        var service = new ImeLogPatternService(rig.Repo.Object, NullLogger<ImeLogPatternService>.Instance);

        await service.GetGlobalCatalogAsync();

        Assert.Contains(missing, rig.Stored);
        rig.Repo.Verify(r => r.GetRuleCatalogStampAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Empty_table_is_seeded_even_behind_a_newer_reseed()
    {
        var rig = new Rig(new List<ImeLogPattern>(), GitHubStamp(Build.AddHours(6)));
        var service = new ImeLogPatternService(rig.Repo.Object, NullLogger<ImeLogPatternService>.Instance, BuildInfo);

        await service.GetGlobalCatalogAsync();

        Assert.Equal(BuiltInImeLogPatterns.GetAll().Count, rig.Stored.Count);
        Assert.Empty(rig.Deleted);
    }

    [Fact]
    public async Task Seed_runs_once_per_instance_also_when_the_gate_is_closed()
    {
        var (table, _) = DriftedTable();
        var rig = new Rig(table, GitHubStamp(Build.AddHours(6)));
        var service = new ImeLogPatternService(rig.Repo.Object, NullLogger<ImeLogPatternService>.Instance, BuildInfo);

        await service.GetGlobalCatalogAsync();
        await service.GetGlobalCatalogAsync();

        rig.Repo.Verify(r => r.GetRuleCatalogStampAsync(RuleCatalogStamp.KindIme), Times.Once);
    }

    [Fact]
    public async Task Code_reseed_stamps_the_embedded_source()
    {
        var (table, _) = DriftedTable();
        var rig = new Rig(table, GitHubStamp(Build.AddHours(6)));
        var service = new ImeLogPatternService(rig.Repo.Object, NullLogger<ImeLogPatternService>.Instance, BuildInfo);

        var (deleted, written) = await service.ReseedBuiltInPatternsAsync();

        Assert.Equal(table.Count, deleted);
        Assert.Equal(BuiltInImeLogPatterns.GetAll().Count, written);
        rig.Repo.Verify(r => r.SetRuleCatalogStampAsync(It.Is<RuleCatalogStamp>(s =>
            s.Kind == RuleCatalogStamp.KindIme && s.Source == RuleCatalogStamp.SourceEmbedded && s.Count == written)), Times.Once);
    }
}

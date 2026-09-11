using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The gate in front of the embedded self-seed: only a GitHub reseed that is NEWER than the
/// deployed binary closes it. The dates are the 2026-09-11 incident — a backend built on
/// 2026-09-10 19:19 UTC re-inserted a pattern that the reseed of 2026-09-11 01:20 had deleted.
/// </summary>
public class RuleCatalogStampTests
{
    private static readonly DateTime Build = new(2026, 9, 10, 19, 19, 57, DateTimeKind.Utc);

    private static RuleCatalogStamp Stamp(string source, DateTime at) => new()
    {
        Kind = RuleCatalogStamp.KindIme, Source = source, StampedAt = at, Count = 82,
    };

    [Fact]
    public void No_stamp_lets_the_embedded_seed_run()
    {
        Assert.True(RuleCatalogStamp.EmbeddedSeedAllowed(null, Build));
    }

    [Fact]
    public void GitHub_reseed_after_the_build_closes_the_gate()
    {
        var reseed = new DateTime(2026, 9, 11, 1, 20, 42, DateTimeKind.Utc);
        Assert.False(RuleCatalogStamp.EmbeddedSeedAllowed(Stamp(RuleCatalogStamp.SourceGitHub, reseed), Build));
    }

    [Fact]
    public void GitHub_reseed_before_or_at_the_build_keeps_it_open()
    {
        Assert.True(RuleCatalogStamp.EmbeddedSeedAllowed(Stamp(RuleCatalogStamp.SourceGitHub, Build.AddHours(-1)), Build));
        Assert.True(RuleCatalogStamp.EmbeddedSeedAllowed(Stamp(RuleCatalogStamp.SourceGitHub, Build), Build));
    }

    [Fact]
    public void Embedded_reseed_never_closes_the_gate()
    {
        Assert.True(RuleCatalogStamp.EmbeddedSeedAllowed(Stamp(RuleCatalogStamp.SourceEmbedded, Build.AddDays(3)), Build));
        Assert.False(RuleCatalogStamp.EmbeddedSeedAllowed(Stamp("GitHub", Build.AddDays(3)), Build)); // source is compared case-insensitively
    }
}

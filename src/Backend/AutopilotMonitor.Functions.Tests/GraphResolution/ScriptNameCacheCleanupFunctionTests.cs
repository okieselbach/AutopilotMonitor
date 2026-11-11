using System;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Functions.Maintenance;
using AutopilotMonitor.Shared.Models.Graph;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutopilotMonitor.Functions.Tests.GraphResolution;

/// <summary>
/// Cleanup contract:
/// <list type="bullet">
///   <item>Positive rows older than 7 days are removed.</item>
///   <item>Positive rows younger than 7 days are kept.</item>
///   <item>NotFound rows follow the same 7 days: older ones are removed, younger ones are kept.</item>
///   <item>A failed delete on one row doesn't poison the run — others still get processed.</item>
/// </list>
/// </summary>
public class ScriptNameCacheCleanupFunctionTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task Deletes_positive_rows_older_than_7_days()
    {
        var fix = new Fixture();
        var oldId = "old-id";
        var freshId = "fresh-id";
        fix.Repo.Entries[(TenantId, ScriptKind.Platform, oldId)] = new ScriptDisplayNameEntry
        {
            TenantId = TenantId,
            Kind = ScriptKind.Platform,
            ScriptId = oldId,
            DisplayName = "Old Script",
            FetchedAt = fix.FixedNow - TimeSpan.FromDays(10),
            IsNotFound = false,
        };
        fix.Repo.Entries[(TenantId, ScriptKind.Platform, freshId)] = new ScriptDisplayNameEntry
        {
            TenantId = TenantId,
            Kind = ScriptKind.Platform,
            ScriptId = freshId,
            DisplayName = "Fresh Script",
            FetchedAt = fix.FixedNow - TimeSpan.FromDays(3),
            IsNotFound = false,
        };

        var result = await fix.Sut.RunCoreAsync(default);

        Assert.Equal(1, result.PositiveDeleted);
        Assert.Equal(0, result.NotFoundDeleted);
        Assert.False(fix.Repo.Entries.ContainsKey((TenantId, ScriptKind.Platform, oldId)));
        Assert.True(fix.Repo.Entries.ContainsKey((TenantId, ScriptKind.Platform, freshId)));
    }

    [Fact]
    public async Task Deletes_notfound_rows_only_after_the_same_7_days()
    {
        // A not-found row that goes after hours instead of days sends one Graph lookup per
        // unknown ID on the first view after every cleanup run.
        var fix = new Fixture();
        var deletedId = "not-found-8-days-ago";
        var recentId = "not-found-2-hours-ago";
        fix.Repo.Entries[(TenantId, ScriptKind.Remediation, deletedId)] = new ScriptDisplayNameEntry
        {
            TenantId = TenantId,
            Kind = ScriptKind.Remediation,
            ScriptId = deletedId,
            DisplayName = null,
            FetchedAt = fix.FixedNow - TimeSpan.FromDays(8),
            IsNotFound = true,
        };
        fix.Repo.Entries[(TenantId, ScriptKind.Remediation, recentId)] = new ScriptDisplayNameEntry
        {
            TenantId = TenantId,
            Kind = ScriptKind.Remediation,
            ScriptId = recentId,
            DisplayName = null,
            FetchedAt = fix.FixedNow - TimeSpan.FromHours(2),
            IsNotFound = true,
        };

        var result = await fix.Sut.RunCoreAsync(default);

        Assert.Equal(0, result.PositiveDeleted);
        Assert.Equal(1, result.NotFoundDeleted);
        Assert.False(fix.Repo.Entries.ContainsKey((TenantId, ScriptKind.Remediation, deletedId)));
        Assert.True(fix.Repo.Entries.ContainsKey((TenantId, ScriptKind.Remediation, recentId)));
    }

    [Fact]
    public async Task Failure_on_one_row_does_not_poison_the_run()
    {
        var fix = new Fixture();
        var id1 = "broken-row";
        var id2 = "next-row";
        fix.Repo.Entries[(TenantId, ScriptKind.Platform, id1)] = new ScriptDisplayNameEntry
        {
            TenantId = TenantId,
            Kind = ScriptKind.Platform,
            ScriptId = id1,
            FetchedAt = fix.FixedNow - TimeSpan.FromDays(10),
        };
        fix.Repo.Entries[(TenantId, ScriptKind.Platform, id2)] = new ScriptDisplayNameEntry
        {
            TenantId = TenantId,
            Kind = ScriptKind.Platform,
            ScriptId = id2,
            FetchedAt = fix.FixedNow - TimeSpan.FromDays(10),
        };
        // First delete throws; second should still be processed.
        fix.Repo.NextDeleteThrows = new InvalidOperationException("simulated transient");

        var result = await fix.Sut.RunCoreAsync(default);

        Assert.Equal(2, result.Scanned);
        Assert.Equal(1, result.Failures);
        Assert.Equal(1, result.PositiveDeleted);
    }

    [Fact]
    public async Task Empty_table_completes_cleanly()
    {
        var fix = new Fixture();
        var result = await fix.Sut.RunCoreAsync(default);

        Assert.Equal(0, result.Scanned);
        Assert.Equal(0, result.PositiveDeleted);
        Assert.Equal(0, result.NotFoundDeleted);
        Assert.Equal(0, result.Failures);
    }

    // ── Fixture ─────────────────────────────────────────────────────────────

    private sealed class Fixture
    {
        public FakeScriptNameCacheRepository Repo { get; } = new();
        public DateTimeOffset FixedNow { get; } = new(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);
        public ScriptNameCacheCleanupFunction Sut { get; }

        public Fixture()
        {
            var time = new FakeTimeProvider(FixedNow);
            Sut = new ScriptNameCacheCleanupFunction(Repo,
                NullLogger<ScriptNameCacheCleanupFunction>.Instance, time);
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}

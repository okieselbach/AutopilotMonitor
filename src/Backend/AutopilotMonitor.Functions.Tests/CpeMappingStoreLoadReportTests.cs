using System.Reflection;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="CpeMappingStore.LoadFailed"/> carries a failed load of the mappings to the ops
/// events. A load that keeps failing is retried again and again, by every scan without a copy
/// and once a minute otherwise, so the report is raised once per
/// <see cref="CpeMappingStore.ReportPause"/> and not per attempt. A load that succeeds ends the
/// pause: the next failure is a new incident.
/// </summary>
public class CpeMappingStoreLoadReportTests
{
    private sealed class Rig
    {
        public Rig()
        {
            Tables.Put(VulnerabilityCacheRows.Mapping("contoso", "contoso widget", "cpe:2.3:a:contoso:widget", "contoso widget"));
            Store = new CpeMappingStore(Tables.Repo.Object, NullLogger<CpeMappingStore>.Instance)
            {
                LoadFailed = (_, servingEarlierCopy) =>
                {
                    Reports.Add(servingEarlierCopy);
                    return ReportFailure == null ? Task.CompletedTask : Task.FromException(ReportFailure);
                },
            };
        }

        public MappingTables Tables { get; } = new();
        public CpeMappingStore Store { get; }

        /// <summary>One entry per report raised: whether an earlier copy was being served.</summary>
        public List<bool> Reports { get; } = new();

        /// <summary>When set, raising the report fails with it.</summary>
        public Exception? ReportFailure { get; set; }

        public void StorageGoesDown() => Tables.ReadFailure = new InvalidOperationException("table storage is not reachable");

        public void StorageComesBack() => Tables.ReadFailure = null;

        public int LoadAttempts => Tables.Repo.Invocations.Count(i => i.Method.Name == nameof(IVulnerabilityRepository.GetAllCpeMappingsAsync));
    }

    [Fact]
    public async Task LoadThatKeepsFailing_WithoutAnEarlierCopy_IsReportedOnce_ThoughEveryCallTriesAgain()
    {
        var rig = new Rig();
        rig.StorageGoesDown();

        for (var call = 0; call < 4; call++)
            await Assert.ThrowsAsync<CpeMappingUnavailableException>(() => rig.Store.GetAsync());

        Assert.Equal(4, rig.LoadAttempts);
        Assert.Equal(new[] { false }, rig.Reports);
    }

    [Fact]
    public async Task ReloadThatKeepsFailing_WhileAnEarlierCopyIsServed_IsReportedOnce()
    {
        var rig = new Rig();
        var good = await rig.Store.GetAsync();
        rig.StorageGoesDown();

        // Every writer of a mapping asks for a fresh read, whatever the pause between retries says.
        for (var call = 0; call < 4; call++)
            Assert.Same(good, await rig.Store.ReloadAsync());

        Assert.Equal(5, rig.LoadAttempts);
        Assert.Equal(new[] { true }, rig.Reports);
    }

    [Fact]
    public async Task LoadThatSucceedsAgain_EndsThePause_SoTheNextFailureIsReported()
    {
        var rig = new Rig();
        rig.StorageGoesDown();
        await Assert.ThrowsAsync<CpeMappingUnavailableException>(() => rig.Store.GetAsync());
        await Assert.ThrowsAsync<CpeMappingUnavailableException>(() => rig.Store.GetAsync());

        rig.StorageComesBack();
        var good = await rig.Store.GetAsync();
        Assert.Equal(new[] { false }, rig.Reports);

        rig.StorageGoesDown();
        Assert.Same(good, await rig.Store.ReloadAsync());
        Assert.Same(good, await rig.Store.ReloadAsync());

        // The second outage is told apart from the first: a copy is being served this time.
        Assert.Equal(new[] { false, true }, rig.Reports);
    }

    /// <summary>
    /// Lets the pause between two reports run out. The hour cannot be waited for, and the store
    /// reads the clock itself.
    /// </summary>
    private static void LetTheReportPauseRunOut(CpeMappingStore store)
    {
        var notBefore = typeof(CpeMappingStore).GetField("_reportNotBeforeTicks", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(notBefore);

        var until = new DateTime((long)notBefore!.GetValue(store)!, DateTimeKind.Utc);
        Assert.InRange(until, DateTime.UtcNow.Add(CpeMappingStore.ReportPause).AddMinutes(-5), DateTime.UtcNow.Add(CpeMappingStore.ReportPause));
        notBefore.SetValue(store, DateTime.UtcNow.AddSeconds(-1).Ticks);
    }

    [Fact]
    public async Task OutageThatOutlastsThePause_IsReportedAgain()
    {
        var rig = new Rig();
        rig.StorageGoesDown();
        await Assert.ThrowsAsync<CpeMappingUnavailableException>(() => rig.Store.GetAsync());
        await Assert.ThrowsAsync<CpeMappingUnavailableException>(() => rig.Store.GetAsync());
        Assert.Single(rig.Reports);

        LetTheReportPauseRunOut(rig.Store);
        await Assert.ThrowsAsync<CpeMappingUnavailableException>(() => rig.Store.GetAsync());
        await Assert.ThrowsAsync<CpeMappingUnavailableException>(() => rig.Store.GetAsync());

        // Once per pause for as long as the outage lasts, not once for good.
        Assert.Equal(new[] { false, false }, rig.Reports);
    }

    [Fact]
    public async Task ReportThatCannotBeRaised_ChangesNothingAboutWhatTheCallerGets()
    {
        var rig = new Rig { ReportFailure = new InvalidOperationException("the ops events cannot be written either") };
        rig.StorageGoesDown();

        await Assert.ThrowsAsync<CpeMappingUnavailableException>(() => rig.Store.GetAsync());

        rig.StorageComesBack();
        var good = await rig.Store.GetAsync();
        rig.StorageGoesDown();

        Assert.Same(good, await rig.Store.ReloadAsync());
        Assert.Equal(new[] { false, true }, rig.Reports);
    }
}

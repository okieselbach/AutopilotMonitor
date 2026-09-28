using System.Text.Json;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Functions.Apps;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// RealmJoin packages as <c>AppInstallSummaries</c> rows (<see cref="AppInstallSources.RealmJoin"/>):
/// the ingest fold of <c>realmjoin_package_started/completed</c>, the scope + package-id row
/// identity, the store-side channel columns, and the channel dimension of the per-app aggregates
/// (an Intune app and a RealmJoin package with the same display name never merge).
/// </summary>
public class RealmJoinPackageInstallRowsTests
{
    private static readonly DateTime T0 = new(2026, 9, 17, 8, 29, 26, DateTimeKind.Utc);
    private const string SessionId = "11111111-2222-3333-4444-555555555555";

    private static Dictionary<string, AppInstallAggregationState> Aggregate(params EnrollmentEvent[] events)
    {
        // Same comparer as the production classification (EventIngestProcessor.Classification).
        var summaries = new Dictionary<string, AppInstallAggregationState>(StringComparer.OrdinalIgnoreCase);
        foreach (var evt in events)
            EventIngestProcessor.AggregateAppInstallEvent(evt, "tenant", SessionId, summaries);
        return summaries;
    }

    private static AppInstallSummary Row(Dictionary<string, AppInstallAggregationState> summaries, string packageId, string scope = "machine")
        => summaries[EventIngestProcessor.RealmJoinAggregationKey(scope, packageId)].Summary;

    private static EnrollmentEvent Started(DateTime at, string packageId = "generic-github-desktop",
        string displayName = "GitHub Desktop", string scope = "machine", string? version = "3.6.5.0")
    {
        var data = new Dictionary<string, object>
        {
            ["packageId"] = packageId,
            ["displayName"] = displayName,
            ["scope"] = scope,
        };
        if (version != null) data["version"] = version;
        return new EnrollmentEvent { EventType = Constants.EventTypes.RealmJoinPackageStarted, Timestamp = at, Data = data };
    }

    private static EnrollmentEvent Completed(DateTime at, bool success = true, int exitCode = 0,
        string packageId = "generic-github-desktop", string displayName = "GitHub Desktop", string scope = "machine")
    {
        var evt = Started(at, packageId, displayName, scope);
        evt.EventType = Constants.EventTypes.RealmJoinPackageCompleted;
        // Wire form of the agent adapter: strings.
        evt.Data!["success"] = success ? "true" : "false";
        evt.Data["lastExitCode"] = exitCode.ToString();
        return evt;
    }

    // ── ingest fold: live watcher events ────────────────────────────────────

    [Fact]
    public void StartedThenCompleted_OneRealmJoinRow_WithMeasuredDuration()
    {
        var s = Row(Aggregate(Started(T0), Completed(T0.AddSeconds(22))), "generic-github-desktop");

        Assert.Equal(AppInstallSources.RealmJoin, s.Source);
        Assert.Equal("generic-github-desktop", s.AppId);
        Assert.Equal("machine", s.InstallScope);
        Assert.Equal("GitHub Desktop", s.AppName);
        Assert.Equal("3.6.5.0", s.AppVersion);
        Assert.Equal("Succeeded", s.Status);
        Assert.Equal("Installed", s.TerminalState);
        Assert.Equal(0, s.ExitCode);
        Assert.Equal(string.Empty, s.FailureCode);
        Assert.Equal(T0, s.StartedAt);
        Assert.Equal(T0, s.LastAttemptStartedAt);
        Assert.Equal(T0.AddSeconds(22), s.CompletedAt);
        Assert.Equal(22, s.DurationSeconds);
        Assert.Equal(1, s.InstallPassCount);
        Assert.True(MetricsMath.HasMeasuredDuration(s));
    }

    [Fact]
    public void FailedPackage_ExitCodeIsTheFailureCode()
    {
        var s = Row(Aggregate(Started(T0), Completed(T0.AddSeconds(40), success: false, exitCode: 1603)), "generic-github-desktop");

        Assert.Equal("Failed", s.Status);
        Assert.Equal("Error", s.TerminalState);
        Assert.Equal(1603, s.ExitCode);
        Assert.Equal("1603", s.FailureCode);
    }

    [Fact]
    public void StartedAndCompletedInOneRegistryPass_IsUnmeasured()
    {
        // Older agents report both events in the same watcher pass, milliseconds apart: that is
        // no install duration.
        var s = Row(Aggregate(Started(T0), Completed(T0.AddMilliseconds(4))), "generic-github-desktop");

        Assert.Equal("Succeeded", s.Status);
        Assert.Equal(0, s.DurationSeconds);
        Assert.False(MetricsMath.HasMeasuredDuration(s));
    }

    [Fact]
    public void MissingDisplayName_UsesPackageId_UntilANameArrives()
    {
        var summaries = Aggregate(Started(T0, displayName: string.Empty));
        Assert.Equal("generic-github-desktop", Row(summaries, "generic-github-desktop").AppName);

        EventIngestProcessor.AggregateAppInstallEvent(Completed(T0.AddSeconds(5)), "tenant", SessionId, summaries);
        Assert.Equal("GitHub Desktop", Row(summaries, "generic-github-desktop").AppName);
    }

    [Fact]
    public void SamePackageIdInBothScopes_IsTwoRows()
    {
        var summaries = Aggregate(
            Completed(T0, scope: "machine"),
            Completed(T0, success: false, exitCode: 1, scope: "user"));

        Assert.Equal("Succeeded", Row(summaries, "generic-github-desktop", "machine").Status);
        Assert.Equal("Failed", Row(summaries, "generic-github-desktop", "user").Status);
    }

    [Fact]
    public void RealmJoinPackage_AndIntuneApp_WithTheSameName_AreTwoRows()
    {
        var ime = new EnrollmentEvent
        {
            EventType = "app_install_completed",
            Timestamp = T0,
            Data = new Dictionary<string, object> { ["appName"] = "GitHub Desktop", ["state"] = "Installed" },
        };
        var summaries = Aggregate(ime, Completed(T0.AddSeconds(1)));

        Assert.Equal(2, summaries.Count);
        Assert.Equal(string.Empty, summaries["GitHub Desktop"].Summary.Source);
        Assert.Equal(AppInstallSources.RealmJoin, Row(summaries, "generic-github-desktop").Source);
    }

    [Fact]
    public void EventWithoutPackageId_IsIgnored()
    {
        var evt = Completed(T0);
        evt.Data!.Remove("packageId");
        Assert.Empty(Aggregate(evt));
    }

    // ── store: row identity, channel columns, reconcile ─────────────────────

    [Fact]
    public void RowKey_ImeUnchanged_RealmJoinKeyedByScopeAndPackageId()
    {
        Assert.Equal($"{SessionId}_Contoso App_2",
            TableStorageService.BuildAppInstallSummaryRowKey(new AppInstallSummary { SessionId = SessionId, AppName = "Contoso App/2" }));

        var rj = new AppInstallSummary
        {
            SessionId = SessionId,
            Source = AppInstallSources.RealmJoin,
            InstallScope = "user",
            AppId = "generic-7zip-usersettings",
            AppName = "7-Zip Settings",
        };
        var key = TableStorageService.BuildAppInstallSummaryRowKey(rj);
        Assert.Equal($"{SessionId}_realmjoin|user|generic-7zip-usersettings", key);
        // The per-session range scans (ESP stamp, close step, deletion) match on this prefix.
        Assert.StartsWith($"{SessionId}_", key, StringComparison.Ordinal);
    }

    [Fact]
    public void Entity_ImeRowHasNoChannelColumns_RealmJoinRowRoundTrips()
    {
        var ime = TableStorageService.BuildAppInstallSummaryEntity(
            new AppInstallSummary { TenantId = "t", SessionId = SessionId, AppName = "App" }, "rk");
        Assert.False(ime.ContainsKey("Source"));
        Assert.False(ime.ContainsKey("InstallScope"));

        var summary = new AppInstallSummary
        {
            TenantId = "t",
            SessionId = SessionId,
            AppName = "7-Zip",
            Source = AppInstallSources.RealmJoin,
            InstallScope = "machine",
            AppId = "generic-7zip",
        };
        var entity = TableStorageService.BuildAppInstallSummaryEntity(summary, "rk");
        var sut = new TableStorageService(new Mock<TableServiceClient>().Object, NullLogger<TableStorageService>.Instance);
        var mapped = sut.MapToAppInstallSummary(entity);

        Assert.Equal(AppInstallSources.RealmJoin, mapped.Source);
        Assert.Equal("machine", mapped.InstallScope);
        Assert.Equal("generic-7zip", mapped.AppId);
    }

    [Fact]
    public void Reconcile_PackageIdFallbackName_KeepsTheStoredDisplayName()
    {
        var existing = new TableEntity("t", "rk") { ["AppName"] = "7-Zip", ["StartedAt"] = T0 };
        var summary = new AppInstallSummary
        {
            Source = AppInstallSources.RealmJoin,
            AppId = "generic-7zip",
            AppName = "generic-7zip",
            StartedAt = T0.AddMinutes(30),
        };

        TableStorageService.ReconcileAppInstallSummaryWithExisting(summary, existing);
        Assert.Equal("7-Zip", summary.AppName);
    }

    [Fact]
    public void EspBlockingStamp_NeverTouchesRealmJoinRows()
    {
        var sets = EspBlockingSets.FromEventData(new Dictionary<string, object>
        {
            ["espTrackedWin32AppIds"] = new[] { "generic-7zip" },
        })!;
        var rj = new TableEntity("t", "rk") { ["Source"] = AppInstallSources.RealmJoin, ["AppId"] = "generic-7zip" };
        var ime = new TableEntity("t", "rk2") { ["AppId"] = "generic-7zip" };

        Assert.False(TableStorageService.ShouldStampEspBlocking(rj, sets));
        Assert.True(TableStorageService.ShouldStampEspBlocking(ime, sets));
    }

    // ── per-app aggregates: the channel is part of the app ──────────────────

    private static AppInstallSummary Summary(string appName, string source, string status = "Succeeded", int duration = 60)
        => new()
        {
            TenantId = "t",
            SessionId = Guid.NewGuid().ToString(),
            AppName = appName,
            Source = source,
            Status = status,
            TerminalState = status == "Failed" ? "Error" : "Installed",
            DurationSeconds = duration,
            StartedAt = DateTime.UtcNow.AddDays(-1),
            LastAttemptStartedAt = DateTime.UtcNow.AddDays(-1),
        };

    private static List<AppInstallSummary> MixedChannelRows() => new()
    {
        Summary("GitHub Desktop", string.Empty),
        Summary("GitHub Desktop", AppInstallSources.RealmJoin, status: "Failed"),
        Summary("GitHub Desktop", AppInstallSources.RealmJoin),
    };

    [Fact]
    public void AppsList_SameNameInTwoChannels_IsTwoItems()
    {
        var root = TestWire.SerializeToElement(AppsAnalyticsHelper.BuildAppsListResponse(MixedChannelRows(), days: 30));
        var apps = root.GetProperty("apps").EnumerateArray().ToList();

        Assert.Equal(2, apps.Count);
        var ime = apps.Single(a => a.GetProperty("source").GetString() == AppInstallSources.Ime);
        var rj = apps.Single(a => a.GetProperty("source").GetString() == AppInstallSources.RealmJoin);
        Assert.Equal(1, ime.GetProperty("totalInstalls").GetInt32());
        Assert.Equal(2, rj.GetProperty("totalInstalls").GetInt32());
        Assert.Equal(1, rj.GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task Analytics_And_Sessions_FilterByChannel()
    {
        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(r => r.GetSessionAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((SessionSummary?)null);

        var rj = await AppsAnalyticsHelper.BuildAnalyticsResponseAsync(
            MixedChannelRows(), sessionRepo.Object, "GitHub Desktop", AppInstallSources.RealmJoin, days: 30);
        Assert.Equal(AppInstallSources.RealmJoin, rj.Source);
        Assert.Equal(2, rj.Summary.TotalInstalls);
        Assert.Equal(1, rj.Summary.Failed);

        var ime = await AppsAnalyticsHelper.BuildAnalyticsResponseAsync(
            MixedChannelRows(), sessionRepo.Object, "GitHub Desktop", AppInstallSources.Ime, days: 30);
        Assert.Equal(AppInstallSources.Ime, ime.Source);
        Assert.Equal(1, ime.Summary.TotalInstalls);

        var sessions = await AppsAnalyticsHelper.BuildSessionsResponseAsync(
            MixedChannelRows(), sessionRepo.Object, "GitHub Desktop", AppInstallSources.RealmJoin, days: 30,
            statusFilter: "all", modelFilter: null, versionFilter: null, offset: 0, limit: 50);
        Assert.Equal(2, sessions.Total);
    }

    [Fact]
    public void AppMetrics_GroupsPerChannel()
    {
        var payload = MetricsMath.BuildAppMetricsPayload(MixedChannelRows());

        Assert.Equal(2, payload.TotalApps);
        var failing = Assert.Single(payload.TopFailingApps);
        Assert.Equal(AppInstallSources.RealmJoin, failing.Source);
        Assert.Equal("GitHub Desktop", failing.AppName);
    }

    [Theory]
    [InlineData(null, true, "ime")]
    [InlineData("", true, "ime")]
    [InlineData("realmjoin", true, "realmjoin")]
    [InlineData(" RealmJoin ", true, "realmjoin")]
    [InlineData("office-c2r", false, "office-c2r")]
    public void SourceQueryParam(string? raw, bool valid, string expected)
    {
        Assert.Equal(valid, AppsAnalyticsHelper.TryParseSourceQueryParam(raw, out var source));
        Assert.Equal(expected, source);
    }
}

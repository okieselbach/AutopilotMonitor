using System.Text.Json;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Functions.Apps;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// F1 PR1 (insights spec §F1, source-data audit Q2/Q3): app identity on the name-keyed
/// summary row and the ESP-blocking join.
///
/// Ingest side: <c>appId</c> is adopted from the app events; a second appId under the same
/// name flags the row (<see cref="AppInstallSummary.AppIdCollision"/>) — in-batch and
/// cross-batch. Store side: sentinel-gated columns + sticky collision. Join side:
/// <see cref="EspBlockingSets"/> is positive evidence only, and
/// <see cref="TableStorageService.ShouldStampEspBlocking"/> never stamps collision rows,
/// id-less rows, or non-members. Aggregate side: collision rows leave every per-app fleet
/// aggregate with a disclosed exclusion count.
/// </summary>
public class AppInstallIdentityAndEspBlockingTests
{
    private static readonly DateTime T0 = new(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc);

    private const string IdA = "aaaaaaaa-1111-2222-3333-444444444444";
    private const string IdB = "bbbbbbbb-1111-2222-3333-444444444444";

    // ── ingest fold: appId adoption + in-batch collision ────────────────────

    private static Dictionary<string, AppInstallAggregationState> Aggregate(params EnrollmentEvent[] events)
    {
        var summaries = new Dictionary<string, AppInstallAggregationState>(StringComparer.OrdinalIgnoreCase);
        foreach (var evt in events)
            EventIngestProcessor.AggregateAppInstallEvent(evt, "tenant", "session", summaries);
        return summaries;
    }

    private static EnrollmentEvent AppEvent(string eventType, string? appId = null, string appName = "App")
    {
        var evt = new EnrollmentEvent
        {
            EventType = eventType,
            Timestamp = T0,
            Data = new Dictionary<string, object> { ["appName"] = appName },
        };
        if (appId != null) evt.Data["appId"] = appId;
        return evt;
    }

    [Fact]
    public void Ingest_AdoptsAppId_FromFirstObservingEvent()
    {
        var s = Aggregate(
            AppEvent("app_install_started", IdA),
            AppEvent("app_install_completed", IdA))["App"].Summary;

        Assert.Equal(IdA, s.AppId);
        Assert.False(s.AppIdCollision);
    }

    [Fact]
    public void Ingest_SameName_DifferentAppId_FlagsCollision_FirstSeenIdWins()
    {
        // Device- + user-scope assignment of the same display name in one batch (audit Q3:
        // observed in production as two "Company Portal" appIds in one session).
        var s = Aggregate(
            AppEvent("app_install_started", IdA),
            AppEvent("app_install_started", IdB))["App"].Summary;

        Assert.True(s.AppIdCollision);
        Assert.Equal(IdA, s.AppId);
    }

    [Fact]
    public void Ingest_AppIdCaseDrift_IsNotACollision()
    {
        var s = Aggregate(
            AppEvent("app_install_started", IdA),
            AppEvent("app_install_completed", IdA.ToUpperInvariant()))["App"].Summary;

        Assert.False(s.AppIdCollision);
    }

    [Fact]
    public void Ingest_EventsWithoutAppId_LeaveSentinelEmpty()
    {
        var s = Aggregate(AppEvent("app_install_completed"))["App"].Summary;
        Assert.Equal(string.Empty, s.AppId);
        Assert.False(s.AppIdCollision);
    }

    // ── reconcile: cross-batch identity ─────────────────────────────────────

    [Fact]
    public void Reconcile_AdoptsStoredAppId_WhenIncomingBatchHasNone()
    {
        var existing = new TableEntity("tenant", "session_App") { ["AppId"] = IdA };
        var summary = new AppInstallSummary { AppName = "App", StartedAt = T0 };

        TableStorageService.ReconcileAppInstallSummaryWithExisting(summary, existing);

        Assert.Equal(IdA, summary.AppId);
        Assert.False(summary.AppIdCollision);
    }

    [Fact]
    public void Reconcile_CrossBatchDifferentAppId_FlagsCollision_StoredIdWins()
    {
        var existing = new TableEntity("tenant", "session_App") { ["AppId"] = IdA };
        var summary = new AppInstallSummary { AppName = "App", AppId = IdB, StartedAt = T0 };

        TableStorageService.ReconcileAppInstallSummaryWithExisting(summary, existing);

        Assert.True(summary.AppIdCollision);
        Assert.Equal(IdA, summary.AppId); // first-seen (stored) identity wins, matching the fold rule
    }

    [Fact]
    public void Reconcile_CollisionIsSticky_AndEspBlockingVerdictIsAdopted()
    {
        var existing = new TableEntity("tenant", "session_App")
        {
            ["AppId"] = IdA,
            ["AppIdCollision"] = true,
            ["EspBlocking"] = true,
        };
        var summary = new AppInstallSummary { AppName = "App", AppId = IdA, StartedAt = T0 };

        TableStorageService.ReconcileAppInstallSummaryWithExisting(summary, existing);

        Assert.True(summary.AppIdCollision);       // one-way sticky
        Assert.True(summary.EspBlocking);          // terminal-path verdict survives late batches
    }

    // ── entity builder: sentinel gating ─────────────────────────────────────

    [Fact]
    public void EntityBuilder_IdentityColumns_AreSentinelGated()
    {
        var empty = TableStorageService.BuildAppInstallSummaryEntity(
            new AppInstallSummary { AppName = "App" }, "rk");
        // Absent columns → Merge-mode preserves prior values / stays unknown.
        Assert.False(empty.ContainsKey("AppId"));
        Assert.False(empty.ContainsKey("EspBlocking"));
        Assert.False(empty.ContainsKey("AppIdCollision"));

        var full = TableStorageService.BuildAppInstallSummaryEntity(
            new AppInstallSummary { AppName = "App", AppId = IdA, EspBlocking = true, AppIdCollision = true }, "rk");
        Assert.Equal(IdA, full.GetString("AppId"));
        Assert.True(full.GetBoolean("EspBlocking"));
        Assert.True(full.GetBoolean("AppIdCollision"));
    }

    // ── EspBlockingSets: positive-evidence parsing ──────────────────────────

    private static Dictionary<string, object> Lists(
        object? win32 = null, object? userWin32 = null, object? msi = null, object? pfn = null,
        long? win32Count = null)
    {
        var d = new Dictionary<string, object> { ["source"] = "registry_firstsync" };
        if (win32 != null) d["espTrackedWin32AppIds"] = win32;
        if (userWin32 != null) d["espTrackedUserWin32AppIds"] = userWin32;
        if (msi != null) d["espTrackedMsiProductCodes"] = msi;
        if (pfn != null) d["espTrackedModernAppPfns"] = pfn;
        if (win32Count.HasValue) d["espTrackedWin32Count"] = win32Count.Value;
        return d;
    }

    [Fact]
    public void Sets_PayloadWithoutListKeys_YieldsNull_NoListsIsNotEmptyLists()
    {
        Assert.Null(EspBlockingSets.FromEventData(null));
        Assert.Null(EspBlockingSets.FromEventData(Lists())); // probe found no Diagnostics key
    }

    [Fact]
    public void Sets_MembershipIsCaseInsensitive_AcrossAllFourLists()
    {
        // Storage rehydration shape: List<object> (EventDataNormalizer).
        var sets = EspBlockingSets.FromEventData(Lists(
            win32: new List<object> { IdA },
            msi: new List<object> { "{11111111-2222-3333-4444-555555555555}" },
            pfn: new List<object> { "Contoso.App_abc123" }))!;

        Assert.True(sets.Contains(IdA.ToUpperInvariant()));
        Assert.True(sets.Contains("{11111111-2222-3333-4444-555555555555}"));
        Assert.True(sets.Contains("Contoso.App_abc123"));
        Assert.False(sets.Contains(IdB));           // absent ⇒ unknown at the caller, never false here
        Assert.False(sets.Contains(null));
        Assert.Equal(3, sets.ListedCount);
        Assert.False(sets.IsTruncated);
    }

    [Fact]
    public void Sets_EmittedCountAboveListLength_MeansTruncated()
    {
        var sets = EspBlockingSets.FromEventData(Lists(
            win32: new List<object> { IdA }, win32Count: 60))!;

        Assert.True(sets.IsTruncated);
    }

    [Fact]
    public void Sets_UserScopeSubset_IsExposedForCoverageLabeling()
    {
        var sets = EspBlockingSets.FromEventData(Lists(
            win32: new List<object> { IdA, IdB },
            userWin32: new List<object> { IdB }))!;

        Assert.Contains(IdB, sets.UserWin32AppIds);
        Assert.DoesNotContain(IdA, sets.UserWin32AppIds);
    }

    // ── terminal-path stamping predicate ────────────────────────────────────

    private static TableEntity Row(string? appId = null, bool? collision = null, bool? espBlocking = null)
    {
        var row = new TableEntity("tenant", "session_App");
        if (appId != null) row["AppId"] = appId;
        if (collision.HasValue) row["AppIdCollision"] = collision.Value;
        if (espBlocking.HasValue) row["EspBlocking"] = espBlocking.Value;
        return row;
    }

    [Fact]
    public void Stamp_OnlyPositiveMembers_NeverIdlessCollidedOrAlreadyStamped()
    {
        var sets = EspBlockingSets.FromEventData(Lists(win32: new List<object> { IdA }))!;

        Assert.True(TableStorageService.ShouldStampEspBlocking(Row(IdA), sets));
        Assert.False(TableStorageService.ShouldStampEspBlocking(Row(), sets));                    // no identity → unknown
        Assert.False(TableStorageService.ShouldStampEspBlocking(Row(IdB), sets));                 // absent ⇒ unknown, never false
        Assert.False(TableStorageService.ShouldStampEspBlocking(Row(IdA, collision: true), sets)); // ambiguous identity
        Assert.False(TableStorageService.ShouldStampEspBlocking(Row(IdA, espBlocking: true), sets)); // idempotent
    }

    // ── per-app fleet aggregates: collision rows leave, disclosed ───────────

    private static AppInstallSummary Summary(
        string appName, string status = "Succeeded", bool collision = false, int duration = 30)
        => new()
        {
            TenantId = "00000000-0000-0000-0000-000000000fa1",
            SessionId = "s-1",
            AppName = appName,
            Status = status,
            TerminalState = status == "Succeeded" ? "Installed" : "Error",
            DurationSeconds = duration,
            StartedAt = DateTime.UtcNow.AddHours(-2),
            AppIdCollision = collision,
        };

    [Fact]
    public void AppMetricsPayload_ExcludesCollisionRowsFromGroups_DisclosesCount()
    {
        var summaries = new List<AppInstallSummary>
        {
            Summary("Clean App"),
            Summary("Clean App"),
            Summary("Company Portal", collision: true),
            Summary("Company Portal", status: "Failed", collision: true),
        };

        var root = JsonSerializer.SerializeToElement(MetricsMath.BuildAppMetricsPayload(summaries));

        Assert.Equal(1, root.GetProperty("totalApps").GetInt32());          // collided name forms no group
        Assert.Equal(4, root.GetProperty("totalInstalls").GetInt32());      // raw row count unchanged
        Assert.Equal(2, root.GetProperty("totalCollisionExcluded").GetInt32());
        Assert.Empty(root.GetProperty("topFailingApps").EnumerateArray());  // the collided Failed row ranks nowhere
    }

    [Fact]
    public void AppsList_ExcludesCollisionRows_DisclosesCount()
    {
        var summaries = new List<AppInstallSummary>
        {
            Summary("Clean App"),
            Summary("Company Portal", collision: true),
        };

        var root = JsonSerializer.SerializeToElement(
            AppsAnalyticsHelper.BuildAppsListResponse(summaries, days: 30));

        var apps = root.GetProperty("apps").EnumerateArray().ToList();
        Assert.Single(apps);
        Assert.Equal("Clean App", apps[0].GetProperty("appName").GetString());
        Assert.Equal(2, root.GetProperty("totalInstalls").GetInt32());
        Assert.Equal(1, root.GetProperty("collisionExcluded").GetInt32());
    }

    [Fact]
    public async Task AppAnalytics_ExcludesCollisionRows_DisclosesCount()
    {
        var repo = new Mock<ISessionRepository>();
        repo.Setup(r => r.GetSessionAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((SessionSummary?)null);

        var summaries = new List<AppInstallSummary>
        {
            Summary("Company Portal"),
            Summary("Company Portal", status: "Failed", collision: true), // the second identity's failure
        };

        var root = JsonSerializer.SerializeToElement(await AppsAnalyticsHelper.BuildAnalyticsResponseAsync(
            summaries, repo.Object, "Company Portal", days: 30));

        Assert.Equal(1, root.GetProperty("collisionExcluded").GetInt32());
        var stats = root.GetProperty("summary");
        Assert.Equal(1, stats.GetProperty("totalInstalls").GetInt32());
        Assert.Equal(0, stats.GetProperty("failed").GetInt32());            // ambiguous failure not attributed
        Assert.Equal(0d, stats.GetProperty("failureRate").GetDouble());
    }

    [Fact]
    public async Task AppAnalytics_AllRowsCollided_ReturnsEmptyShape_WithDisclosure()
    {
        var repo = new Mock<ISessionRepository>();
        repo.Setup(r => r.GetSessionAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((SessionSummary?)null);

        var summaries = new List<AppInstallSummary> { Summary("Company Portal", collision: true) };

        var root = JsonSerializer.SerializeToElement(await AppsAnalyticsHelper.BuildAnalyticsResponseAsync(
            summaries, repo.Object, "Company Portal", days: 30));

        Assert.Equal(1, root.GetProperty("collisionExcluded").GetInt32());
        Assert.Equal(0, root.GetProperty("summary").GetProperty("totalInstalls").GetInt32());
    }
}

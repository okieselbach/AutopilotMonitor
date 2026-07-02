using System;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Audit #3 / Phase 1: <c>BuildSessionIndexEntity</c> is the single SessionsIndex projection manifest.
/// It must be a SUPERSET of every field any <c>MergeSessionIndexAsync</c> call site writes, otherwise a
/// StartedAt-shift full upsert (which rebuilds the index row from this projection) silently DROPS a
/// merged field until the next merge — the recurring drift bug (e.g. ab90423b). These tests lock the
/// projection so a future field added to a merge site but forgotten here is caught.
/// </summary>
public class SessionIndexProjectionTests
{
    private const string TenantId  = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";
    private const string IndexRowKey = "2516000000000000000_22222222-2222-2222-2222-222222222222";

    private static readonly DateTime StartedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BuildSessionIndexEntity_carries_all_merge_union_fields()
    {
        // A fully-populated session row (the primary Sessions truth) — every field a merge site can write.
        var session = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"] = new DateTimeOffset(StartedAt),
            ["Status"] = "Failed",
            ["DeviceName"] = "PC-1",
            ["EventCount"] = 42,
            // The fields that previously drifted (merged but absent from the upsert projection):
            ["AdminMarkedAction"] = "failed",
            ["ImeAgentVersion"] = "1.2.3.4",
            ["FailureSource"] = "ime",
            ["FailureSnapshotJson"] = "{\"x\":1}",
            ["StalledAt"] = new DateTimeOffset(StartedAt.AddMinutes(5)),
            ["PlatformScriptCount"] = 2,
            ["RemediationScriptCount"] = 3,
            ["RebootCount"] = 4,
            ["ExcessiveEventsAlerted"] = true,
            ["ExcessiveEventsAutoActioned"] = true,
            // Self-deploying/kiosk profile marker (session 320b3bf7) — search-filterable.
            ["IsSelfDeployingProfile"] = true,
        };

        var idx = TableStorageService.BuildSessionIndexEntity(session, IndexRowKey, StartedAt);

        Assert.Equal(SessionId, idx.GetString("SessionId"));
        Assert.Equal("Failed", idx.GetString("Status"));
        Assert.Equal(42, idx.GetInt32("EventCount"));
        // Previously-dropped fields are now part of the projection (StartedAt-shift upsert preserves them):
        Assert.Equal("failed", idx.GetString("AdminMarkedAction"));
        Assert.Equal("1.2.3.4", idx.GetString("ImeAgentVersion"));
        Assert.Equal("ime", idx.GetString("FailureSource"));
        Assert.Equal("{\"x\":1}", idx.GetString("FailureSnapshotJson"));
        Assert.True(idx.GetDateTimeOffset("StalledAt").HasValue);
        Assert.Equal(2, idx.GetInt32("PlatformScriptCount"));
        Assert.Equal(3, idx.GetInt32("RemediationScriptCount"));
        // RebootCount is a search-filterable column (rebootCountMin/Max push an OData filter on the
        // index), so it MUST survive a StartedAt-shift full upsert.
        Assert.Equal(4, idx.GetInt32("RebootCount"));
        Assert.True(idx.GetBoolean("ExcessiveEventsAlerted"));
        Assert.True(idx.GetBoolean("ExcessiveEventsAutoActioned"));
        Assert.True(idx.GetBoolean("IsSelfDeployingProfile"));
    }

    [Fact]
    public void BuildSessionIndexEntity_defaults_absent_optional_fields()
    {
        // A minimal new session: optional strings/timestamps absent, counts/flags default.
        var session = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"] = new DateTimeOffset(StartedAt),
            ["Status"] = "InProgress",
        };

        var idx = TableStorageService.BuildSessionIndexEntity(session, IndexRowKey, StartedAt);

        // Nullable-if-present fields are simply absent (mapper reads them as defaults).
        Assert.False(idx.ContainsKey("AdminMarkedAction"));
        Assert.False(idx.ContainsKey("FailureSource"));
        Assert.False(idx.ContainsKey("StalledAt"));
        // Always-present counts/flags mirror the Sessions defaults.
        Assert.Equal(0, idx.GetInt32("PlatformScriptCount"));
        Assert.Equal(0, idx.GetInt32("RemediationScriptCount"));
        Assert.Equal(0, idx.GetInt32("RebootCount"));
        Assert.False(idx.GetBoolean("ExcessiveEventsAlerted"));
        Assert.False(idx.GetBoolean("IsSelfDeployingProfile"));
        Assert.Equal(string.Empty, idx.GetString("ImeAgentVersion"));
    }
}

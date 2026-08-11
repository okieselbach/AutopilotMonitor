using System;
using System.Collections.Generic;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Shared.Models;
using Azure.Data.Tables;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Round-trip pins for the SessionAnnotations Store/Map pair (memory:
/// feedback_table_storage_serialization — every new field in <see cref="SessionAnnotation"/>
/// MUST be exercised in Store+Map so silent drops surface here), plus the RowKey and
/// OData-filter builders.
/// </summary>
public class TableSessionAnnotationRepositoryTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    private static SessionAnnotation FullAnnotation() => new()
    {
        TenantId = TenantId,
        SessionId = SessionId,
        Lane = AnnotationLanes.TenantAdmin,
        Verdict = AnnotationVerdicts.RootCauseConfirmed,
        Note = "Root cause matches what we saw on the device.",
        AuthorUpn = "alice@contoso.com",
        AuthorDisplayName = "Alice Admin",
        CreatedByUpn = "bob@contoso.com",
        CreatedAtUtc = new DateTime(2026, 8, 1, 10, 30, 0, DateTimeKind.Utc),
        UpdatedAtUtc = new DateTime(2026, 8, 11, 9, 15, 0, DateTimeKind.Utc),
        RuleIds = new List<string> { "ANALYZE-ESP-001", "ANALYZE-CORR-003" },
    };

    [Fact]
    public void Roundtrip_all_fields_survive_store_and_map()
    {
        var original = FullAnnotation();

        var mapped = TableSessionAnnotationRepository.MapAnnotation(
            TableSessionAnnotationRepository.StoreAnnotation(original));

        Assert.Equal(original.TenantId, mapped.TenantId);
        Assert.Equal(original.SessionId, mapped.SessionId);
        Assert.Equal(original.Lane, mapped.Lane);
        Assert.Equal(original.Verdict, mapped.Verdict);
        Assert.Equal(original.Note, mapped.Note);
        Assert.Equal(original.AuthorUpn, mapped.AuthorUpn);
        Assert.Equal(original.AuthorDisplayName, mapped.AuthorDisplayName);
        Assert.Equal(original.CreatedByUpn, mapped.CreatedByUpn);
        Assert.Equal(original.CreatedAtUtc, mapped.CreatedAtUtc);
        Assert.Equal(original.UpdatedAtUtc, mapped.UpdatedAtUtc);
        Assert.Equal(original.RuleIds, mapped.RuleIds);
    }

    [Fact]
    public void Roundtrip_null_verdict_and_note_stay_null()
    {
        var original = FullAnnotation();
        original.Verdict = null;
        original.Note = null;
        original.RuleIds = new List<string>();

        var mapped = TableSessionAnnotationRepository.MapAnnotation(
            TableSessionAnnotationRepository.StoreAnnotation(original));

        Assert.Null(mapped.Verdict);
        Assert.Null(mapped.Note);
        Assert.Empty(mapped.RuleIds);
    }

    [Fact]
    public void Store_uses_tenant_pk_and_sessionId_lane_rk()
    {
        var entity = TableSessionAnnotationRepository.StoreAnnotation(FullAnnotation());

        Assert.Equal(TenantId, entity.PartitionKey);
        Assert.Equal($"{SessionId}_{AnnotationLanes.TenantAdmin}", entity.RowKey);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"truncated\":")]
    [InlineData("")]
    [InlineData(null)]
    public void Map_corrupt_or_missing_ruleIds_degrades_to_empty_list(string? corruptJson)
    {
        var entity = TableSessionAnnotationRepository.StoreAnnotation(FullAnnotation());
        entity["RuleIdsJson"] = corruptJson;

        var mapped = TableSessionAnnotationRepository.MapAnnotation(entity);

        Assert.Empty(mapped.RuleIds);
    }

    [Fact]
    public void Map_tolerates_minimal_legacy_entity()
    {
        // A row carrying only PK/RK (e.g. hand-repaired) must map without throwing.
        var entity = new TableEntity(TenantId, $"{SessionId}_{AnnotationLanes.Operator}");

        var mapped = TableSessionAnnotationRepository.MapAnnotation(entity);

        Assert.Equal(TenantId, mapped.TenantId);
        Assert.Equal(string.Empty, mapped.SessionId);
        Assert.Null(mapped.Verdict);
        Assert.Null(mapped.Note);
        Assert.Empty(mapped.RuleIds);
    }

    // ── query filter builder ────────────────────────────────────────────────

    [Fact]
    public void BuildQueryFilter_no_filters_returns_null()
    {
        Assert.Null(TableSessionAnnotationRepository.BuildQueryFilter(null, null, null, null, null));
    }

    [Fact]
    public void BuildQueryFilter_combines_all_clauses_with_and()
    {
        var filter = TableSessionAnnotationRepository.BuildQueryFilter(
            TenantId, AnnotationLanes.GlobalAdmin, AnnotationVerdicts.AnalysisWrong,
            new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(
            $"PartitionKey eq '{TenantId}'" +
            " and Lane eq 'globaladmin'" +
            " and Verdict eq 'analysis_wrong'" +
            " and UpdatedAtUtc ge datetime'2026-08-01T00:00:00Z'" +
            " and UpdatedAtUtc lt datetime'2026-08-11T00:00:00Z'",
            filter);
    }

    [Fact]
    public void BuildQueryFilter_escapes_single_quotes()
    {
        var filter = TableSessionAnnotationRepository.BuildQueryFilter("ten'ant", null, null, null, null);

        Assert.Equal("PartitionKey eq 'ten''ant'", filter);
    }
}

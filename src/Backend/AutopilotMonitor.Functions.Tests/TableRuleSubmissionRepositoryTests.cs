using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Round-trip pins for the RuleSubmissions Store/Map pair (memory:
/// feedback_table_storage_serialization — every new field in <see cref="RuleSubmission"/>
/// MUST be exercised in Store+Map so silent drops surface here), plus the key and filter shapes.
/// </summary>
public class TableRuleSubmissionRepositoryTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    private static RuleSubmission Full() => new()
    {
        SubmissionId = "a1b2c3d4e5f6",
        BatchId = "0f0e0d0c0b0a",
        TenantId = TenantId,
        RuleKind = RuleSubmissionKinds.Analyze,
        SourceRuleId = "ANALYZE-CUSTOM-003",
        Title = "Proxy PAC unreachable",
        Category = "network",
        RuleJson = "{\"ruleId\":\"ANALYZE-CUSTOM-003\",\"title\":\"Proxy PAC unreachable\"}",
        Comment = "Fires when the PAC download fails during ESP.",
        SubmittedBy = "alice@contoso.com",
        SubmittedByName = "Alice Admin",
        ContactEmail = "alice.reply@contoso.com",
        AttributionMode = RuleAttributionModes.Person,
        AttributionName = "Alice A.",
        SubmittedAt = new DateTime(2026, 9, 9, 10, 30, 0, DateTimeKind.Utc),
        Status = RuleSubmissionStatuses.Approved,
        ValidationFindings = new List<RuleSubmissionFinding>
        {
            new() { Level = "info", Message = "Derived from template ANALYZE-SEC-004" },
        },
        SourceFireStats = new RuleSubmissionFireStats { Days = 30, FireCount = 42, SessionsEvaluated = 310, EvaluationCount = 312 },
        ReviewedBy = "ga@example.com",
        ReviewedAt = new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc),
        ReviewComment = "Taken, generalised for the community.",
        WillBeAdapted = true,
        PublishedRuleId = "ANALYZE-NET-002",
        DerivedFromTemplateRuleId = "ANALYZE-SEC-004",
    };

    [Fact]
    public void Roundtrip_all_fields_survive_store_and_map()
    {
        var original = Full();

        var mapped = TableRuleSubmissionRepository.MapSubmission(
            TableRuleSubmissionRepository.StoreSubmission(original));

        Assert.Equal(original.SubmissionId, mapped.SubmissionId);
        Assert.Equal(original.BatchId, mapped.BatchId);
        Assert.Equal(original.TenantId, mapped.TenantId);
        Assert.Equal(original.RuleKind, mapped.RuleKind);
        Assert.Equal(original.SourceRuleId, mapped.SourceRuleId);
        Assert.Equal(original.Title, mapped.Title);
        Assert.Equal(original.Category, mapped.Category);
        Assert.Equal(original.RuleJson, mapped.RuleJson);
        Assert.Equal(original.Comment, mapped.Comment);
        Assert.Equal(original.SubmittedBy, mapped.SubmittedBy);
        Assert.Equal(original.SubmittedByName, mapped.SubmittedByName);
        Assert.Equal(original.ContactEmail, mapped.ContactEmail);
        Assert.Equal(original.AttributionMode, mapped.AttributionMode);
        Assert.Equal(original.AttributionName, mapped.AttributionName);
        Assert.Equal(original.SubmittedAt, mapped.SubmittedAt);
        Assert.Equal(original.Status, mapped.Status);
        Assert.Single(mapped.ValidationFindings);
        Assert.Equal("info", mapped.ValidationFindings[0].Level);
        Assert.Equal(original.ValidationFindings[0].Message, mapped.ValidationFindings[0].Message);
        Assert.NotNull(mapped.SourceFireStats);
        Assert.Equal(42, mapped.SourceFireStats!.FireCount);
        Assert.Equal(310, mapped.SourceFireStats.SessionsEvaluated);
        Assert.Equal(312, mapped.SourceFireStats.EvaluationCount);
        Assert.Equal(30, mapped.SourceFireStats.Days);
        Assert.Equal(original.ReviewedBy, mapped.ReviewedBy);
        Assert.Equal(original.ReviewedAt, mapped.ReviewedAt);
        Assert.Equal(original.ReviewComment, mapped.ReviewComment);
        Assert.True(mapped.WillBeAdapted);
        Assert.Equal(original.PublishedRuleId, mapped.PublishedRuleId);
        Assert.Equal(original.DerivedFromTemplateRuleId, mapped.DerivedFromTemplateRuleId);
    }

    [Fact]
    public void Roundtrip_pending_row_keeps_nulls_null()
    {
        var original = Full();
        original.Status = RuleSubmissionStatuses.Pending;
        original.Comment = null;
        original.ContactEmail = null;
        original.SourceFireStats = null;
        original.ValidationFindings = new List<RuleSubmissionFinding>();
        original.ReviewedBy = null;
        original.ReviewedAt = null;
        original.ReviewComment = null;
        original.WillBeAdapted = false;
        original.PublishedRuleId = null;
        original.DerivedFromTemplateRuleId = null;

        var mapped = TableRuleSubmissionRepository.MapSubmission(
            TableRuleSubmissionRepository.StoreSubmission(original));

        Assert.Null(mapped.Comment);
        Assert.Null(mapped.ContactEmail);
        Assert.Null(mapped.SourceFireStats);
        Assert.Empty(mapped.ValidationFindings);
        Assert.Null(mapped.ReviewedBy);
        Assert.Null(mapped.ReviewedAt);
        Assert.Null(mapped.ReviewComment);
        Assert.False(mapped.WillBeAdapted);
        Assert.Null(mapped.PublishedRuleId);
        Assert.Null(mapped.DerivedFromTemplateRuleId);
    }

    [Fact]
    public void Store_uses_the_single_partition_and_an_inverted_tick_row_key()
    {
        var s = Full();
        var entity = TableRuleSubmissionRepository.StoreSubmission(s);

        Assert.Equal("submissions", entity.PartitionKey);
        Assert.Equal($"{RowKeyCodec.InvertedTicks(s.SubmittedAt)}_{s.SubmissionId}", entity.RowKey);
    }

    [Fact]
    public void Newer_submission_sorts_first()
    {
        var older = Full();
        var newer = Full();
        newer.SubmittedAt = older.SubmittedAt.AddMinutes(5);

        var olderKey = TableRuleSubmissionRepository.StoreSubmission(older).RowKey;
        var newerKey = TableRuleSubmissionRepository.StoreSubmission(newer).RowKey;

        Assert.True(string.CompareOrdinal(newerKey, olderKey) < 0);
    }

    [Fact]
    public void Map_tolerates_a_corrupt_json_column()
    {
        var entity = TableRuleSubmissionRepository.StoreSubmission(Full());
        entity["ValidationFindingsJson"] = "{not json";
        entity["SourceFireStatsJson"] = "[]";

        var mapped = TableRuleSubmissionRepository.MapSubmission(entity);

        Assert.Empty(mapped.ValidationFindings);
        Assert.Null(mapped.SourceFireStats);
    }

    // ── retention rule (shared by the sweep and this pin) ──

    private static readonly DateTime Now = new(2026, 12, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("withdrawn", 29, false)]
    [InlineData("withdrawn", 30, true)]
    [InlineData("declined", 89, false)]
    [InlineData("declined", 90, true)]
    [InlineData("pending", 400, false)]
    [InlineData("approved", 400, false)]
    public void Retention_expires_only_closed_rows_at_their_window(string status, int ageDays, bool expired)
    {
        var s = Full();
        s.Status = status;
        s.SubmittedAt = Now.AddDays(-ageDays);
        s.ReviewedAt = status == "declined" ? Now.AddDays(-ageDays) : null;

        Assert.Equal(expired, RuleSubmissionRetention.IsExpired(s, Now));
    }

    [Fact]
    public void Retention_of_a_declined_row_counts_from_the_decision_not_the_submission()
    {
        var s = Full();
        s.Status = RuleSubmissionStatuses.Declined;
        s.SubmittedAt = Now.AddDays(-200);
        s.ReviewedAt = Now.AddDays(-10);

        Assert.False(RuleSubmissionRetention.IsExpired(s, Now));
    }

    [Theory]
    [InlineData(null, null, "PartitionKey eq 'submissions'")]
    [InlineData("t'1", null, "PartitionKey eq 'submissions' and TenantId eq 't''1'")]
    [InlineData(null, "pending", "PartitionKey eq 'submissions' and Status eq 'pending'")]
    [InlineData("t1", "approved", "PartitionKey eq 'submissions' and TenantId eq 't1' and Status eq 'approved'")]
    public void Filter_escapes_and_composes(string? tenantId, string? status, string expected)
    {
        Assert.Equal(expected, TableRuleSubmissionRepository.BuildFilter(tenantId, status));
    }
}

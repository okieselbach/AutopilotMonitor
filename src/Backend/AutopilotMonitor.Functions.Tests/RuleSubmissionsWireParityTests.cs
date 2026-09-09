using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Wire pins for the rule-submission responses (typed from the start — the anonymous side is
/// the literal a client sees, so a reordered or renamed property fails here first).
/// </summary>
public class RuleSubmissionsWireParityTests
{
    private static readonly DateTime T0 = new(2026, 9, 9, 10, 30, 0, DateTimeKind.Utc);

    private static RuleSubmissionItem Item() => new()
    {
        SubmissionId = "a1b2c3d4e5f6",
        BatchId = "0f0e0d0c0b0a",
        TenantId = "11111111-1111-1111-1111-111111111111",
        RuleKind = "analyze",
        SourceRuleId = "ANALYZE-CUSTOM-003",
        Title = "Proxy PAC unreachable",
        Category = "network",
        Comment = "Fires when the PAC download fails.",
        SubmittedBy = "alice@contoso.com",
        SubmittedByName = "Alice Admin",
        AttributionMode = "anonymous",
        AttributionName = "Community contribution",
        SubmittedAt = T0,
        Status = "approved",
        ValidationFindings = new List<RuleSubmissionFinding> { new() { Level = "info", Message = "Derived from template ANALYZE-SEC-004" } },
        SourceFireStats = new RuleSubmissionFireStats { Days = 30, FireCount = 42, SessionsEvaluated = 310, EvaluationCount = 312 },
        ReviewedBy = "ga@example.com",
        ReviewedAt = T0.AddDays(1),
        ReviewComment = "Taken.",
        WillBeAdapted = true,
        PublishedRuleId = "ANALYZE-NET-002",
        DerivedFromTemplateRuleId = "ANALYZE-SEC-004",
    };

    private static object ItemLiteral() => new
    {
        submissionId = "a1b2c3d4e5f6",
        batchId = "0f0e0d0c0b0a",
        tenantId = "11111111-1111-1111-1111-111111111111",
        ruleKind = "analyze",
        sourceRuleId = "ANALYZE-CUSTOM-003",
        title = "Proxy PAC unreachable",
        category = "network",
        comment = "Fires when the PAC download fails.",
        submittedBy = "alice@contoso.com",
        submittedByName = "Alice Admin",
        attributionMode = "anonymous",
        attributionName = "Community contribution",
        submittedAt = T0,
        status = "approved",
        validationFindings = new List<object> { new { level = "info", message = "Derived from template ANALYZE-SEC-004" } },
        sourceFireStats = new { days = 30, fireCount = 42, sessionsEvaluated = 310, evaluationCount = 312 },
        reviewedBy = "ga@example.com",
        reviewedAt = T0.AddDays(1),
        reviewComment = "Taken.",
        willBeAdapted = true,
        publishedRuleId = "ANALYZE-NET-002",
        derivedFromTemplateRuleId = "ANALYZE-SEC-004",
    };

    [Fact]
    public void Submit_response_shape()
    {
        ApiResponseWireParityTests.AssertWireIdentical(
            new { success = true, message = "Rule submitted for review.", batchId = "0f0e0d0c0b0a", submissions = new List<object> { ItemLiteral() } },
            new SubmitRuleSubmissionsResponse { Success = true, Message = "Rule submitted for review.", BatchId = "0f0e0d0c0b0a", Submissions = new List<RuleSubmissionItem> { Item() } });
    }

    [Fact]
    public void List_nonPaged_has_no_nextLink_key()
    {
        ApiResponseWireParityTests.AssertWireIdentical(
            new { success = true, count = 1, submissions = new List<object> { ItemLiteral() } },
            new RuleSubmissionListResponse { Success = true, Count = 1, Submissions = new List<RuleSubmissionItem> { Item() } });
    }

    [Fact]
    public void List_paged_carries_nextLink()
    {
        ApiResponseWireParityTests.AssertWireIdentical(
            new { success = true, count = 1, submissions = new List<object> { ItemLiteral() }, nextLink = "/api/global/rule-submissions?pageSize=20&continuation=abc" },
            new RuleSubmissionListResponse { Success = true, Count = 1, Submissions = new List<RuleSubmissionItem> { Item() }, NextLink = "/api/global/rule-submissions?pageSize=20&continuation=abc" });
    }

    [Fact]
    public void List_last_page_drops_the_nextLink_key()
    {
        ApiResponseWireParityTests.AssertWireIdentical(
            new { success = true, count = 0, submissions = new List<object>() },
            new RuleSubmissionListResponse { Success = true, Count = 0, Submissions = new List<RuleSubmissionItem>(), NextLink = null });
    }

    [Fact]
    public void Detail_shape_with_analyze_rule_omits_the_gather_slot()
    {
        var rule = new AnalyzeRule
        {
            RuleId = "ANALYZE-CUSTOM-003", Title = "Proxy PAC unreachable", Description = "d", Severity = "warning", Category = "network",
            Explanation = "e", IsBuiltIn = false, Conditions = new List<RuleCondition>(),
        };
        var typed = new RuleSubmissionDetailResponse
        {
            Success = true,
            Submission = Item(),
            AnalyzeRule = rule,
            LiveFireStats = new RuleSubmissionFireStats { Days = 30, FireCount = 1, SessionsEvaluated = 2, EvaluationCount = 3 },
            SuggestedPublishedRuleId = "ANALYZE-NET-002",
            RepoFile = new RuleSubmissionRepoFile { Path = "rules/analyze/ANALYZE-NET-002.json", Content = "{}\n" },
        };
        ApiResponseWireParityTests.AssertWireIdentical(
            new
            {
                success = true,
                submission = ItemLiteral(),
                analyzeRule = rule,
                liveFireStats = new { days = 30, fireCount = 1, sessionsEvaluated = 2, evaluationCount = 3 },
                suggestedPublishedRuleId = "ANALYZE-NET-002",
                repoFile = new { path = "rules/analyze/ANALYZE-NET-002.json", content = "{}\n" },
            },
            typed);
    }

    [Fact]
    public void Review_response_shape()
    {
        ApiResponseWireParityTests.AssertWireIdentical(
            new { success = true, message = "Submission approved.", submission = ItemLiteral() },
            new ReviewRuleSubmissionResponse { Success = true, Message = "Submission approved.", Submission = Item() });
    }
}

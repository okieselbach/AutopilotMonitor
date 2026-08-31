using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Wire-identity proof for the Rules function folder (anonymous-object → typed-DTO
/// migration). Each fact serializes the OLD anonymous literal exactly as it stood at the
/// call site (copied from the pre-migration code, filled with realistic sample values)
/// against the NEW DTO carrying the same values, via
/// <see cref="ApiResponseWireParityTests.AssertWireIdentical"/> — key names, key order and
/// key presence/absence (WhenWritingNull) must match ordinally. Nullable slots additionally
/// get a null case proving the key vanishes identically on both sides.
/// </summary>
public class RulesWireParityTests
{
    // ---- GetAnalyzeRules / GetGlobalAnalyzeRules -----------------------------------------

    [Fact]
    public void AnalyzeRuleListResponse_matches_the_rules_listing_shape()
    {
        var rules = new List<AnalyzeRule>
        {
            new AnalyzeRule { RuleId = "ANALYZE-NET-001" },
            new AnalyzeRule { RuleId = "ANALYZE-APP-017" },
        };

        AssertParity(
            new { success = true, rules },
            new AnalyzeRuleListResponse { Success = true, Rules = rules });
    }

    // ---- GetGatherRules / GetGlobalGatherRules -------------------------------------------

    [Fact]
    public void GatherRuleListResponse_matches_the_rules_listing_shape()
    {
        var rules = new List<GatherRule>
        {
            new GatherRule { RuleId = "GATHER-LOG-001", Author = "Contoso Admin" },
        };

        AssertParity(
            new { success = true, rules },
            new GatherRuleListResponse { Success = true, Rules = rules });
    }

    // ---- GetImeLogPatterns ---------------------------------------------------------------

    [Fact]
    public void ImeLogPatternListResponse_matches_the_patterns_listing_shape()
    {
        var patterns = new List<ImeLogPattern>
        {
            new ImeLogPattern { PatternId = "IME-PS-AGENT-OUTPUT" },
        };

        AssertParity(
            new { success = true, patterns },
            new ImeLogPatternListResponse { Success = true, Patterns = patterns });
    }

    // ---- Create/Update/Delete rule + UpdateImeLogPattern (dual success/message sites) ----

    [Fact]
    public void Rule_mutation_acknowledgements_match_SuccessMessageResponse_on_both_branches()
    {
        foreach (var success in new[] { true, false })
        {
            AssertParity(
                new { success, message = success ? "Rule created" : "Failed to create rule" },
                new SuccessMessageResponse { Success = success, Message = success ? "Rule created" : "Failed to create rule" });

            AssertParity(
                new { success, message = success ? "Rule updated" : "Failed to update rule" },
                new SuccessMessageResponse { Success = success, Message = success ? "Rule updated" : "Failed to update rule" });

            AssertParity(
                new { success, message = success ? "Rule deleted" : "Failed to delete rule" },
                new SuccessMessageResponse { Success = success, Message = success ? "Rule deleted" : "Failed to delete rule" });

            AssertParity(
                new { success, message = success ? "Global pattern updated" : "Failed to update global pattern" },
                new SuccessMessageResponse { Success = success, Message = success ? "Global pattern updated" : "Failed to update global pattern" });
        }
    }

    // ---- CreateAnalyzeRuleFromTemplate ---------------------------------------------------

    [Fact]
    public void CreateAnalyzeRuleFromTemplateResponse_matches_the_created_rule_shape()
    {
        var newRule = new AnalyzeRule { RuleId = "ANALYZE-CUSTOM-0001" };

        AssertParity(
            new { success = true, rule = newRule, message = "Custom rule created from template" },
            new CreateAnalyzeRuleFromTemplateResponse { Success = true, Rule = newRule, Message = "Custom rule created from template" });
    }

    // ---- DryRunAnalyzeRule ---------------------------------------------------------------

    [Fact]
    public void DryRunAnalyzeRuleResponse_matches_the_dry_run_trace_shape()
    {
        var sessionId = "7c1e2a90-1111-4b7c-a1d2-52f3bbbb0001";
        var result = new RuleDryRun
        {
            Verdict = RuleDryRunVerdict.Fired,
            EventCount = 42,
            BaseConfidence = 50,
            FinalConfidence = 80,
            ConfidenceThreshold = 60,
            WouldMarkSessionAsFailed = false,
            MatchedConditions = new Dictionary<string, object>
            {
                ["timeout_seen"] = new { eventId = "evt-0001", value = "timeout" },
            },
        };
        result.Conditions.Add(new RuleDryRunCondition
        {
            Signal = "timeout_seen",
            Source = "event_data",
            EventType = "app_install_failed",
            Required = true,
            Matched = true,
            Evidence = new Dictionary<string, object> { ["field"] = "errorCode" },
            MatchingEventCount = 3,
        });
        result.ConfidenceFactors.Add(new RuleDryRunFactor
        {
            Signal = "timeout_seen",
            Condition = "count >= 2",
            Weight = 30,
            Matched = true,
        });

        AssertParity(
            new { success = true, sessionId, result },
            new DryRunAnalyzeRuleResponse { Success = true, SessionId = sessionId, Result = result });
    }

    // ---- GetRuleResults ------------------------------------------------------------------

    [Fact]
    public void GetRuleResultsResponse_matches_the_analysis_shape_with_persist_failures()
    {
        var sessionId = "7c1e2a90-2222-4b7c-a1d2-52f3bbbb0002";
        var results = new List<RuleResult>
        {
            new RuleResult
            {
                ResultId = "9d0f1a2b-0001-4e0e-8888-52f3bbbb0003",
                SessionId = sessionId,
                TenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d",
                RuleId = "ANALYZE-NET-001",
                RuleTitle = "Network timeout during ESP",
                Severity = "critical",
                Category = "network",
                ConfidenceScore = 90,
                Explanation = "sample",
                DetectedAt = new DateTime(2026, 8, 30, 11, 0, 0, DateTimeKind.Utc),
            },
            new RuleResult
            {
                ResultId = "9d0f1a2b-0002-4e0e-8888-52f3bbbb0004",
                SessionId = sessionId,
                TenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d",
                RuleId = "ANALYZE-APP-017",
                RuleTitle = "Resolved finding",
                Severity = "warning",
                Category = "apps",
                ConfidenceScore = 70,
                Explanation = "sample",
                DetectedAt = new DateTime(2026, 8, 30, 11, 5, 0, DateTimeKind.Utc),
                ResolvedAt = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc),
            },
        };
        var openResults = results.Where(r => r.ResolvedAt == null).ToList();
        var persistFailureRuleIds = new List<string> { "ANALYZE-NET-001" };

        AssertParity(
            new
            {
                success = persistFailureRuleIds.Count == 0,
                sessionId,
                results,
                totalIssues = openResults.Count,
                criticalCount = openResults.Count(r => r.Severity == "critical"),
                highCount = openResults.Count(r => r.Severity == "high"),
                warningCount = openResults.Count(r => r.Severity == "warning"),
                persistFailureCount = persistFailureRuleIds.Count,
                persistFailureRuleIds = persistFailureRuleIds.Count > 0 ? persistFailureRuleIds : null
            },
            new GetRuleResultsResponse
            {
                Success = persistFailureRuleIds.Count == 0,
                SessionId = sessionId,
                Results = results,
                TotalIssues = openResults.Count,
                CriticalCount = openResults.Count(r => r.Severity == "critical"),
                HighCount = openResults.Count(r => r.Severity == "high"),
                WarningCount = openResults.Count(r => r.Severity == "warning"),
                PersistFailureCount = persistFailureRuleIds.Count,
                PersistFailureRuleIds = persistFailureRuleIds.Count > 0 ? persistFailureRuleIds : null
            });
    }

    [Fact]
    public void GetRuleResultsResponse_omits_persistFailureRuleIds_on_the_happy_path()
    {
        var sessionId = "7c1e2a90-3333-4b7c-a1d2-52f3bbbb0005";
        var results = new List<RuleResult>();
        var openResults = results.Where(r => r.ResolvedAt == null).ToList();
        var persistFailureRuleIds = new List<string>();

        AssertParity(
            new
            {
                success = persistFailureRuleIds.Count == 0,
                sessionId,
                results,
                totalIssues = openResults.Count,
                criticalCount = openResults.Count(r => r.Severity == "critical"),
                highCount = openResults.Count(r => r.Severity == "high"),
                warningCount = openResults.Count(r => r.Severity == "warning"),
                persistFailureCount = persistFailureRuleIds.Count,
                persistFailureRuleIds = persistFailureRuleIds.Count > 0 ? persistFailureRuleIds : null
            },
            new GetRuleResultsResponse
            {
                Success = true,
                SessionId = sessionId,
                Results = results,
                TotalIssues = 0,
                CriticalCount = 0,
                HighCount = 0,
                WarningCount = 0,
                PersistFailureCount = 0,
                PersistFailureRuleIds = null,
            });
    }

    // ---- ReseedImeLogPatterns ------------------------------------------------------------

    [Fact]
    public void ReseedImeLogPatternsResponse_matches_the_reseed_summary_shape()
    {
        var deleted = 79;
        var written = 81;

        AssertParity(
            new
            {
                success = true,
                message = $"Reseed complete: {deleted} old patterns removed, {written} patterns written from code.",
                deleted,
                written
            },
            new ReseedImeLogPatternsResponse
            {
                Success = true,
                Message = $"Reseed complete: {deleted} old patterns removed, {written} patterns written from code.",
                Deleted = deleted,
                Written = written
            });
    }

    // ---- TestLogPattern ------------------------------------------------------------------

    [Fact]
    public void TestLogPatternResponse_matches_the_pattern_test_shape()
    {
        var isTextMode = false;
        var result = new LogPatternTestResult
        {
            MatchCount = 1,
            ParseFailureCount = 0,
            TimeoutCount = 0,
        };
        result.Lines.Add(new LogPatternLineResult
        {
            LineNumber = 1,
            Outcome = "matched",
            Groups = new Dictionary<string, string> { ["level"] = "ERROR", ["message"] = "disk full" },
            MatchedText = "ERROR: disk full",
            Component = "Installer",
            CmTraceType = 3,
            Message = "ERROR: disk full while unpacking",
        });
        // no_match row: every nullable slot stays null → the keys must vanish on both sides.
        result.Lines.Add(new LogPatternLineResult
        {
            LineNumber = 2,
            Outcome = "no_match",
        });
        result.Notes.Add("no line matched — remember logparser patterns are case-SENSITIVE (unlike analyze-rule regex conditions)");

        AssertParity(
            new { success = true, format = isTextMode ? "text" : "cmtrace", result },
            new TestLogPatternResponse { Success = true, Format = isTextMode ? "text" : "cmtrace", Result = result });
    }

    // ---- GetPreviewWhitelist -------------------------------------------------------------

    [Fact]
    public void GetPreviewWhitelistResponse_matches_the_approved_tenant_listing_shape()
    {
        var approved = new List<PreviewWhitelistEntity>
        {
            new PreviewWhitelistEntity
            {
                PartitionKey = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d",
                RowKey = "approved",
                ApprovedAt = new DateTime(2026, 8, 30, 9, 30, 0, DateTimeKind.Utc),
                ApprovedBy = "admin@contoso.com",
            },
        };

        AssertParity(
            new { tenants = approved },
            new GetPreviewWhitelistResponse { Tenants = approved });
    }

    // ---- GetPreviewNotificationEmail -----------------------------------------------------

    [Fact]
    public void GetPreviewNotificationEmailResponse_matches_the_coalesced_email_shape()
    {
        string? email = "ops@fabrikam.com";
        AssertParity(
            new { email = email ?? "" },
            new GetPreviewNotificationEmailResponse { Email = email ?? "" });

        // No stored address: the site coalesces to "" — the key stays present.
        email = null;
        AssertParity(
            new { email = email ?? "" },
            new GetPreviewNotificationEmailResponse { Email = "" });
    }

    // ---- GetAllPreviewNotificationEmails -------------------------------------------------

    [Fact]
    public void GetAllPreviewNotificationEmailsResponse_matches_the_address_map_shape()
    {
        var emails = new Dictionary<string, string>
        {
            ["6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d"] = "ops@contoso.com",
            ["0a0a35a2-30b2-4f2f-9a1b-6d9f1a2b3c01"] = "it@fabrikam.com",
        };

        AssertParity(
            new { count = emails.Count, emails },
            new GetAllPreviewNotificationEmailsResponse { Count = emails.Count, Emails = emails });
    }

    private static void AssertParity(object anonymousLiteral, IApiResponse typed)
        => ApiResponseWireParityTests.AssertWireIdentical(anonymousLiteral, typed);
}

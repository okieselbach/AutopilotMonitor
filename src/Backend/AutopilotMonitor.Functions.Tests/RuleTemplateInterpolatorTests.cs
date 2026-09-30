using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// A stored RuleResult keeps the rule's {{token}} template. The web and the MCP server resolve
/// it when they render; a notification has no renderer, so the backend resolves it before the
/// text leaves. Three things are pinned here: the backend resolution equals the shared cases
/// every renderer runs, no placeholder reaches a notification for any built-in rule, and the
/// cut happens after the resolution and on a text boundary.
/// </summary>
public class RuleTemplateInterpolatorTests
{
    private const string TenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "0f1e2d3c-4b5a-4978-8a6b-5c4d3e2f1a0b";
    private const string FailedAppName = "Contoso Productivity Suite";

    // ── Shared cases (tests/fixtures/rule-template-interpolation) ─────────

    private static readonly Lazy<JObject> Fixture = new(() =>
    {
        var path = Path.Combine(FindRepoRoot(), "tests", "fixtures", "rule-template-interpolation", "cases.json");
        // No date parsing: a timestamp-looking string must stay the string the renderers see.
        return JsonConvert.DeserializeObject<JObject>(
            File.ReadAllText(path), new JsonSerializerSettings { DateParseHandling = DateParseHandling.None })!;
    });

    public static IEnumerable<object[]> CaseNames()
        => Fixture.Value["cases"]!.Select(c => new object[] { c.Value<string>("name")! });

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Interpolate_matches_the_shared_case(string name)
    {
        var testCase = Fixture.Value["cases"]!.Single(c => c.Value<string>("name") == name);
        var template = testCase.Value<string?>("template");
        var expected = testCase.Value<string>("expected")!;
        var evidence = testCase["matchedConditions"] as JObject;

        // Evidence as the rule engine builds it (nested dictionaries) ...
        Assert.Equal(expected, RuleTemplateInterpolator.Interpolate(template, AsDictionaries(evidence)));
        // ... and as a JSON read without normalization leaves it (JSON nodes as values).
        Assert.Equal(expected, RuleTemplateInterpolator.Interpolate(template, AsJsonNodes(evidence)));
    }

    [Fact]
    public void Shared_cases_are_present_and_cover_every_auto_field()
    {
        Assert.True(Fixture.Value["cases"]!.Count() >= 20, "cases.json lost cases — the parity guard is only as wide as the file.");
        Assert.Equal(
            Fixture.Value["autoFields"]!.ToObject<string[]>()!,
            RuleEngine.EvidenceAutoFields);
    }

    // ── Notification variant ──────────────────────────────────────────────

    [Fact]
    public void Notification_drops_a_list_item_whose_value_was_not_recorded()
    {
        var text = "Facts:\n\n- **Reason:** `{{reason}}`\n- **ESP HRESULT:** `{{errorCode}}`\n* **App:** {{appName}}\n1. step {{exitCode}}\n\nDone.";
        var evidence = new Dictionary<string, object>
        {
            ["enrollment_failed_reason"] = new Dictionary<string, object> { ["field"] = "reason", ["value"] = "hello_timeout" },
        };

        var rendered = RuleTemplateInterpolator.InterpolateForNotification(text, evidence);

        Assert.Equal("Facts:\n\n- **Reason:** `hello_timeout`\n\nDone.", rendered);
    }

    [Fact]
    public void Notification_replaces_an_unrecorded_value_in_running_text()
    {
        var rendered = RuleTemplateInterpolator.InterpolateForNotification(
            "App **{{appName}}** failed with HRESULT **{{errorCode}}**.",
            new Dictionary<string, object>
            {
                ["failed_app"] = new Dictionary<string, object> { ["field"] = "appName", ["value"] = FailedAppName },
            });

        Assert.Equal($"App **{FailedAppName}** failed with HRESULT **–**.", rendered);
    }

    [Fact]
    public void Notification_removes_the_sentence_about_literal_placeholders()
    {
        var rendered = RuleTemplateInterpolator.InterpolateForNotification(
            "See ANALYZE-APP-013 for the root cause. Any field still shown as a literal `{{…}}` placeholder was simply not recorded. Check the timeline.",
            new Dictionary<string, object>());

        Assert.Equal("See ANALYZE-APP-013 for the root cause. Check the timeline.", rendered);
    }

    [Fact]
    public void Notification_without_evidence_or_text_is_safe()
    {
        Assert.Equal(string.Empty, RuleTemplateInterpolator.InterpolateForNotification(null, null));
        Assert.Equal(string.Empty, RuleTemplateInterpolator.InterpolateForNotification("", new Dictionary<string, object>()));
        Assert.Equal("Reason: –", RuleTemplateInterpolator.InterpolateForNotification("Reason: {{reason}}", null));
    }

    [Fact]
    public void Notification_collapses_the_gap_a_dropped_paragraph_leaves()
    {
        var rendered = RuleTemplateInterpolator.InterpolateForNotification(
            "First.\n\nOnly a literal `{{…}}` remark.\n\nLast.", new Dictionary<string, object>());

        Assert.Equal("First.\n\nLast.", rendered);
    }

    // ── No placeholder leaves the backend ─────────────────────────────────

    [Fact]
    public void No_builtin_rule_sends_a_placeholder_whatever_the_evidence()
    {
        // Worst case for every rule: nothing recorded at all. Both alert builders are the only
        // paths that put a rule explanation into a notification.
        var offenders = new List<string>();
        var templated = 0;

        foreach (var rule in BuiltInAnalyzeRules.GetAll())
        {
            if (rule.Explanation.Contains("{{")) templated++;

            var result = new RuleResult
            {
                RuleId = rule.RuleId,
                RuleTitle = rule.Title,
                Severity = "critical",
                Category = rule.Category,
                Explanation = rule.Explanation,
            };

            var fired = NotificationAlertBuilder.BuildRuleFiredAlert(result, "DESKTOP-TEST01", "SN-0001");
            var enrollment = NotificationAlertBuilder.BuildEnrollmentAlert(
                "DESKTOP-TEST01", "SN-0001", null, null, success: false, failureReason: "x", duration: null);
            NotificationAlertBuilder.AddRuleResultSections(enrollment, new List<RuleResult> { result });

            foreach (var section in fired.Sections.Concat(enrollment.Sections))
            {
                if (section.Text.Contains("{{") || section.Text.Contains("}}"))
                    offenders.Add($"{rule.RuleId}: {section.Text}");
                Assert.True(section.Text.Length <= NotificationAlertBuilder.RuleFiredExplanationMaxLength);
            }
        }

        // Positive control: the catalog really contains templated rules, so an empty offender
        // list is a statement about them and not about a catalog without placeholders.
        Assert.True(templated >= 10, $"Only {templated} built-in rules carry a placeholder — did the catalog load?");
        Assert.True(offenders.Count == 0, "Placeholder reached a notification:\n" + string.Join("\n", offenders));
    }

    // ── End to end: engine evidence → alert ───────────────────────────────

    [Fact]
    public async Task Rule_fired_alert_shows_the_values_the_engine_recorded()
    {
        var result = await RunEnrollmentFailedRuleAsync(withEspFailure: true);

        var alert = NotificationAlertBuilder.BuildRuleFiredAlert(result, "DESKTOP-TEST01", "SN-0001");

        var text = Assert.Single(alert.Sections).Text;
        Assert.Equal(
            "The Autopilot enrollment has explicitly failed.\n\n" +
            "**Key facts extracted from this session's events:**\n\n" +
            "- **Failure reason:** `esp_terminal_failure`\n" +
            $"- **First failed app:** `{FailedAppName}`\n" +
            "- **ESP failed subcategory:** `Apps`\n" +
            "- **ESP HRESULT:** `0x87d1041c` …",
            text);
        // The stored result is untouched: the web and the MCP server still resolve the template.
        Assert.Contains("{{reason}}", result.Explanation);
    }

    [Fact]
    public async Task Rule_fired_alert_reads_evidence_that_went_through_the_table()
    {
        var result = await RunEnrollmentFailedRuleAsync(withEspFailure: true);
        var fresh = NotificationAlertBuilder.BuildRuleFiredAlert(result, null, null).Sections.Single().Text;

        // Same write and read as TableStorageService: JSON column, then nested dictionaries.
        var json = JsonConvert.SerializeObject(result.MatchedConditions);
        result.MatchedConditions = AsDictionaries(JObject.Parse(json))!;

        Assert.Equal(fresh, NotificationAlertBuilder.BuildRuleFiredAlert(result, null, null).Sections.Single().Text);
    }

    [Fact]
    public async Task Rule_fired_alert_omits_the_facts_a_non_esp_failure_never_had()
    {
        var result = await RunEnrollmentFailedRuleAsync(withEspFailure: false);

        var text = Assert.Single(NotificationAlertBuilder.BuildRuleFiredAlert(result, null, null).Sections).Text;

        Assert.Contains("- **Failure reason:** `hello_timeout`", text);
        Assert.DoesNotContain("First failed app", text);
        Assert.DoesNotContain("ESP failed subcategory", text);
        Assert.DoesNotContain("ESP HRESULT:", text);
    }

    [Fact]
    public async Task Enrollment_alert_rule_section_is_resolved_and_cut_on_a_line()
    {
        var result = await RunEnrollmentFailedRuleAsync(withEspFailure: true);
        var alert = NotificationAlertBuilder.BuildEnrollmentAlert(
            "DESKTOP-TEST01", "SN-0001", null, null, success: false, failureReason: "x", duration: null);

        NotificationAlertBuilder.AddRuleResultSections(alert, new List<RuleResult> { result });

        var text = Assert.Single(alert.Sections).Text;
        Assert.True(text.Length <= NotificationAlertBuilder.RuleSectionExplanationMaxLength);
        Assert.EndsWith("- **Failure reason:** `esp_terminal_failure` …", text);
    }

    // ── Cut ───────────────────────────────────────────────────────────────

    [Fact]
    public void Truncate_keeps_short_text()
        => Assert.Equal("short", NotificationAlertBuilder.TruncateAtBoundary("short", 300));

    [Fact]
    public void Truncate_prefers_a_line_break_then_a_sentence_then_a_word()
    {
        var lines = "First paragraph of the text.\n" + new string('a', 40);
        Assert.Equal("First paragraph of the text. …", NotificationAlertBuilder.TruncateAtBoundary(lines, 50));

        var sentences = "One sentence here. Another sentence that runs on and on without an end";
        Assert.Equal("One sentence here. …", NotificationAlertBuilder.TruncateAtBoundary(sentences, 30));

        var words = "word word word word word word word word word word";
        Assert.Equal("word word word word word …", NotificationAlertBuilder.TruncateAtBoundary(words, 30));
    }

    [Fact]
    public void Truncate_never_exceeds_the_limit_and_survives_text_without_any_boundary()
    {
        var unbroken = new string('x', 500);
        var cut = NotificationAlertBuilder.TruncateAtBoundary(unbroken, 100);

        Assert.Equal(100, cut.Length);
        Assert.EndsWith(" …", cut);
    }

    [Fact]
    public void Truncate_ignores_a_boundary_that_would_waste_most_of_the_budget()
    {
        var text = "Hi.\n" + string.Join(" ", Enumerable.Repeat("word", 60));
        var cut = NotificationAlertBuilder.TruncateAtBoundary(text, 100);

        Assert.True(cut.Length > 50, cut);
        Assert.True(cut.Length <= 100);
    }

    [Fact]
    public void Truncate_does_not_leave_a_bold_or_code_span_open()
    {
        var bold = "See the **App Detection Failure During ESP** rule for the cause and the **remediation steps in detail** below";
        Assert.Equal("See the **App Detection Failure During ESP** rule for the cause and the remediation steps …",
            NotificationAlertBuilder.TruncateAtBoundary(bold, 94));

        var code = "The installer returned `1603 fatal error during installation` and stopped the whole chain";
        Assert.Equal("The installer returned 1603 fatal error during …",
            NotificationAlertBuilder.TruncateAtBoundary(code, 50));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static async Task<RuleResult> RunEnrollmentFailedRuleAsync(bool withEspFailure)
    {
        var rule = BuiltInAnalyzeRules.GetAll().Single(r => r.RuleId == "ANALYZE-ENRL-001");

        var events = new List<EnrollmentEvent>
        {
            Event("enrollment_failed", 163, new Dictionary<string, object>
            {
                ["reason"] = withEspFailure ? "esp_terminal_failure" : "hello_timeout",
            }),
        };
        if (withEspFailure)
        {
            events.Add(Event("esp_failure_settle_started", 161, new Dictionary<string, object>
            {
                ["failedSubcategory"] = "Apps",
                ["errorCode"] = "0x87d1041c",
            }));
            events.Add(Event("app_install_failed", 158, new Dictionary<string, object>
            {
                ["appId"] = "00000000-0000-4000-8000-000000000001",
                ["appName"] = FailedAppName,
                ["errorCode"] = "0x87d1041c",
            }));
        }

        var ruleRepo = new Mock<IRuleRepository>();
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(new List<AnalyzeRule> { rule });
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync(TenantId)).ReturnsAsync(new List<AnalyzeRule>());
        ruleRepo.Setup(r => r.GetRuleStatesAsync(It.IsAny<string>())).ReturnsAsync(new Dictionary<string, RuleState>());
        ruleRepo.Setup(r => r.GetRuleResultsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(new List<RuleResult>());

        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(s => s.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>())).ReturnsAsync(events);

        var ruleService = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<RuleTemplateInterpolatorTests>.Instance);

        var outcome = await engine.AnalyzeSessionAsync(TenantId, SessionId);
        return Assert.Single(outcome.Results);
    }

    private static EnrollmentEvent Event(string eventType, long sequence, Dictionary<string, object> data) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = eventType,
        Timestamp = DateTime.UtcNow,
        Sequence = sequence,
        Data = data,
    };

    private static Dictionary<string, object>? AsJsonNodes(JObject? evidence)
        => evidence?.Properties().ToDictionary(p => p.Name, p => (object)p.Value);

    private static Dictionary<string, object>? AsDictionaries(JObject? evidence)
        => evidence?.Properties().ToDictionary(p => p.Name, p => Plain(p.Value)!);

    private static object? Plain(JToken token) => token switch
    {
        JObject o => o.Properties().ToDictionary(p => p.Name, p => Plain(p.Value)!),
        JArray a => a.Select(Plain).ToList(),
        JValue v => v.Value,
        _ => null,
    };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

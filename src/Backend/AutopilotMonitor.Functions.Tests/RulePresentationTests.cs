using System.Text.Json;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The optional presentation block of analyze rules: Store↔Map in both tables (every field in both
/// directions; a legacy row without the column reads back as null, never as an empty block), the
/// create/update validation, the read-time fallback for results stored without one, the engine
/// copying it onto a result, and the schema enums/limits matching the C# vocabularies.
/// </summary>
public class RulePresentationTests
{
    private const string TenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "11111111-2222-3333-4444-555555555555";

    private static RulePresentation Sample() => new()
    {
        Role = RulePresentationRoles.Cause,
        Summary = "{{install_retry_loop.appName}} was started {{install_retry_loop.count}} times",
        Subject = new RuleSubject { Kind = RuleSubjectKinds.App, Key = "{{install_retry_loop.groupKey}}", Name = "{{install_retry_loop.appName}}" },
        Facts = new List<RuleFact>
        {
            new() { Label = "Install starts", Value = "{{install_retry_loop.count}}", Format = RuleFactFormats.Number },
            new() { Label = "App", Value = "{{install_retry_loop.appName}}" },
        },
    };

    // ── Store ↔ Map ────────────────────────────────────────────────────────

    private static (TableStorageService service, Func<TableEntity?> lastUpserted) BuildStorageHarness()
    {
        TableEntity? captured = null;
        var tableClient = new Mock<TableClient>();
        tableClient
            .Setup(c => c.UpsertEntityAsync(It.IsAny<TableEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .Returns<TableEntity, TableUpdateMode, CancellationToken>((e, _, _) =>
            {
                captured = e;
                return Task.FromResult(new Mock<Response>().Object);
            });
        var serviceClient = new Mock<TableServiceClient>();
        serviceClient.Setup(c => c.GetTableClient(It.IsAny<string>())).Returns(tableClient.Object);
        return (new TableStorageService(serviceClient.Object, NullLogger<TableStorageService>.Instance), () => captured);
    }

    [Fact]
    public async Task AnalyzeRule_presentation_survives_store_and_map()
    {
        var (service, lastUpserted) = BuildStorageHarness();
        var rule = new AnalyzeRule { RuleId = "ANALYZE-TEST-901", Title = "t", Severity = "high", Category = "apps", Explanation = "e", Presentation = Sample() };

        Assert.True(await service.StoreAnalyzeRuleAsync(rule, "global"));
        var mapped = service.MapToAnalyzeRule(lastUpserted()!);

        AssertSamePresentation(Sample(), mapped.Presentation);
    }

    [Fact]
    public async Task AnalyzeRule_without_presentation_stores_an_empty_column_and_maps_to_null()
    {
        var (service, lastUpserted) = BuildStorageHarness();
        var rule = new AnalyzeRule { RuleId = "ANALYZE-TEST-902", Title = "t", Severity = "high", Category = "apps", Explanation = "e" };

        Assert.True(await service.StoreAnalyzeRuleAsync(rule, "global"));
        var entity = lastUpserted()!;

        Assert.Equal(string.Empty, entity.GetString("PresentationJson"));
        Assert.Null(service.MapToAnalyzeRule(entity).Presentation);
        entity.Remove("PresentationJson");
        Assert.Null(service.MapToAnalyzeRule(entity).Presentation);
    }

    [Fact]
    public async Task RuleResult_presentation_and_version_survive_store_and_map()
    {
        var (service, lastUpserted) = BuildStorageHarness();
        var result = new RuleResult
        {
            SessionId = SessionId, TenantId = TenantId, RuleId = "ANALYZE-APP-006", RuleTitle = "t", RuleVersion = "2.0.0",
            Severity = "high", Category = "apps", Explanation = "e", Presentation = Sample(),
        };

        Assert.True(await service.StoreRuleResultAsync(result));
        var mapped = service.MapToRuleResult(lastUpserted()!);

        Assert.Equal("2.0.0", mapped.RuleVersion);
        AssertSamePresentation(Sample(), mapped.Presentation);
    }

    [Fact]
    public async Task Legacy_rule_result_maps_to_null_presentation_and_version()
    {
        var (service, lastUpserted) = BuildStorageHarness();
        Assert.True(await service.StoreRuleResultAsync(new RuleResult { SessionId = SessionId, TenantId = TenantId, RuleId = "ANALYZE-APP-006", Explanation = "e" }));
        var entity = lastUpserted()!;
        entity.Remove("PresentationJson");
        entity.Remove("RuleVersion");

        var mapped = service.MapToRuleResult(entity);

        Assert.Null(mapped.Presentation);
        Assert.Null(mapped.RuleVersion);
    }

    // ── Validation ─────────────────────────────────────────────────────────

    [Fact]
    public void Valid_presentation_passes_and_blank_fact_rows_are_dropped()
    {
        var presentation = Sample();
        presentation.Facts!.Add(new RuleFact { Label = " ", Value = "" });
        var rule = new AnalyzeRule { Presentation = presentation };

        AnalyzeRuleService.ValidatePresentation(rule);

        Assert.Equal(2, rule.Presentation!.Facts!.Count);
    }

    [Fact]
    public void Only_blank_fact_rows_leave_no_facts()
    {
        var rule = new AnalyzeRule { Presentation = new RulePresentation { Role = "advisory", Facts = new List<RuleFact> { new() { Label = "", Value = " " } } } };

        AnalyzeRuleService.ValidatePresentation(rule);

        Assert.Null(rule.Presentation!.Facts);
    }

    [Theory]
    [InlineData("verdict", null, null, null, "presentation.role")]
    [InlineData("cause", "", null, null, "presentation.summary")]
    [InlineData("cause", null, "thing", null, "presentation.subject.kind")]
    [InlineData("cause", null, "app", "weird", "presentation.facts[0].format")]
    public void Invalid_presentation_is_rejected_naming_the_field(string role, string? summary, string? subjectKind, string? format, string expectedField)
    {
        var rule = new AnalyzeRule
        {
            Presentation = new RulePresentation
            {
                Role = role,
                Summary = summary,
                Subject = subjectKind == null ? null : new RuleSubject { Kind = subjectKind, Key = "{{a.b}}" },
                Facts = format == null ? null : new List<RuleFact> { new() { Label = "L", Value = "{{a.b}}", Format = format } },
            },
        };

        var ex = Assert.Throws<ArgumentException>(() => AnalyzeRuleService.ValidatePresentation(rule));
        Assert.Contains(expectedField, ex.Message);
    }

    [Fact]
    public void Too_many_facts_and_long_texts_are_rejected()
    {
        var rule = new AnalyzeRule
        {
            Presentation = new RulePresentation
            {
                Role = "cause",
                Summary = new string('s', RulePresentationLimits.SummaryMaxLength + 1),
                Facts = Enumerable.Range(0, RulePresentationLimits.MaxFacts + 1)
                    .Select(i => new RuleFact { Label = new string('l', RulePresentationLimits.LabelMaxLength + 1), Value = "v" }).ToList(),
            },
        };

        var ex = Assert.Throws<ArgumentException>(() => AnalyzeRuleService.ValidatePresentation(rule));

        Assert.Contains("presentation.summary", ex.Message);
        Assert.Contains("at most", ex.Message);
        Assert.Contains("presentation.facts[0].label", ex.Message);
    }

    // ── Read-time fallback ─────────────────────────────────────────────────

    [Fact]
    public async Task Results_without_presentation_get_the_current_rule_presentation()
    {
        var global = new AnalyzeRule { RuleId = "ANALYZE-APP-006", Presentation = Sample() };
        var custom = new AnalyzeRule { RuleId = "CONTOSO-RULE-001", Presentation = new RulePresentation { Role = "advisory" } };
        var stored = new RulePresentation { Role = "context" };
        var results = new List<RuleResult>
        {
            new() { RuleId = "ANALYZE-APP-006" },
            new() { RuleId = "CONTOSO-RULE-001" },
            new() { RuleId = "ANALYZE-ESP-005", Presentation = stored },
            new() { RuleId = "ANALYZE-GONE-001" },
        };

        await ServiceWith(new[] { global }, new[] { custom }).AttachCurrentPresentationsAsync(TenantId, results);

        Assert.Equal("cause", results[0].Presentation?.Role);
        Assert.Equal("advisory", results[1].Presentation?.Role);
        Assert.Same(stored, results[2].Presentation);
        Assert.Null(results[3].Presentation);
    }

    [Fact]
    public async Task Results_that_all_carry_a_presentation_need_no_rule_lookup()
    {
        var ruleRepo = new Mock<IRuleRepository>(MockBehavior.Strict);
        var service = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);

        await service.AttachCurrentPresentationsAsync(TenantId, new List<RuleResult> { new() { RuleId = "X", Presentation = Sample() } });
    }

    [Fact]
    public async Task A_failed_rule_lookup_leaves_the_results_untouched()
    {
        var ruleRepo = new Mock<IRuleRepository>();
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync(It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("storage down"));
        var service = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var results = new List<RuleResult> { new() { RuleId = "ANALYZE-APP-006" } };

        await service.AttachCurrentPresentationsAsync(TenantId, results);

        Assert.Null(results[0].Presentation);
    }

    private static AnalyzeRuleService ServiceWith(IEnumerable<AnalyzeRule> global, IEnumerable<AnalyzeRule> tenant)
    {
        var ruleRepo = new Mock<IRuleRepository>();
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(global.ToList());
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync(TenantId)).ReturnsAsync(tenant.ToList());
        return new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
    }

    // ── Engine ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Engine_copies_presentation_and_version_onto_the_result()
    {
        // A shipped rule (the merge drops global rules outside the shipped catalog as sunset).
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-ENRL-001");
        rule.Presentation = Sample();
        var events = new List<EnrollmentEvent>
        {
            new() { EventId = "evt-1", TenantId = TenantId, SessionId = SessionId, EventType = "enrollment_failed", Timestamp = DateTime.UtcNow, Sequence = 1, Data = new Dictionary<string, object> { ["reason"] = "esp_terminal_failure" } },
        };
        var ruleRepo = new Mock<IRuleRepository>();
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(new List<AnalyzeRule> { rule });
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync(TenantId)).ReturnsAsync(new List<AnalyzeRule>());
        ruleRepo.Setup(r => r.GetRuleStatesAsync(It.IsAny<string>())).ReturnsAsync(new Dictionary<string, RuleState>());
        ruleRepo.Setup(r => r.GetRuleResultsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(new List<RuleResult>());
        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(s => s.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>())).ReturnsAsync(events);
        var engine = new RuleEngine(new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance), ruleRepo.Object, sessionRepo.Object, NullLogger<RulePresentationTests>.Instance);

        var outcome = await engine.AnalyzeSessionAsync(TenantId, SessionId);

        var result = Assert.Single(outcome.Results);
        Assert.Equal(rule.Version, result.RuleVersion);
        AssertSamePresentation(Sample(), result.Presentation);
    }

    // ── Built-in catalog ratchet ───────────────────────────────────────────

    [Fact]
    public void Every_built_in_rule_has_a_presentation_its_conditions_can_fill()
    {
        // Each token must name a condition of the rule and a key that condition's source records
        // (RuleEvidenceKeys, pinned against the evaluators), so a typo or a key the engine never
        // writes fails here instead of silently dropping a fact. Built-in rules use only
        // {{signal.key}}: a bare token resolves through fallbacks that can read another event.
        var rules = BuiltInAnalyzeRules.GetAll();
        var offenders = new List<string>();
        foreach (var rule in rules)
        {
            var p = rule.Presentation;
            if (p == null)
            {
                offenders.Add($"{rule.RuleId}: no presentation");
                continue;
            }
            try { AnalyzeRuleService.ValidatePresentation(rule); }
            catch (ArgumentException ex) { offenders.Add($"{rule.RuleId}: {ex.Message}"); }

            var sourcesBySignal = rule.Conditions
                .GroupBy(c => c.Signal)
                .ToDictionary(g => g.Key, g => g.Select(c => c.Source).ToList());
            var templates = new List<(string Where, string Text)>();
            if (p.Summary != null) templates.Add(("summary", p.Summary));
            if (p.Subject != null)
            {
                templates.Add(("subject.key", p.Subject.Key));
                if (p.Subject.Name != null) templates.Add(("subject.name", p.Subject.Name));
            }
            for (var i = 0; i < (p.Facts?.Count ?? 0); i++)
                templates.Add(($"facts[{i}].value", p.Facts![i].Value));

            foreach (var (where, text) in templates)
            {
                foreach (System.Text.RegularExpressions.Match m in RuleTemplateInterpolator.TokenRegex.Matches(text))
                {
                    var signal = m.Groups[1].Value;
                    if (!m.Groups[2].Success)
                        offenders.Add($"{rule.RuleId} {where}: bare token {m.Value}");
                    else if (!sourcesBySignal.TryGetValue(signal, out var sources))
                        offenders.Add($"{rule.RuleId} {where}: {m.Value} names no condition");
                    else if (!sources.Any(s => RuleEvidenceKeys.BySource.TryGetValue(s, out var keys) && keys.Contains(m.Groups[2].Value)))
                        offenders.Add($"{rule.RuleId} {where}: {m.Value} is not a key that {string.Join("/", sources)} records");
                }
            }

            // Key and name of a subject come from one evidence entry, or they could name two apps.
            if (p.Subject != null)
            {
                var subjectSignals = RuleTemplateInterpolator.TokenRegex.Matches(p.Subject.Key + " " + p.Subject.Name)
                    .Select(m => m.Groups[1].Value)
                    .Distinct()
                    .ToList();
                if (subjectSignals.Count > 1)
                    offenders.Add($"{rule.RuleId} subject: key and name read different signals ({string.Join(", ", subjectSignals)})");
            }
        }

        // Positive control: the shipped catalog loaded, so no offenders is a statement about it.
        Assert.True(rules.Count >= 50, $"Only {rules.Count} built-in rules loaded");
        Assert.True(offenders.Count == 0, "Built-in presentations the evidence cannot fill:\n" + string.Join("\n", offenders));
    }

    // ── Schema ↔ C# vocabularies ───────────────────────────────────────────

    [Fact]
    public void Schema_enums_and_limits_match_the_shared_vocabularies()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "rules", "schema", "analyze-rule.schema.json")));
        var defs = schema.RootElement.GetProperty("$defs");
        var presentation = defs.GetProperty("rulePresentation").GetProperty("properties");
        var subject = defs.GetProperty("ruleSubject").GetProperty("properties");
        var fact = defs.GetProperty("ruleFact").GetProperty("properties");

        Assert.Equal(RulePresentationRoles.All, Strings(presentation.GetProperty("role").GetProperty("enum")));
        Assert.Equal(RuleSubjectKinds.All, Strings(subject.GetProperty("kind").GetProperty("enum")));
        Assert.Equal(RuleFactFormats.All, Strings(fact.GetProperty("format").GetProperty("enum")));
        Assert.Equal(RulePresentationLimits.SummaryMaxLength, presentation.GetProperty("summary").GetProperty("maxLength").GetInt32());
        Assert.Equal(RulePresentationLimits.MaxFacts, presentation.GetProperty("facts").GetProperty("maxItems").GetInt32());
        Assert.Equal(RulePresentationLimits.LabelMaxLength, fact.GetProperty("label").GetProperty("maxLength").GetInt32());
        Assert.Equal(RulePresentationLimits.TemplateMaxLength, fact.GetProperty("value").GetProperty("maxLength").GetInt32());
        Assert.Equal(RulePresentationLimits.TemplateMaxLength, subject.GetProperty("key").GetProperty("maxLength").GetInt32());
        Assert.Equal(RulePresentationLimits.TemplateMaxLength, subject.GetProperty("name").GetProperty("maxLength").GetInt32());
    }

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root (AutopilotMonitor.sln) not found");
    }

    private static void AssertSamePresentation(RulePresentation expected, RulePresentation? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(Newtonsoft.Json.JsonConvert.SerializeObject(expected), Newtonsoft.Json.JsonConvert.SerializeObject(actual));
    }
}

using AutopilotMonitor.Functions.Functions.Rules;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The size limits of a tenant-authored analyze rule (D-410): at most 20 conditions and 10
/// preconditions, and every value a regex or not_regex operator compares — condition value,
/// filter value, event A filter value, precondition value — at most 2,000 characters and
/// parseable. One implementation feeds the dry-run validation and every write path (create,
/// update, template copy); the boundary itself is always allowed.
/// </summary>
public class AnalyzeRuleShapeLimitsTests
{
    private const string TenantId = "tenant-limits";

    [Fact]
    public void A_rule_at_every_limit_passes()
    {
        var rule = Rule(AnalyzeRuleLimits.MaxConditions, AnalyzeRuleLimits.MaxPreconditions);
        var atLimit = new string('a', AnalyzeRuleLimits.MaxRegexLength);
        rule.Conditions[0].Operator = "regex";
        rule.Conditions[0].Value = atLimit;
        rule.Conditions[1].FilterField = "f";
        rule.Conditions[1].FilterOperator = "not_regex";
        rule.Conditions[1].FilterValue = atLimit;
        rule.Conditions[2].EventAFilterOperator = "REGEX";
        rule.Conditions[2].EventAFilterValue = atLimit;
        rule.Preconditions[0].Operator = "regex";
        rule.Preconditions[0].Value = atLimit;

        Assert.Empty(AnalyzeRuleService.ShapeLimitErrors(rule));
    }

    [Fact]
    public void One_condition_or_precondition_over_the_limit_is_named()
    {
        var errors = AnalyzeRuleService.ShapeLimitErrors(Rule(AnalyzeRuleLimits.MaxConditions + 1, AnalyzeRuleLimits.MaxPreconditions + 1));

        Assert.Contains(errors, e => e.Contains("at most 20 conditions (this one has 21)"));
        Assert.Contains(errors, e => e.Contains("at most 10 preconditions (this one has 11)"));
    }

    [Theory]
    [InlineData("conditions[0].value")]
    [InlineData("conditions[0].filterValue")]
    [InlineData("conditions[0].eventAFilterValue")]
    [InlineData("preconditions[0].value")]
    public void A_regex_over_the_length_limit_is_rejected_wherever_it_sits(string field)
    {
        var rule = Rule(1, 1);
        Place(rule, field, "regex", new string('a', AnalyzeRuleLimits.MaxRegexLength + 1));

        var error = Assert.Single(AnalyzeRuleService.ShapeLimitErrors(rule));
        Assert.StartsWith($"{field}: a regex may be at most 2000 characters (this one has 2001)", error);
    }

    [Theory]
    [InlineData("conditions[0].filterValue")]
    [InlineData("conditions[0].eventAFilterValue")]
    [InlineData("preconditions[0].value")]
    public void An_unparseable_regex_is_rejected_beyond_the_condition_value(string field)
    {
        var rule = Rule(1, 1);
        Place(rule, field, "not_regex", "([unclosed");

        var error = Assert.Single(AnalyzeRuleService.ShapeLimitErrors(rule));
        Assert.StartsWith($"{field}: value is not a valid regex", error);
    }

    [Fact]
    public void A_long_value_of_any_other_operator_is_not_limited()
    {
        var rule = Rule(1, 0);
        rule.Conditions[0].Operator = "not_in";
        rule.Conditions[0].Value = string.Join(",", Enumerable.Range(0, 1000).Select(i => $"app-{i:D5}"));

        Assert.Empty(AnalyzeRuleService.ShapeLimitErrors(rule));
    }

    [Fact]
    public void The_dry_run_validation_carries_the_limits()
    {
        var rule = Rule(AnalyzeRuleLimits.MaxConditions + 1, 0);

        Assert.Contains(DryRunAnalyzeRuleFunction.ValidateDraftRule(rule), e => e.Contains("at most 20 conditions"));
    }

    // ===== write paths =====

    [Fact]
    public async Task Create_rejects_an_oversized_rule_before_anything_is_stored()
    {
        var repo = new Mock<IRuleRepository>();
        var service = new AnalyzeRuleService(repo.Object, NullLogger<AnalyzeRuleService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateRuleAsync(TenantId, Rule(AnalyzeRuleLimits.MaxConditions + 1, 0)));
        repo.Verify(r => r.StoreAnalyzeRuleAsync(It.IsAny<AnalyzeRule>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Update_of_a_custom_rule_rejects_an_oversized_regex_before_anything_is_stored()
    {
        var repo = new Mock<IRuleRepository>();
        var service = new AnalyzeRuleService(repo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var rule = Rule(1, 0);
        Place(rule, "conditions[0].value", "not_regex", new string('a', AnalyzeRuleLimits.MaxRegexLength + 1));

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateRuleAsync(TenantId, rule));
        repo.Verify(r => r.StoreAnalyzeRuleAsync(It.IsAny<AnalyzeRule>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Template_copy_rejects_an_oversized_regex_from_its_variable()
    {
        // ANALYZE-SEC-006 shape: the template variable IS the tenant's not_regex allow-list.
        var template = Rule(1, 0);
        template.RuleId = "ANALYZE-SEC-006";
        template.IsBuiltIn = true;
        template.Conditions[0].Operator = "not_regex";
        template.TemplateVariables = new List<TemplateVariable> { new() { Name = "allowList", ConditionIndex = 0, Field = "value" } };

        var repo = new Mock<IRuleRepository>();
        repo.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(new List<AnalyzeRule> { template });
        repo.Setup(r => r.GetAnalyzeRulesAsync(TenantId)).ReturnsAsync(new List<AnalyzeRule>());
        repo.Setup(r => r.GetRuleStatesAsync(TenantId)).ReturnsAsync(new Dictionary<string, RuleState>());
        var service = new AnalyzeRuleService(repo.Object, NullLogger<AnalyzeRuleService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateFromTemplateAsync(TenantId, "ANALYZE-SEC-006",
            new Dictionary<string, string> { ["allowList"] = new string('a', AnalyzeRuleLimits.MaxRegexLength + 1) }));
        repo.Verify(r => r.StoreAnalyzeRuleAsync(It.IsAny<AnalyzeRule>(), TenantId), Times.Never);
    }

    // ===== fixtures =====

    private static AnalyzeRule Rule(int conditions, int preconditions) => new()
    {
        RuleId = "CUSTOM-LIMITS",
        IsBuiltIn = false, // the model defaults to a built-in rule
        Title = "limits",
        Severity = "warning",
        Category = "test",
        BaseConfidence = 80,
        ConfidenceThreshold = 40,
        Conditions = Enumerable.Range(0, conditions).Select(i => new RuleCondition
        {
            Signal = $"s{i}", Source = "event_data", EventType = "app_install_failed", DataField = "errorCode",
            Operator = "equals", Value = "1", Required = true,
        }).ToList(),
        Preconditions = Enumerable.Range(0, preconditions).Select(_ => new RulePrecondition
        {
            Source = "event_data", EventType = "os_info", DataField = "edition", Operator = "equals", Value = "Enterprise",
        }).ToList(),
    };

    private static void Place(AnalyzeRule rule, string field, string op, string value)
    {
        switch (field)
        {
            case "conditions[0].value":
                rule.Conditions[0].Operator = op;
                rule.Conditions[0].Value = value;
                break;
            case "conditions[0].filterValue":
                rule.Conditions[0].FilterField = "f";
                rule.Conditions[0].FilterOperator = op;
                rule.Conditions[0].FilterValue = value;
                break;
            case "conditions[0].eventAFilterValue":
                rule.Conditions[0].EventAFilterField = "f";
                rule.Conditions[0].EventAFilterOperator = op;
                rule.Conditions[0].EventAFilterValue = value;
                break;
            default:
                rule.Preconditions[0].Operator = op;
                rule.Preconditions[0].Value = value;
                break;
        }
    }
}

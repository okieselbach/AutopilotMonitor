using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

public class BuiltInRulesTests
{
    [Fact]
    public void BuiltInGatherRules_LoadsFromEmbeddedResource()
    {
        var rules = BuiltInGatherRules.GetAll();
        Assert.NotNull(rules);
        Assert.NotEmpty(rules);
        Assert.Contains(rules, r => r.RuleId == "GATHER-DEVICE-001");
        Assert.Contains(rules, r => r.RuleId == "GATHER-DEVICE-002");

        foreach (var rule in rules)
        {
            Assert.False(string.IsNullOrEmpty(rule.RuleId), "RuleId must not be empty");
            Assert.False(string.IsNullOrEmpty(rule.Title), $"Rule {rule.RuleId} is missing Title");
            Assert.False(string.IsNullOrEmpty(rule.CollectorType), $"Rule {rule.RuleId} is missing CollectorType");
            Assert.False(string.IsNullOrEmpty(rule.Target), $"Rule {rule.RuleId} is missing Target");
            Assert.False(string.IsNullOrEmpty(rule.Trigger), $"Rule {rule.RuleId} is missing Trigger");
            Assert.False(string.IsNullOrEmpty(rule.OutputEventType), $"Rule {rule.RuleId} is missing OutputEventType");
            Assert.True(rule.IsBuiltIn, $"Rule {rule.RuleId} should have IsBuiltIn=true");
        }
    }

    [Fact]
    public void BuiltInAnalyzeRules_LoadsFromEmbeddedResource()
    {
        var rules = BuiltInAnalyzeRules.GetAll();
        Assert.NotNull(rules);
        Assert.True(rules.Count >= 18, $"Expected at least 18 rules, got {rules.Count}");

        foreach (var rule in rules)
        {
            Assert.False(string.IsNullOrEmpty(rule.RuleId), "RuleId must not be empty");
            Assert.False(string.IsNullOrEmpty(rule.Title), $"Rule {rule.RuleId} is missing Title");
            Assert.False(string.IsNullOrEmpty(rule.Severity), $"Rule {rule.RuleId} is missing Severity");
            Assert.False(string.IsNullOrEmpty(rule.Explanation), $"Rule {rule.RuleId} is missing Explanation");
            Assert.NotNull(rule.Conditions);
            Assert.NotEmpty(rule.Conditions);
            Assert.True(rule.IsBuiltIn, $"Rule {rule.RuleId} should have IsBuiltIn=true");
        }
    }

    [Fact]
    public void BuiltInAnalyzeRules_TemplateVariablesAreValid()
    {
        var rules = BuiltInAnalyzeRules.GetAll();
        var templateRules = rules.Where(r => r.TemplateVariables != null && r.TemplateVariables.Count > 0).ToList();

        foreach (var rule in templateRules)
        {
            // Template rules should be disabled by default (they need configuration first)
            Assert.False(rule.Enabled, $"Template rule {rule.RuleId} should be disabled by default");

            foreach (var tv in rule.TemplateVariables)
            {
                Assert.False(string.IsNullOrEmpty(tv.Name), $"Rule {rule.RuleId}: TemplateVariable is missing Name");
                Assert.False(string.IsNullOrEmpty(tv.Label), $"Rule {rule.RuleId}: TemplateVariable '{tv.Name}' is missing Label");
                Assert.False(string.IsNullOrEmpty(tv.Placeholder), $"Rule {rule.RuleId}: TemplateVariable '{tv.Name}' is missing Placeholder");

                // conditionIndex must reference a valid condition
                Assert.True(tv.ConditionIndex >= 0 && tv.ConditionIndex < rule.Conditions.Count,
                    $"Rule {rule.RuleId}: TemplateVariable '{tv.Name}' has conditionIndex {tv.ConditionIndex} but rule only has {rule.Conditions.Count} conditions");

                // Field must be one of the known fields
                var validFields = new HashSet<string> { "value", "eventType", "dataField", "eventAFilterValue" };
                Assert.True(validFields.Contains(tv.Field),
                    $"Rule {rule.RuleId}: TemplateVariable '{tv.Name}' has unknown field '{tv.Field}'");

                // The placeholder should match the current value in the condition
                var condition = rule.Conditions[tv.ConditionIndex];
                var actualValue = tv.Field switch
                {
                    "value" => condition.Value,
                    "eventType" => condition.EventType,
                    "dataField" => condition.DataField,
                    "eventAFilterValue" => condition.EventAFilterValue,
                    _ => null
                };
                Assert.Equal(tv.Placeholder, actualValue);
            }
        }
    }

    [Fact]
    public void BuiltInAnalyzeRules_ANALYZE_ID_001_IsTemplate()
    {
        var rules = BuiltInAnalyzeRules.GetAll();
        var rule = rules.FirstOrDefault(r => r.RuleId == "ANALYZE-ID-001");

        Assert.NotNull(rule);
        Assert.False(rule.Enabled, "ANALYZE-ID-001 should be disabled (it's a template)");
        Assert.NotNull(rule.TemplateVariables);
        Assert.Single(rule.TemplateVariables);

        var tv = rule.TemplateVariables[0];
        Assert.Equal("cert_subject", tv.Name);
        Assert.Equal("Certificate Subject", tv.Label);
        Assert.Equal(1, tv.ConditionIndex);
        Assert.Equal("value", tv.Field);
        Assert.Equal("CN=YOUR-CERTIFICATE-SUBJECT", tv.Placeholder);
    }

    [Fact]
    public void BuiltInImeLogPatterns_LoadsFromEmbeddedResource()
    {
        var patterns = BuiltInImeLogPatterns.GetAll();
        Assert.NotNull(patterns);
        Assert.True(patterns.Count >= 47, $"Expected at least 47 patterns, got {patterns.Count}");

        foreach (var pattern in patterns)
        {
            Assert.False(string.IsNullOrEmpty(pattern.PatternId), "PatternId must not be empty");
            Assert.False(string.IsNullOrEmpty(pattern.Category), $"Pattern {pattern.PatternId} is missing Category");
            Assert.False(string.IsNullOrEmpty(pattern.Pattern), $"Pattern {pattern.PatternId} is missing Pattern");
            Assert.False(string.IsNullOrEmpty(pattern.Action), $"Pattern {pattern.PatternId} is missing Action");
            Assert.False(string.IsNullOrEmpty(pattern.Description),
                $"Pattern {pattern.PatternId} is missing Description");
            Assert.True(pattern.IsBuiltIn, $"Pattern {pattern.PatternId} should have IsBuiltIn=true");
        }
    }

    [Fact]
    public void BuiltInImeLogPatterns_AllActionsAreKnown()
    {
        var knownActions = new HashSet<string>
        {
            "setCurrentApp", "updateStateInstalled", "updateStateDownloading",
            "updateStateInstalling", "updateStateSkipped", "updateStateError",
            "updateStatePostponed", "espPhaseDetected", "imeStarted",
            "policiesDiscovered", "ignoreCompletedApp", "imeAgentVersion",
            "espTrackStatus", "updateName", "updateWin32AppState",
            "cancelStuckAndSetCurrent", "imeSessionChange", "imeImpersonation",
            "enrollmentCompleted", "updateDoTelemetry", "scriptStarted",
            "scriptContext", "scriptExitCode", "scriptOutput", "scriptCompleted",
            "healthScriptResult",
            "captureExitCode", "captureHResult",
            "captureAppVersion", "captureAppTypeWinGet", "captureAppTypeMsi",
            "captureAttemptNumber", "captureDetectionResult"
        };

        var patterns = BuiltInImeLogPatterns.GetAll();
        foreach (var pattern in patterns)
        {
            Assert.True(knownActions.Contains(pattern.Action),
                $"Pattern {pattern.PatternId} has unknown action '{pattern.Action}'");
        }
    }

    [Fact]
    public void BuiltInImeLogPatterns_AllCategoriesAreValid()
    {
        var validCategories = new HashSet<string> { "always", "currentPhase", "otherPhases" };

        var patterns = BuiltInImeLogPatterns.GetAll();
        foreach (var pattern in patterns)
        {
            Assert.True(validCategories.Contains(pattern.Category),
                $"Pattern {pattern.PatternId} has invalid category '{pattern.Category}'");
        }
    }
}

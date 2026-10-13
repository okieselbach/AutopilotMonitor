using AutopilotMonitor.Functions.Functions.Rules;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins <see cref="RuleEvidenceKeys"/> against the evaluators. Every key an evidence entry carries
/// must be declared for its condition source; otherwise the MCP lint and the presentation ratchet
/// would reject a <c>{{signal.key}}</c> token that resolves, or pass one that never does. The simple
/// sources are covered here; the duration, correlation, clock-skew and array tests call
/// <see cref="AssertDeclared"/> on the evidence they already build.
/// </summary>
public class RuleEvidenceKeysTests
{
    private const string TenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "11111111-2222-3333-4444-555555555555";

    internal static void AssertDeclared(string source, object? evidence)
    {
        var entry = Assert.IsAssignableFrom<IDictionary<string, object>>(evidence);
        var declared = RuleEvidenceKeys.BySource[source];
        var undeclared = entry.Keys.Where(k => !declared.Contains(k)).ToList();
        Assert.True(undeclared.Count == 0, $"{source} evidence carries undeclared keys: {string.Join(", ", undeclared)}");
    }

    [Fact]
    public void Catalog_covers_exactly_the_known_condition_sources()
    {
        Assert.Equal(
            DryRunAnalyzeRuleFunction.KnownSources.OrderBy(s => s, StringComparer.Ordinal),
            RuleEvidenceKeys.BySource.Keys.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void Catalog_lists_every_key_once_per_source()
    {
        foreach (var (source, keys) in RuleEvidenceKeys.BySource)
            Assert.True(keys.Count == keys.Distinct(StringComparer.Ordinal).Count(), $"{source} lists a key twice");
    }

    [Fact]
    public void Engine_copies_the_catalog_auto_fields()
    {
        Assert.Equal(RuleEvidenceKeys.AutoFields, RuleEngine.EvidenceAutoFields);
    }

    [Theory]
    [InlineData("event_type", "app_install_failed", "appName", "exists", "")]
    [InlineData("event_type", "app_install_failed", "", "exists", "")]
    [InlineData("event_type", "app_install_completed", "", "not_exists", "")]
    [InlineData("event_type", "app_install_failed", "missingField", "not_exists", "")]
    [InlineData("event_data", "app_install_failed", "appName", "equals", "Contoso VPN")]
    [InlineData("event_count", "app_install_failed", "", "count_gte", "2")]
    [InlineData("event_count", "app_install_failed", "appId", "count_per_group_gte", "2")]
    public void Simple_sources_write_only_declared_keys(string source, string eventType, string dataField, string op, string value)
    {
        var condition = new RuleCondition
        {
            Signal = "sig",
            Source = source,
            EventType = eventType,
            DataField = dataField,
            Operator = op,
            Value = value,
            Required = true,
        };

        var dry = Engine().DryRunEvaluate(Rule(condition), Events());

        var matched = Assert.IsAssignableFrom<IDictionary<string, object>>(dry.MatchedConditions);
        Assert.True(matched.ContainsKey("sig"), $"{source}/{op} did not match the fixture");
        AssertDeclared(source, matched["sig"]);
    }

    private static AnalyzeRule Rule(RuleCondition condition) => new()
    {
        RuleId = "TEST-EVIDENCE-001",
        Title = "Evidence keys",
        Severity = "warning",
        Category = "apps",
        Explanation = "x",
        Conditions = new List<RuleCondition> { condition },
    };

    private static List<EnrollmentEvent> Events()
    {
        var appId = "00000000-0000-4000-8000-000000000003";
        return Enumerable.Range(0, 2).Select(i => new EnrollmentEvent
        {
            EventId = $"evt-{i}",
            TenantId = TenantId,
            SessionId = SessionId,
            EventType = "app_install_failed",
            Timestamp = new DateTime(2026, 5, 28, 10, 16, 0, DateTimeKind.Utc).AddMinutes(i),
            Sequence = 100 + i,
            Data = new Dictionary<string, object>
            {
                ["appId"] = appId,
                ["appName"] = "Contoso VPN",
                ["errorCode"] = "0x80d03805",
                ["errorPatternId"] = "IME-ERROR-REPORT",
                ["status"] = "Error",
            },
        }).ToList();
    }

    private static RuleEngine Engine()
    {
        var ruleRepo = new Mock<IRuleRepository>();
        var sessionRepo = new Mock<ISessionRepository>();
        var ruleService = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        return new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<RuleEvidenceKeysTests>.Instance);
    }
}

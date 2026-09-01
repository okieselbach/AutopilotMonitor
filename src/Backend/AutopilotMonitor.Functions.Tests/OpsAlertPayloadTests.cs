using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.Models.Notifications;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Ops-event payload delivery: the structured Details object reaches outbound channels, both as
/// readable facts (card + Telegram formats) and verbatim under "data" (generic JSON consumers).
/// Before this, an alert carried only category/event/severity and a tenant GUID.
/// </summary>
public class OpsAlertPayloadTests
{
    // ── Fact flattening ───────────────────────────────────────────────────

    [Fact]
    public void FlattenDetails_ProjectsTopLevelScalars()
    {
        var facts = OpsAlertDispatchService.FlattenDetails(
            """{"domainName":"contoso.example","daysLeft":3,"selfService":true}""");

        Assert.Equal(3, facts.Count);
        Assert.Equal("Domain Name", facts[0].Name);
        Assert.Equal("contoso.example", facts[0].Value);
        Assert.Equal("Days Left", facts[1].Name);
        Assert.Equal("3", facts[1].Value);
        Assert.Equal("Self Service", facts[2].Name);
        Assert.Equal("true", facts[2].Value);
    }

    [Fact]
    public void FlattenDetails_SkipsNullsAndNestedValues()
    {
        var facts = OpsAlertDispatchService.FlattenDetails(
            """{"domainName":null,"nested":{"a":1},"list":[1,2],"keep":"yes"}""");

        Assert.Equal(new[] { "Keep" }, facts.Select(f => f.Name));
    }

    [Fact]
    public void FlattenDetails_CapsFactCount()
    {
        var props = string.Join(",", Enumerable.Range(0, 30).Select(i => $"\"p{i}\":\"v\""));

        Assert.Equal(OpsAlertDispatchService.MaxDetailFacts,
            OpsAlertDispatchService.FlattenDetails("{" + props + "}").Count);
    }

    [Fact]
    public void FlattenDetails_TruncatesLongValues()
    {
        var longValue = new string('x', 400);

        var fact = Assert.Single(OpsAlertDispatchService.FlattenDetails($$"""{"note":"{{longValue}}"}"""));

        Assert.Equal(OpsAlertDispatchService.MaxDetailValueLength + 1, fact.Value.Length); // + ellipsis
        Assert.EndsWith("…", fact.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[1,2,3]")]          // array root
    [InlineData("\"a string\"")]     // scalar root
    public void FlattenDetails_MalformedOrNonObjectPayloadCostsNothing(string? json)
        => Assert.Empty(OpsAlertDispatchService.FlattenDetails(json));

    [Theory]
    [InlineData("domainName", "Domain Name")]
    [InlineData("trialExpiresUtc", "Trial Expires Utc")]
    [InlineData("id", "Id")]
    [InlineData("URL", "URL")]       // consecutive capitals are not split
    public void Humanize_TitleCasesCamelCaseKeys(string input, string expected)
        => Assert.Equal(expected, OpsAlertDispatchService.Humanize(input));

    // ── Generic JSON renderer contract ────────────────────────────────────

    [Fact]
    public void GenericJsonRenderer_EmbedsDataAsRealJsonNotAString()
    {
        var alert = new NotificationAlert
        {
            Title = "t", Summary = "s", EventType = "TenantTrialStarted",
            DataJson = """{"domainName":"contoso.example","selfService":true}""",
        };

        var payload = JObject.Parse(new GenericJsonRenderer().RenderToJson(alert));

        Assert.Equal(JTokenType.Object, payload["data"]!.Type);
        Assert.Equal("contoso.example", payload["data"]!["domainName"]!.Value<string>());
        Assert.True(payload["data"]!["selfService"]!.Value<bool>());
        Assert.Equal("TenantTrialStarted", payload["eventType"]!.Value<string>());
    }

    [Fact]
    public void GenericJsonRenderer_OmitsDataWhenAbsentOrUnusable()
    {
        var renderer = new GenericJsonRenderer();

        Assert.Null(JObject.Parse(renderer.RenderToJson(
            new NotificationAlert { Title = "t", Summary = "s" }))["data"]);

        // Malformed or non-object payloads must not change the shape consumers parse against.
        Assert.Null(JObject.Parse(renderer.RenderToJson(
            new NotificationAlert { Title = "t", Summary = "s", DataJson = "not-json" }))["data"]);
        Assert.Null(JObject.Parse(renderer.RenderToJson(
            new NotificationAlert { Title = "t", Summary = "s", DataJson = "[1,2]" }))["data"]);
    }

    [Fact]
    public void GenericJsonRenderer_SchemaVersionUnchangedByTheAdditiveDataKey()
        => Assert.Equal("1.0", JObject.Parse(new GenericJsonRenderer().RenderToJson(
            new NotificationAlert { Title = "t", Summary = "s", DataJson = """{"a":1}""" }))["schemaVersion"]!.Value<string>());

    // ── Telegram text carries the same information ────────────────────────

    [Fact]
    public void TelegramRendering_IncludesFlattenedFacts()
    {
        var alert = new NotificationAlert
        {
            Title = "Ops Alert: Tenant/TenantTrialStarted",
            Summary = "Pro trial started",
            Severity = NotificationSeverity.Info,
            Facts = OpsAlertDispatchService.FlattenDetails("""{"domainName":"contoso.example"}"""),
        };

        var text = TelegramNotificationService.RenderAlertText(alert);

        Assert.Contains("Domain Name: contoso.example", text);
        Assert.Contains("Pro trial started", text);
    }
}

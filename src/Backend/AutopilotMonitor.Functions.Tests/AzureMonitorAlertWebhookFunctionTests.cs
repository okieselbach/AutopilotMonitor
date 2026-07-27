using AutopilotMonitor.Functions.Functions.Infrastructure;
using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the pure helpers of <see cref="AzureMonitorAlertWebhookFunction"/> — the anonymous,
/// secret-gated bridge that turns Azure Monitor Common Alert Schema notifications into ops
/// events. Load-bearing bits: the secret gate is fail-closed (an unset app setting rejects
/// everything), severity mapping can never escalate on schema drift (unknown → Info), and
/// parsing is defensive (only the alert rule name is mandatory).
/// </summary>
public class AzureMonitorAlertWebhookFunctionTests
{
    // ── SecretMatches ──────────────────────────────────────────────────────

    [Fact]
    public void SecretMatches_accepts_equal_secrets()
    {
        Assert.True(AzureMonitorAlertWebhookFunction.SecretMatches("s3cret-value", "s3cret-value"));
    }

    [Theory]
    [InlineData("wrong", "right")]
    [InlineData("right-but-longer", "right")]
    [InlineData("", "configured")]
    [InlineData(null, "configured")]
    public void SecretMatches_rejects_mismatch_or_missing_provided(string? provided, string configured)
    {
        Assert.False(AzureMonitorAlertWebhookFunction.SecretMatches(provided, configured));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SecretMatches_fails_closed_when_not_configured(string? configured)
    {
        // While the app setting is unset, NOTHING matches — not even an empty provided value.
        Assert.False(AzureMonitorAlertWebhookFunction.SecretMatches("anything", configured));
        Assert.False(AzureMonitorAlertWebhookFunction.SecretMatches("", configured));
        Assert.False(AzureMonitorAlertWebhookFunction.SecretMatches(null, configured));
    }

    // ── MapSeverity ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Sev0", OpsEventSeverity.Critical)]
    [InlineData("Sev1", OpsEventSeverity.Error)]
    [InlineData("Sev2", OpsEventSeverity.Warning)]
    [InlineData("Sev3", OpsEventSeverity.Info)]
    [InlineData("Sev4", OpsEventSeverity.Info)]
    public void MapSeverity_maps_azure_scale_to_ops_scale(string azure, string expected)
    {
        Assert.Equal(expected, AzureMonitorAlertWebhookFunction.MapSeverity(azure));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Sev5")]
    [InlineData("Critical")] // someone posting the ops scale directly must not pass through
    public void MapSeverity_falls_back_to_info_on_unknown_values(string? azure)
    {
        Assert.Equal(OpsEventSeverity.Info, AzureMonitorAlertWebhookFunction.MapSeverity(azure));
    }

    // ── TryParse ───────────────────────────────────────────────────────────

    private const string FullPayload = """
        {
          "schemaId": "azureMonitorCommonAlertSchema",
          "data": {
            "essentials": {
              "alertId": "/subscriptions/xxx/providers/Microsoft.AlertsManagement/alerts/abc",
              "alertRule": "Portal RSC prefetch tail",
              "severity": "Sev2",
              "signalType": "Log",
              "monitorCondition": "Fired",
              "monitoringService": "Log Alerts V2",
              "alertTargetIDs": [
                "/subscriptions/xxx/resourcegroups/rg-autopilotmonitor-prd-gwc/providers/microsoft.insights/components/autopilotmonitorweb"
              ],
              "description": "RSC prefetches exceeding 10s",
              "firedDateTime": "2026-07-27T15:30:00.000Z"
            },
            "alertContext": {
              "condition": {
                "windowSize": "PT1H",
                "allOf": [
                  {
                    "searchQuery": "dependencies | where name contains '_rsc='",
                    "metricValue": 18.0,
                    "operator": "GreaterThan",
                    "threshold": "0"
                  }
                ]
              }
            }
          }
        }
        """;

    [Fact]
    public void TryParse_extracts_essentials_and_metric_value()
    {
        var alert = AzureMonitorAlertWebhookFunction.TryParse(FullPayload);

        Assert.NotNull(alert);
        Assert.Equal("Portal RSC prefetch tail", alert!.AlertRule);
        Assert.Equal("Sev2", alert.Severity);
        Assert.Equal("Fired", alert.MonitorCondition);
        Assert.Equal("RSC prefetches exceeding 10s", alert.Description);
        Assert.Equal("Log Alerts V2", alert.MonitoringService);
        // No targetResourceName in the payload → resource name segment of the first target ID.
        Assert.Equal("autopilotmonitorweb", alert.TargetResource);
        Assert.Equal(18.0, alert.MetricValue);
    }

    [Fact]
    public void TryParse_prefers_explicit_target_resource_name()
    {
        const string payload = """
            {"data":{"essentials":{"alertRule":"r","targetResourceName":"explicit-name",
            "alertTargetIDs":["/sub/x/other-name"]}}}
            """;
        var alert = AzureMonitorAlertWebhookFunction.TryParse(payload);
        Assert.Equal("explicit-name", alert!.TargetResource);
    }

    [Fact]
    public void TryParse_minimal_payload_needs_only_the_alert_rule()
    {
        var alert = AzureMonitorAlertWebhookFunction.TryParse(
            """{"data":{"essentials":{"alertRule":"minimal"}}}""");

        Assert.NotNull(alert);
        Assert.Equal("minimal", alert!.AlertRule);
        Assert.Null(alert.Severity);
        Assert.Equal("Fired", alert.MonitorCondition); // default when absent
        Assert.Null(alert.Description);
        Assert.Null(alert.MetricValue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]                                          // no data
    [InlineData("""{"data":{}}""")]                             // no essentials
    [InlineData("""{"data":{"essentials":{}}}""")]              // no alertRule
    [InlineData("""{"data":{"essentials":{"alertRule":""}}}""")] // empty alertRule
    [InlineData("""{"data":{"essentials":{"alertRule":123}}}""")] // wrong type
    public void TryParse_rejects_unusable_payloads(string? body)
    {
        Assert.Null(AzureMonitorAlertWebhookFunction.TryParse(body!));
    }

    [Fact]
    public void TryParse_tolerates_malformed_alert_context()
    {
        // alertContext with unexpected shapes must not break essentials extraction.
        const string payload = """
            {"data":{"essentials":{"alertRule":"r","severity":"Sev0"},
            "alertContext":{"condition":{"allOf":"not-an-array"}}}}
            """;
        var alert = AzureMonitorAlertWebhookFunction.TryParse(payload);
        Assert.NotNull(alert);
        Assert.Equal("Sev0", alert!.Severity);
        Assert.Null(alert.MetricValue);
    }
}

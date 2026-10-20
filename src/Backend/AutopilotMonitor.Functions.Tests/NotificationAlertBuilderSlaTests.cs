using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The SLA alerts name the tenant the way its people know it: the summary and the "Tenant" fact
/// carry the display name (company name, else domain, else id — <see cref="TenantDisplayName"/>),
/// and the id travels on in the "Tenant ID" fact right after it, for integrations that key on it.
/// </summary>
public class NotificationAlertBuilderSlaTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTime ResolvedAt = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Breach_alert_names_the_tenant_and_carries_its_id_right_after()
    {
        var alert = NotificationAlertBuilder.BuildSlaBreachAlert(TenantId, "Contoso", 80, 95, 10, 2, "SuccessRate");

        AssertTenantFacts(alert, "Contoso");
        Assert.DoesNotContain(TenantId, alert.Summary);
    }

    [Fact]
    public void Resolved_alert_names_the_tenant_in_its_summary()
    {
        var alert = NotificationAlertBuilder.BuildSlaResolvedAlert(TenantId, "Contoso", "SuccessRate", 97, 95,
            ResolvedAt.AddHours(-3), ResolvedAt);

        Assert.Equal("SLA breach resolved for Contoso: Success Rate is back within target.", alert.Summary);
        AssertTenantFacts(alert, "Contoso");
        Assert.Equal("Breach Type", alert.Facts[2].Name);
    }

    [Fact]
    public void Consecutive_failures_alert_names_the_tenant_in_its_summary()
    {
        var alert = NotificationAlertBuilder.BuildConsecutiveFailuresAlert(TenantId, "Contoso", 3, "DESKTOP-0042", "timeout");

        Assert.Equal("Alert: 3 enrollments failed in a row for Contoso", alert.Summary);
        AssertTenantFacts(alert, "Contoso");
        Assert.Equal("Consecutive Failures", alert.Facts[2].Name);
    }

    [Fact]
    public void A_tenant_without_names_falls_back_to_its_id()
    {
        var name = TenantDisplayName.Of(new TenantConfiguration { TenantId = TenantId }, TenantId);
        var alert = NotificationAlertBuilder.BuildConsecutiveFailuresAlert(TenantId, name, 5, null, null);

        Assert.Equal($"Alert: 5 enrollments failed in a row for {TenantId}", alert.Summary);
        AssertTenantFacts(alert, TenantId);
    }

    [Theory]
    [InlineData("Contoso Ltd", "contoso.com", "Contoso Ltd")]
    [InlineData("  ", "contoso.com", "contoso.com")]
    [InlineData(null, null, TenantId)]
    [InlineData(null, "", TenantId)]
    public void Display_name_prefers_the_company_then_the_domain_then_the_id(string? companyName, string? domainName, string expected)
    {
        var config = new TenantConfiguration { TenantId = TenantId, CompanyName = companyName, DomainName = domainName! };

        Assert.Equal(expected, TenantDisplayName.Of(config, TenantId));
    }

    [Fact]
    public void Display_name_without_a_configuration_is_the_id()
        => Assert.Equal(TenantId, TenantDisplayName.Of(null, TenantId));

    private static void AssertTenantFacts(NotificationAlert alert, string expectedName)
    {
        Assert.Equal("Tenant", alert.Facts[0].Name);
        Assert.Equal(expectedName, alert.Facts[0].Value);
        Assert.Equal("Tenant ID", alert.Facts[1].Name);
        Assert.Equal(TenantId, alert.Facts[1].Value);
        Assert.Single(alert.Facts, f => f.Name == "Tenant ID");
    }
}

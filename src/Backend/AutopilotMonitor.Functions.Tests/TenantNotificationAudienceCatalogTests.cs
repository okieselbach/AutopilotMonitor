using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="TenantNotificationAudienceCatalog"/>.
///
/// CORRECTNESS GUARD: The catalog is the single source of truth for who sees which tenant
/// notification type. An unknown type must fail closed (Admin) so that a newly added type
/// does not leak to non-admin members until it is explicitly registered here.
/// </summary>
public class TenantNotificationAudienceCatalogTests
{
    [Theory]
    [InlineData("hardware_rejection", NotificationAudience.Admin)]
    [InlineData("tpm_pss_unsupported", NotificationAudience.Admin)]
    [InlineData("sla_breach", NotificationAudience.Member)]
    [InlineData("sla_consecutive_failures", NotificationAudience.Member)]
    [InlineData("sla_resolved", NotificationAudience.Member)]
    [InlineData("rule_frequency_regression", NotificationAudience.Admin)]
    [InlineData("app_version_duration_regression", NotificationAudience.Admin)]
    public void Resolve_KnownTypes_ReturnsExpectedAudience(string type, NotificationAudience expected)
    {
        Assert.Equal(expected, TenantNotificationAudienceCatalog.Resolve(type));
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        Assert.Equal(NotificationAudience.Admin, TenantNotificationAudienceCatalog.Resolve("HARDWARE_REJECTION"));
        Assert.Equal(NotificationAudience.Member, TenantNotificationAudienceCatalog.Resolve("Sla_Breach"));
    }

    [Theory]
    [InlineData("brand_new_type_not_in_catalog")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Resolve_UnknownOrEmpty_FailsClosedToAdmin(string? type)
    {
        Assert.Equal(NotificationAudience.Admin, TenantNotificationAudienceCatalog.Resolve(type));
    }
}

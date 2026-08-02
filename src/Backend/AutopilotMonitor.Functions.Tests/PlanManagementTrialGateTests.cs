using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="PlanManagementFunction.EvaluateTrialStart"/> — the self-service
/// trial verdict, including the Pro-requires-contact gate. The check ORDER is semantic
/// and pinned here: terminal conditions (consumed / already Pro) must win over the
/// contact-address prompt, and the contact gate exists only at this entry point (never
/// as a runtime lockout for existing Pro tenants).
/// </summary>
public class PlanManagementTrialGateTests
{
    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
    private const string Contact = "it-ops@contoso.com";

    [Fact]
    public void Allowed_WhenCommunityWithContactAndUnconsumedTrial()
    {
        var config = new TenantConfiguration { ContactEmail = Contact };

        Assert.Null(PlanManagementFunction.EvaluateTrialStart(config, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Denied_ContactEmailRequired_WhenNoAddressStored(string? contact)
    {
        var config = new TenantConfiguration { ContactEmail = contact };

        var deny = PlanManagementFunction.EvaluateTrialStart(config, Now);

        Assert.Equal("ContactEmailRequired", deny?.Error);
    }

    [Fact]
    public void Denied_TrialAlreadyConsumed_WinsOverMissingContact()
    {
        // A consumed trial is terminal — prompting for a contact address would be pointless.
        var config = new TenantConfiguration { TrialConsumed = true, ContactEmail = null };

        var deny = PlanManagementFunction.EvaluateTrialStart(config, Now);

        Assert.Equal("TrialAlreadyConsumed", deny?.Error);
    }

    [Fact]
    public void Denied_AlreadyPro_WinsOverMissingContact()
    {
        // Existing Pro tenants without a contact address are nagged via the dashboard
        // banner, never blocked — the verdict must name the Pro state, not the address.
        var config = new TenantConfiguration { PlanTier = "pro", ContactEmail = null };

        var deny = PlanManagementFunction.EvaluateTrialStart(config, Now);

        Assert.Equal("AlreadyPro", deny?.Error);
    }

    [Fact]
    public void Denied_AlreadyPro_ForActiveTrialEdition()
    {
        var config = new TenantConfiguration
        {
            TrialExpiresUtc = Now.AddDays(5),
            TrialConsumed = false, // hypothetical shape — edition resolution alone must deny
            ContactEmail = Contact,
        };

        var deny = PlanManagementFunction.EvaluateTrialStart(config, Now);

        Assert.Equal("AlreadyPro", deny?.Error);
    }
}

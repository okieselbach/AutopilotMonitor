using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="PlanManagementFunction.EvaluateTrialStart"/> — the self-service
/// trial verdict, including the Pro-requires-contact-profile gate (address + company
/// name). The check ORDER is semantic and pinned here: terminal conditions (consumed /
/// already Pro) must win over the contact-profile prompt, and the contact gate exists only
/// at this entry point (never as a runtime lockout for existing Pro tenants).
/// </summary>
public class PlanManagementTrialGateTests
{
    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
    private const string Contact = "it-ops@contoso.com";
    private const string Company = "Contoso Ltd.";

    [Fact]
    public void Allowed_WhenCommunityWithContactProfileAndUnconsumedTrial()
    {
        var config = new TenantConfiguration { ContactEmail = Contact, CompanyName = Company };

        Assert.Null(PlanManagementFunction.EvaluateTrialStart(config, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Denied_ContactProfileRequired_WhenNoAddressStored(string? contact)
    {
        var config = new TenantConfiguration { ContactEmail = contact, CompanyName = Company };

        var deny = PlanManagementFunction.EvaluateTrialStart(config, Now);

        Assert.Equal("ContactProfileRequired", deny?.Code);
        Assert.Contains("contact address", deny?.Message);
        Assert.DoesNotContain("company name", deny?.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Denied_ContactProfileRequired_WhenNoCompanyNameStored(string? company)
    {
        var config = new TenantConfiguration { ContactEmail = Contact, CompanyName = company };

        var deny = PlanManagementFunction.EvaluateTrialStart(config, Now);

        Assert.Equal("ContactProfileRequired", deny?.Code);
        Assert.Contains("company name", deny?.Message);
        Assert.DoesNotContain("contact address", deny?.Message);
    }

    [Fact]
    public void Denied_ContactProfileRequired_NamesEveryMissingPartInOneVerdict()
    {
        // One round trip to learn everything the entry point wants — never "fix A, then
        // discover B".
        var config = new TenantConfiguration();

        var deny = PlanManagementFunction.EvaluateTrialStart(config, Now);

        Assert.Equal("ContactProfileRequired", deny?.Code);
        Assert.Contains("contact address and company name", deny?.Message);
        Assert.Equal(new[] { "contact address", "company name" },
            PlanManagementFunction.MissingContactProfileParts(config));
    }

    [Fact]
    public void Denied_TrialAlreadyConsumed_WinsOverMissingContact()
    {
        // A consumed trial is terminal — prompting for a contact profile would be pointless.
        var config = new TenantConfiguration { TrialConsumed = true, ContactEmail = null, CompanyName = null };

        var deny = PlanManagementFunction.EvaluateTrialStart(config, Now);

        Assert.Equal("TrialAlreadyConsumed", deny?.Code);
    }

    [Fact]
    public void Denied_AlreadyPro_WinsOverMissingContact()
    {
        // Existing Pro tenants without a contact address are nagged via the dashboard
        // banner, never blocked — the verdict must name the Pro state, not the address.
        var config = new TenantConfiguration { PlanTier = "pro", ContactEmail = null, CompanyName = null };

        var deny = PlanManagementFunction.EvaluateTrialStart(config, Now);

        Assert.Equal("AlreadyPro", deny?.Code);
    }

    [Fact]
    public void Denied_AlreadyPro_ForActiveTrialEdition()
    {
        var config = new TenantConfiguration
        {
            TrialExpiresUtc = Now.AddDays(5),
            TrialConsumed = false, // hypothetical shape — edition resolution alone must deny
            ContactEmail = Contact,
            CompanyName = Company,
        };

        var deny = PlanManagementFunction.EvaluateTrialStart(config, Now);

        Assert.Equal("AlreadyPro", deny?.Code);
    }
}

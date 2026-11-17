using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Recipient of an email-template test send: the default follows the real welcome/farewell
/// recipient (activation notification address) before ContactEmail and the caller's UPN; a named
/// recipient passes the same single-address rule as an e-mail channel recipient.
/// </summary>
public class EmailTemplateTestRecipientTests
{
    [Fact]
    public void Default_is_the_notification_address_even_when_ContactEmail_is_set()
        => Assert.Equal(("notify@example.invalid", "notificationEmail"),
            EmailTemplatesFunction.ResolveDefaultTestRecipient("notify@example.invalid", "contact@example.invalid", "admin@example.invalid"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Default_falls_back_to_ContactEmail_without_a_notification_address(string? notificationEmail)
        => Assert.Equal(("contact@example.invalid", "contactEmail"),
            EmailTemplatesFunction.ResolveDefaultTestRecipient(notificationEmail, "contact@example.invalid", "admin@example.invalid"));

    [Fact]
    public void Default_falls_back_to_the_caller_UPN_last()
        => Assert.Equal(("admin@example.invalid", "userPrincipalName"),
            EmailTemplatesFunction.ResolveDefaultTestRecipient(null, " ", "admin@example.invalid"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("System")]
    public void Default_is_none_when_no_field_holds_an_address(string? upn)
        => Assert.Equal(((string?)null, (string?)null),
            EmailTemplatesFunction.ResolveDefaultTestRecipient(null, null, upn));

    [Theory]
    [InlineData("ops@example.invalid")]
    [InlineData("first.last+tag@sub.example.invalid")]
    public void Named_recipient_accepts_one_plain_address(string address)
        => Assert.Null(TenantConfigValidation.ValidateEmailAddress(address));

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("Ops <ops@example.invalid>")]
    [InlineData("ops@localhost")]
    [InlineData("ops@example.invalid; second@example.invalid")]
    public void Named_recipient_rejects_malformed_named_hostless_and_lists(string address)
        => Assert.Equal($"\"{address}\" is not a valid e-mail address.", TenantConfigValidation.ValidateEmailAddress(address));
}

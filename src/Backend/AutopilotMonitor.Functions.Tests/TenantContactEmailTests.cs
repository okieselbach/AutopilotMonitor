using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// ContactEmail is the address enforcement actions and service notices are sent to.
/// The seed must never overwrite a value the tenant owns — that is the whole contract.
/// </summary>
public class TenantContactEmailTests
{
    [Fact]
    public void ContactEmail_defaults_to_null_so_absence_is_distinguishable_from_empty()
    {
        var config = new TenantConfiguration();
        Assert.Null(config.ContactEmail);
    }

    [Fact]
    public void ContactEmail_survives_a_round_trip_through_the_model()
    {
        var config = new TenantConfiguration { ContactEmail = "it-operations@contoso.com" };
        Assert.Equal("it-operations@contoso.com", config.ContactEmail);
    }
}

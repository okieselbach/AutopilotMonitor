using AutopilotMonitor.Functions.Security;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Reads the AADSTS code out of Entra's token error body. The body below has the shape Entra
/// really sends for a tenant whose enterprise application was removed; the ids are synthetic.
/// </summary>
public class EntraTokenRejectionTests
{
    private const string MissingServicePrincipalBody =
        "{\"error\":\"invalid_client\"," +
        "\"error_description\":\"AADSTS7000229: The client application aaaaaaaa-0000-0000-0000-000000000001 is missing service principal in the tenant 11111111-1111-1111-1111-111111111111. See instructions here: https://go.microsoft.com/fwlink/?linkid=2225119\\r\\nTrace ID: 00000000-0000-0000-0000-000000000002\\r\\nCorrelation ID: 00000000-0000-0000-0000-000000000003\\r\\nTimestamp: 2026-11-01 08:00:00Z\"," +
        "\"error_codes\":[7000229]," +
        "\"timestamp\":\"2026-11-01 08:00:00Z\"," +
        "\"trace_id\":\"00000000-0000-0000-0000-000000000002\"," +
        "\"correlation_id\":\"00000000-0000-0000-0000-000000000003\"," +
        "\"error_uri\":\"https://login.microsoftonline.com/error?code=7000229\"}";

    [Fact]
    public void Parse_reads_the_code_and_keeps_only_the_first_line_of_the_description()
    {
        var rejection = EntraTokenRejection.Parse(401, MissingServicePrincipalBody);

        Assert.Equal(401, rejection.StatusCode);
        Assert.Equal("AADSTS7000229", rejection.ErrorCode);
        Assert.True(rejection.IsServicePrincipalMissing);
        Assert.False(rejection.IsOurCredential);
        Assert.StartsWith("AADSTS7000229: The client application", rejection.Description);
        Assert.DoesNotContain("Trace ID", rejection.Description);
    }

    [Fact]
    public void Parse_falls_back_to_the_description_without_error_codes()
    {
        var rejection = EntraTokenRejection.Parse(400, "{\"error\":\"invalid_request\",\"error_description\":\"AADSTS53003: Access has been blocked by Conditional Access policies.\"}");

        Assert.Equal("AADSTS53003", rejection.ErrorCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>Bad Request</html>")]
    [InlineData("[1,2]")]
    [InlineData("{\"error\":\"invalid_request\"}")]
    public void Parse_keeps_only_the_status_when_the_body_carries_no_code(string? body)
    {
        var rejection = EntraTokenRejection.Parse(400, body);

        Assert.Equal(400, rejection.StatusCode);
        Assert.Null(rejection.ErrorCode);
        Assert.Null(rejection.Description);
        Assert.False(rejection.IsServicePrincipalMissing);
        Assert.False(rejection.IsOurCredential);
    }

    [Fact]
    public void Parse_cuts_a_long_description()
    {
        var body = "{\"error_description\":\"AADSTS50000: " + new string('x', 1000) + "\"}";

        Assert.Equal(300, EntraTokenRejection.Parse(400, body).Description!.Length);
    }

    [Theory]
    [InlineData("AADSTS7000215", true)]
    [InlineData("AADSTS7000222", true)]
    [InlineData("AADSTS7000218", true)]
    [InlineData("aadsts7000222", true)]
    [InlineData("AADSTS7000229", false)]
    [InlineData("AADSTS7000112", false)]
    [InlineData("AADSTS53003", false)]
    public void IsOurCredential_only_blames_our_own_secret(string code, bool expected)
    {
        Assert.Equal(expected, new EntraTokenRejection(401, code, null).IsOurCredential);
    }
}

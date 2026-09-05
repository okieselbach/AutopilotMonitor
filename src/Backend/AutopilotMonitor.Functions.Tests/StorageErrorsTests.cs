using Azure;
using AutopilotMonitor.Functions.Helpers;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

public class StorageErrorsTests
{
    private static RequestFailedException Failure(int status, string? code = null)
        => new RequestFailedException(status, "storage said no", code, null);

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void Transient_statuses_are_transient(int status)
        => Assert.True(StorageErrors.IsTransient(Failure(status)));

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(412)]
    [InlineData(413)]
    public void Client_side_statuses_are_not_transient(int status)
        => Assert.False(StorageErrors.IsTransient(Failure(status)));

    [Fact]
    public void Payload_too_large_by_status()
        => Assert.True(StorageErrors.IsPayloadTooLarge(Failure(413)));

    [Theory]
    [InlineData("RequestBodyTooLarge")]
    [InlineData("EntityTooLarge")]
    [InlineData("PropertyValueTooLarge")]
    public void Payload_too_large_by_error_code_even_with_a_400(string code)
        => Assert.True(StorageErrors.IsPayloadTooLarge(Failure(400, code)));

    [Fact]
    public void A_plain_400_is_not_payload_too_large()
        => Assert.False(StorageErrors.IsPayloadTooLarge(Failure(400, "InvalidInput")));

    [Fact]
    public void Already_exists_is_409_or_the_azurite_400_shape()
    {
        Assert.True(StorageErrors.IsAlreadyExists(Failure(409)));
        Assert.True(StorageErrors.IsAlreadyExists(Failure(400, "EntityAlreadyExists")));
        Assert.False(StorageErrors.IsAlreadyExists(Failure(400, "InvalidInput")));
        Assert.False(StorageErrors.IsAlreadyExists(Failure(412)));
    }

    [Fact]
    public void Null_is_never_classified()
    {
        Assert.False(StorageErrors.IsTransient(null!));
        Assert.False(StorageErrors.IsPayloadTooLarge(null!));
        Assert.False(StorageErrors.IsAlreadyExists(null!));
    }
}

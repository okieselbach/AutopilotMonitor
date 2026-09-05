using Azure;
using AutopilotMonitor.Functions.Helpers;
using Xunit;
using System;
using System.IO;
using System.Net.Http;

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

    [Fact]
    public void No_response_at_all_is_transient()
        => Assert.True(StorageErrors.IsTransient(Failure(0)));

    // ---- Exception overload: what an exhausted SDK budget looks like on a path without a caller token ----

    [Fact]
    public void Sdk_network_timeout_is_transient()
        => Assert.True(StorageErrors.IsTransient((Exception)new TaskCanceledException("exceeded the configured timeout")));

    [Fact]
    public void Socket_failures_are_transient()
    {
        Assert.True(StorageErrors.IsTransient(new IOException("connection reset")));
        Assert.True(StorageErrors.IsTransient(new HttpRequestException("name resolution failed")));
    }

    [Fact]
    public void Aggregate_of_retried_attempts_is_transient_only_when_every_attempt_was()
    {
        var allTransient = new AggregateException(new TaskCanceledException(), Failure(503), new IOException());
        var mixed = new AggregateException(new TaskCanceledException(), Failure(404));

        Assert.True(StorageErrors.IsTransient(allTransient));
        Assert.False(StorageErrors.IsTransient(mixed));
        Assert.False(StorageErrors.IsTransient(new AggregateException()));
    }

    [Fact]
    public void Ordinary_exceptions_are_not_transient()
    {
        Assert.False(StorageErrors.IsTransient(new InvalidOperationException()));
        Assert.False(StorageErrors.IsTransient((Exception)Failure(412)));
        Assert.False(StorageErrors.IsTransient((Exception?)null!));
    }
}

using System.Net;
using Azure;
using AutopilotMonitor.Functions.Functions.Ingest;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The agent's uploader halves its batch only on 413 and treats every 5xx as "replay later";
/// <see cref="IngestTelemetryFunction.ClassifyStorageFailure"/> is what keeps an oversize
/// transaction from surfacing as a 500 the device would retry forever.
/// </summary>
public class IngestStorageFailureClassificationTests
{
    private static RequestFailedException Failure(int status, string? code = null)
        => new RequestFailedException(status, "storage", code, null);

    [Fact]
    public void Oversize_transaction_becomes_413_without_retry_after()
    {
        var (status, _, retryAfter) = IngestTelemetryFunction.ClassifyStorageFailure(Failure(413, "RequestBodyTooLarge"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Null(retryAfter);
    }

    [Fact]
    public void Oversize_reported_as_400_with_size_code_still_becomes_413()
    {
        var (status, _, _) = IngestTelemetryFunction.ClassifyStorageFailure(Failure(400, "EntityTooLarge"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
    }

    [Theory]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(500)]
    public void Transient_storage_failure_becomes_503_with_retry_after(int storageStatus)
    {
        var (status, _, retryAfter) = IngestTelemetryFunction.ClassifyStorageFailure(Failure(storageStatus));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(IngestTelemetryFunction.StorageRetryAfterSeconds, retryAfter);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(400)]
    public void Anything_else_stays_500(int storageStatus)
    {
        var (status, _, retryAfter) = IngestTelemetryFunction.ClassifyStorageFailure(Failure(storageStatus, "InvalidInput"));
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Null(retryAfter);
    }
}

using AutopilotMonitor.Functions.Security;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Request-derived blob names must be flat. Azure.Storage.Blobs keeps '/' in the blob URI and
/// System.Uri collapses '..' segments, so "x/../../other-container/y" would resolve outside the
/// intended container. The guard is the last line at every GetBlobClient sink.
/// </summary>
public class BlobNameGuardTests
{
    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111_aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa_diag_request_20260829_120000.zip")]
    [InlineData("report.zip")]
    [InlineData("a.b.c.zip")]
    public void IsFlat_AcceptsFlatNames(string name)
    {
        Assert.True(BlobNameGuard.IsFlat(name));
        Assert.Equal(name, BlobNameGuard.EnsureFlat(name, "name"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TA_x/../../hosted-diagnostics/victim/payload_diag_request_1.zip")]
    [InlineData("a/b.zip")]
    [InlineData("a\\b.zip")]
    [InlineData("..zip")]
    [InlineData("a%2F..%2Fb.zip")]
    [InlineData("a?b.zip")]
    [InlineData("a#b.zip")]
    public void IsFlat_RejectsPathShapedNames(string? name)
    {
        Assert.False(BlobNameGuard.IsFlat(name));
        Assert.Throws<ArgumentException>(() => BlobNameGuard.EnsureFlat(name, "name"));
    }
}

using System.Net;
using AutopilotMonitor.Functions.Services.Vulnerability;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="NvdApiClient.SearchCvesByCpeAsync"/> reads a product's CVEs in full pages: one page
/// when NVD has no more, the first and the last page when it has, and never half an answer. A
/// request that runs into its own time limit fails that request; only the caller's cancellation
/// ends the call with an exception.
/// </summary>
[Collection(NvdTestState.Collection)]
public sealed class NvdApiClientPagingTests : IDisposable
{
    private const string Cpe = "cpe:2.3:a:google:chrome";

    public NvdApiClientPagingTests() => NvdTestState.Reset();

    public void Dispose() => NvdTestState.Reset();

    /// <summary>NVD pages by position: the page at <c>startIndex</c> holds the CVEs numbered from there.</summary>
    private static HttpResponseMessage PageOf(int totalResults, NvdRequest request) => NvdFixture.Page(
        totalResults,
        Enumerable.Range(request.StartIndex + 1, Math.Min(request.ResultsPerPage, totalResults - request.StartIndex))
            .Select(n => new NvdVulnerabilityItem { Cve = new NvdCve { Id = $"CVE-2020-{n:00000}" } }));

    [Theory]
    [InlineData(0)]
    [InlineData(137)]
    [InlineData(2000)]
    public async Task UpToOnePage_IsOneRequestForAFullPageFromTheStart(int totalResults)
    {
        var nvd = new NvdStubHandler(request => PageOf(totalResults, request));

        var result = await NvdFixture.Client(nvd).SearchCvesByCpeAsync(Cpe);

        var request = Assert.Single(nvd.Requests);
        Assert.Equal(Cpe, request.Cpe);
        Assert.EndsWith("&resultsPerPage=2000&startIndex=0", request.Uri.Query);
        Assert.True(result.Succeeded);
        Assert.Equal(totalResults, result.Data.TotalResults);
        Assert.Equal(totalResults, result.Data.Vulnerabilities.Count);
    }

    [Theory]
    [InlineData(4500, 2500, 4000)] // the middle 500 lie years back and are left out
    [InlineData(2500, 500, 2500)]  // the two pages overlap by 1500
    [InlineData(2001, 1, 2001)]
    public async Task MoreThanOnePage_AlsoFetchesTheLastPageAndJoinsBoth(int totalResults, int lastPageStart, int expectedCves)
    {
        var nvd = new NvdStubHandler(request => PageOf(totalResults, request));

        var result = await NvdFixture.Client(nvd).SearchCvesByCpeAsync(Cpe);

        Assert.Equal(new[] { 0, lastPageStart }, nvd.Requests.Select(r => r.StartIndex));
        Assert.All(nvd.Requests, r => Assert.Equal(2000, r.ResultsPerPage));
        Assert.True(result.Succeeded);
        Assert.Equal(totalResults, result.Data.TotalResults);

        var ids = result.Data.Vulnerabilities.Select(v => v.Cve!.Id!).ToList();
        Assert.Equal(expectedCves, ids.Count);
        Assert.Equal(expectedCves, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("CVE-2020-00001", ids);
        Assert.Contains($"CVE-2020-{totalResults:00000}", ids);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    public async Task LastPageFails_TheWholeCallFails(HttpStatusCode lastPageStatus, bool throttled)
    {
        var nvd = new NvdStubHandler(request =>
            request.StartIndex == 0 ? PageOf(4500, request) : NvdFixture.Status(lastPageStatus));

        var result = await NvdFixture.Client(nvd).SearchCvesByCpeAsync(Cpe);

        Assert.Equal(2, nvd.Requests.Count);
        Assert.False(result.Succeeded);
        Assert.Equal(throttled, result.Throttled);
        // No half answer: the first page is not handed out without the newest CVEs.
        Assert.Empty(result.Data.Vulnerabilities);
        Assert.Equal(throttled, NvdApiClient.IsInCooldown);
    }

    [Fact]
    public async Task FirstPageFails_NoSecondRequest()
    {
        var nvd = new NvdStubHandler(_ => NvdFixture.Status(HttpStatusCode.ServiceUnavailable));

        var result = await NvdFixture.Client(nvd).SearchCvesByCpeAsync(Cpe);

        Assert.Single(nvd.Requests);
        Assert.False(result.Succeeded);
        Assert.False(result.Throttled);
    }

    /// <summary>
    /// The 45 s limit itself is not waited for here. What it does to a request is the same as
    /// this: the send is cancelled although the caller's token is still live.
    /// </summary>
    [Fact]
    public async Task RequestCancelledByItsOwnTimeLimit_IsAFailedResultNotAnException()
    {
        var nvd = new NvdStubHandler((_, _) => throw new TaskCanceledException("the request ran out of time"));
        using var caller = new CancellationTokenSource();

        var result = await NvdFixture.Client(nvd).SearchCvesByCpeAsync(Cpe, caller.Token);

        Assert.False(result.Succeeded);
        Assert.False(result.Throttled);
        Assert.Empty(result.Data.Vulnerabilities);
    }

    /// <summary>The limit also covers the body, which is read after the headers have arrived.</summary>
    [Fact]
    public async Task BodyReadCancelledByItsOwnTimeLimit_IsAFailedResultNotAnException()
    {
        var nvd = new NvdStubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new CancelledBody() });

        var result = await NvdFixture.Client(nvd).SearchCvesByCpeAsync(Cpe);

        Assert.False(result.Succeeded);
        Assert.False(result.Throttled);
    }

    [Fact]
    public async Task CallerCancelsDuringTheRequest_Propagates()
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nvd = new NvdStubHandler(async (_, ct) =>
        {
            sent.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return NvdFixture.Page();
        });
        using var caller = new CancellationTokenSource();

        var call = NvdFixture.Client(nvd).SearchCvesByCpeAsync(Cpe, caller.Token);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(30));
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Fact]
    public async Task CallerCancelledBeforeTheCall_PropagatesWithoutARequest()
    {
        var nvd = new NvdStubHandler(_ => NvdFixture.Page());
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NvdFixture.Client(nvd).SearchCvesByCpeAsync(Cpe, caller.Token));

        Assert.Empty(nvd.Requests);
    }

    [Fact]
    public async Task ReadsTheDateCisaListedACveAsExploited()
    {
        // Field names as NVD sends them (API 2.0), shaped like the public CVE-2023-4863.
        const string body = """
            {"resultsPerPage":2,"startIndex":0,"totalResults":2,"format":"NVD_CVE","version":"2.0",
             "vulnerabilities":[
               {"cve":{"id":"CVE-2023-4863","published":"2023-09-12T15:15:24.327","lastModified":"2025-07-30T01:00:01.000",
                 "vulnStatus":"Analyzed","cisaExploitAdd":"2023-09-13","cisaActionDue":"2023-10-04",
                 "descriptions":[{"lang":"en","value":"Heap buffer overflow in libwebp."}],
                 "metrics":{"cvssMetricV31":[{"type":"Primary","cvssData":{"version":"3.1","vectorString":"CVSS:3.1/AV:N/AC:L/PR:N/UI:R/S:U/C:H/I:H/A:H","baseScore":8.8,"baseSeverity":"HIGH"}}]},
                 "configurations":[{"nodes":[{"operator":"OR","negate":false,"cpeMatch":[
                   {"vulnerable":true,"criteria":"cpe:2.3:a:google:chrome:*:*:*:*:*:*:*:*","versionEndExcluding":"116.0.5845.187"}]}]}]}},
               {"cve":{"id":"CVE-2024-0001","published":"2024-01-02T10:00:00.000","descriptions":[],"configurations":[]}}
             ]}
            """;
        var nvd = new NvdStubHandler(_ => NvdFixture.Json(body));

        var result = await NvdFixture.Client(nvd).SearchCvesByCpeAsync(Cpe);

        Assert.True(result.Succeeded);
        var listed = result.Data.Vulnerabilities.Single(v => v.Cve!.Id == "CVE-2023-4863").Cve!;
        Assert.Equal("2023-09-13", listed.CisaExploitAdd);
        Assert.Equal("116.0.5845.187", listed.Configurations.Single().Nodes.Single().CpeMatch.Single().VersionEndExcluding);
        Assert.Null(result.Data.Vulnerabilities.Single(v => v.Cve!.Id == "CVE-2024-0001").Cve!.CisaExploitAdd);
    }

    /// <summary>A body whose read ends the way a cancelled read does.</summary>
    private sealed class CancelledBody : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new CancelledStream());

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new OperationCanceledException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class CancelledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new OperationCanceledException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

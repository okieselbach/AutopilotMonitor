using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Vulnerability;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Operator tool, not a check: reduces a downloaded NVD dictionary feed with the real reader and
/// writes down what came out and what it cost, so a change to the reader or to NVD's feed can be
/// measured before it ships.
/// <code>AM_CPE_FEED_FILE=&lt;path to nvdcpe-2.0.tar.gz&gt; dotnet test --filter CpeDictionaryFeedCheckTests</code>
/// writes <c>&lt;feed&gt;.reduced.json</c> (product keys and titles) and <c>&lt;feed&gt;.summary.json</c> next to
/// the feed. Without the variable the test does nothing.
/// </summary>
public class CpeDictionaryFeedCheckTests
{
    private const string FileVariable = "AM_CPE_FEED_FILE";

    [Fact]
    public async Task Reduce_a_downloaded_feed()
    {
        var file = Environment.GetEnvironmentVariable(FileVariable);
        if (string.IsNullOrWhiteSpace(file)) return;

        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        long heapPeak = heapBefore;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                heapPeak = Math.Max(heapPeak, GC.GetTotalMemory(forceFullCollection: false));
                await Task.Delay(20);
            }
        });

        var watch = Stopwatch.StartNew();
        Dictionary<string, CpeDictionaryProduct> products;
        await using (var feed = File.OpenRead(file))
            products = await CpeDictionaryService.ReduceAsync(feed, CancellationToken.None);
        watch.Stop();
        sampling.Cancel();
        await sampler;

        var stored = JsonSerializer.SerializeToUtf8Bytes(products);
        File.WriteAllText(file + ".reduced.json", JsonSerializer.Serialize(
            new SortedDictionary<string, string>(products.ToDictionary(p => p.Key, p => p.Value.T), StringComparer.Ordinal)));
        File.WriteAllText(file + ".summary.json", JsonSerializer.Serialize(new
        {
            products = products.Count,
            seconds = Math.Round(watch.Elapsed.TotalSeconds, 1),
            storedBytes = stored.Length,
            heapBeforeMb = heapBefore / 1024 / 1024,
            heapPeakMb = heapPeak / 1024 / 1024,
            processPeakWorkingSetMb = Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024,
        }));

        Assert.True(products.Count >= CpeDictionaryService.MinProducts);
    }
}

using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// DO ingest fallback (IME ≥ 1.104 removed the [DO TEL] log line; the agent's DO collector
/// only emits <c>do_telemetry</c> for downloads whose completion a poll itself observed).
/// <c>download_progress</c> events carry the full <c>do*</c> set on every poll and must fold
/// into the summary's Do* columns so interrupted / between-poll-completed downloads still
/// reach the Delivery Optimization aggregates:
///  - progress-only sessions populate Do* incl. a valid DoDownloadMode (DoAggregator gate),
///  - a later do_telemetry stays authoritative,
///  - replayed progress events are idempotent (Math.Max),
///  - DownloadBytes prefers transferred bytes over file size.
/// </summary>
public class AppInstallDoFieldsFallbackTests
{
    private static readonly DateTime T0 = new(2026, 8, 19, 10, 0, 0, DateTimeKind.Utc);

    private static Dictionary<string, AppInstallAggregationState> Aggregate(params EnrollmentEvent[] events)
    {
        var summaries = new Dictionary<string, AppInstallAggregationState>(StringComparer.OrdinalIgnoreCase);
        foreach (var evt in events)
            EventIngestProcessor.AggregateAppInstallEvent(evt, "tenant", "session", summaries);
        return summaries;
    }

    private static EnrollmentEvent DownloadProgress(
        long bytesDownloaded, long doTotal, long doPeers, long doHttp, int doMode,
        long doFileSize = 0, int doPct = 0, long doCacheServer = 0, DateTime? ts = null)
        => new EnrollmentEvent
        {
            EventType = "download_progress",
            Timestamp = ts ?? T0,
            Data = new Dictionary<string, object>
            {
                ["appName"] = "App",
                ["bytesDownloaded"] = bytesDownloaded,
                ["doFileSize"] = doFileSize,
                ["doTotalBytesDownloaded"] = doTotal,
                ["doBytesFromPeers"] = doPeers,
                ["doBytesFromHttp"] = doHttp,
                ["doPercentPeerCaching"] = doPct,
                ["doDownloadMode"] = doMode,
                ["doBytesFromCacheServer"] = doCacheServer,
            },
        };

    private static EnrollmentEvent DoTelemetry(
        long doFileSize, long doTotal, long doPeers, long doHttp, int doMode,
        string? duration = null)
    {
        var data = new Dictionary<string, object>
        {
            ["appName"] = "App",
            ["doFileSize"] = doFileSize,
            ["doTotalBytesDownloaded"] = doTotal,
            ["doBytesFromPeers"] = doPeers,
            ["doBytesFromHttp"] = doHttp,
            ["doPercentPeerCaching"] = 50,
            ["doDownloadMode"] = doMode,
        };
        if (duration != null) data["doDownloadDuration"] = duration;
        return new EnrollmentEvent { EventType = "do_telemetry", Timestamp = T0, Data = data };
    }

    [Fact]
    public void ProgressOnly_PopulatesDoColumns_AndSurvivesDoAggregator()
    {
        // An interrupted download: progress events observed, do_telemetry never fired.
        var s = Aggregate(
            DownloadProgress(bytesDownloaded: 1000, doTotal: 1000, doPeers: 600, doHttp: 400, doMode: 1, doFileSize: 5000, doPct: 60),
            DownloadProgress(bytesDownloaded: 3000, doTotal: 3000, doPeers: 2000, doHttp: 1000, doMode: 1, doFileSize: 5000, doPct: 66)
        )["App"].Summary;

        Assert.Equal(3000, s.DoTotalBytesDownloaded);
        Assert.Equal(2000, s.DoBytesFromPeers);
        Assert.Equal(1000, s.DoBytesFromHttp);
        Assert.Equal(5000, s.DoFileSize);
        Assert.Equal(66, s.DoPercentPeerCaching);
        Assert.Equal(1, s.DoDownloadMode);
        Assert.Equal(3000, s.DownloadBytes); // from bytesDownloaded, not the file size

        // The whole point: DoDownloadMode >= 0 keeps the row inside the DO aggregate.
        var aggregate = DoAggregator.Compute(new[] { s });
        Assert.Equal(1, aggregate.DoAppCount);
        Assert.Equal(2000, aggregate.BytesFromPeers);
    }

    [Fact]
    public void ProgressReplay_IsIdempotent_MaxFoldNeverRegresses()
    {
        // Out-of-order / replayed batch: the older (smaller) progress arrives again last.
        var s = Aggregate(
            DownloadProgress(bytesDownloaded: 3000, doTotal: 3000, doPeers: 2000, doHttp: 1000, doMode: 1),
            DownloadProgress(bytesDownloaded: 1000, doTotal: 1000, doPeers: 600, doHttp: 400, doMode: 1)
        )["App"].Summary;

        Assert.Equal(3000, s.DoTotalBytesDownloaded);
        Assert.Equal(2000, s.DoBytesFromPeers);
        Assert.Equal(1000, s.DoBytesFromHttp);
    }

    [Fact]
    public void DoTelemetry_StaysAuthoritative_OverEarlierProgress()
    {
        // Progress saw a partial state; the final telemetry read has the complete numbers
        // (which can be LOWER per bucket than a transient progress max — telemetry wins).
        var s = Aggregate(
            DownloadProgress(bytesDownloaded: 4000, doTotal: 4000, doPeers: 3500, doHttp: 500, doMode: 1, doPct: 87),
            DoTelemetry(doFileSize: 5000, doTotal: 5000, doPeers: 3000, doHttp: 2000, doMode: 1, duration: "00:01:40")
        )["App"].Summary;

        Assert.Equal(5000, s.DoTotalBytesDownloaded);
        Assert.Equal(3000, s.DoBytesFromPeers);   // telemetry overwrote the higher progress value
        Assert.Equal(2000, s.DoBytesFromHttp);
        Assert.Equal(50, s.DoPercentPeerCaching);
        Assert.Equal("00:01:40", s.DoDownloadDuration);
        Assert.Equal(100, s.DownloadDurationSeconds);
    }

    [Fact]
    public void DoTelemetry_WithUnsetMode_DoesNotRegressAKnownMode()
    {
        // A telemetry event whose DownloadMode property was absent parses as -1; it must not
        // re-hide the row from DoAggregator after progress already established the mode.
        var s = Aggregate(
            DownloadProgress(bytesDownloaded: 1000, doTotal: 1000, doPeers: 600, doHttp: 400, doMode: 3),
            DoTelemetry(doFileSize: 1000, doTotal: 1000, doPeers: 600, doHttp: 400, doMode: -1)
        )["App"].Summary;

        Assert.Equal(3, s.DoDownloadMode);
    }

    [Fact]
    public void DownloadBytes_PrefersTransferredBytes_OverFileSize()
    {
        // Historic bug: DownloadBytes was max-ed with doFileSize, inflating the transfer
        // measure. With a real transfer total present the file size must not win.
        var s = Aggregate(
            DoTelemetry(doFileSize: 9000, doTotal: 5000, doPeers: 3000, doHttp: 2000, doMode: 1)
        )["App"].Summary;

        Assert.Equal(5000, s.DownloadBytes);
        Assert.Equal(9000, s.DoFileSize); // the file size column itself keeps the real value
    }

    [Fact]
    public void DownloadBytes_FallsBackToFileSize_WhenNoTransferTotal()
    {
        var evt = new EnrollmentEvent
        {
            EventType = "do_telemetry",
            Timestamp = T0,
            Data = new Dictionary<string, object> { ["appName"] = "App", ["doFileSize"] = 7000L, ["doDownloadMode"] = 1 },
        };
        var s = Aggregate(evt)["App"].Summary;
        Assert.Equal(7000, s.DownloadBytes);
    }
}

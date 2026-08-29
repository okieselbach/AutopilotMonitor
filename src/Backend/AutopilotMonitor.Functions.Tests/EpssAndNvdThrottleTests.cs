using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Vulnerability;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AutopilotMonitor.Functions.Services.Vulnerability.VulnerabilityCorrelationService;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the EPSS integration and the NVD throttle contract:
/// FIRST's envelope parses with invariant-culture string numbers, a 429 opens a bounded cooldown
/// (Retry-After honoured but capped), the NVD→cache projection carries the CVSS vector, EPSS
/// application never erases an existing score, and the act/attend/track priority is exactly the
/// three documented rules.
/// </summary>
public class EpssAndNvdThrottleTests
{
    // -----------------------------------------------------------------------
    // EpssApiClient
    // -----------------------------------------------------------------------

    private const string SampleEnvelope = """
        {"status":"OK","status-code":200,"version":"1.0","access":"public","total":2,"offset":0,"limit":100,
         "data":[
           {"cve":"CVE-2024-21447","epss":"0.00383","percentile":"0.73461","date":"2026-08-28"},
           {"cve":"cve-2023-4863","epss":"0.94210","percentile":"0.99891","date":"2026-08-28"},
           {"cve":"CVE-2020-0001","epss":"not-a-number","percentile":"0.5","date":"2026-08-28"},
           {"cve":"","epss":"0.1","percentile":"0.5","date":"2026-08-28"}
         ]}
        """;

    [Fact]
    public void ParseResponse_ParsesInvariantStrings_AndDropsUnparseableRows()
    {
        var scores = EpssApiClient.ParseResponse(SampleEnvelope);

        Assert.Equal(2, scores.Count);
        var a = scores.Single(s => s.CveId == "CVE-2024-21447");
        Assert.Equal(0.00383, a.Score, 6);
        Assert.Equal(0.73461, a.Percentile, 6);
        Assert.Equal("2026-08-28", a.Date);
        // Ids are normalised to upper case so cache lookups are stable.
        Assert.Contains(scores, s => s.CveId == "CVE-2023-4863");
    }

    [Fact]
    public void ParseResponse_EmptyOrMissingData_ReturnsEmpty()
    {
        Assert.Empty(EpssApiClient.ParseResponse("""{"status":"OK","data":[]}"""));
        Assert.Empty(EpssApiClient.ParseResponse("""{"status":"OK"}"""));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(_respond(request));
        }
    }

    [Fact]
    public async Task GetScoresAsync_BatchesByHundred_DedupesIds_AndSurvivesAFailedBatch()
    {
        int call = 0;
        var handler = new StubHandler(req =>
        {
            call++;
            if (call == 2)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            // Echo the first id of the batch back as scored.
            var query = Uri.UnescapeDataString(req.RequestUri!.Query);
            var firstId = query.Substring("?cve=".Length).Split(',')[0];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"status":"OK","data":[{"cve":"{{firstId}}","epss":"0.5","percentile":"0.9","date":"2026-08-28"}]}""")
            };
        });
        var client = new EpssApiClient(new HttpClient(handler), NullLogger<EpssApiClient>.Instance);

        // 150 distinct ids + 10 duplicates (case-varied) ⇒ exactly two requests.
        var ids = Enumerable.Range(1, 150).Select(i => $"CVE-2024-{10000 + i}").ToList();
        ids.AddRange(ids.Take(10).Select(id => id.ToLowerInvariant()));

        var result = await client.GetScoresAsync(ids);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Single(result); // batch 1 scored one id, batch 2 failed soft
        Assert.True(result.ContainsKey("cve-2024-10001")); // case-insensitive map
        Assert.All(handler.Requests, u => Assert.StartsWith(EpssApiClient.EpssUrl, u.ToString()));
    }

    // -----------------------------------------------------------------------
    // NVD throttle cooldown
    // -----------------------------------------------------------------------

    private static readonly DateTime Now = new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ComputeCooldown_NoHeader_UsesDefault()
    {
        Assert.Equal(NvdApiClient.ThrottleCooldown, NvdApiClient.ComputeCooldown(null, Now));
    }

    [Fact]
    public void ComputeCooldown_RetryAfterDelta_IsHonoured()
    {
        var header = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
        Assert.Equal(TimeSpan.FromSeconds(45), NvdApiClient.ComputeCooldown(header, Now));
    }

    [Fact]
    public void ComputeCooldown_RetryAfterDate_IsRelativeToNow()
    {
        var header = new RetryConditionHeaderValue(new DateTimeOffset(Now.AddSeconds(20)));
        Assert.Equal(TimeSpan.FromSeconds(20), NvdApiClient.ComputeCooldown(header, Now));
    }

    [Fact]
    public void ComputeCooldown_IsCapped_SoAServerCannotParkUsForAnHour()
    {
        var header = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
        Assert.Equal(NvdApiClient.MaxRetryAfter, NvdApiClient.ComputeCooldown(header, Now));
    }

    [Fact]
    public void ComputeCooldown_PastDateOrZero_FallsBackToDefault()
    {
        Assert.Equal(NvdApiClient.ThrottleCooldown,
            NvdApiClient.ComputeCooldown(new RetryConditionHeaderValue(new DateTimeOffset(Now.AddSeconds(-5))), Now));
        Assert.Equal(NvdApiClient.ThrottleCooldown,
            NvdApiClient.ComputeCooldown(new RetryConditionHeaderValue(TimeSpan.Zero), Now));
    }

    [Fact]
    public void NvdFetchResult_FailureShapes_CarryEmptyDataButNeverSucceeded()
    {
        var throttled = NvdFetchResult<NvdCveResponse>.ThrottledResult();
        Assert.False(throttled.Succeeded);
        Assert.True(throttled.Throttled);
        Assert.NotNull(throttled.Data);
        Assert.Empty(throttled.Data.Vulnerabilities);

        var failed = NvdFetchResult<NvdCveResponse>.Failed();
        Assert.False(failed.Succeeded);
        Assert.False(failed.Throttled);

        var ok = NvdFetchResult<NvdCveResponse>.Ok(new NvdCveResponse { TotalResults = 1 });
        Assert.True(ok.Succeeded);
        Assert.Equal(1, ok.Data.TotalResults);
    }

    // -----------------------------------------------------------------------
    // NVD → CachedCve projection
    // -----------------------------------------------------------------------

    [Fact]
    public void ProjectNvdResponse_CarriesVector_PrefersV31_AndKeepsOnlyVulnerableRanges()
    {
        var response = new NvdCveResponse
        {
            Vulnerabilities =
            {
                new NvdVulnerabilityItem
                {
                    Cve = new NvdCve
                    {
                        Id = "CVE-2024-1",
                        Published = "2026-01-01T00:00:00.000",
                        Descriptions = { new NvdDescription { Lang = "en", Value = "desc" } },
                        Metrics = new NvdMetrics
                        {
                            CvssMetricV30 = { new NvdCvssMetricV31 { CvssData = new NvdCvssData { BaseScore = 5, BaseSeverity = "MEDIUM", VectorString = "CVSS:3.0/AV:L" } } },
                            CvssMetricV31 = { new NvdCvssMetricV31 { CvssData = new NvdCvssData { BaseScore = 9.8, BaseSeverity = "CRITICAL", VectorString = "CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H" } } },
                        },
                        Configurations =
                        {
                            new NvdConfiguration
                            {
                                Nodes =
                                {
                                    new NvdNode
                                    {
                                        CpeMatch =
                                        {
                                            new NvdCpeMatch { Vulnerable = true, Criteria = "cpe:2.3:a:v:p:*:*:*:*:*:*:*:*", VersionEndExcluding = "2.0" },
                                            new NvdCpeMatch { Vulnerable = false, Criteria = "cpe:2.3:o:microsoft:windows:*:*:*:*:*:*:*:*" },
                                        }
                                    }
                                }
                            }
                        }
                    }
                },
                new NvdVulnerabilityItem
                {
                    Cve = new NvdCve { Id = "CVE-2024-2", Descriptions = { new NvdDescription { Lang = "en", Value = "no metrics" } } }
                },
            }
        };

        var cves = VulnerabilityCorrelationService.ProjectNvdResponse(response);

        Assert.Equal(2, cves.Count);
        var first = cves[0];
        Assert.Equal(9.8, first.CvssScore);
        Assert.Equal("CRITICAL", first.CvssSeverity);
        Assert.Equal("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H", first.CvssVector);
        Assert.Single(first.AffectedVersions);
        Assert.Equal("2.0", first.AffectedVersions[0].VersionEndExcluding);
        Assert.Null(first.EpssScore); // never defaulted before enrichment

        var second = cves[1];
        Assert.Equal(0, second.CvssScore);
        Assert.Equal("UNKNOWN", second.CvssSeverity);
        Assert.Null(second.CvssVector);
    }

    [Fact]
    public void ProjectNvdResponse_Null_ReturnsEmpty()
    {
        Assert.Empty(VulnerabilityCorrelationService.ProjectNvdResponse(null));
    }

    // -----------------------------------------------------------------------
    // EPSS application onto cached CVEs
    // -----------------------------------------------------------------------

    [Fact]
    public void ApplyEpssScores_SetsNewScores_KeepsUnscoredUntouched_AndReportsOnlyChanges()
    {
        var cves = new List<CachedCve>
        {
            new() { CveId = "CVE-1", EpssScore = 0.2, EpssPercentile = 0.8, EpssDate = "2026-08-27" },
            new() { CveId = "CVE-2" },
            new() { CveId = "CVE-3", EpssScore = 0.5, EpssPercentile = 0.9, EpssDate = "2026-08-28" },
        };
        var scores = new Dictionary<string, EpssScore>(StringComparer.OrdinalIgnoreCase)
        {
            ["cve-1"] = new EpssScore { CveId = "CVE-1", Score = 0.25, Percentile = 0.85, Date = "2026-08-28" }, // changed
            ["CVE-3"] = new EpssScore { CveId = "CVE-3", Score = 0.5, Percentile = 0.9, Date = "2026-08-28" },   // unchanged
            // CVE-2 absent: FIRST did not score it (or its batch failed) ⇒ stays null
        };

        var changed = VulnerabilityCorrelationService.ApplyEpssScores(cves, scores);

        Assert.Equal(1, changed);
        Assert.Equal(0.25, cves[0].EpssScore);
        Assert.Equal("2026-08-28", cves[0].EpssDate);
        Assert.Null(cves[1].EpssScore);
        Assert.Equal(0.5, cves[2].EpssScore);
    }

    [Fact]
    public void ApplyEpssScores_NeverErasesYesterdaysScore_WhenTodayHasNone()
    {
        var cves = new List<CachedCve> { new() { CveId = "CVE-1", EpssScore = 0.3, EpssPercentile = 0.7, EpssDate = "2026-08-27" } };
        var changed = VulnerabilityCorrelationService.ApplyEpssScores(cves, new Dictionary<string, EpssScore>());
        Assert.Equal(0, changed);
        Assert.Equal(0.3, cves[0].EpssScore);
    }

    [Fact]
    public void BuildCveCacheRow_PassesCachedAtThrough_SoEpssRewritesDoNotLookLikeNvdFetches()
    {
        var cachedAt = new DateTime(2026, 8, 20, 3, 0, 0, DateTimeKind.Utc);
        var row = VulnerabilityCorrelationService.BuildCveCacheRow("cpe:2.3:a:v:p", new List<CachedCve> { new() { CveId = "CVE-1" } }, cachedAt);

        Assert.Equal(cachedAt.ToString("o"), row["CachedAt"]);
        Assert.Equal("cpe:2.3:a:v:p", row["CpeUri"]);
        Assert.Equal(1, row["TotalCves"]);
        Assert.True(row.ContainsKey("CveDataJson"));
    }

    [Fact]
    public void CachedCve_RoundTrips_WithoutWritingNulls_SoLegacyRowsStayReadable()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new List<CachedCve> { new() { CveId = "CVE-1", CvssScore = 7 } }, JsonOptions);
        Assert.DoesNotContain("EpssScore", json);
        Assert.DoesNotContain("CvssVector", json);

        // A row written before this change (no EPSS/vector properties at all) still deserialises.
        var legacy = """[{"CveId":"CVE-9","CvssScore":4.3,"CvssSeverity":"MEDIUM","Description":"","PublishedDate":"","AffectedVersions":[]}]""";
        var cves = System.Text.Json.JsonSerializer.Deserialize<List<CachedCve>>(legacy, JsonOptions)!;
        Assert.Null(cves[0].EpssScore);
        Assert.Null(cves[0].CvssVector);
    }

    // -----------------------------------------------------------------------
    // Priority
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(true, null, 0.0, CvePriority.Act)]
    [InlineData(true, 0.001, 2.0, CvePriority.Act)]
    [InlineData(false, 0.1, 2.0, CvePriority.Attend)]
    [InlineData(false, 0.0999, 2.0, CvePriority.Track)]
    [InlineData(false, null, 9.0, CvePriority.Attend)]
    [InlineData(false, 0.0, 9.8, CvePriority.Attend)]
    [InlineData(false, null, 8.9, CvePriority.Track)]
    [InlineData(false, 0.05, 7.5, CvePriority.Track)]
    public void Compute_FollowsTheThreeRules(bool kev, double? epss, double cvss, string expected)
    {
        Assert.Equal(expected, CvePriority.Compute(kev, epss, cvss));
    }

    [Fact]
    public void Max_PicksHigherBand_AndTreatsUnknownAsLowest()
    {
        Assert.Equal(CvePriority.Act, CvePriority.Max(CvePriority.Track, CvePriority.Act));
        Assert.Equal(CvePriority.Attend, CvePriority.Max(CvePriority.Attend, null));
        Assert.Equal(CvePriority.Track, CvePriority.Max(null, ""));
    }

    [Fact]
    public void StampFindingPriority_RollsUpHighestBand_AndMaxEpss()
    {
        var finding = new Dictionary<string, object> { ["maxEpssScore"] = 0.9 /* stale value, must be recomputed */ };
        var vulns = new List<object>
        {
            new Dictionary<string, object> { ["priority"] = CvePriority.Track, ["epssScore"] = 0.02 },
            new Dictionary<string, object> { ["priority"] = CvePriority.Attend, ["epssScore"] = 0.4 },
            new Dictionary<string, object> { ["priority"] = CvePriority.Track },
        };

        VulnerabilityCorrelationService.StampFindingPriority(finding, vulns);

        Assert.Equal(CvePriority.Attend, finding["priority"]);
        Assert.Equal(0.4, finding["maxEpssScore"]);
    }

    [Fact]
    public void StampFindingPriority_NoScoredCves_OmitsMaxEpss()
    {
        var finding = new Dictionary<string, object>();
        VulnerabilityCorrelationService.StampFindingPriority(finding, new List<object> { new Dictionary<string, object> { ["priority"] = CvePriority.Track } });
        Assert.Equal(CvePriority.Track, finding["priority"]);
        Assert.False(finding.ContainsKey("maxEpssScore"));
    }
}

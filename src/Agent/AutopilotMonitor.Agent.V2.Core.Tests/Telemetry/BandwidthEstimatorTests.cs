using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Telemetry
{
    /// <summary>
    /// Pins the passive-bandwidth math that feeds the one-shot network_bandwidth_estimate
    /// event: per-FileId baselining (first sighting counts nothing), per-file delta windows
    /// (a job that vanishes from the DO list and returns must be averaged over its own
    /// absence, not one poll), counter-reset and gap re-baselining, the WAN/LAN source
    /// split, and the p90/bucket/confidence reduction.
    /// </summary>
    public class BandwidthEstimatorTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

        private static List<BandwidthJobSample> Jobs(params BandwidthJobSample[] jobs)
            => new List<BandwidthJobSample>(jobs);

        private static BandwidthJobSample Job(string fileId, long wan, long lan = 0)
            => new BandwidthJobSample { FileId = fileId, WanBytes = wan, LanBytes = lan };

        [Fact]
        public void FirstSighting_OnlyBaselines_NoSample()
        {
            var estimator = new BandwidthEstimator(3);
            // 500 MB already on the counter at first sight — must NOT be booked as one interval.
            estimator.AddSnapshot(T0, Jobs(Job("a", 500_000_000)));

            Assert.Null(estimator.TryBuildEstimate());
        }

        [Fact]
        public void SteadyDownload_ProducesCorrectMbps()
        {
            var estimator = new BandwidthEstimator(3);
            estimator.AddSnapshot(T0, Jobs(Job("a", 0)));
            // 6 MB in 3 s = 6e6 * 8 / 3 / 1e6 = 16 Mbps
            estimator.AddSnapshot(T0.AddSeconds(3), Jobs(Job("a", 6_000_000)));

            var estimate = estimator.TryBuildEstimate();
            Assert.NotNull(estimate);
            Assert.Equal(1, estimate.WanSampleCount);
            Assert.Equal(16.0, estimate.WanMbpsP90.Value, 3);
            Assert.Equal(16.0, estimate.WanMbpsMax.Value, 3);
            Assert.Equal(6_000_000, estimate.WanBytesObserved);
            Assert.Equal("10-50", estimate.Bucket);
        }

        [Fact]
        public void ParallelJobs_RatesAdd()
        {
            var estimator = new BandwidthEstimator(3);
            estimator.AddSnapshot(T0, Jobs(Job("a", 0), Job("b", 0)));
            // 3 MB + 3 MB in 3 s = 8 + 8 = 16 Mbps combined
            estimator.AddSnapshot(T0.AddSeconds(3), Jobs(Job("a", 3_000_000), Job("b", 3_000_000)));

            var estimate = estimator.TryBuildEstimate();
            Assert.Equal(16.0, estimate.WanMbpsP90.Value, 3);
        }

        [Fact]
        public void JobVanishesAndReturns_WithinWindow_AveragedOverOwnAbsence()
        {
            var estimator = new BandwidthEstimator(3);
            estimator.AddSnapshot(T0, Jobs(Job("a", 0), Job("b", 0)));
            // "a" missing from this poll; "b" moves 3 MB / 3 s = 8 Mbps.
            estimator.AddSnapshot(T0.AddSeconds(3), Jobs(Job("b", 3_000_000)));
            // "a" returns with 6 MB accumulated over ITS 6 s window → 8 Mbps, not 16.
            // "b" adds another 3 MB / 3 s = 8 Mbps → combined sample 16 Mbps.
            estimator.AddSnapshot(T0.AddSeconds(6), Jobs(Job("a", 6_000_000), Job("b", 6_000_000)));

            var estimate = estimator.TryBuildEstimate();
            Assert.Equal(2, estimate.WanSampleCount);
            Assert.Equal(16.0, estimate.WanMbpsMax.Value, 3);
        }

        [Fact]
        public void GapBeyondWindow_RebaselinesWithoutSample()
        {
            var estimator = new BandwidthEstimator(3);
            estimator.AddSnapshot(T0, Jobs(Job("a", 0)));
            // 60 s dormancy gap (> 3 × 3 s): 100 MB accrued — must NOT become a rate sample.
            estimator.AddSnapshot(T0.AddSeconds(60), Jobs(Job("a", 100_000_000)));

            Assert.Null(estimator.TryBuildEstimate());

            // But the re-baseline must hold: the next in-window delta samples normally.
            estimator.AddSnapshot(T0.AddSeconds(63), Jobs(Job("a", 106_000_000)));
            var estimate = estimator.TryBuildEstimate();
            Assert.Equal(1, estimate.WanSampleCount);
            Assert.Equal(16.0, estimate.WanMbpsP90.Value, 3);
        }

        [Fact]
        public void CounterReset_RebaselinesWithoutSample()
        {
            var estimator = new BandwidthEstimator(3);
            estimator.AddSnapshot(T0, Jobs(Job("a", 50_000_000)));
            // DO job restart: counter moved backwards — no (negative or bogus) sample.
            estimator.AddSnapshot(T0.AddSeconds(3), Jobs(Job("a", 1_000_000)));

            Assert.Null(estimator.TryBuildEstimate());
        }

        [Fact]
        public void TinyMovement_BelowThreshold_NoSampleButNotNullWhenOthersExist()
        {
            var estimator = new BandwidthEstimator(3);
            estimator.AddSnapshot(T0, Jobs(Job("a", 0)));
            // 100 KB < 256 KB threshold → no rate sample.
            estimator.AddSnapshot(T0.AddSeconds(3), Jobs(Job("a", 100_000)));
            Assert.Null(estimator.TryBuildEstimate());

            // A real sample afterwards; the tiny delta still counted as observed bytes.
            estimator.AddSnapshot(T0.AddSeconds(6), Jobs(Job("a", 6_100_000)));
            var estimate = estimator.TryBuildEstimate();
            Assert.Equal(1, estimate.WanSampleCount);
            Assert.Equal(6_100_000, estimate.WanBytesObserved);
        }

        [Fact]
        public void LanTraffic_DoesNotInflateWanEstimate()
        {
            var estimator = new BandwidthEstimator(3);
            estimator.AddSnapshot(T0, Jobs(Job("a", wan: 0, lan: 0)));
            // 375 MB from LAN peers in 3 s (≈1 Gbps LAN) + 6 MB from the internet (16 Mbps).
            estimator.AddSnapshot(T0.AddSeconds(3), Jobs(Job("a", wan: 6_000_000, lan: 375_000_000)));

            var estimate = estimator.TryBuildEstimate();
            Assert.Equal(16.0, estimate.WanMbpsP90.Value, 3);
            Assert.Equal(1000.0, estimate.LanMbpsP90.Value, 3);
            Assert.Equal("10-50", estimate.Bucket); // bucket keyed to WAN, not LAN
        }

        [Fact]
        public void LanOnlySession_YieldsUnknownBucketAndNoWanFigure()
        {
            var estimator = new BandwidthEstimator(3);
            estimator.AddSnapshot(T0, Jobs(Job("a", wan: 0, lan: 0)));
            estimator.AddSnapshot(T0.AddSeconds(3), Jobs(Job("a", wan: 0, lan: 375_000_000)));

            var estimate = estimator.TryBuildEstimate();
            Assert.NotNull(estimate);
            Assert.Null(estimate.WanMbpsP90);
            Assert.Equal(0, estimate.WanSampleCount);
            Assert.Equal("unknown", estimate.Bucket);
            Assert.Equal(1, estimate.LanSampleCount);
        }

        [Theory]
        [InlineData(3_000_000, "<10")]      // 8 Mbps
        [InlineData(6_000_000, "10-50")]    // 16 Mbps
        [InlineData(30_000_000, "50-100")]  // 80 Mbps
        [InlineData(75_000_000, "100-250")] // 200 Mbps
        [InlineData(150_000_000, "250+")]   // 400 Mbps
        public void Buckets_MapFromWanP90(long bytesPer3s, string expectedBucket)
        {
            var estimator = new BandwidthEstimator(3);
            estimator.AddSnapshot(T0, Jobs(Job("a", 0)));
            estimator.AddSnapshot(T0.AddSeconds(3), Jobs(Job("a", bytesPer3s)));

            Assert.Equal(expectedBucket, estimator.TryBuildEstimate().Bucket);
        }

        [Fact]
        public void P90_ClipsSingleBurstOutlier()
        {
            var estimator = new BandwidthEstimator(3);
            estimator.AddSnapshot(T0, Jobs(Job("a", 0)));
            // 19 steady polls at 16 Mbps, then one DO accounting burst at ~160 Mbps.
            long total = 0;
            for (int i = 1; i <= 19; i++)
            {
                total += 6_000_000;
                estimator.AddSnapshot(T0.AddSeconds(3 * i), Jobs(Job("a", total)));
            }
            total += 60_000_000;
            estimator.AddSnapshot(T0.AddSeconds(60), Jobs(Job("a", total)));

            var estimate = estimator.TryBuildEstimate();
            Assert.Equal(20, estimate.WanSampleCount);
            Assert.Equal(16.0, estimate.WanMbpsP90.Value, 3); // p90 stays on the steady plateau
            Assert.Equal(160.0, estimate.WanMbpsMax.Value, 3);
        }

        [Fact]
        public void Confidence_ScalesWithSamplesAndVolume()
        {
            // low: 1 sample / 6 MB
            var low = new BandwidthEstimator(3);
            low.AddSnapshot(T0, Jobs(Job("a", 0)));
            low.AddSnapshot(T0.AddSeconds(3), Jobs(Job("a", 6_000_000)));
            Assert.Equal("low", low.TryBuildEstimate().Confidence);

            // medium: ≥3 samples and ≥25 MB
            var medium = new BandwidthEstimator(3);
            medium.AddSnapshot(T0, Jobs(Job("a", 0)));
            for (int i = 1; i <= 3; i++)
                medium.AddSnapshot(T0.AddSeconds(3 * i), Jobs(Job("a", i * 10_000_000L)));
            Assert.Equal("medium", medium.TryBuildEstimate().Confidence);

            // high: ≥10 samples and ≥150 MB
            var high = new BandwidthEstimator(3);
            high.AddSnapshot(T0, Jobs(Job("a", 0)));
            for (int i = 1; i <= 10; i++)
                high.AddSnapshot(T0.AddSeconds(3 * i), Jobs(Job("a", i * 20_000_000L)));
            Assert.Equal("high", high.TryBuildEstimate().Confidence);
        }

        [Fact]
        public void ExportImport_Roundtrip_PreservesEstimate()
        {
            var original = new BandwidthEstimator(3);
            original.AddSnapshot(T0, Jobs(Job("a", 0, 0)));
            original.AddSnapshot(T0.AddSeconds(3), Jobs(Job("a", 6_000_000, 3_000_000)));
            original.AddSnapshot(T0.AddSeconds(6), Jobs(Job("a", 12_000_000, 6_000_000)));

            // Restart: fresh estimator seeded from the exported state.
            var resumed = new BandwidthEstimator(3);
            resumed.ImportState(original.ExportState());

            var before = original.TryBuildEstimate();
            var after = resumed.TryBuildEstimate();
            Assert.Equal(before.WanMbpsP90, after.WanMbpsP90);
            Assert.Equal(before.WanSampleCount, after.WanSampleCount);
            Assert.Equal(before.WanBytesObserved, after.WanBytesObserved);
            Assert.Equal(before.LanMbpsP90, after.LanMbpsP90);
            Assert.Equal(before.Confidence, after.Confidence);
        }

        [Fact]
        public void ImportedState_MergesWithNewSamples()
        {
            // Pre-reboot run: two 16 Mbps samples.
            var preReboot = new BandwidthEstimator(3);
            preReboot.AddSnapshot(T0, Jobs(Job("a", 0)));
            preReboot.AddSnapshot(T0.AddSeconds(3), Jobs(Job("a", 6_000_000)));
            preReboot.AddSnapshot(T0.AddSeconds(6), Jobs(Job("a", 12_000_000)));

            // Post-reboot run resumes the state and adds one more sample from a NEW job.
            var postReboot = new BandwidthEstimator(3);
            postReboot.ImportState(preReboot.ExportState());
            postReboot.AddSnapshot(T0.AddSeconds(300), Jobs(Job("b", 0)));
            postReboot.AddSnapshot(T0.AddSeconds(303), Jobs(Job("b", 6_000_000)));

            var estimate = postReboot.TryBuildEstimate();
            Assert.Equal(3, estimate.WanSampleCount);
            Assert.Equal(18_000_000, estimate.WanBytesObserved);
        }

        [Fact]
        public void ImportState_SanitizesGarbage()
        {
            var estimator = new BandwidthEstimator(3);
            estimator.ImportState(new BandwidthEstimatorState
            {
                WanSamplesMbps = new List<double> { 16.0, double.NaN, double.PositiveInfinity, -5.0, 0.0 },
                LanSamplesMbps = null,
                WanBytesObserved = -1_000, // negative counter ignored
                LanBytesObserved = 0,
            });

            var estimate = estimator.TryBuildEstimate();
            Assert.Equal(1, estimate.WanSampleCount); // only the finite positive sample survived
            Assert.Equal(16.0, estimate.WanMbpsP90.Value, 3);
            Assert.Equal(0, estimate.WanBytesObserved);
        }

        [Fact]
        public void ImportState_NullIsIgnored()
        {
            var estimator = new BandwidthEstimator(3);
            estimator.ImportState(null);
            Assert.Null(estimator.TryBuildEstimate());
        }

        [Fact]
        public void EmptyOrNullSnapshots_AreIgnored()
        {
            var estimator = new BandwidthEstimator(3);
            estimator.AddSnapshot(T0, null);
            estimator.AddSnapshot(T0, Jobs());
            estimator.AddSnapshot(T0.AddSeconds(3), Jobs(Job(null, 5_000_000), Job("", 5_000_000)));

            Assert.Null(estimator.TryBuildEstimate());
        }
    }
}

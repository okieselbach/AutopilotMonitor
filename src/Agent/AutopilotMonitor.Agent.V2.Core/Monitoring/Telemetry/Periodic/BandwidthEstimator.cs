using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic
{
    /// <summary>
    /// One DO job's cumulative byte counters at a single poll, split by source class.
    /// WAN = BytesFromHttp + BytesFromInternetPeers (traffic that traversed the internet line).
    /// LAN = LAN/group/link-local peers + Connected Cache (local sources — must NOT count
    /// towards the internet-bandwidth estimate or a peer-fed branch office would report
    /// hundreds of Mbps on a 10-Mbit line).
    /// </summary>
    public struct BandwidthJobSample
    {
        public string FileId;
        public long WanBytes;
        public long LanBytes;
    }

    /// <summary>
    /// Serializable accumulator state for restart persistence. Baselines are deliberately
    /// NOT part of it: a reboot gap always exceeds the per-file delta window, so restored
    /// baselines would be discarded on first sight anyway — only the reduced samples and
    /// byte counters carry information across restarts.
    /// </summary>
    public sealed class BandwidthEstimatorState
    {
        public List<double> WanSamplesMbps { get; set; }
        public List<double> LanSamplesMbps { get; set; }
        public long WanBytesObserved { get; set; }
        public long LanBytesObserved { get; set; }
    }

    /// <summary>Result of <see cref="BandwidthEstimator.TryBuildEstimate"/>.</summary>
    public sealed class BandwidthEstimate
    {
        public double? WanMbpsP90 { get; set; }
        public double? WanMbpsMax { get; set; }
        public int WanSampleCount { get; set; }
        public long WanBytesObserved { get; set; }

        public double? LanMbpsP90 { get; set; }
        public double? LanMbpsMax { get; set; }
        public int LanSampleCount { get; set; }
        public long LanBytesObserved { get; set; }

        /// <summary>WAN p90 bucket: "&lt;10" | "10-50" | "50-100" | "100-250" | "250+" | "unknown".</summary>
        public string Bucket { get; set; }

        /// <summary>"high" | "medium" | "low" — driven by WAN sample count and observed volume.</summary>
        public string Confidence { get; set; }
    }

    /// <summary>
    /// Passive bandwidth estimator fed from the DO poll loop. Tracks per-FileId cumulative
    /// counters, turns poll-to-poll deltas into Mbps rate samples, and reduces them to a
    /// p90/max estimate at enrollment end. Pure arithmetic on data the collector already
    /// polls — deliberately generates zero network traffic and zero measurable CPU load.
    ///
    /// Delta windows are tracked PER FileId (not per snapshot): DO's job list churns, and a
    /// job that drops out for a few polls and returns with accumulated bytes must have its
    /// delta averaged over ITS OWN absence window — booking it into a single poll interval
    /// would overshoot the rate by the number of missed polls.
    ///
    /// Sampling rules (each keeps a distinct distortion out of the estimate):
    ///  - A FileId's first appearance only sets its baseline (its pre-existing bytes would
    ///    otherwise be booked into one interval and overshoot massively).
    ///  - A counter that moved backwards re-baselines that FileId (DO job restart).
    ///  - A per-file window outside [MinElapsedSeconds, MaxElapsedFactor × interval]
    ///    re-baselines that file without a rate contribution (dormancy, long list absence).
    ///  - Poll sums below MinSampleBytes produce no rate sample (idle/installing polls would
    ///    flood the distribution with near-zero noise) but still count as observed bytes.
    /// Taking the p90 of the surviving samples approximates the sustained line capacity
    /// from below: ramp-up and tail-end polls sit in the lower quantiles, and single-poll
    /// accounting bursts above the p90 are clipped by not using the max as the headline.
    /// </summary>
    public sealed class BandwidthEstimator
    {
        private const long MinSampleBytes = 256 * 1024;      // ignore polls with < 256 KB movement
        private const double MinElapsedSeconds = 1.0;
        private const int MaxElapsedFactor = 3;               // per-file gap > 3× interval → re-baseline only
        private const int MaxSamples = 4096;                  // safety cap (≈ 3.4 h at 3 s polls)

        // Confidence thresholds (WAN): enough independent samples AND enough volume that
        // TCP has demonstrably saturated the line at least part of the time.
        private const int HighSampleCount = 10;
        private const long HighBytes = 150L * 1024 * 1024;    // 150 MB
        private const int MediumSampleCount = 3;
        private const long MediumBytes = 25L * 1024 * 1024;   // 25 MB

        private sealed class FileBaseline
        {
            public long WanBytes;
            public long LanBytes;
            public DateTime LastSeenUtc;
        }

        private readonly object _sync = new object();
        private readonly double _maxElapsedSeconds;

        private readonly Dictionary<string, FileBaseline> _baselines =
            new Dictionary<string, FileBaseline>(StringComparer.OrdinalIgnoreCase);

        private readonly List<double> _wanSamplesMbps = new List<double>();
        private readonly List<double> _lanSamplesMbps = new List<double>();
        private long _wanBytesObserved;
        private long _lanBytesObserved;

        public BandwidthEstimator(int pollIntervalSeconds)
        {
            if (pollIntervalSeconds <= 0) pollIntervalSeconds = 3;
            _maxElapsedSeconds = pollIntervalSeconds * MaxElapsedFactor;
        }

        /// <summary>
        /// Feeds one poll's job list. <paramref name="utcNow"/> is injected (not read from the
        /// clock) so tests can drive synthetic timelines.
        /// </summary>
        public void AddSnapshot(DateTime utcNow, IReadOnlyList<BandwidthJobSample> jobs)
        {
            if (jobs == null || jobs.Count == 0) return;

            lock (_sync)
            {
                // Per-poll aggregates. Rates add across parallel jobs; each job's rate is
                // averaged over its own last-seen window so list churn cannot distort it.
                double wanRateMbps = 0, lanRateMbps = 0;
                long wanDelta = 0, lanDelta = 0;

                for (int i = 0; i < jobs.Count; i++)
                {
                    var job = jobs[i];
                    if (string.IsNullOrEmpty(job.FileId)) continue;

                    FileBaseline baseline;
                    if (!_baselines.TryGetValue(job.FileId, out baseline))
                    {
                        _baselines[job.FileId] = new FileBaseline
                        {
                            WanBytes = job.WanBytes,
                            LanBytes = job.LanBytes,
                            LastSeenUtc = utcNow
                        };
                        continue;
                    }

                    var elapsed = (utcNow - baseline.LastSeenUtc).TotalSeconds;
                    var fileWanDelta = job.WanBytes - baseline.WanBytes;
                    var fileLanDelta = job.LanBytes - baseline.LanBytes;

                    // Counter reset (job restart) or an implausible window → re-baseline only.
                    var valid = fileWanDelta >= 0 && fileLanDelta >= 0 &&
                                elapsed >= MinElapsedSeconds && elapsed <= _maxElapsedSeconds;

                    baseline.WanBytes = job.WanBytes;
                    baseline.LanBytes = job.LanBytes;
                    baseline.LastSeenUtc = utcNow;

                    if (!valid) continue;

                    wanDelta += fileWanDelta;
                    lanDelta += fileLanDelta;
                    wanRateMbps += fileWanDelta * 8.0 / elapsed / 1_000_000.0;
                    lanRateMbps += fileLanDelta * 8.0 / elapsed / 1_000_000.0;
                }

                _wanBytesObserved += wanDelta;
                _lanBytesObserved += lanDelta;

                if (wanDelta >= MinSampleBytes && _wanSamplesMbps.Count < MaxSamples)
                    _wanSamplesMbps.Add(wanRateMbps);
                if (lanDelta >= MinSampleBytes && _lanSamplesMbps.Count < MaxSamples)
                    _lanSamplesMbps.Add(lanRateMbps);
            }
        }

        /// <summary>Copies the accumulator state for restart persistence (see <see cref="ImportState"/>).</summary>
        public BandwidthEstimatorState ExportState()
        {
            lock (_sync)
            {
                return new BandwidthEstimatorState
                {
                    WanSamplesMbps = new List<double>(_wanSamplesMbps),
                    LanSamplesMbps = new List<double>(_lanSamplesMbps),
                    WanBytesObserved = _wanBytesObserved,
                    LanBytesObserved = _lanBytesObserved,
                };
            }
        }

        /// <summary>
        /// Seeds the accumulator from a persisted state (previous agent run of the SAME
        /// session — the reboot survivor path). Defensive against a tampered/corrupt file:
        /// non-finite or negative sample values are dropped, list sizes and counters are
        /// clamped. Intended for a freshly constructed estimator; imported samples simply
        /// prepend the ones this run will collect.
        /// </summary>
        public void ImportState(BandwidthEstimatorState state)
        {
            if (state == null) return;
            lock (_sync)
            {
                ImportSamples(state.WanSamplesMbps, _wanSamplesMbps);
                ImportSamples(state.LanSamplesMbps, _lanSamplesMbps);
                if (state.WanBytesObserved > 0) _wanBytesObserved += state.WanBytesObserved;
                if (state.LanBytesObserved > 0) _lanBytesObserved += state.LanBytesObserved;
            }
        }

        private static void ImportSamples(List<double> source, List<double> target)
        {
            if (source == null) return;
            for (int i = 0; i < source.Count && target.Count < MaxSamples; i++)
            {
                var v = source[i];
                if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0) continue;
                target.Add(v);
            }
        }

        /// <summary>Returns the estimate, or null when not a single valid rate sample exists.</summary>
        public BandwidthEstimate TryBuildEstimate()
        {
            lock (_sync)
            {
                if (_wanSamplesMbps.Count == 0 && _lanSamplesMbps.Count == 0) return null;

                var estimate = new BandwidthEstimate
                {
                    WanSampleCount = _wanSamplesMbps.Count,
                    WanBytesObserved = _wanBytesObserved,
                    LanSampleCount = _lanSamplesMbps.Count,
                    LanBytesObserved = _lanBytesObserved,
                };

                if (_wanSamplesMbps.Count > 0)
                {
                    estimate.WanMbpsP90 = Percentile90(_wanSamplesMbps);
                    estimate.WanMbpsMax = Max(_wanSamplesMbps);
                }
                if (_lanSamplesMbps.Count > 0)
                {
                    estimate.LanMbpsP90 = Percentile90(_lanSamplesMbps);
                    estimate.LanMbpsMax = Max(_lanSamplesMbps);
                }

                estimate.Bucket = ToBucket(estimate.WanMbpsP90);
                estimate.Confidence = ToConfidence(_wanSamplesMbps.Count, _wanBytesObserved);
                return estimate;
            }
        }

        private static string ToBucket(double? wanMbpsP90)
        {
            if (!wanMbpsP90.HasValue) return "unknown";
            var v = wanMbpsP90.Value;
            if (v < 10) return "<10";
            if (v < 50) return "10-50";
            if (v < 100) return "50-100";
            if (v < 250) return "100-250";
            return "250+";
        }

        private static string ToConfidence(int wanSamples, long wanBytes)
        {
            if (wanSamples >= HighSampleCount && wanBytes >= HighBytes) return "high";
            if (wanSamples >= MediumSampleCount && wanBytes >= MediumBytes) return "medium";
            return "low";
        }

        private static double Percentile90(List<double> samples)
        {
            var sorted = new List<double>(samples);
            sorted.Sort();
            var index = (int)Math.Ceiling(0.9 * sorted.Count) - 1;
            if (index < 0) index = 0;
            return sorted[index];
        }

        private static double Max(List<double> samples)
        {
            var max = double.MinValue;
            for (int i = 0; i < samples.Count; i++)
                if (samples[i] > max) max = samples[i];
            return max;
        }
    }
}

using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the CMTrace time-skew regression tripwire (CmTraceSkewTripwire.Evaluate): fires only
/// when the IME-vs-other median delta divergence sits on the 15-minute offset grid (residual
/// &lt; 2 min) AND both sides have enough samples over enough distinct upload batches, judged
/// over the session's most recent ingest era. The tripwire's goal state is to never fire, so
/// the must-stay-silent directions are the primary cases: off-grid replays, sub-grid
/// divergence, thin samples, batch-composition artifacts, common-mode spool latency, and the
/// skewed-but-ancient leg of a resumed pre-provisioning session must all pass without a hit.
/// </summary>
public class CmTraceSkewTripwireTests
{
    /// <summary>Reference ingest era used by every single-era scan below.</summary>
    private static readonly DateTime EraBase = new DateTime(2026, 9, 1, 8, 20, 0, DateTimeKind.Utc);

    /// <summary>Scan with the given per-side medians, comfortably above all sample/batch minimums.</summary>
    private static SessionSkewScan MakeScan(
        double imeDeltaMinutes, double otherDeltaMinutes,
        int imeSamples = 30, int otherSamples = 30,
        int imeBatches = 5, int otherBatches = 5)
    {
        var scan = new SessionSkewScan();
        AddSamples(scan, isIme: true, imeDeltaMinutes, imeSamples, imeBatches, EraBase);
        AddSamples(scan, isIme: false, otherDeltaMinutes, otherSamples, otherBatches, EraBase);
        return scan;
    }

    /// <summary>
    /// Adds <paramref name="count"/> identical samples spread round-robin over
    /// <paramref name="batches"/> ReceivedAt stamps one minute apart, starting at
    /// <paramref name="eraStart"/> — one continuous ingest era.
    /// </summary>
    private static void AddSamples(
        SessionSkewScan scan, bool isIme, double deltaMinutes, int count, int batches, DateTime eraStart)
    {
        for (int i = 0; i < count; i++)
            scan.Add(isIme, deltaMinutes, eraStart.AddMinutes(i % Math.Max(batches, 1)));
    }

    private static List<double> ImeDeltas(SessionSkewScan scan)
        => scan.ImeSamples.Select(s => s.DeltaMinutes).ToList();

    // ── Must fire ──────────────────────────────────────────────────────────

    [Fact]
    public void Fires_OnLargeNegativeGridDivergence()
    {
        // −540 min = the field-measured E.-Australia-style skew (−9 h), exactly 36 grid steps.
        // Both sides share 1.2 min upload latency — it cancels in the difference.
        var result = CmTraceSkewTripwire.Evaluate(MakeScan(imeDeltaMinutes: -538.8, otherDeltaMinutes: 1.2));

        Assert.NotNull(result);
        Assert.Equal(-36, result!.GridSteps);
        Assert.Equal(-540.0, result.DiffMinutes, 3);
        Assert.True(result.ResidualMinutes < CmTraceSkewTripwire.ResidualToleranceMinutes);
    }

    [Fact]
    public void Fires_OnSingleGridStepWithBoundaryResidual()
    {
        // +15 min divergence with residual 1.9 — just inside the 2-min tolerance.
        var result = CmTraceSkewTripwire.Evaluate(MakeScan(imeDeltaMinutes: 16.9, otherDeltaMinutes: 0.0));

        Assert.NotNull(result);
        Assert.Equal(1, result!.GridSteps);
        Assert.Equal(1.9, result.ResidualMinutes, 3);
    }

    // ── Must stay silent ───────────────────────────────────────────────────

    [Fact]
    public void Silent_WhenResidualOffGrid()
    {
        // 17.5 min divergence: nearest grid step is 15, residual 2.5 ≥ tolerance — the replay
        // signature (old log lines re-read late land anywhere, not on the zone grid).
        Assert.Null(CmTraceSkewTripwire.Evaluate(MakeScan(imeDeltaMinutes: 17.5, otherDeltaMinutes: 0.0)));
    }

    [Fact]
    public void Silent_WhenDivergenceBelowOneGridStep()
    {
        // 9 min rounds to 1 step but with residual 6 — and 7 min rounds to 0 steps. Both silent.
        Assert.Null(CmTraceSkewTripwire.Evaluate(MakeScan(imeDeltaMinutes: 9.0, otherDeltaMinutes: 0.0)));
        Assert.Null(CmTraceSkewTripwire.Evaluate(MakeScan(imeDeltaMinutes: 7.0, otherDeltaMinutes: 0.0)));
    }

    [Fact]
    public void Silent_WhenImeSampleCountBelowMinimum()
    {
        var scan = MakeScan(imeDeltaMinutes: -540.0, otherDeltaMinutes: 0.0,
            imeSamples: CmTraceSkewTripwire.MinSamplesPerSide - 1);
        Assert.Null(CmTraceSkewTripwire.Evaluate(scan));
    }

    [Fact]
    public void Silent_WhenDistinctBatchesBelowMinimum()
    {
        // A perfect grid divergence backed by only 2 upload batches on one side is a
        // batch-composition artifact (ReceivedAt is stamped per batch, not per event).
        var scan = MakeScan(imeDeltaMinutes: -540.0, otherDeltaMinutes: 0.0,
            otherBatches: CmTraceSkewTripwire.MinDistinctBatchesPerSide - 1);
        Assert.Null(CmTraceSkewTripwire.Evaluate(scan));
    }

    [Fact]
    public void Silent_OnCommonModeSpoolLatency()
    {
        // Offline spool: BOTH sides uploaded 45 min late. The difference of medians is ~0 —
        // shared latency must cancel, never read as skew.
        Assert.Null(CmTraceSkewTripwire.Evaluate(MakeScan(imeDeltaMinutes: 45.3, otherDeltaMinutes: 45.1)));
    }

    [Fact]
    public void Silent_OnNullOrEmptyScan()
    {
        Assert.Null(CmTraceSkewTripwire.Evaluate(null));
        Assert.Null(CmTraceSkewTripwire.Evaluate(new SessionSkewScan()));
        Assert.Null(CmTraceSkewTripwire.ResolveEraStartUtc(new SessionSkewScan()));
    }

    [Fact]
    public void MedianIsRobustAgainstMinorityOutliers()
    {
        // A handful of replayed old lines (huge off-grid deltas) inside an otherwise healthy
        // IME stream must not drag the median onto the grid.
        var scan = MakeScan(imeDeltaMinutes: 0.5, otherDeltaMinutes: 0.4);
        scan.Add(isIme: true, -1327.0, EraBase);
        scan.Add(isIme: true, -738.0, EraBase);
        Assert.Null(CmTraceSkewTripwire.Evaluate(scan));
    }

    [Fact]
    public void Silent_OnBacklogReplayBurstWhoseMedianLandsOnGrid()
    {
        // Field case 2026-08-28 (session d832560a): the agent died on auth failures, the relaunch
        // re-tailed the IME log and uploaded 508 lines in one burst with a continuum of ages
        // (46…733 min, quartiles 52 / 92 / 100) — median 91 min, i.e. 6×15 by pure chance.
        // The other side was live. The per-sample grid-conformity guard must keep this silent.
        // The burst arrives in ONE batch, inside the same era as the live traffic.
        var scan = MakeScan(imeDeltaMinutes: 0.1, otherDeltaMinutes: 0.1, imeSamples: 100, imeBatches: 80);
        var burstReceivedAt = EraBase.AddMinutes(80);
        void AddRamp(double from, double to, int count)
        {
            for (int i = 0; i < count; i++)
                scan.Add(isIme: true, from + i * (to - from) / count, burstReceivedAt);
        }
        AddRamp(46.0, 88.0, 125);
        AddRamp(88.0, 100.0, 250);
        AddRamp(100.0, 733.0, 125);

        var medianIme = ImeDeltas(scan).OrderBy(v => v).ElementAt(scan.ImeSamples.Count / 2);
        Assert.InRange(medianIme, 88.0, 92.0); // sanity: the median really does sit near 6×15

        Assert.Null(CmTraceSkewTripwire.Evaluate(scan));
        Assert.True(CmTraceSkewTripwire.ComputeGridConformantFraction(ImeDeltas(scan), 0.1) < 0.5);
    }

    [Fact]
    public void Fires_OnTwoWriterErasBothOnGrid()
    {
        // e9753578 shape: one file mixing PDT (−420) and CEST (+120) lines relative to the agent
        // zone. Every line is individually on the grid, so the conformity guard must NOT mask it.
        var scan = MakeScan(imeDeltaMinutes: -420.0, otherDeltaMinutes: 0.0, imeSamples: 65);
        for (int i = 0; i < 35; i++)
            scan.Add(isIme: true, 120.0, EraBase);

        var result = CmTraceSkewTripwire.Evaluate(scan);
        Assert.NotNull(result);
        Assert.Equal(-28, result!.GridSteps);
        Assert.Equal(1.0, result.GridConformantFraction, 3);
    }

    [Fact]
    public void GridConformantFraction_CountsZeroAndAnyMultiple()
    {
        var deltas = new List<double> { 0.5, 15.9, -30.1, 7.5, 37.5 }; // last two are off-grid
        Assert.Equal(0.6, CmTraceSkewTripwire.ComputeGridConformantFraction(deltas, 0.0), 3);
        Assert.Equal(0, CmTraceSkewTripwire.ComputeGridConformantFraction(new List<double>(), 0.0));
    }

    // ── Ingest-era window ──────────────────────────────────────────────────

    private static readonly DateTime PartOneLeg = new DateTime(2026, 8, 20, 8, 20, 0, DateTimeKind.Utc);
    private static readonly DateTime PartTwoLeg = new DateTime(2026, 9, 1, 8, 20, 0, DateTimeKind.Utc);

    [Fact]
    public void Silent_OnPreProvisioningHandoverWhereOnlyTheOldLegWasSkewed()
    {
        // Field 2026-09-01, three sessions of one tenant (e797117b / c06d639d / d7c8032b):
        // Part 1 ran on 2026-08-20 under agent 2.0.1409 — before per-line anchoring — and left
        // 26 IME samples at exactly −60 min; Part 2 ran 12 days later on 2.0.1445 and is clean.
        // The stale leg outnumbers the fresh one 26:9 and is perfectly grid-conformant, so
        // nothing but the era window can separate them (event rows carry no agent version).
        var scan = new SessionSkewScan();
        AddSamples(scan, isIme: true, -60.0, count: 26, batches: 20, eraStart: PartOneLeg);
        AddSamples(scan, isIme: false, 0.0, count: 116, batches: 60, eraStart: PartOneLeg);
        AddSamples(scan, isIme: true, -0.16, count: 9, batches: 6, eraStart: PartTwoLeg);
        AddSamples(scan, isIme: false, -0.26, count: 95, batches: 40, eraStart: PartTwoLeg);

        Assert.Null(CmTraceSkewTripwire.Evaluate(scan));
        Assert.Equal(PartTwoLeg, CmTraceSkewTripwire.ResolveEraStartUtc(scan));

        // …and the old leg really is what used to fire: judged on its own it still does.
        var oldLegOnly = new SessionSkewScan();
        AddSamples(oldLegOnly, isIme: true, -60.0, count: 26, batches: 20, eraStart: PartOneLeg);
        AddSamples(oldLegOnly, isIme: false, 0.0, count: 116, batches: 60, eraStart: PartOneLeg);
        var legacy = CmTraceSkewTripwire.Evaluate(oldLegOnly);
        Assert.NotNull(legacy);
        Assert.Equal(-4, legacy!.GridSteps);
    }

    [Fact]
    public void Fires_WhenTheCurrentEraIsSkewed_AndReportsWhatTheOlderLegContributed()
    {
        // The window must not become a blanket mute for resumed sessions: a skew in the leg the
        // running agent just produced still fires, and the report says how much history it left
        // out so an operator can tell a fresh regression from an inherited one.
        var scan = new SessionSkewScan();
        AddSamples(scan, isIme: true, 0.1, count: 30, batches: 10, eraStart: PartOneLeg);
        AddSamples(scan, isIme: false, 0.1, count: 30, batches: 10, eraStart: PartOneLeg);
        AddSamples(scan, isIme: true, -60.0, count: 25, batches: 8, eraStart: PartTwoLeg);
        AddSamples(scan, isIme: false, 0.0, count: 40, batches: 12, eraStart: PartTwoLeg);

        var result = CmTraceSkewTripwire.Evaluate(scan);

        Assert.NotNull(result);
        Assert.Equal(-4, result!.GridSteps);
        Assert.Equal(-60.0, result.DiffMinutes, 3);
        Assert.Equal(25, result.ImeSampleCount);
        Assert.Equal(40, result.OtherSampleCount);
        Assert.Equal(8, result.ImeBatchCount);
        Assert.Equal(12, result.OtherBatchCount);
        Assert.Equal(PartTwoLeg, result.EraStartUtc);
        Assert.Equal(30, result.ImeSamplesOutsideEra);
        Assert.Equal(30, result.OtherSamplesOutsideEra);
    }

    [Fact]
    public void EraWindow_SplitsOnGapsWiderThanTheThreshold_NotOnTotalSpan()
    {
        // A long leg that keeps uploading is ONE era even when it spans far more than the gap
        // threshold end to end — the boundary is the gap between consecutive batches.
        var start = PartTwoLeg;
        var continuous = new SessionSkewScan();
        for (int i = 0; i < 8; i++)
            continuous.Add(isIme: false, 0.0, start.AddMinutes(30 * i)); // 3.5 h span, 30 min gaps
        Assert.Equal(start, CmTraceSkewTripwire.ResolveEraStartUtc(continuous));

        // Exactly at the threshold is still one era (the split needs a STRICTLY wider gap).
        var atThreshold = new SessionSkewScan();
        atThreshold.Add(isIme: false, 0.0, start);
        atThreshold.Add(isIme: false, 0.0, start.AddHours(CmTraceSkewTripwire.EraGapHours));
        Assert.Equal(start, CmTraceSkewTripwire.ResolveEraStartUtc(atThreshold));

        // One silence past the threshold ends the era, and the boundary is found across BOTH
        // sides — an IME-only batch after the gap must still open the new era.
        var split = new SessionSkewScan();
        split.Add(isIme: false, 0.0, start);
        split.Add(isIme: true, 0.0, start.AddHours(3));
        Assert.Equal(start.AddHours(3), CmTraceSkewTripwire.ResolveEraStartUtc(split));
    }

    [Fact]
    public void SampleCap_KeepsTheNewestSamples()
    {
        // The cap is a memory backstop for pathological sessions. Since the era window judges
        // the NEWEST leg, the buffer must drop the oldest entries — keeping the head instead
        // would blind the detector on exactly the sessions big enough to reach the cap.
        var scan = new SessionSkewScan();
        int adds = SessionSkewScan.MaxSamplesPerSide + 5_000;
        for (int i = 0; i < adds; i++)
            scan.Add(isIme: true, i, EraBase.AddSeconds(i));

        Assert.True(scan.ImeSamples.Count >= SessionSkewScan.MaxSamplesPerSide);
        Assert.True(scan.ImeSamples.Count < SessionSkewScan.MaxSamplesPerSide * 2);
        Assert.Equal((double)(adds - 1), scan.ImeSamples[scan.ImeSamples.Count - 1].DeltaMinutes);
        Assert.True(scan.ImeSamples[0].DeltaMinutes > 0); // the oldest adds were trimmed away
    }

    // ── Bias suppression predicate ─────────────────────────────────────────

    [Fact]
    public void BiasDominated_WhenBiasIsHalfOrMore()
    {
        Assert.True(CmTraceSkewTripwire.IsBiasDominated(new Dictionary<string, int>
        {
            ["bias"] = 50,
            ["line-anchored"] = 50,
        }));
        Assert.True(CmTraceSkewTripwire.IsBiasDominated(new Dictionary<string, int>
        {
            ["Bias"] = 3, // case-insensitive
        }));
    }

    [Fact]
    public void NotBiasDominated_WhenAnchoredMajorityOrNoData()
    {
        Assert.False(CmTraceSkewTripwire.IsBiasDominated(new Dictionary<string, int>
        {
            ["bias"] = 10,
            ["line-anchored"] = 80,
            ["reader-zone-fallback"] = 11,
        }));
        // Empty/missing histogram fails open toward reporting — a storage error must not
        // silently swallow a genuine regression hit.
        Assert.False(CmTraceSkewTripwire.IsBiasDominated(new Dictionary<string, int>()));
        Assert.False(CmTraceSkewTripwire.IsBiasDominated(null));
    }
}

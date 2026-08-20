using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the CMTrace time-skew regression tripwire (CmTraceSkewTripwire.Evaluate): fires only
/// when the IME-vs-other median delta divergence sits on the 15-minute offset grid (residual
/// &lt; 2 min) AND both sides have enough samples over enough distinct upload batches. The
/// tripwire's goal state is to never fire, so the must-stay-silent directions are the primary
/// cases: off-grid replays, sub-grid divergence, thin samples, batch-composition artifacts,
/// and common-mode spool latency must all pass without a hit.
/// </summary>
public class CmTraceSkewTripwireTests
{
    /// <summary>Scan with the given per-side medians, comfortably above all sample/batch minimums.</summary>
    private static SessionSkewScan MakeScan(
        double imeDeltaMinutes, double otherDeltaMinutes,
        int imeSamples = 30, int otherSamples = 30,
        int imeBatches = 5, int otherBatches = 5)
    {
        var scan = new SessionSkewScan
        {
            ImeDistinctBatchCount = imeBatches,
            OtherDistinctBatchCount = otherBatches,
        };
        for (int i = 0; i < imeSamples; i++)
            scan.ImeDeltaMinutes.Add(imeDeltaMinutes);
        for (int i = 0; i < otherSamples; i++)
            scan.OtherDeltaMinutes.Add(otherDeltaMinutes);
        return scan;
    }

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
    }

    [Fact]
    public void MedianIsRobustAgainstMinorityOutliers()
    {
        // A handful of replayed old lines (huge off-grid deltas) inside an otherwise healthy
        // IME stream must not drag the median onto the grid.
        var scan = MakeScan(imeDeltaMinutes: 0.5, otherDeltaMinutes: 0.4);
        scan.ImeDeltaMinutes.Add(-1327.0);
        scan.ImeDeltaMinutes.Add(-738.0);
        Assert.Null(CmTraceSkewTripwire.Evaluate(scan));
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

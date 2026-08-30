using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Ime;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The pattern-drift loop's pure parts: the statistic (<see cref="ImePatternDriftEvaluator"/>),
/// the histogram filter (<see cref="ImePatternHealthService.ExtractBuiltInHits"/>) and the
/// operator-view projection (<see cref="ImePatternHealthService.BuildResponse"/>).
/// </summary>
public class ImePatternHealthTests
{
    private static ImePatternStatsEntry Row(string version, string patternId, int sessions, int withHit, DateTime? flagged = null) => new()
    {
        Version = version, PatternId = patternId, Sessions = sessions, SessionsWithHit = withHit, Hits = withHit * 3, DriftFlaggedAt = flagged,
    };

    private static List<ImePatternStatsEntry> Fleet(int candidateSessions, int candidateEspPhaseHits, int candidateScriptHits = 0) => new()
    {
        Row("1.104.102.0", "IME-ESP-PHASE", 900, 890),
        Row("1.104.102.0", "PS-AGENT-OUTPUT", 900, 300),       // conditional: 33 % on the baseline
        Row("1.104.102.0", "IME-DO-TEL", 900, 0),              // dead everywhere
        Row("1.103.101.0", "IME-ESP-PHASE", 400, 399),
        Row("1.105.103.0", "IME-ESP-PHASE", candidateSessions, candidateEspPhaseHits),
        Row("1.105.103.0", "PS-AGENT-OUTPUT", candidateSessions, candidateScriptHits),
        Row("1.105.103.0", "IME-DO-TEL", candidateSessions, 0),
    };

    [Fact]
    public void Baseline_is_the_version_with_most_sessions_excluding_the_candidate()
    {
        var all = Fleet(30, 0);
        Assert.Equal("1.104.102.0", ImePatternDriftEvaluator.SelectBaseline(all, "1.105.103.0"));
        // A candidate that is itself the biggest version is never its own baseline.
        Assert.Equal("1.103.101.0", ImePatternDriftEvaluator.SelectBaseline(all, "1.104.102.0"));
    }

    [Fact]
    public void No_baseline_when_no_other_version_has_enough_sessions()
    {
        var all = new List<ImePatternStatsEntry> { Row("1.105.103.0", "IME-ESP-PHASE", 500, 0), Row("1.104.102.0", "IME-ESP-PHASE", 50, 50) };
        Assert.Null(ImePatternDriftEvaluator.SelectBaseline(all, "1.105.103.0"));
        Assert.Empty(ImePatternDriftEvaluator.Evaluate(all, "1.105.103.0"));
    }

    [Fact]
    public void Expected_pattern_with_zero_hits_after_the_threshold_is_drift()
    {
        var findings = ImePatternDriftEvaluator.Evaluate(Fleet(30, 0), "1.105.103.0");
        var f = Assert.Single(findings);
        Assert.Equal("IME-ESP-PHASE", f.PatternId);
        Assert.Equal("1.104.102.0", f.BaselineVersion);
        Assert.InRange(f.BaselineRate, 0.98, 1.0);
        Assert.Equal(30, f.Sessions);
    }

    [Fact]
    public void Below_the_session_threshold_nothing_is_judged()
    {
        Assert.Empty(ImePatternDriftEvaluator.Evaluate(Fleet(24, 0), "1.105.103.0"));
    }

    [Fact]
    public void Conditional_and_dead_patterns_never_alarm()
    {
        // PS-AGENT-OUTPUT is 33 % on the baseline, IME-DO-TEL 0 % — neither is "expected".
        var findings = ImePatternDriftEvaluator.Evaluate(Fleet(200, 195), "1.105.103.0");
        Assert.Empty(findings);
    }

    [Fact]
    public void A_single_hit_clears_the_suspicion_and_a_flagged_cell_is_not_reported_twice()
    {
        Assert.Empty(ImePatternDriftEvaluator.Evaluate(Fleet(200, 1), "1.105.103.0"));

        var flagged = Fleet(30, 0);
        flagged.Single(r => r.Version == "1.105.103.0" && r.PatternId == "IME-ESP-PHASE").DriftFlaggedAt = DateTime.UtcNow;
        Assert.Empty(ImePatternDriftEvaluator.Evaluate(flagged, "1.105.103.0"));
    }

    [Fact]
    public void ExtractBuiltInHits_keeps_shipped_ids_only_and_reads_nested_json_shapes()
    {
        var hits = new JObject
        {
            ["IME-ESP-PHASE"] = 12,
            ["ime-started"] = 1,                    // case-insensitive id
            ["CUSTOM-TENANT-THING"] = 99,           // never global
            ["IME-DO-TEL"] = 0,                     // zero is data
            ["PS-AGENT-OUTPUT"] = "not-a-number",
        };
        var data = new Dictionary<string, object> { ["hits"] = hits, ["imeVersion"] = "1.105.103.0" };

        var result = ImePatternHealthService.ExtractBuiltInHits(data);

        Assert.Equal(12, result["IME-ESP-PHASE"]);
        Assert.Equal(1, result["IME-STARTED"]);
        Assert.Equal(0, result["IME-DO-TEL"]);
        Assert.False(result.ContainsKey("CUSTOM-TENANT-THING"));
        Assert.False(result.ContainsKey("PS-AGENT-OUTPUT"));
    }

    [Fact]
    public void ExtractBuiltInHits_without_a_histogram_is_empty()
    {
        Assert.Empty(ImePatternHealthService.ExtractBuiltInHits(null));
        Assert.Empty(ImePatternHealthService.ExtractBuiltInHits(new Dictionary<string, object> { ["linesRead"] = 5 }));
    }

    [Fact]
    public void BuildResponse_projects_versions_patterns_cells_and_alerts()
    {
        var flaggedAt = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        var stats = Fleet(30, 0);
        stats.Single(r => r.Version == "1.105.103.0" && r.PatternId == "IME-ESP-PHASE").DriftFlaggedAt = flaggedAt;
        var history = new List<ImeVersionHistoryEntry>
        {
            new() { Version = "1.105.103.0", FirstSeenAt = flaggedAt.AddDays(-4), LastSeenAt = flaggedAt, SessionCount = 31 },
            new() { Version = "1.104.102.0", FirstSeenAt = flaggedAt.AddDays(-40), LastSeenAt = flaggedAt, SessionCount = 6634 },
        };
        var catalog = new List<ImeLogPattern>
        {
            new() { PatternId = "IME-ESP-PHASE", Category = "always", Enabled = true },
            new() { PatternId = "PS-AGENT-OUTPUT", Category = "always", Enabled = true },
            new() { PatternId = "IME-NEW-THING", Category = "currentPhase", Enabled = true }, // no data yet
        };

        var r = ImePatternHealthService.BuildResponse(stats, history, catalog, flaggedAt);

        Assert.Equal("1.104.102.0", r.BaselineVersion);
        Assert.Equal("1.105.103.0", r.Versions[0].Version); // newest first
        Assert.Equal(30, r.Versions[0].Sessions);
        Assert.Equal(31, r.Versions[0].FleetSessions);

        var esp = r.Patterns.Single(p => p.PatternId == "IME-ESP-PHASE");
        Assert.True(esp.Expected);
        var ps = r.Patterns.Single(p => p.PatternId == "PS-AGENT-OUTPUT");
        Assert.False(ps.Expected);
        Assert.Contains(r.Patterns, p => p.PatternId == "IME-NEW-THING" && p.BaselineRate == null);
        // Retired IDs that still have rows stay visible as disabled.
        Assert.Contains(r.Patterns, p => p.PatternId == "IME-DO-TEL" && !p.Enabled);

        var alert = Assert.Single(r.Alerts);
        Assert.Equal("IME-ESP-PHASE", alert.PatternId);
        Assert.Equal(flaggedAt, alert.FlaggedAt);
        Assert.Equal(7, r.Cells.Count);
    }

    [Fact]
    public void BuiltInPatternIds_matches_the_embedded_catalog()
    {
        var ids = BuiltInImeLogPatterns.BuiltInPatternIds.Value;
        Assert.Contains("IME-ESP-PHASE", ids);
        Assert.Contains("PS-SCRIPT-RESULT", ids);
        Assert.Equal(BuiltInImeLogPatterns.GetAll().Count, ids.Count);
    }
}

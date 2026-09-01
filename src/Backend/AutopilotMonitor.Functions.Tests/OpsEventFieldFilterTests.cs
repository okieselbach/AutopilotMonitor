using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Shared.DataAccess;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the ops-events field filters (eventType / severity / minSeverity). Two properties matter:
/// (1) they are SERVER-SIDE — an operator asking for one event type must not pull a whole category
/// over the wire, and the paged fan-out must honour the exact same clauses as the single-category
/// and unpaged paths; (2) they are bound into the continuation fingerprint, so a token minted for
/// one filter cannot page a different one.
/// </summary>
public class OpsEventFieldFilterTests
{
    private static NameValueCollection Query(params (string Key, string Value)[] pairs)
    {
        var q = new NameValueCollection();
        foreach (var (k, v) in pairs) q[k] = v;
        return q;
    }

    // ── Parsing ────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_NoFilterParams_YieldsEmptyFilters_AndNoExtras()
    {
        var result = OpsEventFilterRequest.Parse(Query(("category", "Agent")));

        Assert.Null(result.Error);
        Assert.True(result.Filters.IsEmpty);
        Assert.Empty(OpsEventFilterRequest.ToExtras(result.Filters));
    }

    [Fact]
    public void Parse_EventType_IsVerbatim_NotCaseFolded()
    {
        // Table Storage cannot fold case: whatever the caller names is what the store is asked for.
        var result = OpsEventFilterRequest.Parse(Query(("eventType", "AgentEmergencyBreak")));

        Assert.Null(result.Error);
        Assert.Equal("AgentEmergencyBreak", result.Filters.EventType);
    }

    [Theory]
    [InlineData("warning", "Warning")]
    [InlineData("ERROR", "Error")]
    [InlineData("Critical", "Critical")]
    public void Parse_Severity_IsNormalisedToCanonicalCasing(string input, string expected)
    {
        // Without normalisation a "?severity=warning" would produce a Severity eq 'warning'
        // clause, which matches zero rows and reads like "nothing happened".
        var result = OpsEventFilterRequest.Parse(Query(("severity", input)));

        Assert.Null(result.Error);
        Assert.Equal(expected, result.Filters.Severity);
    }

    [Fact]
    public void Parse_UnknownSeverity_IsRejected_NotSilentlyDropped()
    {
        var severity = OpsEventFilterRequest.Parse(Query(("severity", "Warn")));
        var minSeverity = OpsEventFilterRequest.Parse(Query(("minSeverity", "sev2")));

        Assert.NotNull(severity.Error);
        Assert.Contains("severity must be one of", severity.Error);
        Assert.Contains("Warn", severity.Error);
        Assert.NotNull(minSeverity.Error);
        Assert.Contains("minSeverity must be one of", minSeverity.Error);
    }

    [Fact]
    public void ToExtras_EmitsNormalisedValues_InFixedOrder()
    {
        // Fixed order + normalised values: the fingerprint minted here must match the one
        // recomputed when the echoed nextLink params come back on the follow-up request.
        var result = OpsEventFilterRequest.Parse(Query(
            ("severity", "error"), ("eventType", "AgentEmergencyBreak"), ("minSeverity", "warning")));

        var extras = OpsEventFilterRequest.ToExtras(result.Filters);

        Assert.Equal(new[] { "eventType", "severity", "minSeverity" }, extras.Select(e => e.Key).ToArray());
        Assert.Equal(new[] { "AgentEmergencyBreak", "Error", "Warning" }, extras.Select(e => e.Value).ToArray());
    }

    // ── Severity ladder ────────────────────────────────────────────────────────

    [Fact]
    public void SeverityLadder_RanksInfoLowest_CriticalHighest()
    {
        Assert.True(OpsEventSeverity.Rank(OpsEventSeverity.Info) < OpsEventSeverity.Rank(OpsEventSeverity.Warning));
        Assert.True(OpsEventSeverity.Rank(OpsEventSeverity.Warning) < OpsEventSeverity.Rank(OpsEventSeverity.Error));
        Assert.True(OpsEventSeverity.Rank(OpsEventSeverity.Error) < OpsEventSeverity.Rank(OpsEventSeverity.Critical));
        Assert.Equal(-1, OpsEventSeverity.Rank("Warn"));
        Assert.Equal(-1, OpsEventSeverity.Rank(null));
    }

    [Fact]
    public void AtOrAbove_ReturnsTheThresholdAndEverythingAboveIt()
    {
        Assert.Equal(new[] { "Warning", "Error", "Critical" }, OpsEventSeverity.AtOrAbove("Warning").ToArray());
        Assert.Equal(new[] { "Critical" }, OpsEventSeverity.AtOrAbove("Critical").ToArray());
        Assert.Equal(OpsEventSeverity.All, OpsEventSeverity.AtOrAbove("Info").ToArray());
        Assert.Empty(OpsEventSeverity.AtOrAbove("Warn"));
    }

    // ── Clause building (both builders) ────────────────────────────────────────

    [Fact]
    public void BuildFilter_EmitsServerSideClauses_ForEveryField()
    {
        var f = TableOpsEventRepository.BuildFilter(
            "Agent", null, null,
            new OpsEventQueryFilters { EventType = "AgentEmergencyBreak", Severity = "Error" });

        Assert.NotNull(f);
        Assert.Contains("EventType eq 'AgentEmergencyBreak'", f);
        Assert.Contains("Severity eq 'Error'", f);
    }

    [Fact]
    public void BuildFilter_MinSeverity_ExpandsToOrSet_NotALexicographicRange()
    {
        // "Critical" < "Error" < "Info" < "Warning" lexicographically — a ge comparison would
        // silently return the wrong rows, so the threshold has to become an OR-set.
        var f = TableOpsEventRepository.BuildFilter(
            "Agent", null, null, new OpsEventQueryFilters { MinSeverity = "Warning" });

        Assert.NotNull(f);
        Assert.Contains("(Severity eq 'Warning' or Severity eq 'Error' or Severity eq 'Critical')", f);
        Assert.DoesNotContain("Severity ge", f);
        Assert.DoesNotContain("Severity eq 'Info'", f);
    }

    [Fact]
    public void BuildFilter_MinSeverityInfo_EmitsNoClause_ItIsTheFloor()
    {
        var f = TableOpsEventRepository.BuildFilter(
            "Agent", null, null, new OpsEventQueryFilters { MinSeverity = "Info" });

        Assert.Equal("PartitionKey eq 'Agent'", f);
    }

    [Fact]
    public void BuildFilter_NoFilters_IsUnchanged()
    {
        // Regression guard: the pre-existing filter surface must not shift when no field filter
        // is named — every stored continuation token depends on the same rows coming back.
        Assert.Equal(
            TableOpsEventRepository.BuildFilter("Agent", null, null),
            TableOpsEventRepository.BuildFilter("Agent", null, null, new OpsEventQueryFilters()));
        Assert.Null(TableOpsEventRepository.BuildFilter(null, null, null, new OpsEventQueryFilters()));
    }

    [Fact]
    public void BuildFilter_EscapesQuotes_SoAFilterCannotBreakOutOfItsLiteral()
    {
        var f = TableOpsEventRepository.BuildFilter(
            null, null, null, new OpsEventQueryFilters { EventType = "Agent'Break" });

        Assert.Equal("EventType eq 'Agent''Break'", f);
    }

    [Fact]
    public void FanOutBuilder_HonoursTheSameFieldClauses_AsTheSingleCategoryBuilder()
    {
        // The all-categories paged path builds its own filter (it adds the per-partition RowKey
        // bound). If it skipped the field filters, "no category named" would silently return
        // unfiltered rows — the exact bug this test exists to prevent.
        var filters = new OpsEventQueryFilters { EventType = "AgentEmergencyBreak", MinSeverity = "Error" };

        var fanOut = TableOpsEventRepository.BuildFilterWithRowKeyBound("Agent", null, null, "0009", filters);

        Assert.Contains("EventType eq 'AgentEmergencyBreak'", fanOut);
        Assert.Contains("(Severity eq 'Error' or Severity eq 'Critical')", fanOut);
        Assert.Contains("RowKey gt '0009'", fanOut);
    }

    // ── Continuation fingerprint ───────────────────────────────────────────────

    [Fact]
    public void Fingerprint_BindsTheFieldFilters_SoATokenCannotCrossFilters()
    {
        var from = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        const string caller = "11111111-1111-1111-1111-111111111111";

        string Fp(params (string Key, string Value)[] pairs) => DateWindowPagination.Fingerprint(
            scope: "ops-events", callerTenantId: caller, dateFrom: from, dateTo: to,
            extras: OpsEventFilterRequest.ToExtras(OpsEventFilterRequest.Parse(Query(pairs)).Filters));

        var unfiltered = Fp();
        var byType = Fp(("eventType", "AgentEmergencyBreak"));
        var byOtherType = Fp(("eventType", "AgentTimeout"));
        var byMinSeverity = Fp(("minSeverity", "Error"));

        Assert.NotEqual(unfiltered, byType);
        Assert.NotEqual(byType, byOtherType);
        Assert.NotEqual(unfiltered, byMinSeverity);
        // Casing is normalised before fingerprinting, so the same query in two spellings
        // shares one token instead of minting two that each reject the other.
        Assert.Equal(byMinSeverity, Fp(("minSeverity", "error")));
    }

    [Fact]
    public void Fingerprint_WithNoFieldFilters_MatchesThePreFilterToken()
    {
        // Backward compatibility: tokens minted before the field filters existed carry only the
        // category/tenantId extras. Appending the (empty) field extras must not change the hash,
        // or every in-flight pagination would break on deploy.
        var from = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        const string caller = "11111111-1111-1111-1111-111111111111";
        var legacyExtras = new List<KeyValuePair<string, string?>>
        {
            new KeyValuePair<string, string?>("category", "Agent"),
        };
        var withEmptyFilters = new List<KeyValuePair<string, string?>>(legacyExtras);
        withEmptyFilters.AddRange(OpsEventFilterRequest.ToExtras(new OpsEventQueryFilters()));

        Assert.Equal(
            DateWindowPagination.Fingerprint("ops-events", caller, from, null, legacyExtras),
            DateWindowPagination.Fingerprint("ops-events", caller, from, null, withEmptyFilters));
    }
}

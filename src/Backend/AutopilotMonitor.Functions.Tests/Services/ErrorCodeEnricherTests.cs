#nullable enable
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests.Services;

public sealed class ErrorCodeEnricherTests
{
    private static EnrollmentEvent EventWithData(Dictionary<string, object> data) => new EnrollmentEvent
    {
        EventType = "test",
        Source = "test",
        Message = "test",
        Data = data,
    };

    /// <summary>The <c>*Info</c> sibling as the wire sees it: a string-keyed dictionary.</summary>
    private static Dictionary<string, object> Info(EnrollmentEvent evt, string key) =>
        Assert.IsType<Dictionary<string, object>>(evt.Data[key]);

    [Fact]
    public void EnrichEvent_adds_errorCodeInfo_for_known_hex_code()
    {
        var evt = EventWithData(new() { { "errorCode", "0x80070005" } });

        ErrorCodeEnricher.EnrichEvent(evt);

        Assert.True(evt.Data.ContainsKey("errorCodeInfo"));
        var info = Info(evt, "errorCodeInfo");
        Assert.Equal("Access is denied", info["description"]);
        Assert.Equal("high", info["confidence"]);
        Assert.StartsWith("msdoc:", (string)info["source"]);
        Assert.Equal("win32", info["category"]);
        Assert.Equal("ERROR_ACCESS_DENIED", info["symbol"]);
        Assert.False(info.ContainsKey("derivedFromWin32"));
    }

    [Fact]
    public void EnrichEvent_adds_exitCodeInfo_for_msi_decimal()
    {
        var evt = EventWithData(new() { { "exitCode", "1603" } });

        ErrorCodeEnricher.EnrichEvent(evt);

        var info = Info(evt, "exitCodeInfo");
        Assert.Contains("fatal error", (string)info["description"]);
        Assert.Equal("ERROR_INSTALL_FAILURE", info["symbol"]);
        Assert.Equal("msi", info["category"]);
    }

    [Fact]
    public void EnrichEvent_reports_hresult_from_win32_derivation_as_data()
    {
        var evt = EventWithData(new() { { "hresultFromWin32", "0x80070643" } });

        ErrorCodeEnricher.EnrichEvent(evt);

        var info = Info(evt, "hresultFromWin32Info");
        Assert.Equal(1603, info["derivedFromWin32"]);
        Assert.Equal("ERROR_INSTALL_FAILURE", info["symbol"]);
        Assert.DoesNotContain("1603", (string)info["description"]);
    }

    [Fact]
    public void EnrichEvent_omits_symbol_when_the_entry_has_none()
    {
        var evt = EventWithData(new() { { "hresult", "0x87d1041c" } });

        ErrorCodeEnricher.EnrichEvent(evt);

        var info = Info(evt, "hresultInfo");
        Assert.False(info.ContainsKey("symbol"));
        Assert.Equal("intune-win32", info["category"]);
    }

    [Fact]
    public void EnrichEvent_handles_failureCode_key()
    {
        var evt = EventWithData(new() { { "failureCode", "0x87d00324" } });

        ErrorCodeEnricher.EnrichEvent(evt);

        Assert.True(evt.Data.ContainsKey("failureCodeInfo"));
    }

    [Fact]
    public void EnrichEvent_matches_keys_case_insensitively()
    {
        var evt = EventWithData(new() { { "ErrorCode", "0x80070005" } });

        ErrorCodeEnricher.EnrichEvent(evt);

        Assert.True(evt.Data.ContainsKey("errorCodeInfo"));
    }

    [Fact]
    public void EnrichEvent_skips_unknown_code()
    {
        var evt = EventWithData(new() { { "errorCode", "0xDEADBEEF" } });

        ErrorCodeEnricher.EnrichEvent(evt);

        Assert.False(evt.Data.ContainsKey("errorCodeInfo"));
    }

    [Fact]
    public void EnrichEvent_is_idempotent()
    {
        var existing = new { description = "preserved", confidence = "high", source = "test" };
        var evt = EventWithData(new() {
            { "errorCode", "0x80070005" },
            { "errorCodeInfo", existing }
        });

        ErrorCodeEnricher.EnrichEvent(evt);

        // Pre-existing errorCodeInfo must not be overwritten.
        Assert.Same(existing, evt.Data["errorCodeInfo"]);
    }

    [Fact]
    public void EnrichEvent_adds_enforcementStateInfo_for_numeric_state()
    {
        // registry_app_state carries the IME enforcement state as a numeric string.
        var evt = EventWithData(new() { { "enforcementState", "1000" } });

        ErrorCodeEnricher.EnrichEvent(evt);

        var info = evt.Data["enforcementStateInfo"];
        Assert.Equal("Success", info.GetType().GetProperty("name")?.GetValue(info));
        Assert.False(string.IsNullOrEmpty(info.GetType().GetProperty("description")?.GetValue(info) as string));
    }

    [Fact]
    public void EnrichEvent_skips_unknown_enforcement_state_and_stays_idempotent()
    {
        var unknown = EventWithData(new() { { "enforcementState", "424242" } });
        ErrorCodeEnricher.EnrichEvent(unknown);
        Assert.False(unknown.Data.ContainsKey("enforcementStateInfo"));

        var existing = new { name = "kept" };
        var evt = EventWithData(new() { { "enforcementState", "1000" }, { "enforcementStateInfo", existing } });
        ErrorCodeEnricher.EnrichEvent(evt);
        Assert.Same(existing, evt.Data["enforcementStateInfo"]);
    }

    [Fact]
    public void EnrichEvent_leaves_nested_objects_untouched()
    {
        // app_install_summary carries per-app objects; only top-level keys are enriched.
        var nested = new Dictionary<string, object> { { "exitCode", "1603" } };
        var evt = EventWithData(new() { { "apps", new List<object> { nested } } });

        ErrorCodeEnricher.EnrichEvent(evt);

        Assert.Single(evt.Data);
        Assert.Single(nested);
    }

    [Fact]
    public void EnrichEvent_skips_gather_rule_events_by_default()
    {
        // A logparser gather rule on HP Image Assistant.log captured `exitcode` — HPiA has its own
        // exit-code table, so the MSI/Win32 catalog meaning ("ERROR_SUCCESS") must not be attached
        // unless the rule opted in (no enrichErrorCodes marker here).
        var evt = new EnrollmentEvent
        {
            EventType = "HPiA-UpdateStatus",
            Source = "GatherRuleExecutor",
            Message = "Gather: HP Updater Log Analyze",
            Data = new()
            {
                { "exitcode", "0" },
                { "errorCode", "0x80070005" },
                { "enforcementState", "1000" },
                { "ruleId", "hpia-log-collect" },
            },
        };

        ErrorCodeEnricher.EnrichEvent(evt);

        Assert.DoesNotContain(evt.Data.Keys, k => k.EndsWith("Info"));
        Assert.Equal(4, evt.Data.Count);
    }

    [Fact]
    public void EnrichEvent_matches_gather_rule_source_case_insensitively()
    {
        var evt = EventWithData(new() { { "exitCode", "1603" } });
        evt.Source = "gatherruleexecutor";

        ErrorCodeEnricher.EnrichEvent(evt);

        Assert.False(evt.Data.ContainsKey("exitCodeInfo"));
    }

    [Theory]
    [InlineData(true)]        // in-process CLR bool
    [InlineData("true")]      // string form after DataJson roundtrip
    [InlineData("True")]
    public void EnrichEvent_enriches_gather_rule_events_that_opted_in(object marker)
    {
        // A rule that parses an msiexec log set enrichErrorCodes; the agent stamped the marker.
        var evt = EventWithData(new()
        {
            { "exitCode", "1603" },
            { "ruleId", "msi-log" },
            { "enrichErrorCodes", marker },
        });
        evt.Source = "GatherRuleExecutor";

        ErrorCodeEnricher.EnrichEvent(evt);

        Assert.Equal("ERROR_INSTALL_FAILURE", Info(evt, "exitCodeInfo")["symbol"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData("false")]
    [InlineData("")]
    public void EnrichEvent_ignores_gather_rule_marker_that_is_not_true(object marker)
    {
        var evt = EventWithData(new() { { "exitCode", "1603" }, { "enrichErrorCodes", marker } });
        evt.Source = "GatherRuleExecutor";

        ErrorCodeEnricher.EnrichEvent(evt);

        Assert.False(evt.Data.ContainsKey("exitCodeInfo"));
    }

    [Fact]
    public void EnrichEvent_marker_on_non_gather_event_changes_nothing()
    {
        // Built-in events are always enriched; the marker is only consulted for gather events.
        var evt = EventWithData(new() { { "exitCode", "1603" }, { "enrichErrorCodes", false } });

        ErrorCodeEnricher.EnrichEvent(evt);

        Assert.True(evt.Data.ContainsKey("exitCodeInfo"));
    }

    [Fact]
    public void EnrichEvent_handles_null_data_dictionary()
    {
        var evt = new EnrollmentEvent { EventType = "x", Source = "x", Message = "x", Data = null! };

        // Should not throw.
        ErrorCodeEnricher.EnrichEvent(evt);
    }

    [Fact]
    public void EnrichEvent_handles_empty_data_dictionary()
    {
        var evt = EventWithData(new());

        ErrorCodeEnricher.EnrichEvent(evt);

        Assert.Empty(evt.Data);
    }

    [Fact]
    public void EnrichEvent_handles_signed_decimal_hresult()
    {
        var evt = EventWithData(new() { { "errorCode", "-2147024891" } });

        ErrorCodeEnricher.EnrichEvent(evt);

        Assert.Equal("Access is denied", Info(evt, "errorCodeInfo")["description"]);
    }

    [Fact]
    public void EnrichEvent_enriches_multiple_code_keys_in_same_event()
    {
        // A real-world app_install_summary entry can carry both exitCode and hresultFromWin32.
        var evt = EventWithData(new()
        {
            { "exitCode", "1603" },
            { "hresultFromWin32", "0x80070005" }
        });

        ErrorCodeEnricher.EnrichEvent(evt);

        Assert.True(evt.Data.ContainsKey("exitCodeInfo"));
        Assert.True(evt.Data.ContainsKey("hresultFromWin32Info"));
    }

    [Fact]
    public void EnrichEvents_processes_a_collection()
    {
        var events = new[]
        {
            EventWithData(new() { { "errorCode", "0x80070005" } }),
            EventWithData(new() { { "exitCode", "1603" } }),
            EventWithData(new() { { "irrelevantKey", "noop" } }),
        };

        ErrorCodeEnricher.EnrichEvents(events);

        Assert.True(events[0].Data.ContainsKey("errorCodeInfo"));
        Assert.True(events[1].Data.ContainsKey("exitCodeInfo"));
        Assert.DoesNotContain(events[2].Data.Keys, k => k.EndsWith("Info"));
    }

    [Fact]
    public void EnrichEvents_handles_null_input()
    {
        // Should not throw.
        ErrorCodeEnricher.EnrichEvents(null);
    }
}

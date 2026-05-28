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

    [Fact]
    public void EnrichEvent_adds_errorCodeInfo_for_known_hex_code()
    {
        var evt = EventWithData(new() { { "errorCode", "0x80070005" } });

        ErrorCodeEnricher.EnrichEvent(evt);

        Assert.True(evt.Data.ContainsKey("errorCodeInfo"));
        var info = evt.Data["errorCodeInfo"];
        var description = info.GetType().GetProperty("description")?.GetValue(info)?.ToString();
        var confidence = info.GetType().GetProperty("confidence")?.GetValue(info)?.ToString();
        var source = info.GetType().GetProperty("source")?.GetValue(info)?.ToString();
        Assert.Contains("Access denied", description ?? "");
        Assert.Equal("high", confidence);
        Assert.False(string.IsNullOrEmpty(source));
    }

    [Fact]
    public void EnrichEvent_adds_exitCodeInfo_for_msi_decimal()
    {
        var evt = EventWithData(new() { { "exitCode", "1603" } });

        ErrorCodeEnricher.EnrichEvent(evt);

        Assert.True(evt.Data.ContainsKey("exitCodeInfo"));
        var info = evt.Data["exitCodeInfo"];
        Assert.Contains("Fatal error", info.GetType().GetProperty("description")?.GetValue(info)?.ToString() ?? "");
    }

    [Fact]
    public void EnrichEvent_handles_hresult_key()
    {
        var evt = EventWithData(new() { { "hresult", "0x87d1041c" } });

        ErrorCodeEnricher.EnrichEvent(evt);

        Assert.True(evt.Data.ContainsKey("hresultInfo"));
    }

    [Fact]
    public void EnrichEvent_handles_hresultFromWin32_key()
    {
        var evt = EventWithData(new() { { "hresultFromWin32", "0x80070005" } });

        ErrorCodeEnricher.EnrichEvent(evt);

        Assert.True(evt.Data.ContainsKey("hresultFromWin32Info"));
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

        Assert.True(evt.Data.ContainsKey("errorCodeInfo"));
        var info = evt.Data["errorCodeInfo"];
        Assert.Contains("Access denied", info.GetType().GetProperty("description")?.GetValue(info)?.ToString() ?? "");
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

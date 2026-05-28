#nullable enable
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Services;
using Xunit;

namespace AutopilotMonitor.Functions.Tests.Shared;

public sealed class ErrorCodeCatalogTests
{
    [Fact]
    public void All_loads_entries_from_embedded_resource()
    {
        var all = ErrorCodeCatalog.All;

        Assert.NotEmpty(all);
        Assert.True(all.Count > 20, $"Expected catalog to have >20 entries, got {all.Count}");
    }

    [Fact]
    public void TryLookup_returns_null_for_null_or_empty()
    {
        Assert.Null(ErrorCodeCatalog.TryLookup(null));
        Assert.Null(ErrorCodeCatalog.TryLookup(""));
        Assert.Null(ErrorCodeCatalog.TryLookup("   "));
    }

    [Fact]
    public void TryLookup_returns_null_for_unknown_code()
    {
        Assert.Null(ErrorCodeCatalog.TryLookup("0xDEADBEEF"));
        Assert.Null(ErrorCodeCatalog.TryLookup("99999"));
    }

    [Fact]
    public void TryLookup_finds_lowercased_hex_direct()
    {
        var entry = ErrorCodeCatalog.TryLookup("0x80070005");
        Assert.NotNull(entry);
        Assert.Contains("Access denied", entry!.Description);
        Assert.Equal(ErrorCodeConfidence.High, entry.Confidence);
    }

    [Fact]
    public void TryLookup_normalises_uppercase_hex()
    {
        var entry = ErrorCodeCatalog.TryLookup("0X80070005");
        Assert.NotNull(entry);
        Assert.Contains("Access denied", entry!.Description);
    }

    [Fact]
    public void TryLookup_normalises_short_hex_without_leading_zeros()
    {
        // "0x70005" should pad to "0x00070005" — which doesn't exist; but "0x80070005" should
        // be reachable via "0X0000000080070005" (extra leading zeros).
        var entry = ErrorCodeCatalog.TryLookup("0x0000000080070005");
        Assert.NotNull(entry);
        Assert.Contains("Access denied", entry!.Description);
    }

    [Fact]
    public void TryLookup_converts_signed_decimal_hresult_to_hex()
    {
        // -2147024891 (signed decimal) == 0x80070005 (unsigned hex) == E_ACCESSDENIED
        var entry = ErrorCodeCatalog.TryLookup("-2147024891");
        Assert.NotNull(entry);
        Assert.Contains("Access denied", entry!.Description);
    }

    [Fact]
    public void TryLookup_finds_msi_decimal_exit_code()
    {
        var entry = ErrorCodeCatalog.TryLookup("1603");
        Assert.NotNull(entry);
        Assert.Contains("Fatal error", entry!.Description);
    }

    [Fact]
    public void TryLookup_finds_intune_specific_code()
    {
        var entry = ErrorCodeCatalog.TryLookup("0x87d1041c");
        Assert.NotNull(entry);
        Assert.Contains("Application not detected", entry!.Description);
        Assert.Equal(ErrorCodeConfidence.High, entry.Confidence);
    }

    [Fact]
    public void TryLookup_preserves_confidence_levels()
    {
        var high = ErrorCodeCatalog.TryLookup("0x80070005");
        var medium = ErrorCodeCatalog.TryLookup("0x87d30000");
        var low = ErrorCodeCatalog.TryLookup("0x87d30004");

        Assert.Equal(ErrorCodeConfidence.High, high!.Confidence);
        Assert.Equal(ErrorCodeConfidence.Medium, medium!.Confidence);
        Assert.Equal(ErrorCodeConfidence.Low, low!.Confidence);
    }

    [Fact]
    public void TryLookup_entries_have_source_attribution()
    {
        var entry = ErrorCodeCatalog.TryLookup("0x80070005");
        Assert.NotNull(entry);
        Assert.False(string.IsNullOrWhiteSpace(entry!.Source), "Source must not be empty");
    }
}

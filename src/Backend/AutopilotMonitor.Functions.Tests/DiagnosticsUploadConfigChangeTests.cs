using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The Collect Logs capability flip that feeds the DiagnosticsUploadEnabled/Disabled ops
/// events: only a change of "would an on-demand upload work" counts, never a plain edit.
/// </summary>
public class DiagnosticsUploadConfigChangeTests
{
    private static TenantConfiguration Cfg(string? mode, string? destination = null, string? sas = null) => new()
    {
        TenantId = "t1",
        DiagnosticsUploadMode = mode!,
        DiagnosticsUploadDestination = destination!,
        DiagnosticsBlobSasUrl = sas!,
    };

    [Theory]
    [InlineData(null, null, null, false)]
    [InlineData("Off", "Hosted", null, false)]
    [InlineData("OnFailure", null, null, false)]
    [InlineData("OnFailure", "Hosted", null, true)]
    [InlineData("Always", "CustomerSas", "https://x.blob.core.windows.net/c?sig=1", true)]
    [InlineData("Always", null, "https://x.blob.core.windows.net/c?sig=1", true)]
    public void IsConfigured_mirrors_the_feature_flag_semantics(string? mode, string? dest, string? sas, bool expected)
    {
        Assert.Equal(expected, DiagnosticsUploadConfigChange.IsConfigured(Cfg(mode, dest, sas)));
        Assert.False(DiagnosticsUploadConfigChange.IsConfigured(null));
    }

    [Fact]
    public void Detect_quick_config_flip_reports_enabled_with_destination_and_mode()
    {
        // The Collect Logs quick-config dialog: Off/unset -> Hosted + OnFailure.
        var change = DiagnosticsUploadConfigChange.Detect(Cfg("Off"), Cfg("OnFailure", "Hosted"));

        Assert.NotNull(change);
        Assert.True(change!.Enabled);
        Assert.Equal("Hosted", change.Destination);
        Assert.Equal("OnFailure", change.Mode);
    }

    [Fact]
    public void Detect_missing_before_row_counts_as_not_configured()
    {
        var change = DiagnosticsUploadConfigChange.Detect(null, Cfg("Always", "Hosted"));
        Assert.True(change?.Enabled);
    }

    [Fact]
    public void Detect_reports_disabled_when_mode_goes_off()
    {
        var change = DiagnosticsUploadConfigChange.Detect(Cfg("Always", "Hosted"), Cfg("Off", "Hosted"));

        Assert.NotNull(change);
        Assert.False(change!.Enabled);
        Assert.Equal("Off", change.Mode);
    }

    [Fact]
    public void Detect_ignores_edits_that_keep_the_capability_state()
    {
        // Mode change while staying on: no flip.
        Assert.Null(DiagnosticsUploadConfigChange.Detect(Cfg("OnFailure", "Hosted"), Cfg("Always", "Hosted")));
        // Destination not yet usable in both states: no flip.
        Assert.Null(DiagnosticsUploadConfigChange.Detect(Cfg("Off"), Cfg("OnFailure")));
        // Unchanged.
        Assert.Null(DiagnosticsUploadConfigChange.Detect(Cfg("Off"), Cfg("Off")));
    }
}

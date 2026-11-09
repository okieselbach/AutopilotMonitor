using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The setup guide's markers (POST config/{tenantId}/setup-guide) and the done rules the feature
/// flags derive from the row.
/// </summary>
public class SetupGuideFunctionTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
    private const string Admin = "admin@contoso.com";

    // ── step parsing ──

    [Theory]
    [InlineData("settings", "settings")]
    [InlineData(" Hardware ", "hardware")]
    [InlineData("DISMISS", "dismiss")]
    [InlineData("restore", "restore")]
    [InlineData("validation", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeStep_accepts_only_the_four_steps(string? input, string? expected)
    {
        Assert.Equal(expected, SetupGuideFunction.NormalizeStep(input));
    }

    // ── ApplyStep ──

    [Fact]
    public void Settings_step_is_stamped_once()
    {
        var row = new TenantConfiguration();

        Assert.True(SetupGuideFunction.ApplyStep(row, "settings", Admin, Now));
        Assert.Equal(Now, row.SetupGuideSettingsReviewedAt);
        Assert.Equal(Admin, row.UpdatedBy);

        // A second completion keeps the first one on record and writes nothing.
        Assert.False(SetupGuideFunction.ApplyStep(row, "settings", "other@contoso.com", Now.AddHours(1)));
        Assert.Equal(Now, row.SetupGuideSettingsReviewedAt);
    }

    [Fact]
    public void Hardware_step_is_stamped_once()
    {
        var row = new TenantConfiguration();

        Assert.True(SetupGuideFunction.ApplyStep(row, "hardware", Admin, Now));
        Assert.Equal(Now, row.SetupGuideHardwareReviewedAt);
        Assert.False(SetupGuideFunction.ApplyStep(row, "hardware", Admin, Now.AddMinutes(5)));
    }

    [Fact]
    public void Dismiss_records_who_and_restore_clears_it()
    {
        var row = new TenantConfiguration();

        Assert.True(SetupGuideFunction.ApplyStep(row, "dismiss", Admin, Now));
        Assert.Equal(Now, row.SetupGuideDismissedAt);
        Assert.Equal(Admin, row.SetupGuideDismissedBy);
        Assert.False(SetupGuideFunction.ApplyStep(row, "dismiss", Admin, Now.AddMinutes(1)));

        Assert.True(SetupGuideFunction.ApplyStep(row, "restore", Admin, Now.AddMinutes(2)));
        Assert.Null(row.SetupGuideDismissedAt);
        Assert.Null(row.SetupGuideDismissedBy);
        Assert.False(SetupGuideFunction.ApplyStep(row, "restore", Admin, Now.AddMinutes(3)));
    }

    [Fact]
    public void Unknown_step_writes_nothing()
    {
        var row = new TenantConfiguration { UpdatedBy = "earlier" };

        Assert.False(SetupGuideFunction.ApplyStep(row, "validation", Admin, Now));
        Assert.Equal("earlier", row.UpdatedBy);
    }

    // ── SetupGuideState.SettingsDone ──

    [Fact]
    public void Settings_are_open_on_a_new_tenant()
    {
        Assert.False(SetupGuideState.SettingsDone(TenantConfiguration.CreateDefault("t")));
    }

    [Fact]
    public void Settings_are_done_once_reviewed_even_if_nothing_was_applied()
    {
        var config = TenantConfiguration.CreateDefault("t");
        config.SetupGuideSettingsReviewedAt = Now;

        Assert.True(SetupGuideState.SettingsDone(config));
    }

    [Fact]
    public void Settings_are_done_when_all_three_are_already_on()
    {
        var config = TenantConfiguration.CreateDefault("t");
        config.EnableTimezoneAutoSet = true;
        config.EnableSoftwareInventoryAnalyzer = true;
        config.DiagnosticsUploadDestination = "Hosted";
        config.DiagnosticsUploadMode = "OnFailure";

        Assert.True(SetupGuideState.SettingsDone(config));
    }

    [Fact]
    public void Settings_stay_open_while_one_of_the_three_is_off()
    {
        var config = TenantConfiguration.CreateDefault("t");
        config.EnableTimezoneAutoSet = true;
        config.EnableSoftwareInventoryAnalyzer = true;
        config.DiagnosticsUploadDestination = "Hosted";
        config.DiagnosticsUploadMode = "Off";

        Assert.False(SetupGuideState.SettingsDone(config));
    }

    // ── SetupGuideState.HardwareDone ──

    [Fact]
    public void Hardware_is_open_with_the_default_whitelist()
    {
        Assert.False(SetupGuideState.HardwareDone(TenantConfiguration.CreateDefault("t")));
    }

    [Fact]
    public void Hardware_is_done_once_reviewed()
    {
        var config = TenantConfiguration.CreateDefault("t");
        config.SetupGuideHardwareReviewedAt = Now;

        Assert.True(SetupGuideState.HardwareDone(config));
    }

    [Theory]
    [InlineData("Dell*,HP*,Lenovo*,Microsoft Corporation,VMware*", "*")]
    [InlineData("*", "*")]
    [InlineData("Dell*,HP*,Lenovo*,Microsoft Corporation", "Latitude*")]
    public void Hardware_is_done_when_the_whitelist_differs_from_the_default(string manufacturers, string models)
    {
        var config = TenantConfiguration.CreateDefault("t");
        config.ManufacturerWhitelist = manufacturers;
        config.ModelWhitelist = models;

        Assert.True(SetupGuideState.HardwareDone(config));
    }

    [Fact]
    public void Hardware_ignores_order_case_and_spacing_of_the_default_entries()
    {
        var config = TenantConfiguration.CreateDefault("t");
        config.ManufacturerWhitelist = "microsoft corporation, lenovo*,HP*, dell*";

        Assert.False(SetupGuideState.HardwareDone(config));
    }
}

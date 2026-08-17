using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Default-value tests for the IME-installer archiving settings. The flag gates the
/// ime-msi-archive queue worker (pause, not drop); the size cap guards the download.
/// Defaulting the flag to TRUE is deliberate: archiving is operator infrastructure with
/// ~one small download per Microsoft IME release, and a version missed while the flag is
/// off is gone for good (the versionless CDN moves on).
/// </summary>
public class AdminConfigurationImeMsiArchivingTests
{
    [Fact]
    public void ImeMsiArchivingEnabled_defaults_to_true_on_new_config()
    {
        var cfg = new AdminConfiguration();
        Assert.True(cfg.ImeMsiArchivingEnabled);
    }

    [Fact]
    public void ImeMsiArchivingEnabled_defaults_to_true_on_CreateDefault()
    {
        var cfg = AdminConfiguration.CreateDefault();
        Assert.True(cfg.ImeMsiArchivingEnabled);
    }

    [Fact]
    public void MaxImeMsiDownloadSizeMB_defaults_to_250()
    {
        // 250 MB: ~20x today's MSI so a legitimately grown installer is never missed,
        // while a tampered URL still cannot stream gigabytes (Oliver, 2026-08-18).
        Assert.Equal(250, new AdminConfiguration().MaxImeMsiDownloadSizeMB);
        Assert.Equal(250, AdminConfiguration.CreateDefault().MaxImeMsiDownloadSizeMB);
    }

    [Fact]
    public void Settings_persist_on_the_config()
    {
        var cfg = new AdminConfiguration { ImeMsiArchivingEnabled = false, MaxImeMsiDownloadSizeMB = 50 };
        Assert.False(cfg.ImeMsiArchivingEnabled);
        Assert.Equal(50, cfg.MaxImeMsiDownloadSizeMB);
    }
}

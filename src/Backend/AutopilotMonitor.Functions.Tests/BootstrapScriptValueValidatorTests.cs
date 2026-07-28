using AutopilotMonitor.Functions.Security;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the injection-defense invariants of <see cref="BootstrapScriptValueValidator"/>,
/// ported from the portal's utils/bootstrapValidation.ts (and its vitest suite). The
/// validated values are interpolated into a PowerShell script that runs as SYSTEM
/// during OOBE — any relaxation here widens the injection surface.
/// </summary>
public class BootstrapScriptValueValidatorTests
{
    private const string GoodTenant = "11111111-1111-1111-1111-111111111111";
    private const string GoodToken = "22222222-2222-2222-2222-222222222222";
    private const string GoodUrl = "https://download.autopilotmonitor.com/agent/AutopilotMonitor-Agent.zip";

    private static DateTime FutureExpiry => DateTime.UtcNow.AddHours(24);

    private static bool Validate(
        string tenantId = GoodTenant,
        string token = GoodToken,
        string url = GoodUrl,
        DateTime? expiresAt = null,
        BootstrapScriptValueValidator.ValidationFailure? expectedFailure = null)
    {
        var ok = BootstrapScriptValueValidator.TryValidate(
            tenantId, token, url, expiresAt ?? FutureExpiry, out _, out var failure);
        if (!ok && expectedFailure.HasValue)
            Assert.Equal(expectedFailure.Value, failure);
        return ok;
    }

    [Fact]
    public void Accepts_valid_values()
    {
        Assert.True(Validate());
    }

    // ── tenantId / token: exact canonical GUIDs only ──
    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("11111111111111111111111111111111")]                  // "N" format — no hyphens
    [InlineData("{11111111-1111-1111-1111-111111111111}")]            // "B" format
    [InlineData(" 11111111-1111-1111-1111-111111111111")]             // leading space (TryParseExact would trim)
    [InlineData("11111111-1111-1111-1111-11111111111$")]
    public void Rejects_non_canonical_tenant_ids(string tenantId)
    {
        Assert.False(Validate(tenantId: tenantId,
            expectedFailure: BootstrapScriptValueValidator.ValidationFailure.TenantId));
    }

    [Fact]
    public void Rejects_non_guid_token()
    {
        Assert.False(Validate(token: "'; $(calc); '",
            expectedFailure: BootstrapScriptValueValidator.ValidationFailure.Token));
    }

    // ── agentDownloadUrl: https + host allow-list + strict path ──
    [Theory]
    [InlineData("https://evil.example.com/x.zip'; $(calc); '")]        // hostile payload from the vitest suite
    [InlineData("https://evil.example.com/agent/AutopilotMonitor-Agent.zip")]
    [InlineData("http://download.autopilotmonitor.com/agent/AutopilotMonitor-Agent.zip")]   // not https
    [InlineData("https://download.autopilotmonitor.com:8443/agent/AutopilotMonitor-Agent.zip")] // explicit port
    [InlineData("https://user:pw@download.autopilotmonitor.com/agent/AutopilotMonitor-Agent.zip")] // userinfo
    [InlineData("https://download.autopilotmonitor.com/agent/AutopilotMonitor-Agent.zip?x=1")]  // query
    [InlineData("https://download.autopilotmonitor.com/agent/AutopilotMonitor-Agent.zip#f")]    // fragment
    [InlineData("https://download.autopilotmonitor.com/other/AutopilotMonitor-Agent.zip")]      // wrong prefix
    [InlineData("https://download.autopilotmonitor.com/agent/.hidden.zip")]                     // leading dot
    [InlineData("https://download.autopilotmonitor.com/agent/AutopilotMonitor-Agent.exe")]      // not .zip
    [InlineData("")]
    public void Rejects_disallowed_download_urls(string url)
    {
        Assert.False(Validate(url: url,
            expectedFailure: BootstrapScriptValueValidator.ValidationFailure.AgentDownloadUrl));
    }

    [Theory]
    [InlineData("https://download.autopilotmonitor.com/agent/AutopilotMonitor-Agent.zip")]
    [InlineData("https://autopilotmonitor.blob.core.windows.net/agent/AutopilotMonitor-Agent.zip")] // legacy blob host
    [InlineData("https://download.autopilotmonitor.com/agent/AutopilotMonitor-Agent-v2.zip")]
    public void Accepts_allowlisted_download_urls(string url)
    {
        Assert.True(Validate(url: url));
    }

    [Fact]
    public void Rejects_filename_longer_than_80_chars()
    {
        // First char + 80 more before ".zip" exceeds the {0,79} tail bound.
        var tooLong = "https://download.autopilotmonitor.com/agent/a" + new string('b', 80) + ".zip";
        Assert.False(Validate(url: tooLong,
            expectedFailure: BootstrapScriptValueValidator.ValidationFailure.AgentDownloadUrl));

        var atLimit = "https://download.autopilotmonitor.com/agent/a" + new string('b', 75) + ".zip";
        Assert.True(Validate(url: atLimit));
    }

    // ── expiresAt: strictly future, at most 14 days out ──
    [Fact]
    public void Rejects_past_expiry()
    {
        Assert.False(Validate(expiresAt: DateTime.UtcNow.AddMinutes(-1),
            expectedFailure: BootstrapScriptValueValidator.ValidationFailure.ExpiresAt));
    }

    [Fact]
    public void Rejects_expiry_beyond_14_days()
    {
        Assert.False(Validate(expiresAt: DateTime.UtcNow.AddDays(15),
            expectedFailure: BootstrapScriptValueValidator.ValidationFailure.ExpiresAt));
    }

    [Fact]
    public void Accepts_expiry_within_the_window()
    {
        Assert.True(Validate(expiresAt: DateTime.UtcNow.AddDays(7)));
    }
}

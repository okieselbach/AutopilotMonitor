using AutopilotMonitor.Functions.Functions.Bootstrap;
using AutopilotMonitor.Functions.Security;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the OOBE bootstrap script generator, ported from the portal's
/// app/go/[code]/route.ts. The golden test asserts byte parity (modulo line
/// endings) with the original TypeScript template output — the reference file
/// TestData/oobe-bootstrap-golden.ps1 was captured by evaluating the TS template
/// literal with the same fixed inputs. Ports the behavioral cases of the vitest
/// suite app/go/[code]/__tests__/route.test.ts that concern script generation.
/// </summary>
public class OobeBootstrapScriptGeneratorTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string Token = "22222222-2222-2222-2222-222222222222";
    private const string DownloadUrl = "https://download.autopilotmonitor.com/agent/AutopilotMonitor-Agent.zip";

    private static BootstrapScriptValueValidator.ValidatedValues FixedValues() =>
        new(TenantId, Token, DownloadUrl,
            new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc));

    private static string Generate() =>
        OobeBootstrapScriptGenerator.GenerateSuccessScript(
            FixedValues(),
            new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc));

    // ── Golden parity with the original Next.js template ──
    [Fact]
    public void SuccessScript_matches_the_typescript_reference_output()
    {
        var goldenPath = Path.Combine(AppContext.BaseDirectory, "TestData", "oobe-bootstrap-golden.ps1");
        var golden = File.ReadAllText(goldenPath);

        var actual = Generate();

        // Normalize line endings on both sides: git checkout / editors may flip
        // LF↔CRLF in either the .cs raw string or the golden asset; PowerShell
        // accepts both. Content parity is what the test pins.
        static string Normalize(string s) => s.Replace("\r\n", "\n");
        Assert.Equal(Normalize(golden), Normalize(actual));
    }

    // ── Success-path markers (vitest parity) ──
    [Fact]
    public void SuccessScript_contains_the_expected_markers()
    {
        var script = Generate();

        Assert.StartsWith("#Requires -RunAsAdministrator", script);
        Assert.Contains($"--tenant-id '{TenantId}'", script);
        Assert.Contains($"--bootstrap-token '{Token}'", script);
        Assert.Contains($"$AgentDownloadUrl = '{DownloadUrl}'", script);
        Assert.Contains("Generated: 2026-07-28T12:00:00.000Z", script);
        Assert.Contains("Expires:   2026-08-01T12:00:00.000Z", script);
        Assert.Contains("https://portal.autopilotmonitor.com", script);
    }

    // ── SECURITY INVARIANT: every substituted value sits inside a single-quoted
    //    PS literal (single-quoted strings do not expand $(), $var, or backticks).
    //    The expiry timestamp appears twice: once in the plain-text comment block
    //    and once quoted in the Write-Log line. ──
    [Fact]
    public void SuccessScript_frames_all_interpolated_values_in_single_quotes()
    {
        var script = Generate();

        Assert.Contains($"'{DownloadUrl}'", script);
        Assert.Contains($"'{Token}'", script);
        Assert.Contains($"'{TenantId}'", script);
        Assert.Contains("'Bootstrap token expires: 2026-08-01T12:00:00.000Z'", script);
    }

    // ── Error script: message surfaces via Write-Host, always as a script ──
    [Fact]
    public void ErrorScript_contains_the_message()
    {
        var script = OobeBootstrapScriptGenerator.GenerateErrorScript("Invalid bootstrap code format.");

        Assert.Contains("ERROR: Invalid bootstrap code format.", script);
        Assert.Contains("Write-Host", script);
    }

    [Fact]
    public void ErrorScript_doubles_single_quotes_for_powershell()
    {
        var script = OobeBootstrapScriptGenerator.GenerateErrorScript("it's a 'bad' code");

        Assert.Contains("it''s a ''bad'' code", script);
    }

    // ── Cap is applied to the RAW message before quote-escaping, so a
    //    quote-heavy message cannot double past the cap. ──
    [Fact]
    public void ErrorScript_caps_the_message_at_200_chars_before_escaping()
    {
        var script = OobeBootstrapScriptGenerator.GenerateErrorScript(new string('X', 300));

        Assert.Contains(new string('X', 200) + "...", script);
        Assert.DoesNotContain(new string('X', 201), script);
    }

    [Fact]
    public void ErrorScript_cap_counts_raw_length_not_escaped_length()
    {
        var script = OobeBootstrapScriptGenerator.GenerateErrorScript(new string('\'', 300));

        // 200 raw quotes escape to 400 — allowed, because the cap is pre-escape.
        Assert.Contains(new string('\'', 400) + "...", script);
    }
}

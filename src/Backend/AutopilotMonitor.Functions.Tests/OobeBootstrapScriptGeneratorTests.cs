using AutopilotMonitor.Functions.Functions.Bootstrap;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the OOBE bootstrap script generator, ported from the portal's
/// app/go/[code]/route.ts. The golden file TestData/oobe-bootstrap-golden.ps1 is the
/// pin of the shipped template (modulo line endings): first captured from the TS
/// template literal, since D-354 extended with the installer's shared install helpers
/// (payload catalog check, protected agent folder, run folder), which the generator
/// embeds verbatim. Ports the behavioral cases of the
/// vitest suite app/go/[code]/__tests__/route.test.ts that concern script generation.
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
        Assert.Contains($"'--tenant-id', '{TenantId}'", script);
        Assert.Contains($"'--bootstrap-token', '{Token}'", script);
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

    // ── D-354: the payload is checked against its signed catalog before --install, with the
    //    installer's own code for that and for the agent folder ──
    [Fact]
    public void SuccessScript_embeds_the_installers_shared_install_helpers_verbatim()
    {
        var installer = File.ReadAllText(Path.Combine(FindRepoRoot(), "scripts", "Bootstrap", "Install-AutopilotMonitor.ps1"))
            .Replace("\r\n", "\n");
        var begin = installer.IndexOf(OobeBootstrapScriptGenerator.SharedBlockBegin, StringComparison.Ordinal);
        var end = installer.IndexOf('\n', installer.IndexOf(OobeBootstrapScriptGenerator.SharedBlockEnd, StringComparison.Ordinal));
        var block = installer.Substring(begin, end - begin);

        Assert.Contains("function Get-AgentPayloadRejections", block);
        Assert.Contains("function Install-VerifiedAgentPayload", block);
        Assert.Contains("function Initialize-AgentBase", block);
        Assert.Contains("function New-RunFolder", block);
        Assert.Contains("function Remove-OwnTree", block);
        Assert.Contains("function Invoke-AgentInstallMode", block);
        Assert.Contains(block, Generate().Replace("\r\n", "\n"));
    }

    [Fact]
    public void SuccessScript_prepares_the_folder_and_verifies_the_payload_before_installing_the_agent()
    {
        var script = Generate();

        var prepare = script.IndexOf("Initialize-AgentBase -Path $AgentBasePath", StringComparison.Ordinal);
        var verify = script.IndexOf(
            "Install-VerifiedAgentPayload -ZipPath $zipPath -StagingPath $staging -TargetPath $AgentBinPath -ExpectedPublisher $ExpectedPublisher -CatalogName $AgentCatalogName",
            StringComparison.Ordinal);
        var install = script.IndexOf("Invoke-AgentInstallMode -ExePath $agentExePath -Arguments @('--install'", StringComparison.Ordinal);

        Assert.True(prepare > 0, "the generated script never prepares the agent folder");
        Assert.True(verify > prepare, "the payload check must run in the prepared folder");
        Assert.True(install > verify, "--install must run after the payload check");
        Assert.DoesNotContain("Expand-Archive -Path $zipPath -DestinationPath $AgentBinPath", script);
        Assert.DoesNotContain("$env:TEMP", script);
    }

    // #Requires is ignored under irm | iex, so the script checks for itself before it changes anything.
    [Fact]
    public void SuccessScript_refuses_to_run_without_elevation_before_it_changes_anything()
    {
        var script = Generate();

        var check = script.IndexOf("if (-not (Test-IsElevated)) {", StringComparison.Ordinal);
        var tryStart = script.IndexOf("\ntry {", StringComparison.Ordinal);

        Assert.True(check > tryStart && tryStart > 0, "the elevation check must open the main block");
        Assert.True(check < script.IndexOf("Initialize-AgentBase -Path $AgentBasePath", StringComparison.Ordinal));
    }

    [Fact]
    public void SuccessScript_takes_pin_and_catalog_name_from_the_constants()
    {
        var script = Generate();

        Assert.Contains($"$ExpectedPublisher = '{Constants.CodeSigningPublisherPattern}'", script);
        Assert.Contains($"$AgentCatalogName  = '{Constants.AgentPayloadCatalogFileName}'", script);
    }

    [Fact]
    public void SuccessScript_leaves_no_placeholder_unreplaced()
    {
        Assert.DoesNotMatch("__[A-Z_]+__", Generate());
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
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

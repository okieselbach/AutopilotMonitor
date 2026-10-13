using System.Text.RegularExpressions;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The publisher pin and the payload catalog name live in places that cannot share code: the C#
/// constants (OOBE bootstrap template, agent self-updater), the bootstrap installer, the frozen
/// loader and the release workflow. A drift would make one verifier refuse what the others
/// accept, or the pipeline publish a catalog no device looks for, so each literal is pinned
/// against its constant here.
/// </summary>
public class CodeSigningParityTests
{
    private static readonly Regex ExpectedPublisherLine = new(
        "^\\$ExpectedPublisher = \"(?<pin>[^\"]+)\"\\r?$", RegexOptions.Multiline);

    [Theory]
    [InlineData("Install-AutopilotMonitor.ps1")]
    [InlineData("Start-AutopilotMonitor.ps1")]
    public void Bootstrap_script_pin_matches_the_constant(string scriptName)
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "scripts", "Bootstrap", scriptName));

        var matches = ExpectedPublisherLine.Matches(source);

        Assert.Single(matches);
        Assert.Equal(Constants.CodeSigningPublisherPattern, matches[0].Groups["pin"].Value);
    }

    [Fact]
    public void Pin_is_a_subject_pattern_not_a_thumbprint()
    {
        Assert.StartsWith("*O=", Constants.CodeSigningPublisherPattern);
        Assert.EndsWith("*", Constants.CodeSigningPublisherPattern);
        Assert.DoesNotMatch("^[0-9A-Fa-f]{40}$", Constants.CodeSigningPublisherPattern.Trim('*'));
        // The agent's matcher implements -like for * and ? only (PayloadCatalogVerifier).
        Assert.DoesNotContain("[", Constants.CodeSigningPublisherPattern);
    }

    [Fact]
    public void Release_workflow_catalog_name_matches_the_constant()
    {
        var workflow = File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "workflows", "build-agent.yml"));

        var match = Regex.Match(workflow, "^\\s+PAYLOAD_CATALOG: (?<name>\\S+)\\r?$", RegexOptions.Multiline);

        Assert.True(match.Success, "build-agent.yml no longer declares PAYLOAD_CATALOG");
        Assert.Equal(Constants.AgentPayloadCatalogFileName, match.Groups["name"].Value);
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
}

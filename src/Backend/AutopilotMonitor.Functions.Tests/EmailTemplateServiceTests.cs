using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the operator-override layer over the built-in transactional templates:
/// override wins when stored, built-in otherwise; placeholder rendering matches the
/// built-ins' empty-domain fallback; the send path is fail-soft on storage errors while the
/// admin path is not; save validates and invalidates the cache; reset is idempotent.
/// </summary>
public sealed class EmailTemplateServiceTests
{
    [Fact]
    public async Task NoOverride_ReturnsBuiltIn_Rendered()
    {
        var (sut, _) = Build();

        var html = await sut.GetHtmlAsync(EmailTemplateKind.Welcome, "contoso.invalid");

        Assert.Equal(EmailTemplates.GetPreviewApprovedHtml("contoso.invalid"), html);
        Assert.DoesNotContain(EmailTemplateService.DomainPlaceholder, html);
    }

    [Fact]
    public async Task Override_WinsAndRendersPlaceholder()
    {
        var (sut, repo) = Build();
        repo.Setup(r => r.GetEmailTemplateOverrideAsync("farewell"))
            .ReturnsAsync(new EmailTemplateOverride { Kind = "farewell", Html = "<p>Bye {{domainName}}</p>" });

        var html = await sut.GetHtmlAsync(EmailTemplateKind.Farewell, "contoso.invalid");

        Assert.Equal("<p>Bye contoso.invalid</p>", html);
    }

    [Fact]
    public async Task Override_EmptyDomain_FallsBackToGenericLabel_LikeBuiltIn()
    {
        var (sut, repo) = Build();
        repo.Setup(r => r.GetEmailTemplateOverrideAsync("welcome"))
            .ReturnsAsync(new EmailTemplateOverride { Kind = "welcome", Html = "Hi {{domainName}}" });

        Assert.Equal("Hi your organization", await sut.GetHtmlAsync(EmailTemplateKind.Welcome, ""));
    }

    [Fact]
    public async Task StorageError_SendPath_FallsBackToBuiltIn_AdminPath_Throws()
    {
        var (sut, repo) = Build();
        repo.Setup(r => r.GetEmailTemplateOverrideAsync(It.IsAny<string>()))
            .ThrowsAsync(new System.InvalidOperationException("storage down"));

        var html = await sut.GetHtmlAsync(EmailTemplateKind.Welcome, "contoso.invalid");
        Assert.Contains("contoso.invalid", html);

        await Assert.ThrowsAsync<System.InvalidOperationException>(() => sut.GetOverrideAsync(EmailTemplateKind.Welcome));
    }

    [Fact]
    public async Task Save_Validates_ThenInvalidatesCache()
    {
        var (sut, repo) = Build();
        repo.Setup(r => r.GetEmailTemplateOverrideAsync("welcome")).ReturnsAsync((EmailTemplateOverride?)null);

        // warm the cache with "no override"
        Assert.Null(await sut.GetOverrideAsync(EmailTemplateKind.Welcome));

        await Assert.ThrowsAsync<System.ArgumentException>(() => sut.SaveOverrideAsync(EmailTemplateKind.Welcome, "   ", "ga@contoso.invalid"));
        await Assert.ThrowsAsync<System.ArgumentException>(() =>
            sut.SaveOverrideAsync(EmailTemplateKind.Welcome, new string('x', EmailTemplateService.MaxHtmlLength + 1), "ga@contoso.invalid"));
        repo.Verify(r => r.SaveEmailTemplateOverrideAsync(It.IsAny<EmailTemplateOverride>()), Times.Never);

        EmailTemplateOverride? stored = null;
        repo.Setup(r => r.SaveEmailTemplateOverrideAsync(It.IsAny<EmailTemplateOverride>()))
            .Callback<EmailTemplateOverride>(e => stored = e).Returns(Task.CompletedTask);
        repo.Setup(r => r.GetEmailTemplateOverrideAsync("welcome")).ReturnsAsync(() => stored);

        var saved = await sut.SaveOverrideAsync(EmailTemplateKind.Welcome, "<p>{{domainName}}</p>", "ga@contoso.invalid");

        Assert.Equal("welcome", saved.Kind);
        Assert.Equal("ga@contoso.invalid", saved.UpdatedBy);
        // cache was invalidated: the next read sees the stored override, not the cached null
        Assert.NotNull(await sut.GetOverrideAsync(EmailTemplateKind.Welcome));
        Assert.Equal("<p>contoso.invalid</p>", await sut.GetHtmlAsync(EmailTemplateKind.Welcome, "contoso.invalid"));
    }

    [Fact]
    public async Task Delete_ClearsCache_ReturnsToBuiltIn()
    {
        var (sut, repo) = Build();
        var stored = new EmailTemplateOverride { Kind = "farewell", Html = "custom {{domainName}}" };
        repo.Setup(r => r.GetEmailTemplateOverrideAsync("farewell")).ReturnsAsync(() => stored);
        Assert.Equal("custom contoso.invalid", await sut.GetHtmlAsync(EmailTemplateKind.Farewell, "contoso.invalid"));

        repo.Setup(r => r.DeleteEmailTemplateOverrideAsync("farewell")).Callback(() => stored = null!).Returns(Task.CompletedTask);
        await sut.DeleteOverrideAsync(EmailTemplateKind.Farewell, "ga@contoso.invalid");

        Assert.Equal(EmailTemplates.GetOffboardingFarewellHtml("contoso.invalid"),
            await sut.GetHtmlAsync(EmailTemplateKind.Farewell, "contoso.invalid"));
    }

    [Theory]
    [InlineData("welcome", true)]
    [InlineData("Farewell", true)]
    [InlineData("invoice", false)]
    [InlineData(null, false)]
    public void TryParseKind_AcceptsOnlyKnownKinds(string? value, bool expected)
        => Assert.Equal(expected, EmailTemplateService.TryParseKind(value, out _));

    [Fact]
    public void BuiltInRaw_ContainsPlaceholder_ForBothKinds()
    {
        Assert.Contains(EmailTemplateService.DomainPlaceholder, EmailTemplateService.BuiltInRaw(EmailTemplateKind.Welcome));
        Assert.Contains(EmailTemplateService.DomainPlaceholder, EmailTemplateService.BuiltInRaw(EmailTemplateKind.Farewell));
    }

    private static (EmailTemplateService sut, Mock<IConfigRepository> repo) Build()
    {
        var repo = new Mock<IConfigRepository>(MockBehavior.Loose);
        var sut = new EmailTemplateService(repo.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<EmailTemplateService>.Instance);
        return (sut, repo);
    }
}

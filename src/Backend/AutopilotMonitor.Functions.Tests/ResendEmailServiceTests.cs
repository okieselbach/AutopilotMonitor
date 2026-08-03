using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Coverage for the post-offboarding farewell-email send path. The actual Resend HTTP
/// call is not exercised here — every test drives one of the no-op gates. Pins:
/// <list type="bullet">
///   <item><see cref="ResendEmailService.SendAsync"/> never throws — not when the API key
///         is empty and not when the recipient is missing. The handler's fail-soft
///         try/catch is a belt; this is the suspenders.</item>
///   <item>Gate ordering: missing-key short-circuits before missing-recipient.</item>
///   <item>The farewell template is final: no [DRAFT]/placeholder residue, domain is
///         interpolated, empty domain falls back to "your organization".</item>
/// </list>
/// </summary>
public sealed class ResendEmailServiceTests
{
    [Fact]
    public async Task SendAsync_EmptyApiKey_NoOps_AndLogsMissingKeyDebugLine()
    {
        var logger = new CapturingLogger<ResendEmailService>();
        var sut = Build(apiKey: "", logger);

        await sut.SendAsync(
            toEmail: "ops@contoso.invalid",
            domainName: "contoso.invalid",
            tenantId: "88888888-8888-8888-8888-888888888888",
            ct: CancellationToken.None);

        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("RESEND_API_KEY not configured"));
    }

    [Fact]
    public async Task SendAsync_EmptyRecipient_NoOps_AndLogsMissingEmailDebugLine()
    {
        var logger = new CapturingLogger<ResendEmailService>();
        var sut = Build(apiKey: "any-non-empty-key", logger);

        await sut.SendAsync(
            toEmail: "",
            domainName: "contoso.invalid",
            tenantId: "88888888-8888-8888-8888-888888888888");

        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("No notification email captured"));
    }

    [Fact]
    public async Task SendAsync_EmptyApiKey_ShortCircuitsBeforeRecipientCheck()
    {
        // Pins the gate ordering: missing-key > missing-email. Both are empty here; only
        // the key line must show up.
        var logger = new CapturingLogger<ResendEmailService>();
        var sut = Build(apiKey: "", logger);

        await sut.SendAsync(
            toEmail: "",
            domainName: "contoso.invalid",
            tenantId: "88888888-8888-8888-8888-888888888888");

        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("RESEND_API_KEY not configured"));
        Assert.DoesNotContain(logger.Entries,
            e => e.Message.Contains("No notification email captured"));
    }

    [Fact]
    public void FarewellTemplate_IsFinal_NoDraftResidue()
    {
        Assert.DoesNotContain("DRAFT", EmailTemplates.OffboardingFarewellSubject);

        var html = EmailTemplates.GetOffboardingFarewellHtml("contoso.invalid");
        Assert.DoesNotContain("DRAFT", html);
        Assert.DoesNotContain("TEMPLATE NOT FINALISED", html);
        Assert.DoesNotContain("TODO", html);
    }

    [Fact]
    public void FarewellTemplate_InterpolatesDomain_AndLinksFeedbackChannels()
    {
        var html = EmailTemplates.GetOffboardingFarewellHtml("contoso.invalid");

        Assert.Contains("contoso.invalid", html);
        // The mail is sent from a noreply address after portal access is gone, so the
        // feedback pointers must be external channels the recipient can actually reach.
        Assert.Contains("https://github.com/okieselbach/AutopilotMonitor/issues", html);
        Assert.Contains("linkedin.com", html);
    }

    [Fact]
    public void FarewellTemplate_EmptyDomain_FallsBackToGenericLabel()
    {
        var html = EmailTemplates.GetOffboardingFarewellHtml("");
        Assert.Contains("your organization", html);
    }

    private static ResendEmailService Build(string apiKey, ILogger<ResendEmailService> logger)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RESEND_API_KEY"] = apiKey,
            })
            .Build();
        return new ResendEmailService(config, logger);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, System.Exception? exception,
            System.Func<TState, System.Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

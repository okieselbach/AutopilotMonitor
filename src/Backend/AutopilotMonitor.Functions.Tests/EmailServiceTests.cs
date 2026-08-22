using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Coverage for the provider-neutral <see cref="EmailService"/> (welcome + farewell mails).
/// The provider HTTP call is driven through a stub handler, so the Mandrill wire format,
/// the result-array interpretation and the fail-soft contract are pinned without network.
/// <list type="bullet">
///   <item>Neither send method ever throws — not on a missing key, missing recipient,
///         HTTP 500, a rejected recipient, nor a malformed body.</item>
///   <item>Gate ordering: missing-key short-circuits before missing-recipient.</item>
///   <item>Tracking is off on the wire (trust-page claim: provider receives only the
///         recipient address and tenant domain).</item>
///   <item>The farewell template is final: no [DRAFT]/placeholder residue, domain is
///         interpolated, empty domain falls back to "your organization".</item>
/// </list>
/// </summary>
public sealed class EmailServiceTests
{
    private const string TenantId = "88888888-8888-8888-8888-888888888888";

    // ----- configuration gates -----

    [Fact]
    public async Task SendAsync_EmptyApiKey_NoOps_AndLogsMissingKeyDebugLine()
    {
        var (sut, handler, logger) = Build(apiKey: "");

        await sut.SendAsync("ops@contoso.invalid", "contoso.invalid", TenantId, CancellationToken.None);

        Assert.Equal(0, handler.CallCount);
        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("Email:ApiKey not configured"));
    }

    [Fact]
    public async Task SendAsync_EmptyRecipient_NoOps_AndLogsMissingEmailDebugLine()
    {
        var (sut, handler, logger) = Build(apiKey: "any-non-empty-key");

        await sut.SendAsync("", "contoso.invalid", TenantId);

        Assert.Equal(0, handler.CallCount);
        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("No notification email captured"));
    }

    [Fact]
    public async Task SendAsync_EmptyApiKey_ShortCircuitsBeforeRecipientCheck()
    {
        // Pins the gate ordering: missing-key > missing-email. Both are empty here; only
        // the key line must show up.
        var (sut, _, logger) = Build(apiKey: "");

        await sut.SendAsync("", "contoso.invalid", TenantId);

        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("Email:ApiKey not configured"));
        Assert.DoesNotContain(logger.Entries,
            e => e.Message.Contains("No notification email captured"));
    }

    [Fact]
    public async Task SendPreviewApprovedEmailAsync_EmptyApiKey_NoOps()
    {
        var (sut, handler, logger) = Build(apiKey: "");

        await sut.SendPreviewApprovedEmailAsync("it@contoso.invalid", "contoso.invalid");

        Assert.Equal(0, handler.CallCount);
        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("Email:ApiKey not configured"));
    }

    // ----- wire format -----

    [Fact]
    public async Task SendPreviewApprovedEmailAsync_PostsMandrillPayload_WithTrackingOff()
    {
        var (sut, handler, logger) = Build(apiKey: "md-test-key");

        await sut.SendPreviewApprovedEmailAsync("it@contoso.invalid", "contoso.invalid");

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(EmailService.DefaultEndpoint, handler.LastRequest.RequestUri!.ToString());

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var root = doc.RootElement;
        Assert.Equal("md-test-key", root.GetProperty("key").GetString());

        var message = root.GetProperty("message");
        Assert.Equal(EmailService.DefaultFromAddress, message.GetProperty("from_email").GetString());
        Assert.Equal(EmailService.DefaultFromName, message.GetProperty("from_name").GetString());
        Assert.Equal("it@contoso.invalid", message.GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal("to", message.GetProperty("to")[0].GetProperty("type").GetString());
        Assert.Equal(EmailTemplates.PreviewApprovedSubject, message.GetProperty("subject").GetString());
        Assert.Contains("contoso.invalid", message.GetProperty("html").GetString());
        Assert.True(message.GetProperty("auto_text").GetBoolean());
        Assert.False(message.GetProperty("track_opens").GetBoolean());
        Assert.False(message.GetProperty("track_clicks").GetBoolean());
        Assert.Equal("welcome", message.GetProperty("tags")[0].GetString());

        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("Preview approval email sent"));
    }

    [Fact]
    public async Task SendAsync_UsesFarewellTemplate_AndFarewellTag()
    {
        var (sut, handler, logger) = Build(apiKey: "md-test-key");

        await sut.SendAsync("ops@contoso.invalid", "contoso.invalid", TenantId);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var message = doc.RootElement.GetProperty("message");
        Assert.Equal(EmailTemplates.OffboardingFarewellSubject, message.GetProperty("subject").GetString());
        Assert.Equal("offboarding-farewell", message.GetProperty("tags")[0].GetString());
        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("Offboard farewell email sent"));
    }

    [Fact]
    public async Task ConfiguredEndpointAndSender_OverrideDefaults()
    {
        var (sut, handler, _) = Build(apiKey: "md-test-key", extra: new Dictionary<string, string?>
        {
            ["Email:Endpoint"] = "https://mail.example.invalid/send",
            ["Email:FromAddress"] = "hello@example.invalid",
            ["Email:FromName"] = "Example Sender",
        });

        await sut.SendPreviewApprovedEmailAsync("it@contoso.invalid", "contoso.invalid");

        Assert.Equal("https://mail.example.invalid/send", handler.LastRequest!.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(handler.LastBody!);
        var message = doc.RootElement.GetProperty("message");
        Assert.Equal("hello@example.invalid", message.GetProperty("from_email").GetString());
        Assert.Equal("Example Sender", message.GetProperty("from_name").GetString());
    }

    // ----- provider responses / fail-soft -----

    [Fact]
    public async Task ProviderHttpError_DoesNotThrow_AndLogsWarning_WithoutSuccessLine()
    {
        var (sut, _, logger) = Build(apiKey: "md-test-key",
            responder: _ => Json(HttpStatusCode.InternalServerError,
                "{\"status\":\"error\",\"code\":-1,\"name\":\"Invalid_Key\",\"message\":\"Invalid API key\"}"));

        await sut.SendAsync("ops@contoso.invalid", "contoso.invalid", TenantId);

        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("500") && e.Message.Contains("Invalid_Key"));
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Information);
    }

    [Fact]
    public async Task ProviderRejectsRecipient_LogsWarningWithReason_WithoutSuccessLine()
    {
        var (sut, _, logger) = Build(apiKey: "md-test-key",
            responder: _ => Json(HttpStatusCode.OK,
                "[{\"email\":\"ops@contoso.invalid\",\"status\":\"rejected\",\"reject_reason\":\"hard-bounce\",\"_id\":\"abc\"}]"));

        await sut.SendAsync("ops@contoso.invalid", "contoso.invalid", TenantId);

        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("rejected") && e.Message.Contains("hard-bounce"));
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Information);
    }

    [Theory]
    [InlineData("sent")]
    [InlineData("queued")]
    [InlineData("scheduled")]
    public async Task ProviderAcceptedStatuses_CountAsSuccess(string status)
    {
        var (sut, _, logger) = Build(apiKey: "md-test-key",
            responder: _ => Json(HttpStatusCode.OK,
                $"[{{\"email\":\"ops@contoso.invalid\",\"status\":\"{status}\",\"_id\":\"abc\"}}]"));

        await sut.SendAsync("ops@contoso.invalid", "contoso.invalid", TenantId);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task MalformedProviderBody_DoesNotThrow_AndLogsWarning()
    {
        var (sut, _, logger) = Build(apiKey: "md-test-key",
            responder: _ => Json(HttpStatusCode.OK, "not json"));

        await sut.SendAsync("ops@contoso.invalid", "contoso.invalid", TenantId);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Information);
    }

    [Fact]
    public async Task TransportException_DoesNotThrow_AndLogsWarning()
    {
        var (sut, _, logger) = Build(apiKey: "md-test-key",
            responder: _ => throw new HttpRequestException("simulated provider outage"));

        await sut.SendPreviewApprovedEmailAsync("it@contoso.invalid", "contoso.invalid");

        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("Failed to send welcome mail"));
    }

    // ----- templates -----

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

    // ----- harness -----

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static (EmailService sut, StubHandler handler, CapturingLogger<EmailService> logger) Build(
        string apiKey,
        Dictionary<string, string?>? extra = null,
        System.Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        var settings = new Dictionary<string, string?> { ["Email:ApiKey"] = apiKey };
        if (extra is not null)
            foreach (var kv in extra) settings[kv.Key] = kv.Value;

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var handler = new StubHandler();
        if (responder is not null) handler.Responder = responder;
        var logger = new CapturingLogger<EmailService>();
        var sut = new EmailService(new HttpClient(handler, disposeHandler: false), config, logger);
        return (sut, handler, logger);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public System.Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = _ =>
            Json(HttpStatusCode.OK, "[{\"email\":\"x@y.invalid\",\"status\":\"sent\",\"_id\":\"abc\"}]");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return Responder(request);
        }
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

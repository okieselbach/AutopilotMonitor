using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace AutopilotMonitor.Functions.Telemetry;

/// <summary>
/// Some outbound URLs are credentials: a Web Push endpoint is a per-device send capability (K20), and a
/// webhook URL lets whoever holds it post into the customer's channel (Discord, Slack and Telegram carry
/// the token in the path, a Teams workflow the signature in the query, a generic webhook anything). The
/// HttpClients that send to such URLs carry <see cref="MarkingHandler"/>, which marks the async flow of
/// each request; every dependency row emitted inside a marked flow keeps scheme + host only, in
/// <c>Data</c> and <c>Name</c> (D-424). Target, ResultCode and Duration stay, which is all
/// <c>/platform-health</c> reads. The mark sits on the client, as the SSRF gate sits on its connection:
/// it covers every request the client sends, retries included, whichever host the URL names.
/// </summary>
public sealed class CredentialUrlRedactionProcessor : ITelemetryProcessor
{
    private static readonly AsyncLocal<bool> InCredentialRequest = new();

    private readonly ITelemetryProcessor _next;

    public CredentialUrlRedactionProcessor(ITelemetryProcessor next) => _next = next;

    public void Process(ITelemetry item)
    {
        if (InCredentialRequest.Value && item is DependencyTelemetry dependency)
            Redact(dependency);
        _next.Process(item);
    }

    internal static void Redact(DependencyTelemetry dependency)
    {
        // A row whose URL does not parse keeps nothing of it: the host from Target, else nothing.
        var origin = Uri.TryCreate(dependency.Data, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host}"
            : dependency.Target ?? string.Empty;
        dependency.Data = origin;
        dependency.Name = MethodOf(dependency.Name) is { } method ? $"{method} {origin}" : origin;
    }

    /// <summary>The HTTP method the collector put in front of the path ("POST /api/webhooks/…").</summary>
    private static string? MethodOf(string? name)
    {
        var space = name?.IndexOf(' ') ?? -1;
        return space > 0 ? name![..space] : null;
    }

    /// <summary>
    /// The mark for one credential-bearing HttpClient. The value set here flows into the inner handlers,
    /// where the collector tracks the dependency, and ends with the request (this override is async, and
    /// so is HttpClient's own send), so nothing the caller logs or sends afterwards is touched.
    /// </summary>
    public sealed class MarkingHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            InCredentialRequest.Value = true;
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}

using System.Net;
using System.Text;

namespace AutopilotMonitor.Push;

/// <summary>
/// Posts an encrypted body to a push resource (RFC 8030) with VAPID authorization and classifies
/// the answer. One in-process retry for 5xx and network failures, none for anything else; the
/// caller's <see cref="HttpClient"/> brings the connect gate and timeouts. Nothing here logs,
/// and no exception or <see cref="PushSendResult.Reason"/> ever carries the endpoint.
/// </summary>
public sealed class WebPushSender
{
    /// <summary>Largest body a push service must accept (RFC 8030 section 7.2); larger bodies are refused before sending.</summary>
    public const int MaxBodyLength = 4096;

    /// <summary>Longest <c>Retry-After</c> honoured for the in-process retry; longer values fall back to <see cref="DefaultRetryDelay"/>.</summary>
    public static readonly TimeSpan MaxHonouredRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>Delay before the single retry when the response named none.</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>Reason reported when the connect gate refused the destination.</summary>
    public const string SsrfRefusedReason = "ssrf_refused";

    private const int MaxReasonLength = 200;
    private const int MaxReasonBytesRead = 1024;
    private const string SsrfExceptionTypeName = "SsrfException";

    private readonly HttpClient _httpClient;
    private readonly VapidAuthorization _authorization;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a sender over an <see cref="HttpClient"/> the host configured (connect gate, no
    /// redirects, timeout). <paramref name="authorization"/> defaults to a fresh token cache;
    /// <paramref name="timeProvider"/> drives token expiry and the retry delay.
    /// </summary>
    public WebPushSender(HttpClient httpClient, VapidAuthorization? authorization = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _authorization = authorization ?? new VapidAuthorization();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Sends one <c>aes128gcm</c> body to the subscription, signed with <paramref name="key"/>
    /// (the key the subscription was created with) and <paramref name="subject"/>.
    /// </summary>
    public async Task<PushSendResult> SendAsync(
        WebPushSubscription subscription,
        byte[] aes128gcmBody,
        PushSendOptions options,
        VapidKey key,
        string subject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(aes128gcmBody);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(subject);

        if (aes128gcmBody.Length > MaxBodyLength)
        {
            return new PushSendResult(PushOutcome.PayloadTooLarge, null, null, "payload_exceeds_4096");
        }

        for (var attempt = 1; ; attempt++)
        {
            var isLastAttempt = attempt >= 2;
            TimeSpan? retryDelay;
            PushSendResult result;
            try
            {
                using var request = BuildRequest(subscription, aes128gcmBody, options, key, subject);
                using var response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                result = await ClassifyAsync(response, cancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode < 500 || isLastAttempt)
                {
                    return result;
                }

                retryDelay = result.RetryAfter is { } retryAfter && retryAfter <= MaxHonouredRetryAfter && retryAfter >= TimeSpan.Zero
                    ? retryAfter
                    : DefaultRetryDelay;
            }
            catch (HttpRequestException ex) when (IsSsrfRefusal(ex))
            {
                return new PushSendResult(PushOutcome.Failed, null, null, SsrfRefusedReason);
            }
            catch (HttpRequestException ex)
            {
                if (isLastAttempt)
                {
                    return new PushSendResult(PushOutcome.Failed, null, null, $"network_error:{ex.HttpRequestError}");
                }

                retryDelay = DefaultRetryDelay;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (isLastAttempt)
                {
                    return new PushSendResult(PushOutcome.Failed, null, null, "timeout");
                }

                retryDelay = DefaultRetryDelay;
            }

            await Task.Delay(retryDelay.Value, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private HttpRequestMessage BuildRequest(WebPushSubscription subscription, byte[] body, PushSendOptions options, VapidKey key, string subject)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, subscription.Endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("TTL", options.TtlSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Urgency", options.UrgencyHeaderValue);
        if (options.Topic is not null)
        {
            request.Headers.TryAddWithoutValidation("Topic", options.Topic);
        }

        var authorization = _authorization.BuildHeaderValue(subscription.Endpoint, key, subject, _timeProvider.GetUtcNow());
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }

    private async Task<PushSendResult> ClassifyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var retryAfter = ReadRetryAfter(response);
        var reason = await ReadReasonAsync(response, cancellationToken).ConfigureAwait(false);
        var outcome = status switch
        {
            >= 200 and < 300 => PushOutcome.Delivered,
            404 or 410 => PushOutcome.Gone,
            413 => PushOutcome.PayloadTooLarge,
            429 => PushOutcome.RetryLater,
            400 or 401 or 403 => PushOutcome.ConfigError,
            _ => PushOutcome.Failed,
        };
        return new PushSendResult(outcome, status, retryAfter, reason);
    }

    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta;
        }

        if (header.Date is { } date)
        {
            var remaining = date - _timeProvider.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        return null;
    }

    private static async Task<string?> ReadReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            return null;
        }

        var buffer = new byte[MaxReasonBytesRead];
        var read = 0;
        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }
        }

        if (read == 0)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, read);
        var builder = new StringBuilder(Math.Min(text.Length, MaxReasonLength));
        foreach (var c in text)
        {
            if (char.IsControl(c))
            {
                continue;
            }

            builder.Append(c);
            if (builder.Length == MaxReasonLength)
            {
                break;
            }
        }

        var reason = builder.ToString().Trim();
        return reason.Length == 0 ? null : reason;
    }

    // The SSRF gate lives in the host; matched by name so this library stays dependency-free.
    private static bool IsSsrfRefusal(HttpRequestException ex) =>
        ex.InnerException is { } inner && string.Equals(inner.GetType().Name, SsrfExceptionTypeName, StringComparison.Ordinal);
}

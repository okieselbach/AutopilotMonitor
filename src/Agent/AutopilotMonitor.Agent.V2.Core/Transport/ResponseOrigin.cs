#nullable enable
using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AutopilotMonitor.Agent.V2.Core.Transport
{
    /// <summary>Who answered a 401/403 — see <see cref="ResponseOriginClassifier"/>.</summary>
    public enum ResponseOrigin
    {
        /// <summary>The backend: the response echoes the request's <c>X-Correlation-ID</c>. A real authorization verdict.</summary>
        Backend = 0,

        /// <summary>
        /// The platform page of a stopped web app (no echo, "This web app is stopped"). The endpoint
        /// itself is down; counts toward the auth shutdown so a dead endpoint does not keep the agent alive.
        /// </summary>
        PlatformStopped = 1,

        /// <summary>
        /// Anything else without the echo: the platform's "Client Certificate Required" page (the device
        /// certificate did not reach the backend) or a page of a proxy on the network path. Never an
        /// authorization verdict.
        /// </summary>
        Foreign = 2,
    }

    /// <summary>
    /// A classified 401/403 plus the evidence the agent reports about it (log, distress,
    /// <c>telemetry_upload_blocked</c>). Built by <see cref="ResponseOriginClassifier.Classify"/>.
    /// </summary>
    public sealed class ResponseClassification
    {
        /// <summary>Foreign sub-reason: the platform answered "Client Certificate Required".</summary>
        public const string ClientCertificateRequired = "client_certificate_required";

        /// <summary>Foreign sub-reason: any other answer that did not come from the backend (e.g. a proxy page).</summary>
        public const string ForeignResponse = "foreign_response";

        private ResponseClassification(
            ResponseOrigin origin,
            int statusCode,
            string? foreignReason,
            string? pageTitle,
            bool isHtml,
            string? serverCertIssuer)
        {
            Origin = origin;
            StatusCode = statusCode;
            ForeignReason = foreignReason;
            PageTitle = pageTitle;
            IsHtml = isHtml;
            ServerCertIssuer = serverCertIssuer;
        }

        public ResponseOrigin Origin { get; }

        /// <summary>HTTP status of the classified response (401 or 403).</summary>
        public int StatusCode { get; }

        /// <summary><see cref="ClientCertificateRequired"/> or <see cref="ForeignResponse"/> for <see cref="ResponseOrigin.Foreign"/>; null otherwise.</summary>
        public string? ForeignReason { get; }

        /// <summary>HTML <c>&lt;title&gt;</c> of the page (at most 80 characters), null when absent.</summary>
        public string? PageTitle { get; }

        /// <summary>True when the body is an HTML page (declared <c>text/html</c> or sniffed).</summary>
        public bool IsHtml { get; }

        /// <summary>Issuer of the server certificate of the last validated backend connection (<see cref="ServerCertificateObserver"/>); null when unknown.</summary>
        public string? ServerCertIssuer { get; }

        public bool IsForeign => Origin == ResponseOrigin.Foreign;

        internal static ResponseClassification ForBackend(int statusCode) =>
            new ResponseClassification(ResponseOrigin.Backend, statusCode, null, null, isHtml: false, serverCertIssuer: null);

        internal static ResponseClassification ForPlatformStopped(int statusCode, string? pageTitle, bool isHtml, string? serverCertIssuer) =>
            new ResponseClassification(ResponseOrigin.PlatformStopped, statusCode, null, pageTitle, isHtml, serverCertIssuer);

        internal static ResponseClassification ForForeign(int statusCode, string foreignReason, string? pageTitle, bool isHtml, string? serverCertIssuer) =>
            new ResponseClassification(ResponseOrigin.Foreign, statusCode, foreignReason, pageTitle, isHtml, serverCertIssuer);
    }

    /// <summary>
    /// Decides whether a 401/403 came from the backend at all. The backend's correlation
    /// middleware echoes the request's <c>X-Correlation-ID</c> on every response it writes (the
    /// agent sends a fresh id per request); the Azure platform's own pages and a proxy's pages never
    /// carry it. The echo is therefore the only proof of origin — body shape, content type and
    /// title are evidence, not proof. Shared by every agent call site that can receive a 401/403.
    /// </summary>
    public static class ResponseOriginClassifier
    {
        /// <summary>Request header the backend echoes on every response it writes.</summary>
        public const string CorrelationIdHeader = "X-Correlation-ID";

        /// <summary>Only the head of a body is inspected; platform and proxy pages are a few KB.</summary>
        public const int MaxBodyChars = 16 * 1024;

        private const int MaxTitleLength = 80;

        // Text of the platform page of a stopped web app ("Error 403 - This web app is stopped.").
        private const string StoppedAppMarker = "This web app is stopped";

        // Text of the platform page served when no client certificate arrived
        // ("Error 403 - Forbidden: Client Certificate Required.").
        private const string ClientCertificateRequiredMarker = "Client Certificate Required";

        /// <summary>
        /// Classifies a 401/403. <paramref name="sentCorrelationId"/> is the id this request carried;
        /// <paramref name="bodyPrefix"/> is the head of the body (<see cref="ReadBodyPrefixAsync"/>);
        /// <paramref name="serverCertIssuer"/> is passed through as evidence. An echo always wins:
        /// a backend page is the backend's verdict whatever it looks like.
        /// </summary>
        public static ResponseClassification Classify(
            HttpResponseMessage response,
            string? sentCorrelationId,
            string? bodyPrefix,
            string? serverCertIssuer = null)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));

            var statusCode = (int)response.StatusCode;
            if (EchoesCorrelationId(response, sentCorrelationId))
                return ResponseClassification.ForBackend(statusCode);

            var body = Cap(bodyPrefix);
            var isHtml = LooksLikeHtml(response, body);
            var title = TryExtractHtmlTitle(body);

            if (Contains(body, StoppedAppMarker))
                return ResponseClassification.ForPlatformStopped(statusCode, title, isHtml, serverCertIssuer);

            var reason = Contains(body, ClientCertificateRequiredMarker)
                ? ResponseClassification.ClientCertificateRequired
                : ResponseClassification.ForeignResponse;
            return ResponseClassification.ForForeign(statusCode, reason, title, isHtml, serverCertIssuer);
        }

        /// <summary>
        /// The first <see cref="MaxBodyChars"/> characters of the body, null when there is none or it
        /// cannot be read. Never throws — the body is evidence only.
        /// </summary>
        public static async Task<string?> ReadBodyPrefixAsync(HttpResponseMessage response)
        {
            var content = response?.Content;
            if (content == null) return null;
            try
            {
                return Cap(await content.ReadAsStringAsync().ConfigureAwait(false));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// True when <paramref name="response"/> carries <paramref name="sentCorrelationId"/> in
        /// <see cref="CorrelationIdHeader"/> — the backend wrote it. Also used on 2xx answers, where
        /// a missing echo is only counted (<c>BackendTelemetryUploader</c>), never acted on.
        /// </summary>
        public static bool EchoesCorrelationId(HttpResponseMessage response, string? sentCorrelationId)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            if (string.IsNullOrEmpty(sentCorrelationId)) return false;
            if (!response.Headers.TryGetValues(CorrelationIdHeader, out var values)) return false;

            foreach (var value in values)
            {
                if (string.Equals(value?.Trim(), sentCorrelationId, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// True when the body is an HTML page rather than a JSON/plain-text reply. Checks the declared
        /// Content-Type first, then sniffs the body prefix so a page served with a generic
        /// Content-Type is still recognized.
        /// </summary>
        private static bool LooksLikeHtml(HttpResponseMessage response, string? body)
        {
            var mediaType = response.Content?.Headers?.ContentType?.MediaType;
            if (string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.IsNullOrWhiteSpace(body))
                return false;

            var head = body!.TrimStart();
            return head.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                   head.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Best-effort extraction of the HTML &lt;title&gt; (e.g. "Web App - Unavailable") so logs and
        /// reports name the page without dumping markup. Null when absent, empty or the regex times out.
        /// </summary>
        private static string? TryExtractHtmlTitle(string? body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try
            {
                var match = Regex.Match(
                    body, @"<title[^>]*>\s*(.*?)\s*</title>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline,
                    TimeSpan.FromMilliseconds(250));
                if (!match.Success) return null;

                var title = match.Groups[1].Value;
                return title.Length == 0 ? null : (title.Length > MaxTitleLength ? title.Substring(0, MaxTitleLength) : title);
            }
            catch
            {
                return null;
            }
        }

        private static bool Contains(string? body, string marker) =>
            body != null && body.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;

        private static string? Cap(string? body) =>
            body != null && body.Length > MaxBodyChars ? body.Substring(0, MaxBodyChars) : body;
    }
}

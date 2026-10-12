using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Transport;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Transport
{
    /// <summary>
    /// Thin RPC client for the four backend endpoints that are not part of the telemetry-upload
    /// path: <c>POST /api/agent/register-session</c>, <c>GET /api/agent/config</c>,
    /// <c>POST /api/agent/upload-url</c> (diagnostics SAS), <c>POST /api/agent/error</c>.
    /// <para>
    /// Pipeline-agnostic: the caller supplies a fully configured <see cref="HttpClient"/> via the
    /// constructor (built in <c>BackendClientFactory</c> with optional client cert,
    /// gzip/deflate decompression, <c>NetworkMetricsRecordingHandler</c> and User-Agent already
    /// wired). Disposal of the HttpClient transfers to this client so the existing
    /// <c>TerminationPipeline</c> contract stays intact.
    /// </para>
    /// <para>
    /// Network counters (<see cref="NetworkMetrics"/>) are recorded by the
    /// <see cref="NetworkMetricsRecordingHandler"/> in the HttpClient pipeline — no inline
    /// counting in this class. The handler treats every non-2xx as <c>failed=true</c>, matching
    /// the legacy semantics where <c>EnsureSuccessStatusCode</c> would have triggered the
    /// failed branch.
    /// </para>
    /// </summary>
    public class BackendApiClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly string _manufacturer;
        private readonly string _model;
        private readonly string _serialNumber;
        private readonly bool _useBootstrapTokenAuth;
        private readonly string _bootstrapToken;
        private readonly string _agentVersion;
        private readonly Logging.AgentLogger _logger;
        private readonly ServerCertificateObserver _serverCertificates;

        /// <summary>
        /// Protected ctor for testability — <c>SessionRegistrationHelperTests.FakeApiClient</c>
        /// subclasses this and overrides <see cref="RegisterSessionAsync"/> without spinning up
        /// any HTTP plumbing.
        /// </summary>
        protected BackendApiClient() { }

        /// <param name="serverCertificates">
        /// Observer installed on the <paramref name="httpClient"/>'s handler; its issuer becomes
        /// evidence on a 401/403 that did not come from the backend. Null = no evidence.
        /// </param>
        public BackendApiClient(
            HttpClient httpClient,
            string baseUrl,
            string manufacturer,
            string model,
            string serialNumber,
            bool useBootstrapTokenAuth,
            string bootstrapToken,
            string agentVersion,
            Logging.AgentLogger logger,
            ServerCertificateObserver serverCertificates = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            if (string.IsNullOrEmpty(baseUrl)) throw new ArgumentException("baseUrl is mandatory.", nameof(baseUrl));
            _baseUrl = baseUrl.TrimEnd('/');
            _manufacturer = manufacturer;
            _model = model;
            _serialNumber = serialNumber;
            _useBootstrapTokenAuth = useBootstrapTokenAuth;
            _bootstrapToken = bootstrapToken;
            _agentVersion = agentVersion;
            _logger = logger;
            _serverCertificates = serverCertificates;
        }

        /// <summary>
        /// Registers a new enrollment session.
        /// <para>
        /// <c>virtual</c> so test doubles (e.g. <c>SessionRegistrationHelperTests.FakeApiClient</c>)
        /// can intercept the HTTP call without spinning up a real mTLS stack. Production path is
        /// unchanged — the virtual dispatch cost is negligible vs. the network round-trip.
        /// </para>
        /// </summary>
        public virtual async Task<RegisterSessionResponse> RegisterSessionAsync(SessionRegistration registration)
        {
            var request = new RegisterSessionRequest { Registration = registration };
            var endpoint = _useBootstrapTokenAuth
                ? Constants.ApiEndpoints.BootstrapRegisterSession
                : Constants.ApiEndpoints.RegisterSession;
            var url = $"{_baseUrl}{endpoint}";

            return await PostAsync<RegisterSessionRequest, RegisterSessionResponse>(url, request).ConfigureAwait(false);
        }

        /// <summary>
        /// Fetches the agent configuration (collector toggles + gather rules) from the backend.
        /// Retries up to 3 times on 503 (with Retry-After honored, capped at 120s).
        /// </summary>
        public async Task<AgentConfigResponse> GetAgentConfigAsync(string tenantId)
        {
            var configEndpoint = _useBootstrapTokenAuth
                ? Constants.ApiEndpoints.BootstrapGetAgentConfig
                : Constants.ApiEndpoints.GetAgentConfig;
            var url = $"{_baseUrl}{configEndpoint}?tenantId={Uri.EscapeDataString(tenantId)}";

            const int maxAttempts = 3;
            const int maxRetryAfterSeconds = 120;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                using (var httpRequest = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    var correlationId = AddSecurityHeaders(httpRequest);
                    _logger?.Debug($"GetAgentConfigAsync: GET {url} (attempt {attempt}/{maxAttempts})");

                    HttpResponseMessage response;
                    try
                    {
                        response = await _httpClient.SendAsync(httpRequest).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogTransportFailure($"GetAgentConfigAsync GET {url}", ex);
                        throw;
                    }

                    using (response)
                    {
                        if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable && attempt < maxAttempts)
                        {
                            var retryAfterSeconds = 30;
                            if (response.Headers.TryGetValues("Retry-After", out var retryValues) &&
                                int.TryParse(retryValues.FirstOrDefault(), out var parsedRetry))
                            {
                                retryAfterSeconds = Math.Min(parsedRetry, maxRetryAfterSeconds);
                            }

                            _logger?.Warning($"GetAgentConfigAsync: 503 Service Unavailable (attempt {attempt}/{maxAttempts}). Retrying in {retryAfterSeconds}s...");
                            await Task.Delay(retryAfterSeconds * 1000).ConfigureAwait(false);
                            continue;
                        }

                        await ThrowOnAuthFailureAsync(response, correlationId).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();

                        var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return JsonConvert.DeserializeObject<AgentConfigResponse>(responseJson);
                    }
                }
            }

            throw new Exception("GetAgentConfigAsync: backend returned 503 after all retry attempts");
        }

        /// <summary>
        /// Requests a short-lived SAS URL for diagnostics package upload. Called just before
        /// upload so the URL is never stored in config or on disk.
        /// </summary>
        public async Task<GetDiagnosticsUploadUrlResponse> GetDiagnosticsUploadUrlAsync(
            string tenantId, string sessionId, string fileName)
        {
            var url = $"{_baseUrl}{Constants.ApiEndpoints.GetDiagnosticsUploadUrl}";
            var request = new GetDiagnosticsUploadUrlRequest
            {
                TenantId = tenantId,
                SessionId = sessionId,
                FileName = fileName
            };
            return await PostAsync<GetDiagnosticsUploadUrlRequest, GetDiagnosticsUploadUrlResponse>(url, request).ConfigureAwait(false);
        }

        /// <summary>
        /// Sends a critical error report to the emergency channel endpoint.
        /// Fire-and-forget: swallows all exceptions so a failure here never cascades.
        /// Default 5-second per-request timeout so it cannot block the upload loop; the
        /// emergency-break path passes a longer <paramref name="timeout"/> because its first
        /// request of a freshly booted process pays cold TLS + DNS + a possible backend
        /// cold start (observed 503s take well over 5 s).
        /// Returns <c>true</c> when the HTTP round-trip completed (the endpoint answers 200 for
        /// every report it receives), <c>false</c> on timeout/transport failure or when a
        /// 401/403 came from something other than the backend (the report never arrived) — the
        /// caller-side anti-flood layer uses this to decide whether the report counts as delivered.
        /// Virtual for tests.
        /// </summary>
        public virtual async Task<bool> ReportAgentErrorAsync(AgentErrorReport report, TimeSpan? timeout = null)
        {
            try
            {
                var errorEndpoint = _useBootstrapTokenAuth
                    ? Constants.ApiEndpoints.BootstrapReportError
                    : Constants.ApiEndpoints.ReportAgentError;
                var url = $"{_baseUrl}{errorEndpoint}";
                var json = JsonConvert.SerializeObject(report);

                using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                })
                {
                    // Backend security validation requires tenant id before parsing the body.
                    httpRequest.Headers.Add("X-Tenant-Id", report.TenantId);
                    var correlationId = AddSecurityHeaders(httpRequest);

                    using (var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5)))
                    using (var response = await _httpClient.SendAsync(httpRequest, cts.Token).ConfigureAwait(false))
                    {
                        // Every status counts as delivered (unchanged) — except a 401/403 without the
                        // backend's correlation echo: then the report never reached the backend.
                        if (!IsAuthFailureStatus(response.StatusCode))
                            return true;

                        var body = await ResponseOriginClassifier.ReadBodyPrefixAsync(response).ConfigureAwait(false);
                        var classification = ResponseOriginClassifier.Classify(response, correlationId, body);
                        if (!classification.IsForeign)
                            return true;

                        _logger?.Debug(
                            $"ReportAgentErrorAsync: http {(int)response.StatusCode} did not come from the backend " +
                            $"({classification.ForeignReason}) — report not delivered.");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug($"ReportAgentErrorAsync: emergency channel failed: {ex.Message}");
                return false;
            }
        }

        private async Task<TResponse> PostAsync<TRequest, TResponse>(string url, TRequest data)
        {
            var json = JsonConvert.SerializeObject(data);

            using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            })
            {
                var correlationId = AddSecurityHeaders(httpRequest);

                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.SendAsync(httpRequest).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogTransportFailure($"PostAsync POST {url}", ex);
                    throw;
                }

                using (response)
                {
                    await ThrowOnAuthFailureAsync(response, correlationId).ConfigureAwait(false);
                    await ThrowOnCodedConflictAsync(response).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return JsonConvert.DeserializeObject<TResponse>(responseJson);
                }
            }
        }

        /// <summary>
        /// Walks the inner-exception chain of a transport failure and logs every link with
        /// type, message, and (for <see cref="System.Net.WebException"/>) the
        /// <see cref="System.Net.WebException.Status"/> code. The status code distinguishes
        /// TLS-layer failures (<c>SecureChannelFailure</c>) from network/timeout/DNS failures
        /// — useful for any future transport-failure investigation, not just the TPM-PSS one
        /// this was originally added for.
        /// </summary>
        private void LogTransportFailure(string operation, Exception ex)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"{operation} — transport failure exception chain:");
                var current = ex;
                int depth = 0;
                while (current != null)
                {
                    sb.AppendLine($"  [{depth}] {current.GetType().FullName}: {current.Message}");
                    if (current is System.Net.WebException we)
                    {
                        sb.AppendLine($"       WebException.Status = {we.Status}");
                    }
                    current = current.InnerException;
                    depth++;
                }
                _logger?.Error(sb.ToString());
            }
            catch
            {
                // Diagnostic logging must never mask the original exception.
            }
        }

        /// <summary>
        /// Throws <see cref="BackendAuthException"/> for 401/403 responses so callers can
        /// distinguish authentication failures from transient server errors.
        /// <para>
        /// Only a response that echoes this request's <c>X-Correlation-ID</c> is the backend's
        /// verdict (<see cref="ResponseOriginClassifier"/>). Anything else never came from the
        /// backend's auth layer: the platform page of a stopped app (field case 2026-08-09: a
        /// pre-cutover build pointed at a retired Function App), or the platform's "Client
        /// Certificate Required" page / a proxy page when TLS inspection on the network path
        /// strips the device certificate. Reporting those as "device is not authorized" sends
        /// troubleshooting down the wrong path, so each origin gets its own message and the
        /// exception carries the classification.
        /// </para>
        /// </summary>
        private async Task ThrowOnAuthFailureAsync(HttpResponseMessage response, string correlationId)
        {
            if (!IsAuthFailureStatus(response.StatusCode))
                return;

            var statusCode = (int)response.StatusCode;
            var statusText = $"{statusCode} {response.StatusCode}";
            var body = await ResponseOriginClassifier.ReadBodyPrefixAsync(response).ConfigureAwait(false);
            var classification = ResponseOriginClassifier.Classify(
                response, correlationId, body, _serverCertificates?.LastIssuer);

            if (classification.Origin != ResponseOrigin.Backend)
            {
                throw new BackendAuthException(
                    DescribeNonBackendAnswer(response, statusText, classification),
                    statusCode,
                    classification: classification);
            }

            // Backend JSON bodies may carry a machine-readable errorCode (Shared
            // Constants.AgentErrorCodes) that tells the caller how to react — e.g.
            // session_owner_mismatch → rotate the session id instead of treating this as a
            // certificate rejection. Absent/unparseable → null, classic auth failure.
            throw new BackendAuthException(
                $"Backend returned {statusText}. " +
                "The device is not authorized. Check client certificate and Autopilot device validation.",
                statusCode,
                errorCode: TryExtractErrorCode(body),
                classification: classification);
        }

        /// <summary>
        /// Message for a 401/403 the backend did not author. The cause leads because
        /// <c>RegistrationFailureSummary.LastError</c> keeps only 256 characters; HTML answers
        /// keep the phrase "HTML platform error page" plus the page title (ANALYZE-NET-001 matches both).
        /// </summary>
        private static string DescribeNonBackendAnswer(HttpResponseMessage response, string statusText, ResponseClassification classification)
        {
            var host = response.RequestMessage?.RequestUri?.Host ?? "(unknown host)";
            var titleText = classification.PageTitle != null ? $" (\"{classification.PageTitle}\")" : string.Empty;
            var htmlPage = $"an HTML platform error page{titleText}";

            if (classification.Origin == ResponseOrigin.PlatformStopped)
            {
                return $"Backend returned {statusText} with {(classification.IsHtml ? htmlPage : "a platform error page")} from '{host}'. " +
                       "This response did not come from the Autopilot Monitor backend — the endpoint appears stopped or retired. " +
                       "This is NOT a device-authorization failure; an outdated agent build pointing at a stale ApiBaseUrl " +
                       "is the most likely cause.";
            }

            var cause = classification.ForeignReason == ResponseClassification.ClientCertificateRequired
                ? "The device certificate did not reach the backend (TLS inspection or a proxy on the network path)"
                : "Something on the network path answered instead of the backend (TLS inspection, a proxy or a security gateway)";
            var answer = classification.IsHtml ? htmlPage : "a response without the backend's correlation echo";
            return $"{cause}: {statusText} from '{host}' was {answer}. This is NOT a device-authorization failure.";
        }

        private static bool IsAuthFailureStatus(HttpStatusCode statusCode) =>
            statusCode == HttpStatusCode.Unauthorized || statusCode == HttpStatusCode.Forbidden;

        /// <summary>
        /// Throws <see cref="BackendRefusalException"/> for a 409 whose JSON body carries a
        /// machine-readable <c>errorCode</c> — register-session answers
        /// <c>session_owner_mismatch</c> when another tenant claimed the session id, which no retry
        /// can clear but a session rotation can. A 409 without a code falls through to
        /// <c>EnsureSuccessStatusCode</c> as before.
        /// </summary>
        private static async Task ThrowOnCodedConflictAsync(HttpResponseMessage response)
        {
            if (response.StatusCode != System.Net.HttpStatusCode.Conflict)
                return;

            string body = null;
            try
            {
                body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch
            {
                // No body, no code — the generic 409 handling applies.
            }

            var errorCode = TryExtractErrorCode(body);
            if (errorCode == null)
                return;

            throw new BackendRefusalException(
                $"Backend returned 409 Conflict with error code {errorCode}.",
                (int)response.StatusCode,
                errorCode);
        }

        /// <summary>
        /// Pulls <c>errorCode</c> out of a backend JSON error body. Tolerant: any parse failure
        /// or a non-string value yields null, never an exception.
        /// </summary>
        internal static string TryExtractErrorCode(string body)
        {
            if (string.IsNullOrWhiteSpace(body) || body.TrimStart().Length == 0 || body.TrimStart()[0] != '{')
                return null;
            try
            {
                var obj = Newtonsoft.Json.Linq.JObject.Parse(body);
                var token = obj["errorCode"] ?? obj["ErrorCode"];
                if (token == null || token.Type != Newtonsoft.Json.Linq.JTokenType.String)
                    return null;
                var value = (string)token;
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Adds device / version / bootstrap-token security headers and returns the request's
        /// correlation id. Client certificate is sent at the TLS layer via the externally-supplied
        /// HttpClient's HttpClientHandler.
        /// </summary>
        private string AddSecurityHeaders(HttpRequestMessage request)
        {
            // Per-request correlation id for end-to-end tracing (mirrors BackendTelemetryUploader).
            // The backend's CorrelationIdMiddleware reads X-Correlation-ID; without it the backend
            // mints a fresh id per inbound request and agent↔backend calls cannot be tied together.
            // Generated per request so each attempt (e.g. config-fetch 503 retries) is independently
            // traceable — and so the echo on a 401/403 proves which request the backend answered.
            var correlationId = Guid.NewGuid().ToString("N");
            request.Headers.Add(ResponseOriginClassifier.CorrelationIdHeader, correlationId);
            _logger?.Debug($"BackendApiClient: {request.Method} {request.RequestUri?.AbsolutePath} (corr={correlationId})");

            if (_useBootstrapTokenAuth && !string.IsNullOrEmpty(_bootstrapToken))
                request.Headers.Add("X-Bootstrap-Token", _bootstrapToken);

            if (!string.IsNullOrEmpty(_manufacturer))
                request.Headers.Add("X-Device-Manufacturer", _manufacturer);
            if (!string.IsNullOrEmpty(_model))
                request.Headers.Add("X-Device-Model", _model);
            if (!string.IsNullOrEmpty(_serialNumber))
                request.Headers.Add("X-Device-SerialNumber", _serialNumber);
            if (!string.IsNullOrEmpty(_agentVersion))
                request.Headers.Add("X-Agent-Version", _agentVersion);

            return correlationId;
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    /// <summary>
    /// Thrown for a 401 or 403. <see cref="Origin"/> says who answered: the backend (the device is
    /// not authorized), the platform page of a stopped app (<see cref="EndpointUnavailable"/>), or
    /// something on the network path (<see cref="IsForeignResponse"/>) — the last two say nothing
    /// about this device's authorization.
    /// </summary>
    public class BackendAuthException : Exception
    {
        /// <summary>
        /// HTTP status code of the response (401 or 403).
        /// Used by the DistressReporter to classify the failure type.
        /// </summary>
        public int StatusCode { get; }

        /// <summary>Classification and evidence of the response; <see cref="ResponseOrigin.Backend"/> when constructed without one.</summary>
        public ResponseClassification Classification { get; }

        public ResponseOrigin Origin => Classification.Origin;

        /// <summary>
        /// True only for the platform page of a stopped app: the endpoint itself is down. Counts
        /// toward the auth shutdown like a backend rejection, without a distress report.
        /// </summary>
        public bool EndpointUnavailable => Origin == ResponseOrigin.PlatformStopped;

        /// <summary>
        /// True when something other than the backend answered (the platform's "Client Certificate
        /// Required" page, a proxy page): the device certificate did not reach the backend. Never
        /// counts as an authentication failure.
        /// </summary>
        public bool IsForeignResponse => Origin == ResponseOrigin.Foreign;

        /// <summary>
        /// Machine-readable code from the backend's JSON error body (see Shared
        /// <c>Constants.AgentErrorCodes</c>), or null when the body carried none.
        /// </summary>
        public string ErrorCode { get; }

        public BackendAuthException(string message, int statusCode = 0, string errorCode = null, ResponseClassification classification = null) : base(message)
        {
            StatusCode = statusCode;
            ErrorCode = errorCode;
            Classification = classification ?? ResponseClassification.ForBackend(statusCode);
        }
    }

    /// <summary>
    /// Thrown when the backend refuses a request on a non-auth status (409) with a machine-readable
    /// code from Shared <c>Constants.AgentErrorCodes</c>. Unlike <see cref="BackendAuthException"/>
    /// it says nothing about the device's authorization and must never feed the auth-failure tracker.
    /// </summary>
    public class BackendRefusalException : Exception
    {
        /// <summary>HTTP status code returned by the backend (409).</summary>
        public int StatusCode { get; }

        /// <summary>Machine-readable code from the backend's JSON body (never null).</summary>
        public string ErrorCode { get; }

        public BackendRefusalException(string message, int statusCode, string errorCode) : base(message)
        {
            StatusCode = statusCode;
            ErrorCode = errorCode;
        }
    }
}

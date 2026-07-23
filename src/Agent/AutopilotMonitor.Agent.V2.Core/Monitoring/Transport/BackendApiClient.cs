using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

        /// <summary>
        /// Protected ctor for testability — <c>SessionRegistrationHelperTests.FakeApiClient</c>
        /// subclasses this and overrides <see cref="RegisterSessionAsync"/> without spinning up
        /// any HTTP plumbing.
        /// </summary>
        protected BackendApiClient() { }

        public BackendApiClient(
            HttpClient httpClient,
            string baseUrl,
            string manufacturer,
            string model,
            string serialNumber,
            bool useBootstrapTokenAuth,
            string bootstrapToken,
            string agentVersion,
            Logging.AgentLogger logger)
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
                    AddSecurityHeaders(httpRequest);
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

                        ThrowOnAuthFailure(response);
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
        /// Returns <c>true</c> when the HTTP round-trip completed (the endpoint always
        /// answers 200), <c>false</c> on timeout/transport failure — the caller-side
        /// anti-flood layer uses this to decide whether the report counts as delivered.
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
                    AddSecurityHeaders(httpRequest);

                    using (var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5)))
                    using (var response = await _httpClient.SendAsync(httpRequest, cts.Token).ConfigureAwait(false))
                    {
                        // Response status is deliberately ignored — endpoint always returns 200.
                        return true;
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
                AddSecurityHeaders(httpRequest);

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
                    ThrowOnAuthFailure(response);
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
        /// </summary>
        private static void ThrowOnAuthFailure(HttpResponseMessage response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new BackendAuthException(
                    $"Backend returned {(int)response.StatusCode} {response.StatusCode}. " +
                    "The device is not authorized. Check client certificate and Autopilot device validation.",
                    (int)response.StatusCode);
            }
        }

        /// <summary>
        /// Adds device / version / bootstrap-token security headers. Client certificate is sent
        /// at the TLS layer via the externally-supplied HttpClient's HttpClientHandler.
        /// </summary>
        private void AddSecurityHeaders(HttpRequestMessage request)
        {
            // Per-request correlation id for end-to-end tracing (mirrors BackendTelemetryUploader).
            // The backend's CorrelationIdMiddleware reads X-Correlation-ID; without it the backend
            // mints a fresh id per inbound request and agent↔backend calls cannot be tied together.
            // Generated per request so each attempt (e.g. config-fetch 503 retries) is independently
            // traceable.
            var correlationId = Guid.NewGuid().ToString("N");
            request.Headers.Add("X-Correlation-ID", correlationId);
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
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    /// <summary>
    /// Thrown when the backend returns 401 or 403, indicating the device is not authorized.
    /// </summary>
    public class BackendAuthException : Exception
    {
        /// <summary>
        /// HTTP status code returned by the backend (401 or 403).
        /// Used by the DistressReporter to classify the failure type.
        /// </summary>
        public int StatusCode { get; }

        public BackendAuthException(string message, int statusCode = 0) : base(message)
        {
            StatusCode = statusCode;
        }
    }
}

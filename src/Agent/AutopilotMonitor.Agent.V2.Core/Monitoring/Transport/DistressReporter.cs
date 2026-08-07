using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Transport
{
    /// <summary>
    /// Sends pre-auth distress signals to the backend when the authenticated error channel
    /// is unreachable (cert missing, hardware blocked, device not registered, etc.).
    ///
    /// Uses its own plain HttpClient (no client certificate, no BackendApiClient dependency)
    /// so it works even when BackendApiClient cannot be constructed.
    ///
    /// Anti-flood guarantees (per session):
    ///   - Same error key (ErrorType + HTTP status code) is only ever sent once per session
    ///   - At most <see cref="MaxReportsPerSession"/> distress reports total per session
    ///   - At most one report every <see cref="MinIntervalMinutes"/> minutes
    ///
    /// A failure in this reporter is silently swallowed — it must never cascade into the main loop.
    /// </summary>
    public class DistressReporter : IDisposable
    {
        private const int MaxReportsPerSession = 3;
        private const int MinIntervalMinutes = 30;

        private readonly HttpClient _httpClient;
        private readonly string _distressUrl;
        private readonly string _tenantId;
        private readonly string _manufacturer;
        private readonly string _model;
        private readonly string _serialNumber;
        private readonly string _agentVersion;
        private readonly AgentLogger _logger;

        // Cert context captured once at construction. Used to enrich every outgoing distress
        // report with the agent's view of the client cert (UNVERIFIED on the backend; corroborates
        // / contradicts the server-side cert chain it actually saw). All-null when the agent
        // started without a client cert — that itself is the diagnostic signal.
        private readonly DistressCertSourceState _capturedCertState;
        private readonly string _capturedCertThumbprint;
        private readonly string _capturedCertSubject;
        private readonly string _capturedCertIssuer;
        private readonly DateTime? _capturedCertNotBefore;
        private readonly DateTime? _capturedCertNotAfter;

        private readonly HashSet<string> _reportedKeys = new HashSet<string>();
        private readonly object _antiFloodLock = new object();
        private DateTime? _lastReportTime;
        private int _reportCount;

        // Local W365 marker verdict, captured once at construction (same UNVERIFIED contract
        // as the hardware fields). Optional ctor arg so test fakes stay source-compatible.
        private readonly bool _isCloudPc;

        public DistressReporter(
            string baseUrl,
            string tenantId,
            string manufacturer,
            string model,
            string serialNumber,
            string agentVersion,
            AgentLogger logger,
            X509Certificate2 clientCertificate = null,
            bool isCloudPc = false)
        {
            _tenantId = tenantId;
            _manufacturer = manufacturer;
            _model = model;
            _serialNumber = serialNumber;
            _agentVersion = agentVersion;
            _logger = logger;
            _isCloudPc = isCloudPc;

            // Capture cert context. Each field is independently nullable so the backend can tell
            // "agent had no cert at all" (NoCertInStore + all-null fields) from "agent had a cert
            // but the backend rejected it" (Found + populated fields).
            if (clientCertificate != null)
            {
                _capturedCertThumbprint = clientCertificate.Thumbprint;
                _capturedCertSubject = clientCertificate.Subject;
                _capturedCertIssuer = clientCertificate.Issuer;
                _capturedCertNotBefore = clientCertificate.NotBefore.ToUniversalTime();
                _capturedCertNotAfter = clientCertificate.NotAfter.ToUniversalTime();

                var now = DateTime.UtcNow;
                if (now > _capturedCertNotAfter || now < _capturedCertNotBefore)
                    _capturedCertState = DistressCertSourceState.FoundExpired;
                else if (!clientCertificate.HasPrivateKey)
                    _capturedCertState = DistressCertSourceState.FoundMissingPrivateKey;
                else
                    _capturedCertState = DistressCertSourceState.Found;
            }
            else
            {
                _capturedCertState = DistressCertSourceState.NoCertInStore;
            }

            var cleanBaseUrl = baseUrl?.TrimEnd('/') ?? Constants.ApiBaseUrl;
            _distressUrl = $"{cleanBaseUrl}{Constants.ApiEndpoints.ReportDistress}";

            // Plain HttpClient — no client cert, no mTLS.
            // Deliberately isolated from BackendApiClient.
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5)
            };

            var ua = string.IsNullOrEmpty(agentVersion)
                ? "AutopilotMonitor.Agent"
                : $"AutopilotMonitor.Agent/{agentVersion}";
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
        }

        /// <summary>
        /// Attempts to send a distress report. Returns immediately without awaiting
        /// (intended to be called as fire-and-forget via <c>_ = reporter.TrySendAsync(...)</c>).
        ///
        /// All anti-flood checks are applied synchronously before the async HTTP call,
        /// so repeated calls are cheap when suppressed.
        /// </summary>
        public virtual async Task TrySendAsync(
            DistressErrorType errorType,
            string message,
            int? httpStatusCode = null)
        {
            var key = $"{errorType}:{httpStatusCode}";
            int currentReportCount;

            lock (_antiFloodLock)
            {
                if (_reportCount >= MaxReportsPerSession)
                {
                    _logger?.Debug($"[DistressChannel] Suppressed ({_reportCount}/{MaxReportsPerSession} reports used): {key}");
                    return;
                }

                if (_reportedKeys.Contains(key))
                {
                    _logger?.Debug($"[DistressChannel] Suppressed (already reported): {key}");
                    return;
                }

                if (_lastReportTime != null &&
                    (DateTime.UtcNow - _lastReportTime.Value).TotalMinutes < MinIntervalMinutes)
                {
                    _logger?.Debug($"[DistressChannel] Suppressed (cooldown active, last report at {_lastReportTime.Value:HH:mm:ss}): {key}");
                    return;
                }

                _reportedKeys.Add(key);
                _reportCount++;
                _lastReportTime = DateTime.UtcNow;
                currentReportCount = _reportCount;
            }

            var statusText = httpStatusCode.HasValue ? $" HTTP {httpStatusCode}" : string.Empty;
            _logger?.Warning($"[DistressChannel] Sending report {currentReportCount}/{MaxReportsPerSession}: {errorType}{statusText}");

            var report = new DistressReport
            {
                TenantId = _tenantId,
                ErrorType = errorType,
                Manufacturer = _manufacturer,
                Model = _model,
                SerialNumber = _serialNumber,
                AgentVersion = _agentVersion,
                HttpStatusCode = httpStatusCode,
                Message = Truncate(message, 256),
                Timestamp = DateTime.UtcNow,
                IsCloudPc = _isCloudPc,
                CertSourceState = _capturedCertState,
                CertThumbprint = _capturedCertThumbprint,
                CertSubject = Truncate(_capturedCertSubject, 96),
                CertIssuer = Truncate(_capturedCertIssuer, 96),
                CertNotBefore = _capturedCertNotBefore,
                CertNotAfter = _capturedCertNotAfter,
            };

            try
            {
                var json = JsonConvert.SerializeObject(report);

                using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, _distressUrl)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                })
                {
                    // Only header needed: TenantId for the backend's first gate
                    httpRequest.Headers.Add("X-Tenant-Id", _tenantId);

                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    using (var response = await _httpClient.SendAsync(httpRequest, cts.Token))
                    {
                        // Response status is deliberately ignored — endpoint always returns 200
                    }
                }
            }
            catch (Exception ex)
            {
                // Swallow — distress channel must never cascade failures into the caller
                _logger?.Debug($"[DistressChannel] Failed to send report: {ex.Message}");
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}

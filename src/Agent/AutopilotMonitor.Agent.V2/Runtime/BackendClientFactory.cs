using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Runtime
{
    /// <summary>
    /// Phase 3 + Phase 5 of <see cref="Program"/>'s <c>RunAgent</c>: backend HTTP-clients
    /// (BackendApiClient + reporters + auth-failure tracker) and the mTLS-backed telemetry
    /// uploader. Phase 4 (RemoteConfig fetch + merge + binary-integrity check) lives in
    /// <c>RunAgent</c> between the two factory calls and consumes Phase 3's clients to
    /// produce the merged <see cref="AgentConfigResponse"/>.
    /// <para>
    /// <b>NetworkMetrics ownership</b>: the bundle owns one <see cref="NetworkMetrics"/>
    /// instance which is shared by both HttpClients (BackendApiClient + telemetry uploader)
    /// via <see cref="NetworkMetricsRecordingHandler"/> in each pipeline. Without this
    /// share <c>agent_metrics_snapshot.net_total_requests</c> would only reflect the few
    /// BackendApiClient calls and undercount by ~25-30x.
    /// </para>
    /// </summary>
    internal static class BackendClientFactory
    {
        /// <summary>
        /// Phase 3 — construct the backend HTTP clients, the distress / emergency reporters
        /// and the auth-failure tracker. Resolves the MDM client certificate when cert-mode
        /// is enabled; on cert-missing the agent still starts (V1 parity — config-fetch will
        /// 401, AuthCertificateMissing distress fires, Phase 5's mTLS pipeline then refuses
        /// to start with Exit 4). Tracker ceilings reflect the CLI/bootstrap defaults;
        /// tenant-policy overrides are applied later via
        /// <see cref="AuthFailureTracker.UpdateLimits"/> once <c>RemoteConfigMerger</c> has run.
        /// </summary>
        public static BackendAuthBundle BuildAuthClients(
            AgentConfiguration agentConfig,
            string agentVersion,
            AgentLogger logger)
        {
            if (agentConfig == null) throw new ArgumentNullException(nameof(agentConfig));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            // Single source of truth for HTTP request/byte counters. Shared with the mTLS
            // telemetry pipeline in BuildTelemetryClients so net_total_requests reflects every
            // outbound call (legacy BackendApiClient calls + dominant /api/agent/telemetry POSTs).
            var networkMetrics = new NetworkMetrics();

            var hardware = HardwareInfo.GetHardwareInfo(logger);

            // Resolve the MDM client cert when cert-mode is on. Cert-missing is non-fatal here:
            // BackendApiClient still gets a plain (no-cert) HttpClient and the agent continues so
            // (a) Phase 4 config-fetch can fall back to cached config on 401 and (b) the
            // AuthCertificateMissing distress reaches the backend before Phase 5's mTLS gate.
            X509Certificate2 clientCertificate = null;
            if (agentConfig.UseClientCertAuth)
            {
                logger.Debug("Client certificate authentication enabled — searching for certificate...");
                clientCertificate = new DefaultCertificateResolver().FindClientCertificate(logger);
                if (clientCertificate != null)
                {
                    logger.Info($"Client certificate loaded successfully (Thumbprint={clientCertificate.Thumbprint}).");
                }
                else
                {
                    logger.Warning("Client certificate authentication enabled but no certificate found — requests will be sent WITHOUT certificate (will likely fail security validation).");
                }
            }
            else
            {
                logger.Debug("Client certificate authentication disabled.");
            }

            var apiHttpClient = BuildBackendApiHttpClient(networkMetrics, clientCertificate, agentVersion);

            var backendApiClient = new BackendApiClient(
                httpClient: apiHttpClient,
                baseUrl: agentConfig.ApiBaseUrl,
                manufacturer: hardware.Manufacturer,
                model: hardware.Model,
                serialNumber: hardware.SerialNumber,
                useBootstrapTokenAuth: agentConfig.UseBootstrapTokenAuth,
                bootstrapToken: agentConfig.BootstrapToken,
                agentVersion: agentVersion,
                logger: logger);

            // M4.6.γ — Emergency + Distress reporters. Plumbed into RemoteConfigService so
            // Config-fetch failures (auth vs network) flow to the correct channel. Also fires
            // an initial AuthCertificateMissing distress when the MDM cert was expected but
            // not found (Legacy parity — surfaces pre-MDM-enrollment dead-ends to the backend
            // via the cert-less distress channel).
            var distressReporter = new DistressReporter(
                baseUrl: agentConfig.ApiBaseUrl,
                tenantId: agentConfig.TenantId,
                manufacturer: hardware.Manufacturer,
                model: hardware.Model,
                serialNumber: hardware.SerialNumber,
                agentVersion: agentVersion,
                logger: logger,
                clientCertificate: clientCertificate,
                isCloudPc: CloudPcDetector.DetectIsCloudPc());

            var emergencyReporter = new EmergencyReporter(
                apiClient: backendApiClient,
                sessionId: agentConfig.SessionId,
                tenantId: agentConfig.TenantId,
                agentVersion: agentVersion,
                logger: logger);

            if (agentConfig.UseClientCertAuth && clientCertificate == null)
            {
                _ = distressReporter.TrySendAsync(
                    DistressErrorType.AuthCertificateMissing,
                    "MDM certificate not found in LocalMachine or CurrentUser store");
            }

            // Central observer for consecutive 401/403 responses. Initialised with the CLI/bootstrap
            // defaults on AgentConfiguration; UpdateLimits is called after RemoteConfigMerger.Merge
            // so tenant-policy overrides take effect. ThresholdExceeded is wired in RunAgent, once
            // the shutdown signal is available, to trigger a clean agent exit when the limit is hit.
            // V1 parity — the distress reporter is plumbed in at construction so the tracker is
            // the single dispatch point for auth-failure distress (first failure only).
            var authFailureTracker = new AuthFailureTracker(
                maxFailures: agentConfig.MaxAuthFailures,
                timeoutMinutes: agentConfig.AuthFailureTimeoutMinutes,
                clock: SystemClock.Instance,
                logger: logger,
                distressReporter: distressReporter);

            return new BackendAuthBundle(
                backendApiClient: backendApiClient,
                networkMetrics: networkMetrics,
                hasClientCertificate: clientCertificate != null,
                manufacturer: hardware.Manufacturer,
                model: hardware.Model,
                serialNumber: hardware.SerialNumber,
                distressReporter: distressReporter,
                emergencyReporter: emergencyReporter,
                authFailureTracker: authFailureTracker);
        }

        /// <summary>
        /// Phase 5 — construct the mTLS-backed <see cref="HttpClient"/> and the
        /// <see cref="BackendTelemetryUploader"/>. Reads the bundle's shared
        /// <see cref="NetworkMetrics"/> so the agent_metrics_snapshot
        /// net_total_requests reflects the dominant <c>/api/agent/telemetry</c> POST stream.
        /// </summary>
        public static TelemetryClientResult BuildTelemetryClients(
            AgentConfiguration agentConfig,
            BackendAuthBundle auth,
            string agentVersion,
            AgentLogger logger)
        {
            if (agentConfig == null) throw new ArgumentNullException(nameof(agentConfig));
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            HttpClient mtlsHttpClient;
            try
            {
                // Telemetry path is cert-mandatory: MtlsHttpClientFactory.Create throws on
                // missing cert which surfaces here as Exit 4 (V1 parity).
                mtlsHttpClient = MtlsHttpClientFactory.Create(
                    resolver: new DefaultCertificateResolver(),
                    logger: logger,
                    metrics: auth.NetworkMetrics);
            }
            catch (InvalidOperationException ex)
            {
                logger.Error("mTLS HttpClient creation failed — cannot upload telemetry.", ex);
                return TelemetryClientResult.Exit(4);
            }

            BackendTelemetryUploader uploader;
            try
            {
                uploader = new BackendTelemetryUploader(
                    httpClient: mtlsHttpClient,
                    baseUrl: agentConfig.ApiBaseUrl,
                    tenantId: agentConfig.TenantId,
                    manufacturer: auth.Manufacturer,
                    model: auth.Model,
                    serialNumber: auth.SerialNumber,
                    bootstrapToken: agentConfig.UseBootstrapTokenAuth ? agentConfig.BootstrapToken : null,
                    agentVersion: agentVersion,
                    authFailureTracker: auth.AuthFailureTracker,
                    logger: logger);     // Plan §5 Fix 5 — upload-cadence logging
            }
            catch (Exception ex)
            {
                logger.Error("BackendTelemetryUploader construction failed.", ex);
                return TelemetryClientResult.Exit(5);
            }

            return TelemetryClientResult.Continue(mtlsHttpClient, uploader);
        }

        /// <summary>
        /// Builds the cert-optional HttpClient used by <see cref="BackendApiClient"/>.
        /// Cert is attached at the TLS layer when present; missing cert degrades to a plain
        /// (no-cert) handler so the agent can continue starting up — the eventual 401 from
        /// the backend then drives the AuthFailureTracker / shutdown gate.
        /// <para>
        /// Pipeline composition (top → bottom):
        /// <list type="number">
        ///   <item><see cref="NetworkMetricsRecordingHandler"/> — records every SendAsync into
        ///     the shared <see cref="NetworkMetrics"/> counter.</item>
        ///   <item><see cref="HttpClientHandler"/> — TLS, optional client cert,
        ///     gzip/deflate response decompression.</item>
        /// </list>
        /// </para>
        /// </summary>
        internal static HttpClient BuildBackendApiHttpClient(
            NetworkMetrics networkMetrics,
            X509Certificate2 clientCertificate,
            string agentVersion)
        {
            var inner = new HttpClientHandler();
            if (inner.SupportsAutomaticDecompression)
            {
                inner.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            }
            if (clientCertificate != null)
            {
                inner.ClientCertificates.Add(clientCertificate);
            }

            var pipeline = new NetworkMetricsRecordingHandler(networkMetrics, inner);
            var client = new HttpClient(pipeline)
            {
                Timeout = TimeSpan.FromSeconds(30),
            };

            var ua = string.IsNullOrEmpty(agentVersion)
                ? "AutopilotMonitor.Agent"
                : $"AutopilotMonitor.Agent/{agentVersion}";
            client.DefaultRequestHeaders.UserAgent.ParseAdd(ua);

            return client;
        }
    }

    /// <summary>
    /// Phase 3 outcome — the backend-facing clients, the cached hardware identity they were
    /// built with, and the shared <see cref="NetworkMetrics"/> counter (also injected into
    /// the mTLS pipeline in Phase 5). RunAgent retains this bundle through the rest of
    /// startup so the lifecycle / termination wiring can read each client without
    /// re-construction.
    /// </summary>
    internal sealed class BackendAuthBundle
    {
        public BackendApiClient BackendApiClient { get; }
        public NetworkMetrics NetworkMetrics { get; }
        /// <summary>
        /// True iff cert-mode was enabled and a client certificate was successfully resolved
        /// from the Windows cert store. Used by tests as the observable proof that the
        /// cert-lookup path was (or wasn't) exercised. Production code uses the bool to
        /// decide whether <c>AuthCertificateMissing</c> distress should fire.
        /// </summary>
        public bool HasClientCertificate { get; }
        public string Manufacturer { get; }
        public string Model { get; }
        public string SerialNumber { get; }
        public DistressReporter DistressReporter { get; }
        public EmergencyReporter EmergencyReporter { get; }
        public AuthFailureTracker AuthFailureTracker { get; }

        public BackendAuthBundle(
            BackendApiClient backendApiClient,
            NetworkMetrics networkMetrics,
            bool hasClientCertificate,
            string manufacturer,
            string model,
            string serialNumber,
            DistressReporter distressReporter,
            EmergencyReporter emergencyReporter,
            AuthFailureTracker authFailureTracker)
        {
            BackendApiClient = backendApiClient;
            NetworkMetrics = networkMetrics;
            HasClientCertificate = hasClientCertificate;
            Manufacturer = manufacturer;
            Model = model;
            SerialNumber = serialNumber;
            DistressReporter = distressReporter;
            EmergencyReporter = emergencyReporter;
            AuthFailureTracker = authFailureTracker;
        }
    }

    /// <summary>
    /// Phase 5 outcome — either an early exit (V1 parity: 4 = mTLS construction failure,
    /// 5 = uploader construction failure) or a Continue payload carrying the live mTLS
    /// <see cref="HttpClient"/> and <see cref="BackendTelemetryUploader"/>.
    /// </summary>
    internal sealed class TelemetryClientResult
    {
        public bool ShouldExit { get; }
        public int ExitCode { get; }
        public HttpClient MtlsHttpClient { get; }
        public BackendTelemetryUploader Uploader { get; }

        private TelemetryClientResult(bool shouldExit, int exitCode, HttpClient mtls, BackendTelemetryUploader uploader)
        {
            ShouldExit = shouldExit;
            ExitCode = exitCode;
            MtlsHttpClient = mtls;
            Uploader = uploader;
        }

        public static TelemetryClientResult Exit(int code)
            => new TelemetryClientResult(true, code, null, null);

        public static TelemetryClientResult Continue(HttpClient mtls, BackendTelemetryUploader uploader)
            => new TelemetryClientResult(false, 0, mtls, uploader);
    }
}

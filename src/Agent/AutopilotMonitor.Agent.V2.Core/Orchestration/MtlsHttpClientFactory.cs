#nullable enable
using System;
using System.Net;
using System.Net.Http;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Transport;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Baut einen <see cref="HttpClient"/> mit mTLS-Client-Cert für den
    /// <see cref="Transport.Telemetry.BackendTelemetryUploader"/>. Plan §4.x M4.4.5.d.
    /// <para>
    /// <b>Design</b>: Static Factory + <see cref="ICertificateResolver"/>-Seam statt direkte
    /// Abhängigkeit an <see cref="CertificateHelper"/>. Tests injizieren einen
    /// Fake-Resolver mit einem self-signed Cert; Prod nutzt
    /// <see cref="DefaultCertificateResolver"/>, der den Windows-Cert-Store durchsucht.
    /// </para>
    /// <para>
    /// <b>Konfiguration</b> (analog Legacy <c>BackendApiClient</c>):
    /// <list type="bullet">
    ///   <item><see cref="DecompressionMethods.GZip"/> + <see cref="DecompressionMethods.Deflate"/></item>
    ///   <item>Default-Timeout 30s (override via Parameter)</item>
    ///   <item>Keine <c>CookieContainer</c>, keine Default-Credentials</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class MtlsHttpClientFactory
    {
        /// <summary>Plan §4.x M4.4.5.d — Default-Timeout (analog Legacy).</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Baut einen <see cref="HttpClientHandler"/> mit attached Client-Cert. Wirft
        /// <see cref="InvalidOperationException"/> wenn <paramref name="resolver"/> kein
        /// Zertifikat liefert — der Agent darf ohne Cert nicht starten (mTLS ist Pflicht).
        /// <para>
        /// Separat exponiert, damit Tests direkt auf dem Handler asserten können
        /// (<see cref="HttpClient"/>'s <c>_handler</c>-Feld ist internal und in net48
        /// reflection-unzuverlässig).
        /// </para>
        /// <para>
        /// <paramref name="serverCertificates"/>, when supplied, observes the server certificate of
        /// every new connection without changing the validation verdict.
        /// </para>
        /// </summary>
        public static HttpClientHandler CreateHandler(
            ICertificateResolver resolver,
            AgentLogger logger,
            ServerCertificateObserver? serverCertificates = null)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            var cert = resolver.FindClientCertificate(logger);
            if (cert == null)
            {
                throw new InvalidOperationException(
                    "MtlsHttpClientFactory: no MDM client certificate found. " +
                    "Agent cannot start without a client cert (mTLS is mandatory). " +
                    "Check cert enrollment status in HKLM\\SOFTWARE\\Microsoft\\Enrollments.");
            }

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
            handler.ClientCertificates.Add(cert);
            serverCertificates?.Attach(handler);

            logger.Info(
                $"MtlsHttpClientFactory: handler ready (cert thumbprint={cert.Thumbprint}).");

            return handler;
        }

        /// <summary>
        /// Baut einen fertig konfigurierten <see cref="HttpClient"/> — Handler via
        /// <see cref="CreateHandler"/>, Timeout via Parameter (default <see cref="DefaultTimeout"/>).
        /// <para>
        /// When <paramref name="metrics"/> is supplied, every outbound request through the returned
        /// client is recorded on that shared <see cref="NetworkMetrics"/> instance via a
        /// <see cref="NetworkMetricsRecordingHandler"/> in the pipeline. This is how the
        /// V2 telemetry-upload path (<c>POST /api/agent/telemetry</c>) gets counted in
        /// <c>net_total_requests</c> — without it the metric only reflects the legacy
        /// <c>BackendApiClient</c> calls and undercounts by ~25-30x.
        /// </para>
        /// </summary>
        public static HttpClient Create(
            ICertificateResolver resolver,
            AgentLogger logger,
            TimeSpan? timeout = null,
            NetworkMetrics? metrics = null,
            ServerCertificateObserver? serverCertificates = null)
        {
            var innerHandler = CreateHandler(resolver, logger, serverCertificates);
            HttpMessageHandler pipelineHead = innerHandler;
            if (metrics != null)
            {
                pipelineHead = new NetworkMetricsRecordingHandler(metrics, innerHandler);
            }
            return new HttpClient(pipelineHead)
            {
                Timeout = timeout ?? DefaultTimeout,
            };
        }
    }
}

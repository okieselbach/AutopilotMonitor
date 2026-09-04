using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services.Diagnostics;
using AutopilotMonitor.Shared.Diagnostics;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Diagnostics
{
    /// <summary>
    /// Mints a short-lived, self-authenticating download ticket for a single diagnostics blob.
    /// Authorization (<c>MemberRead</c> + cross-tenant scoping) is enforced here by
    /// <see cref="Middleware.PolicyEnforcementMiddleware"/>; the returned ticket then lets a
    /// caller that holds NO JWT in its shell (e.g. an MCP/AI client) download the blob via
    /// <see cref="DiagnosticsTicketDownloadFunction"/> with no auth header.
    /// <para>
    /// The ticket binds the authorized tenantId + blobName into an HMAC signature, so it cannot be
    /// retargeted to another tenant or blob. TTL is <see cref="DiagnosticsDownloadTicket.DefaultTtl"/>
    /// (10 min). The customer's long-lived container SAS is NEVER handed to the client — downloads
    /// always proxy through the backend (byte stream only; no unzip/parse).
    /// </para>
    /// </summary>
    public class DiagnosticsDownloadTicketFunction
    {
        private readonly ILogger<DiagnosticsDownloadTicketFunction> _logger;
        private readonly DiagnosticsBlobStreamer _streamer;

        public DiagnosticsDownloadTicketFunction(
            ILogger<DiagnosticsDownloadTicketFunction> logger,
            DiagnosticsBlobStreamer streamer)
        {
            _logger = logger;
            _streamer = streamer;
        }

        [Function("DiagnosticsDownloadTicket")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "diagnostics/download-ticket")] HttpRequestData req)
        {
            try
            {
                // MemberRead + cross-tenant (?tenantId=) enforced by PolicyEnforcementMiddleware
                // (catalog: TenantScoping.QueryParam). TargetTenantId is the validated tenant.
                var requestCtx = req.GetRequestContext();
                var tenantId = requestCtx.TargetTenantId;

                if (string.IsNullOrEmpty(tenantId))
                {
                    return await req.BadRequestAsync("tenantId query parameter is required.");
                }

                // blobName accepted from body (preferred) or ?blobName= query (fallback).
                string? blobName = null;
                try
                {
                    var body = await System.Text.Json.JsonSerializer.DeserializeAsync<TicketRequest>(
                        req.Body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    blobName = body?.BlobName;
                }
                catch { /* body optional; fall back to query */ }
                if (string.IsNullOrEmpty(blobName))
                    blobName = HttpUtility.ParseQueryString(req.Url.Query)["blobName"];

                if (string.IsNullOrEmpty(blobName))
                {
                    return await req.BadRequestAsync("blobName is required.");
                }

                // Reject malformed / cross-tenant blob names before minting (defence-in-depth on
                // top of the middleware's tenant scoping).
                var (destination, classifyErr) = DiagnosticsDownloadFunction.ClassifyBlobName(blobName, tenantId);
                if (classifyErr != null)
                {
                    _logger.LogWarning(
                        "DiagnosticsDownloadTicket: rejecting blob {Blob} for tenant {TenantId}: {Reason}",
                        blobName, tenantId, classifyErr);
                    return await req.BadRequestAsync("Invalid blob name.");
                }

                var destinationLabel = destination == DiagnosticsDownloadFunction.BlobDestination.Hosted
                    ? "Hosted" : "CustomerSas";

                var issuedAt = DateTimeOffset.UtcNow;
                var ticket = DiagnosticsDownloadTicket.Encode(tenantId, blobName, destinationLabel, issuedAt);
                var expiresAt = issuedAt.Add(DiagnosticsDownloadTicket.DefaultTtl).UtcDateTime;

                // Best-effort size so the client knows what it's about to download.
                long? sizeBytes;
                using (var sizeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    sizeBytes = await _streamer.TryGetSizeAsync(tenantId, blobName, sizeCts.Token);
                }

                _logger.LogInformation(
                    "DiagnosticsDownloadTicket: issued for tenant {TenantId}, blob {Blob} (destination={Destination}), user {User}, expires {ExpiresAt}",
                    tenantId, blobName, destinationLabel, requestCtx.UserPrincipalName, expiresAt.ToString("O"));

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new DiagnosticsDownloadTicketResponse
                {
                    Success = true,
                    Url = $"/api/diagnostics/download?t={Uri.EscapeDataString(ticket)}",
                    ExpiresAt = expiresAt,
                    BlobName = blobName,
                    Destination = destinationLabel,
                    SizeBytes = sizeBytes,
                });
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "DiagnosticsDownloadTicket");
            }
        }

        private sealed class TicketRequest
        {
            public string? BlobName { get; set; }
        }
    }
}

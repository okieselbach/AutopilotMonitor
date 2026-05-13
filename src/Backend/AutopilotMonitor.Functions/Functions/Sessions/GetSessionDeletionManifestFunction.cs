using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    /// <summary>
    /// <c>GET /api/admin/sessions/{sessionId}/deletion-manifest?tenantId=&amp;manifestId=&amp;mode=...</c> —
    /// reads the persisted cascade-delete snapshot + progress for an in-flight / poisoned cascade.
    /// Distinct from <see cref="GetSessionDeletePreviewFunction"/>, which builds a fresh dry-run
    /// manifest from current data: for a running cascade that view is misleading because the live
    /// data is already partially deleted. The Session Cleanup admin page uses THIS endpoint to
    /// show the operator the actual snapshot the worker captured at cascade start.
    /// <para>
    /// GA-only enforcement comes from <c>EndpointAccessPolicyCatalog</c>. Cross-tenant access is
    /// the design intent — the caller passes the row's <c>tenantId</c> from the listing endpoint;
    /// no <c>SessionsIndex</c> fallback because the index row is often the first thing the cascade
    /// deletes, so we cannot rely on it for a Running/Poisoned manifest.
    /// </para>
    /// </summary>
    public class GetSessionDeletionManifestFunction
    {
        private readonly ILogger<GetSessionDeletionManifestFunction> _logger;
        private readonly BlobStorageService _blob;

        public GetSessionDeletionManifestFunction(
            ILogger<GetSessionDeletionManifestFunction> logger,
            BlobStorageService blob)
        {
            _logger = logger;
            _blob = blob;
        }

        [Function("GetSessionDeletionManifest")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "admin/sessions/{sessionId}/deletion-manifest")]
            HttpRequestData req,
            string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return await BadRequest(req, "sessionId is required");
            }

            var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);
            var tenantId = query["tenantId"];
            var manifestId = query["manifestId"];
            var mode = (query["mode"] ?? "summary").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(tenantId) || !Guid.TryParse(tenantId, out _))
            {
                return await BadRequest(req, "Query parameter 'tenantId' must be a GUID.");
            }
            if (string.IsNullOrWhiteSpace(manifestId))
            {
                return await BadRequest(req, "Query parameter 'manifestId' is required.");
            }
            if (mode != "summary" && mode != "full" && mode != "download")
            {
                return await BadRequest(req, "mode must be one of: summary, full, download");
            }

            DeletionManifest manifest;
            string snapshotSha;
            try
            {
                (manifest, snapshotSha) = await _blob.DownloadDeletionManifestWithShaAsync(
                    tenantId!, sessionId, manifestId!, req.FunctionContext.CancellationToken);
            }
            catch (Azure.RequestFailedException rfe) when (rfe.Status == 404)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new
                {
                    success = false,
                    message = $"Manifest blob not found for tenant={tenantId} session={sessionId} manifestId={manifestId}.",
                });
                return notFound;
            }
            catch (InvalidDataException ex)
            {
                _logger.LogError(ex,
                    "GetSessionDeletionManifest: snapshot integrity failure for tenant={TenantId} session={SessionId} manifestId={ManifestId}",
                    tenantId, sessionId, manifestId);
                var corrupt = req.CreateResponse(HttpStatusCode.Conflict);
                await corrupt.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Snapshot SHA-256 mismatch — the manifest blob has been tampered with or the producer crashed.",
                    error = ex.Message,
                });
                return corrupt;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "GetSessionDeletionManifest: unexpected error for tenant={TenantId} session={SessionId} manifestId={ManifestId}",
                    tenantId, sessionId, manifestId);
                var error = req.CreateResponse(HttpStatusCode.InternalServerError);
                await error.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return error;
            }

            // Progress blob — best-effort. A Preparing-phase manifest may exist without a progress
            // blob (the producer hasn't reached the upload step yet); surface as null and let the
            // UI render "no progress yet" instead of failing the whole request.
            DeletionProgress? progress = null;
            try
            {
                var (p, _) = await _blob.DownloadDeletionProgressAsync(
                    tenantId!, sessionId, manifestId!, req.FunctionContext.CancellationToken);
                progress = p;
            }
            catch (Azure.RequestFailedException rfe) when (rfe.Status == 404)
            {
                _logger.LogInformation(
                    "GetSessionDeletionManifest: no progress blob yet (likely Preparing phase) for tenant={TenantId} session={SessionId} manifestId={ManifestId}",
                    tenantId, sessionId, manifestId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "GetSessionDeletionManifest: failed to read progress blob — returning manifest only. tenant={TenantId} session={SessionId} manifestId={ManifestId}",
                    tenantId, sessionId, manifestId);
            }

            return mode switch
            {
                "full"     => await BuildFullResponse(req, manifest, progress, snapshotSha),
                "download" => await BuildDownloadResponse(req, manifest, tenantId!, sessionId, manifestId!),
                _          => await BuildSummaryResponse(req, manifest, progress, snapshotSha),
            };
        }

        private static async Task<HttpResponseData> BadRequest(HttpRequestData req, string message)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { success = false, message });
            return bad;
        }

        private static async Task<HttpResponseData> BuildSummaryResponse(
            HttpRequestData req, DeletionManifest manifest, DeletionProgress? progress, string snapshotSha)
        {
            long totalRowCount = 0;
            var sampleKeys = new Dictionary<string, List<object>>(StringComparer.Ordinal);
            foreach (var step in manifest.Steps)
            {
                totalRowCount += step.RowCount;
                if (step.Rows.Count == 0) continue;
                var key = step.Table ?? step.Step ?? $"order_{step.Order}";
                sampleKeys[key] = step.Rows.Take(5).Select(r => (object)new { pk = r.Pk, rk = r.Rk }).ToList();
            }

            // Serialize once to report bytes — same projection the producer would upload (modulo gzip).
            var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, DeletionManifestJson.SerializerOptions);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                mode = "summary",
                source = "stored",
                manifestId = manifest.ManifestId,
                schemaHash = manifest.SchemaHash,
                snapshotSha256 = snapshotSha,
                estimatedRowCount = totalRowCount,
                estimatedSnapshotBytes = serializedBytes.LongLength,
                preflightCounts = manifest.PreflightCounts,
                sampleKeys,
                progress = progress == null ? null : new
                {
                    progress.SnapshotSha256,
                    completedStepOrders = progress.CompletedSteps,
                    progress.VerificationDone,
                    progress.TombstoneStarted,
                    progress.CompletedAt,
                    aggregateDecrementsApplied = progress.AggregateDecrementsApplied?.Count ?? 0,
                    restoreReIncrementsApplied = progress.RestoreReIncrementsApplied?.Count ?? 0,
                    progress.LastFailureType,
                    progress.LastFailureMessage,
                    // Codex F2 round-3: this is the verifier's OBSERVED count, capped at the
                    // sample size — UI surfaces it as a lower bound when it equals the cap.
                    progress.LastObservedResidualCount,
                    progress.LastResidualSampleJson,
                },
            });
            return response;
        }

        private static async Task<HttpResponseData> BuildFullResponse(
            HttpRequestData req, DeletionManifest manifest, DeletionProgress? progress, string snapshotSha)
        {
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            response.Headers.Add("X-Deletion-Manifest-Source", "stored");
            response.Headers.Add("X-Deletion-Manifest-Snapshot-Sha256", snapshotSha);
            response.Headers.Add("X-Deletion-Manifest-Has-Progress", (progress != null).ToString());
            var json = JsonSerializer.SerializeToUtf8Bytes(new
            {
                manifest,
                progress,
                snapshotSha256 = snapshotSha,
            }, DeletionManifestJson.SerializerOptions);
            await response.Body.WriteAsync(json, 0, json.Length);
            return response;
        }

        private static async Task<HttpResponseData> BuildDownloadResponse(
            HttpRequestData req, DeletionManifest manifest, string tenantId, string sessionId, string manifestId)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(manifest, DeletionManifestJson.SerializerOptions);
            byte[] gzipped;
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    await gzip.WriteAsync(json, 0, json.Length);
                }
                gzipped = output.ToArray();
            }

            var fileName = $"{tenantId}_{sessionId}_{manifestId}.snapshot.json.gz";
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/gzip");
            response.Headers.Add("Content-Disposition", $"attachment; filename=\"{fileName}\"");
            await response.Body.WriteAsync(gzipped, 0, gzipped.Length);
            return response;
        }
    }
}

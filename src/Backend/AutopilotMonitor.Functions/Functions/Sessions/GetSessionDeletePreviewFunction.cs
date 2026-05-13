using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    /// <summary>
    /// <c>GET /api/admin/sessions/{sessionId}/delete/preview?mode={summary|full|download}</c> —
    /// read-only cascade-delete preview gated on <c>GlobalAdminOnly</c>. Runs the
    /// <see cref="DeletionManifestBuilder"/> against current data, returns the would-be manifest,
    /// and audits <c>deletion_preview</c>. NEVER mutates state, NEVER uploads a blob, NEVER
    /// enqueues — safety of the real delete must come from the producer's own CAS-then-build-then-upload
    /// sequence, not from a prior preview call (the gap between preview and delete is unsafe by
    /// design — preview is a debug tool, not a precondition; Plan §5 PR3 / §18).
    /// </summary>
    public class GetSessionDeletePreviewFunction
    {
        private readonly ILogger<GetSessionDeletePreviewFunction> _logger;
        private readonly DeletionManifestBuilder _builder;
        private readonly SessionDeletionGuard _guard;
        private readonly ISessionRepository _sessionRepo;
        private readonly IMaintenanceRepository _maintenanceRepo;

        public GetSessionDeletePreviewFunction(
            ILogger<GetSessionDeletePreviewFunction> logger,
            DeletionManifestBuilder builder,
            SessionDeletionGuard guard,
            ISessionRepository sessionRepo,
            IMaintenanceRepository maintenanceRepo)
        {
            _logger = logger;
            _builder = builder;
            _guard = guard;
            _sessionRepo = sessionRepo;
            _maintenanceRepo = maintenanceRepo;
        }

        [Function("GetSessionDeletePreview")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "admin/sessions/{sessionId}/delete/preview")] HttpRequestData req,
            string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "sessionId is required" });
                return badRequest;
            }

            var requestCtx = req.GetRequestContext();
            var actorEmail = TenantHelper.GetUserIdentifier(req);
            var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);
            var mode = (query["mode"] ?? "summary").ToLowerInvariant();
            if (mode != "summary" && mode != "full" && mode != "download")
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteAsJsonAsync(new { success = false, message = "mode must be one of: summary, full, download" });
                return bad;
            }

            // Resolve tenantId. Three-tier fallback ordered by Codex F1 review:
            //   1. Explicit ?tenantId=<guid> on GA calls — only path that lets a Global Admin
            //      preview a session in a tenant other than their JWT home tenant.
            //   2. JWT TargetTenantId, where the policy middleware mirrors the JWT tid for
            //      non-RouteParam routes. Sufficient for tenant admins acting on their own tenant.
            //   3. SessionsIndex lookup keyed by sessionId — the historical fallback for the
            //      "global" sentinel; preserved so existing callers without ?tenantId still work.
            var explicitTenantId = query["tenantId"];
            string tenantId;
            if (requestCtx.IsGlobalAdmin
                && !string.IsNullOrEmpty(explicitTenantId)
                && Guid.TryParse(explicitTenantId, out _))
            {
                tenantId = explicitTenantId!;
            }
            else
            {
                tenantId = requestCtx.TargetTenantId;
                if (string.IsNullOrEmpty(tenantId) || string.Equals(tenantId, "global", StringComparison.OrdinalIgnoreCase))
                {
                    tenantId = await _sessionRepo.FindSessionTenantIdAsync(sessionId) ?? string.Empty;
                }
            }
            if (string.IsNullOrEmpty(tenantId))
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { success = false, message = $"Session {sessionId} not found in any tenant." });
                return notFound;
            }

            // Guard hint: if a cascade is in flight, surface that to the caller. The preview
            // itself never acquires any lock — this read is pure observation, and a guarded
            // cascade may still race with the preview's enumeration. Operators should treat
            // the response as "snapshot of current state at preview time", not a blocking lock.
            string? inFlightHint = null;
            try
            {
                await _guard.EnsureWritableAsync(tenantId, sessionId, callerContext: "DeletePreview");
            }
            catch (SessionDeletionLockedException locked)
            {
                inFlightHint = $"Cascade in progress (state={locked.CurrentState}, manifestId={locked.ManifestId ?? "(none)"}); preview shows current data anyway.";
                _logger.LogInformation(
                    "DeletePreview hint: cascade in flight tenant={TenantId} session={SessionId} state={State} manifestId={ManifestId}",
                    tenantId, sessionId, locked.CurrentState, locked.ManifestId);
            }

            // Build the manifest (read-only — no state changes, no blob writes).
            var actor = new DeletionActor { Type = "admin", Actor = actorEmail };
            var stopwatch = Stopwatch.StartNew();
            DeletionManifest manifest;
            try
            {
                manifest = await _builder.BuildAsync(
                    tenantId, sessionId,
                    reason: "preview",
                    actor: actor,
                    retentionContext: new DeletionRetentionContext());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "DeletePreview failed: tenant={TenantId} session={SessionId} mode={Mode}",
                    tenantId, sessionId, mode);
                var error = req.CreateResponse(HttpStatusCode.InternalServerError);
                await error.WriteAsJsonAsync(new { success = false, message = "Failed to build deletion manifest", error = ex.Message });
                return error;
            }
            stopwatch.Stop();
            var builderDurationMs = stopwatch.ElapsedMilliseconds;

            // Audit (Plan §7 deletion_preview). Counts the same per-mode for observability.
            try
            {
                await _maintenanceRepo.LogAuditEntryAsync(
                    tenantId,
                    action: "deletion_preview",
                    entityType: "Session",
                    entityId: sessionId,
                    performedBy: actorEmail,
                    details: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["mode"] = mode,
                        ["builderDurationMs"] = builderDurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["schemaHash"] = manifest.SchemaHash,
                        ["totalSteps"] = manifest.Steps.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DeletePreview audit emission failed (non-fatal). tenant={TenantId} session={SessionId}", tenantId, sessionId);
            }

            return mode switch
            {
                "full"     => await BuildFullResponse(req, manifest, inFlightHint, builderDurationMs),
                "download" => await BuildDownloadResponse(req, manifest, tenantId, sessionId),
                _          => await BuildSummaryResponse(req, manifest, inFlightHint, builderDurationMs),
            };
        }

        private static async Task<HttpResponseData> BuildSummaryResponse(
            HttpRequestData req, DeletionManifest manifest, string? inFlightHint, long builderDurationMs)
        {
            var sampleKeys = new Dictionary<string, List<object>>(StringComparer.Ordinal);
            long totalRowCount = 0;
            foreach (var step in manifest.Steps)
            {
                totalRowCount += step.RowCount;
                if (step.Rows.Count == 0) continue;
                var key = step.Table ?? step.Step ?? $"order_{step.Order}";
                sampleKeys[key] = step.Rows.Take(5).Select(r => (object)new { pk = r.Pk, rk = r.Rk }).ToList();
            }

            var estimatedBytes = EstimateSnapshotSizeBytes(manifest);
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                mode = "summary",
                inFlightHint,
                preflightCounts = manifest.PreflightCounts,
                sampleKeys,
                estimatedRowCount = totalRowCount,
                estimatedSnapshotBytes = estimatedBytes,
                builderDurationMs,
                schemaHash = manifest.SchemaHash,
                manifestId = manifest.ManifestId,
            });
            return response;
        }

        private static async Task<HttpResponseData> BuildFullResponse(
            HttpRequestData req, DeletionManifest manifest, string? inFlightHint, long builderDurationMs)
        {
            // mode=full returns the RAW manifest JSON — byte-identical to what the producer
            // would upload to the deletion-manifests blob container (modulo gzip). This lets
            // operators diff a dry-run against the actual cascade's snapshot without parsing
            // through an envelope. Side-channel data (in-flight hint, builder duration) is
            // surfaced via response headers so the body stays clean.
            var json = JsonSerializer.SerializeToUtf8Bytes(manifest, DeletionManifestJson.SerializerOptions);
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            response.Headers.Add("X-Deletion-Preview-Mode", "full");
            response.Headers.Add("X-Deletion-Preview-Builder-Duration-Ms", builderDurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
            response.Headers.Add("X-Deletion-Preview-Manifest-Id", manifest.ManifestId);
            response.Headers.Add("X-Deletion-Preview-Schema-Hash", manifest.SchemaHash);
            if (!string.IsNullOrEmpty(inFlightHint))
            {
                response.Headers.Add("X-Deletion-Preview-InFlight-Hint", inFlightHint);
            }
            await response.Body.WriteAsync(json, 0, json.Length);
            return response;
        }

        private async Task<HttpResponseData> BuildDownloadResponse(
            HttpRequestData req, DeletionManifest manifest, string tenantId, string sessionId)
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

            var fileName = $"{tenantId}_{sessionId}_dryrun_{DateTime.UtcNow:yyyyMMddTHHmmssZ}.snapshot.json.gz";
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/gzip");
            response.Headers.Add("Content-Disposition", $"attachment; filename=\"{fileName}\"");
            await response.Body.WriteAsync(gzipped, 0, gzipped.Length);
            return response;
        }

        private static long EstimateSnapshotSizeBytes(DeletionManifest manifest)
        {
            // Cheap estimate — serialize once + report length. Compression ratio of ~0.2 gets
            // applied client-side if the operator wants the on-wire size.
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, DeletionManifestJson.SerializerOptions);
                return bytes.LongLength;
            }
            catch
            {
                return -1;
            }
        }
    }
}

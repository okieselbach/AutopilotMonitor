using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Azure.Storage.Blobs.Models;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.Diagnostics;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Manages session report submissions from Tenant Admins.
    /// Creates a ZIP with session data, events, analysis results, timeline TXT, events CSV,
    /// and optional screenshot, uploads to central blob storage, and stores metadata via INotificationRepository.
    /// </summary>
    public class SessionReportService
    {
        private readonly INotificationRepository _notificationRepo;
        private readonly ILogger<SessionReportService> _logger;
        private readonly BlobStorageService _blobStorage;
        private readonly TableStorageService _tableStorage;
        private readonly SessionReportDiagnosticsArchiveCopier _diagnosticsArchiveCopier;
        private readonly TenantConfigurationService _configService;
        private readonly IRuleRepository _ruleRepo;
        private readonly ISessionAnnotationRepository _annotationRepo;
        private const string ContainerName = "session-reports";

        public SessionReportService(
            INotificationRepository notificationRepo,
            BlobStorageService blobStorage,
            TableStorageService tableStorage,
            SessionReportDiagnosticsArchiveCopier diagnosticsArchiveCopier,
            TenantConfigurationService configService,
            IRuleRepository ruleRepo,
            ISessionAnnotationRepository annotationRepo,
            ILogger<SessionReportService> logger)
        {
            _notificationRepo = notificationRepo;
            _logger = logger;
            _blobStorage = blobStorage;
            _tableStorage = tableStorage;
            _diagnosticsArchiveCopier = diagnosticsArchiveCopier;
            _configService = configService;
            _ruleRepo = ruleRepo;
            _annotationRepo = annotationRepo;
        }

        /// <summary>
        /// Creates a ZIP from the provided data, uploads to central blob storage,
        /// and records metadata in the SessionReports table.
        /// </summary>
        public async Task<SessionReportMetadata> SubmitReportAsync(
            SubmitSessionReportRequest request,
            string submittedBy)
        {
            // Both identifiers shape blob names below; a non-GUID value could smuggle path
            // separators / dot segments into the storage URI (cross-container write).
            SecurityValidator.EnsureValidGuid(request.TenantId, nameof(request.TenantId));
            SecurityValidator.EnsureValidGuid(request.SessionId, nameof(request.SessionId));

            var reportId = Guid.NewGuid().ToString("N")[..12];
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var blobName = $"{request.TenantId}_{request.SessionId}_diag_request_{timestamp}.zip";

            // 0. Optional: preserve the session's diagnostics ZIP alongside the report.
            //    Session diag blobs die with the session (user delete / retention); the copy in
            //    the session-reports container does not. Fail-soft: a failed copy never blocks
            //    the report — the status lands on the row + in report-metadata.json instead.
            //    The source blob name/destination is recorded too: when the copy failed, the
            //    operator can still fetch the original while the session is alive.
            string? copiedDiagnosticsBlobName = null;
            string? diagnosticsCopyStatus = null;
            string? sourceDiagnosticsBlobName = null;
            string? sourceDiagnosticsDestination = null;
            if (request.IncludeDiagnostics)
            {
                var session = await _tableStorage.GetSessionAsync(request.TenantId, request.SessionId);
                if (session == null)
                {
                    diagnosticsCopyStatus = SessionReportDiagnosticsArchiveCopier.Statuses.FailedSessionNotFound;
                }
                else
                {
                    sourceDiagnosticsBlobName = session.DiagnosticsBlobName;
                    sourceDiagnosticsDestination = session.DiagnosticsBlobDestination;
                    var destinationName = $"{request.TenantId}_{request.SessionId}_diag_archive_{timestamp}.zip";
                    var copyResult = await _diagnosticsArchiveCopier.CopyAsync(
                        request.TenantId, request.SessionId, session.DiagnosticsBlobName, destinationName);
                    diagnosticsCopyStatus = copyResult.Status;
                    if (copyResult.Success)
                        copiedDiagnosticsBlobName = destinationName;
                }
            }

            // Tenant-side detection context (custom rule/pattern state at report time — it may
            // have changed by the time an operator investigates). Fail-soft; never blocks.
            var tenantContext = await CollectTenantContextAsync(request.TenantId, reportId);

            // Human annotations at report time — the submitter's own verdict/notes are exactly
            // the context an investigating operator wants first, and the snapshot survives the
            // session's retention/delete. ALL lanes are included: the report blob is readable
            // only through the GA-only session-reports routes, so the platform-internal
            // globaladmin lane never reaches tenant callers this way. Fail-soft; never blocks.
            var annotations = await CollectAnnotationsAsync(request.TenantId, request.SessionId, reportId);

            // Decode attachments up front so report-metadata.json can record their outcome
            // instead of silently dropping invalid payloads.
            var (screenshotBytes, screenshotStatus) = DecodeAttachment(request.ScreenshotBase64, reportId, "screenshot");
            var (agentLogBytes, agentLogStatus) = DecodeAttachment(request.AgentLogBase64, reportId, "agent log");

            // 1. Create ZIP in memory
            using var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                // Session row as CSV
                if (!string.IsNullOrEmpty(request.SessionCsv))
                {
                    AddTextEntry(archive, "session.csv", request.SessionCsv);
                }

                // Events CSV export (raw table data)
                if (!string.IsNullOrEmpty(request.EventsCsv))
                {
                    AddTextEntry(archive, "events.csv", request.EventsCsv);
                }

                // Analysis rule results CSV
                if (!string.IsNullOrEmpty(request.RuleResultsCsv))
                {
                    AddTextEntry(archive, "ruleresults.csv", request.RuleResultsCsv);
                }

                // Timeline TXT export (UI representation)
                if (!string.IsNullOrEmpty(request.TimelineExportTxt))
                {
                    AddTextEntry(archive, "timeline.txt", request.TimelineExportTxt);
                }

                // Human annotations snapshot (all lanes) — omitted when none exist.
                if (annotations is { Count: > 0 })
                {
                    AddJsonEntry(archive, "annotations.json", annotations);
                }

                // Report metadata
                AddJsonEntry(archive, "report-metadata.json", new
                {
                    reportId,
                    request.TenantId,
                    request.SessionId,
                    request.Comment,
                    request.Email,
                    submittedBy,
                    submittedAt = DateTime.UtcNow.ToString("O"),
                    export = new
                    {
                        exportedEventCount = request.ExportedEventCount,
                        sessionEventCount = request.SessionEventCount,
                        eventStreamActive = request.EventStreamActive
                    },
                    includedDiagnostics = new
                    {
                        requested = request.IncludeDiagnostics,
                        blobName = copiedDiagnosticsBlobName,
                        status = diagnosticsCopyStatus,
                        sourceBlobName = sourceDiagnosticsBlobName,
                        sourceDestination = sourceDiagnosticsDestination
                    },
                    attachments = new
                    {
                        screenshot = screenshotStatus,
                        agentLog = agentLogStatus
                    },
                    // Count only — the rows themselves live in annotations.json. 0 with a
                    // missing annotations.json ⇒ either none existed or the read failed soft.
                    annotationCount = annotations?.Count ?? 0,
                    tenantContext
                });

                // Optional screenshot
                if (screenshotBytes != null)
                {
                    var ext = Path.GetExtension(request.ScreenshotFileName ?? ".png");
                    if (string.IsNullOrEmpty(ext)) ext = ".png";
                    var entry = archive.CreateEntry($"screenshot{ext}", CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    await entryStream.WriteAsync(screenshotBytes);
                }

                // Optional agent log file (max 5 MB enforced by frontend)
                if (agentLogBytes != null)
                {
                    var logFileName = SanitizeZipEntryName(request.AgentLogFileName, "agent.log");
                    var entry = archive.CreateEntry(logFileName, CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    await entryStream.WriteAsync(agentLogBytes);
                }
            }

            // 2. Upload ZIP to central blob storage
            zipStream.Position = 0;
            var containerClient = _blobStorage.GetContainerClient(ContainerName);
            await containerClient.CreateIfNotExistsAsync();
            var blobClient = containerClient.GetBlobClient(BlobNameGuard.EnsureFlat(blobName, nameof(blobName)));
            await blobClient.UploadAsync(zipStream, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/zip" }
            });

            _logger.LogInformation(
                "Session report uploaded: ReportId={ReportId}, BlobName={BlobName}, Tenant={TenantId}, Session={SessionId}",
                reportId, blobName, request.TenantId, request.SessionId);

            // 3. Store metadata via repository
            var now = DateTime.UtcNow;
            var metadata = new SessionReportMetadata
            {
                ReportId = reportId,
                TenantId = request.TenantId,
                SessionId = request.SessionId,
                Comment = request.Comment,
                Email = request.Email,
                BlobName = blobName,
                SubmittedBy = submittedBy,
                SubmittedAt = now,
                ReportType = ReportTypes.Session,
                DiagnosticsBlobName = copiedDiagnosticsBlobName,
                DiagnosticsCopyStatus = diagnosticsCopyStatus
            };

            await _notificationRepo.StoreSessionReportMetadataAsync(metadata);

            return metadata;
        }

        /// <summary>
        /// Submits a diagnostic-files report (no session context).
        /// Persists into the same SessionReports table + session-reports container, but the
        /// resulting ZIP only contains user-attached files plus a thin metadata header —
        /// no events.csv / timeline.txt / ruleresults.csv synthesis.
        /// </summary>
        public async Task<SessionReportMetadata> SubmitDiagFilesReportAsync(
            SubmitDiagFilesReportRequest request,
            string submittedBy)
        {
            SecurityValidator.EnsureValidGuid(request.TenantId, nameof(request.TenantId));

            var reportId = Guid.NewGuid().ToString("N")[..12];
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var blobName = $"{request.TenantId}_diag_files_{timestamp}.zip";

            var (screenshotBytes, screenshotStatus) = DecodeAttachment(request.ScreenshotBase64, reportId, "screenshot");
            var (agentLogBytes, agentLogStatus) = DecodeAttachment(request.AgentLogBase64, reportId, "log payload");

            using var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                AddJsonEntry(archive, "report-metadata.json", new
                {
                    reportId,
                    reportType = ReportTypes.DiagFiles,
                    request.TenantId,
                    request.Comment,
                    request.Email,
                    submittedBy,
                    submittedAt = DateTime.UtcNow.ToString("O"),
                    attachments = new
                    {
                        screenshot = screenshotStatus,
                        agentLog = agentLogStatus
                    }
                });

                if (screenshotBytes != null)
                {
                    var ext = Path.GetExtension(request.ScreenshotFileName ?? ".png");
                    if (string.IsNullOrEmpty(ext)) ext = ".png";
                    var entry = archive.CreateEntry($"screenshot{ext}", CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    await entryStream.WriteAsync(screenshotBytes);
                }

                if (agentLogBytes != null)
                {
                    var logFileName = SanitizeZipEntryName(request.AgentLogFileName, "diag-files.bin");
                    var entry = archive.CreateEntry(logFileName, CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    await entryStream.WriteAsync(agentLogBytes);
                }
            }

            zipStream.Position = 0;
            var containerClient = _blobStorage.GetContainerClient(ContainerName);
            await containerClient.CreateIfNotExistsAsync();
            var blobClient = containerClient.GetBlobClient(BlobNameGuard.EnsureFlat(blobName, nameof(blobName)));
            await blobClient.UploadAsync(zipStream, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/zip" }
            });

            _logger.LogInformation(
                "Diag-files report uploaded: ReportId={ReportId}, BlobName={BlobName}, Tenant={TenantId}",
                reportId, blobName, request.TenantId);

            var metadata = new SessionReportMetadata
            {
                ReportId = reportId,
                TenantId = request.TenantId,
                SessionId = string.Empty,
                Comment = request.Comment,
                Email = request.Email,
                BlobName = blobName,
                SubmittedBy = submittedBy,
                SubmittedAt = DateTime.UtcNow,
                ReportType = ReportTypes.DiagFiles
            };

            await _notificationRepo.StoreSessionReportMetadataAsync(metadata);

            return metadata;
        }

        /// <summary>
        /// Returns all session reports newest-first, optionally filtered to a single tenant.
        /// </summary>
        public Task<List<SessionReportMetadata>> GetAllReportsAsync(string? tenantId = null)
            => _notificationRepo.GetSessionReportsAsync(tenantId);

        /// <summary>
        /// Returns a single page of session reports newest-first, optionally
        /// filtered to a single tenant. The opaque <c>NextRawToken</c> on the
        /// returned <see cref="RawPage{T}"/> is what the function layer wraps
        /// with the wire continuation envelope.
        /// </summary>
        public Task<RawPage<SessionReportMetadata>> GetReportsPageAsync(
            string? tenantId, int pageSize, string? continuation)
            => _notificationRepo.GetSessionReportsPageAsync(tenantId, pageSize, continuation);

        /// <summary>
        /// Updates the AdminNote field for a report identified by reportId.
        /// </summary>
        public async Task<bool> UpdateAdminNoteAsync(string reportId, string adminNote)
        {
            return await _notificationRepo.UpdateSessionReportAdminNoteAsync(reportId, adminNote);
        }

        /// <summary>
        /// Snapshot of the tenant-side detection state relevant to a session report: upload
        /// destination flags and the IDs of custom rules/patterns active at report time.
        /// Deliberately excludes anything secret-bearing (SAS URLs, tokens) and full rule
        /// definitions — IDs + timestamps are enough to correlate with the (persisted) rule
        /// tables even after the customer edits them. Fail-soft: null on any error.
        /// </summary>
        private async Task<object?> CollectTenantContextAsync(string tenantId, string reportId)
        {
            try
            {
                var config = await _configService.GetConfigurationAsync(tenantId);
                var gatherRules = await _ruleRepo.GetGatherRulesAsync(tenantId);
                var analyzeRules = await _ruleRepo.GetAnalyzeRulesAsync(tenantId);
                var imePatterns = await _ruleRepo.GetImeLogPatternsAsync(tenantId);

                return new
                {
                    diagnosticsUploadMode = config.DiagnosticsUploadMode,
                    diagnosticsUploadDestination = config.DiagnosticsUploadDestination,
                    gatherRuleDebugLogEnabled = config.EnableGatherRuleDebugLog,
                    customGatherRules = gatherRules
                        .Select(r => new { ruleId = r.RuleId, updatedAt = r.UpdatedAt }).ToList(),
                    customAnalyzeRules = analyzeRules
                        .Select(r => new { ruleId = r.RuleId, updatedAt = r.UpdatedAt }).ToList(),
                    customImePatternIds = imePatterns.Select(p => p.PatternId).ToList()
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Report {ReportId}: tenant-context snapshot failed — omitted from metadata", reportId);
                return null;
            }
        }

        /// <summary>
        /// Snapshot of the session's human annotations (all lanes) for annotations.json.
        /// Fail-soft: a failed read yields null and never blocks the report.
        /// </summary>
        private async Task<List<object>?> CollectAnnotationsAsync(string tenantId, string sessionId, string reportId)
        {
            try
            {
                var rows = await _annotationRepo.GetForSessionAsync(tenantId, sessionId);
                return rows.Select(a => (object)new
                {
                    lane = a.Lane,
                    verdict = a.Verdict,
                    note = a.Note,
                    authorUpn = a.AuthorUpn,
                    authorDisplayName = a.AuthorDisplayName,
                    createdByUpn = a.CreatedByUpn,
                    createdAtUtc = a.CreatedAtUtc,
                    updatedAtUtc = a.UpdatedAtUtc,
                    ruleIds = a.RuleIds,
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Report {ReportId}: annotations snapshot failed — omitted from package", reportId);
                return null;
            }
        }

        /// <summary>
        /// Decodes an optional base64 attachment. Returns the bytes plus a status string
        /// ("none" | "included" | "invalid-base64") that is persisted in report-metadata.json —
        /// an invalid payload must be visible to the operator, not silently dropped.
        /// </summary>
        private (byte[]? Bytes, string Status) DecodeAttachment(string? base64, string reportId, string label)
        {
            if (string.IsNullOrEmpty(base64))
                return (null, "none");
            try
            {
                return (Convert.FromBase64String(base64), "included");
            }
            catch (FormatException ex)
            {
                _logger.LogWarning(ex, "Invalid base64 {Label} data in report {ReportId}", label, reportId);
                return (null, "invalid-base64");
            }
        }

        /// <summary>
        /// Sanitizes a caller-supplied file name for use as a ZIP entry: strips any path
        /// components (no traversal-shaped entries inside the archive) and falls back to
        /// <paramref name="fallback"/> when nothing usable remains.
        /// </summary>
        internal static string SanitizeZipEntryName(string? fileName, string fallback)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return fallback;
            // Normalize both separator styles, then keep only the final segment.
            var candidate = fileName.Replace('\\', '/');
            var lastSlash = candidate.LastIndexOf('/');
            if (lastSlash >= 0)
                candidate = candidate[(lastSlash + 1)..];
            candidate = candidate.Trim();
            if (string.IsNullOrEmpty(candidate) || candidate == "." || candidate == "..")
                return fallback;
            return candidate;
        }

        private static void AddJsonEntry(ZipArchive archive, string name, object data)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(JsonConvert.SerializeObject(data, Formatting.Indented));
        }

        private static void AddTextEntry(ZipArchive archive, string name, string content)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }
    }
}

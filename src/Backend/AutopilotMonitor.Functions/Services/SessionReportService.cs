using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Azure.Storage.Blobs.Models;
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
        private const string ContainerName = "session-reports";

        public SessionReportService(
            INotificationRepository notificationRepo,
            BlobStorageService blobStorage,
            TableStorageService tableStorage,
            SessionReportDiagnosticsArchiveCopier diagnosticsArchiveCopier,
            ILogger<SessionReportService> logger)
        {
            _notificationRepo = notificationRepo;
            _logger = logger;
            _blobStorage = blobStorage;
            _tableStorage = tableStorage;
            _diagnosticsArchiveCopier = diagnosticsArchiveCopier;
        }

        /// <summary>
        /// Creates a ZIP from the provided data, uploads to central blob storage,
        /// and records metadata in the SessionReports table.
        /// </summary>
        public async Task<SessionReportMetadata> SubmitReportAsync(
            SubmitSessionReportRequest request,
            string submittedBy)
        {
            var reportId = Guid.NewGuid().ToString("N")[..12];
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var blobName = $"{request.TenantId}_{request.SessionId}_diag_request_{timestamp}.zip";

            // 0. Optional: preserve the session's diagnostics ZIP alongside the report.
            //    Session diag blobs die with the session (user delete / retention); the copy in
            //    the session-reports container does not. Fail-soft: a failed copy never blocks
            //    the report — the status lands on the row + in report-metadata.json instead.
            string? copiedDiagnosticsBlobName = null;
            string? diagnosticsCopyStatus = null;
            if (request.IncludeDiagnostics)
            {
                var session = await _tableStorage.GetSessionAsync(request.TenantId, request.SessionId);
                if (session == null)
                {
                    diagnosticsCopyStatus = SessionReportDiagnosticsArchiveCopier.Statuses.FailedSessionNotFound;
                }
                else
                {
                    var destinationName = $"{request.TenantId}_{request.SessionId}_diag_archive_{timestamp}.zip";
                    var copyResult = await _diagnosticsArchiveCopier.CopyAsync(
                        request.TenantId, request.SessionId, session.DiagnosticsBlobName, destinationName);
                    diagnosticsCopyStatus = copyResult.Status;
                    if (copyResult.Success)
                        copiedDiagnosticsBlobName = destinationName;
                }
            }

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
                    includedDiagnostics = new
                    {
                        requested = request.IncludeDiagnostics,
                        blobName = copiedDiagnosticsBlobName,
                        status = diagnosticsCopyStatus
                    }
                });

                // Optional screenshot
                if (!string.IsNullOrEmpty(request.ScreenshotBase64))
                {
                    try
                    {
                        var screenshotBytes = Convert.FromBase64String(request.ScreenshotBase64);
                        var ext = Path.GetExtension(request.ScreenshotFileName ?? ".png");
                        if (string.IsNullOrEmpty(ext)) ext = ".png";
                        var entry = archive.CreateEntry($"screenshot{ext}", CompressionLevel.Optimal);
                        using var entryStream = entry.Open();
                        await entryStream.WriteAsync(screenshotBytes);
                    }
                    catch (FormatException ex)
                    {
                        _logger.LogWarning(ex, "Invalid base64 screenshot data in report {ReportId}", reportId);
                    }
                }

                // Optional agent log file (max 5 MB enforced by frontend)
                if (!string.IsNullOrEmpty(request.AgentLogBase64))
                {
                    try
                    {
                        var logBytes = Convert.FromBase64String(request.AgentLogBase64);
                        var logFileName = request.AgentLogFileName ?? "agent.log";
                        var entry = archive.CreateEntry(logFileName, CompressionLevel.Optimal);
                        using var entryStream = entry.Open();
                        await entryStream.WriteAsync(logBytes);
                    }
                    catch (FormatException ex)
                    {
                        _logger.LogWarning(ex, "Invalid base64 agent log data in report {ReportId}", reportId);
                    }
                }
            }

            // 2. Upload ZIP to central blob storage
            zipStream.Position = 0;
            var containerClient = _blobStorage.GetContainerClient(ContainerName);
            await containerClient.CreateIfNotExistsAsync();
            var blobClient = containerClient.GetBlobClient(blobName);
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
            var reportId = Guid.NewGuid().ToString("N")[..12];
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var blobName = $"{request.TenantId}_diag_files_{timestamp}.zip";

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
                    submittedAt = DateTime.UtcNow.ToString("O")
                });

                if (!string.IsNullOrEmpty(request.ScreenshotBase64))
                {
                    try
                    {
                        var screenshotBytes = Convert.FromBase64String(request.ScreenshotBase64);
                        var ext = Path.GetExtension(request.ScreenshotFileName ?? ".png");
                        if (string.IsNullOrEmpty(ext)) ext = ".png";
                        var entry = archive.CreateEntry($"screenshot{ext}", CompressionLevel.Optimal);
                        using var entryStream = entry.Open();
                        await entryStream.WriteAsync(screenshotBytes);
                    }
                    catch (FormatException ex)
                    {
                        _logger.LogWarning(ex, "Invalid base64 screenshot data in diag-files report {ReportId}", reportId);
                    }
                }

                if (!string.IsNullOrEmpty(request.AgentLogBase64))
                {
                    try
                    {
                        var logBytes = Convert.FromBase64String(request.AgentLogBase64);
                        var logFileName = string.IsNullOrEmpty(request.AgentLogFileName)
                            ? "diag-files.bin"
                            : request.AgentLogFileName;
                        var entry = archive.CreateEntry(logFileName, CompressionLevel.Optimal);
                        using var entryStream = entry.Open();
                        await entryStream.WriteAsync(logBytes);
                    }
                    catch (FormatException ex)
                    {
                        _logger.LogWarning(ex, "Invalid base64 log payload in diag-files report {ReportId}", reportId);
                    }
                }
            }

            zipStream.Position = 0;
            var containerClient = _blobStorage.GetContainerClient(ContainerName);
            await containerClient.CreateIfNotExistsAsync();
            var blobClient = containerClient.GetBlobClient(blobName);
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

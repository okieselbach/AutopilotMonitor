using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime
{
    /// <summary>
    /// Result of a diagnostics package upload attempt.
    /// </summary>
    public class DiagnosticsUploadResult
    {
        /// <summary>Blob name on success, null on failure or skip.</summary>
        public string BlobName { get; set; }

        /// <summary>
        /// Where the blob was uploaded: <c>"CustomerSas"</c> or <c>"Hosted"</c>. Verbatim
        /// from the backend's upload-url response. Surfaced into the
        /// <c>diagnostics_uploaded</c> event so the backend can stamp it on the
        /// <c>Sessions</c> row alongside the blob name — the download path then knows
        /// which storage to fetch from, even if the tenant later switches destinations.
        /// Null when the upload was skipped or against a legacy backend that doesn't
        /// return the field.
        /// </summary>
        public string Destination { get; set; }

        /// <summary>
        /// First part of the SAS URL (up to and including the first query param) with the token truncated,
        /// so the event shows the target container without leaking the full signature.
        /// Example: "https://account.blob.core.windows.net/diagnostics?sp=(truncated)"
        /// </summary>
        public string SasUrlPrefix { get; set; }

        /// <summary>Human-readable error code/message when upload failed. Null on success or skip.</summary>
        public string ErrorCode { get; set; }

        public bool Success => BlobName != null;
    }

    /// <summary>
    /// Caps that bound a diagnostics archive build. Files beyond these limits are skipped
    /// and recorded in <c>_TRUNCATED.txt</c>. Defaults are sized to keep an upload below
    /// ~500 MB uncompressed; a single runaway log cannot exceed 100 MB.
    /// </summary>
    internal sealed class DiagnosticsBudget
    {
        public long MaxSingleFileBytes { get; set; }
        public long MaxTotalUncompressedBytes { get; set; }
        public int MaxFileCount { get; set; }

        public static DiagnosticsBudget Default => new DiagnosticsBudget
        {
            MaxSingleFileBytes = 100L * 1024 * 1024,
            MaxTotalUncompressedBytes = 500L * 1024 * 1024,
            MaxFileCount = 5000,
        };
    }

    /// <summary>
    /// Creates and uploads a diagnostics ZIP package (agent logs + IME logs + session info)
    /// to the tenant's Azure Blob Storage container via a short-lived SAS URL.
    /// The SAS URL is fetched on-demand just before upload and never stored in config or on disk.
    /// </summary>
    public class DiagnosticsPackageService
    {
        private readonly AgentConfiguration _configuration;
        private readonly AgentLogger _logger;
        private readonly BackendApiClient _apiClient;
        private readonly HttpClient _httpClient;

        // Test seam: tests can shrink caps to trigger truncation paths without producing
        // hundreds of MB of fixture data. Production callers leave this at Default.
        internal DiagnosticsBudget Budget { get; set; } = DiagnosticsBudget.Default;

        // Tracks per-build inclusion totals + skip reasons. Threaded through every
        // AddLogFiles call so caps are global across all sections, not per section.
        private sealed class BudgetTracker
        {
            public DiagnosticsBudget Budget { get; }
            public long TotalBytes { get; private set; }
            public int FileCount { get; private set; }
            public List<SkipRecord> Skipped { get; } = new List<SkipRecord>();

            public BudgetTracker(DiagnosticsBudget budget) { Budget = budget; }

            public bool WouldExceedTotal(long fileSize) =>
                TotalBytes + fileSize > Budget.MaxTotalUncompressedBytes;

            public bool WouldExceedCount() =>
                FileCount + 1 > Budget.MaxFileCount;

            public void RecordIncluded(long size)
            {
                TotalBytes += size;
                FileCount++;
            }

            public void RecordSkip(string path, string reason, long size) =>
                Skipped.Add(new SkipRecord(path, reason, size));

            public bool HasSkips => Skipped.Count > 0;
        }

        private readonly struct SkipRecord
        {
            public string Path { get; }
            public string Reason { get; }
            public long Size { get; }
            public SkipRecord(string path, string reason, long size)
            {
                Path = path; Reason = reason; Size = size;
            }
        }

        // Built-in sections come from the Shared catalog (DiagnosticsBuiltInSections.All) — the
        // same list the backend serves to the portal, so what administrators see is exactly
        // what this service collects. Test seam: section id → folder; production leaves the
        // map empty and every section resolves from the catalog's (unexpanded) source folder.
        private readonly IReadOnlyDictionary<string, string> _sectionFolderOverrides;

        // Scenario oracle for DiagnosticsSectionCondition.DevicePreparation. Production reads
        // the deterministic WDP registry marker (the same gate every other WDP switch uses);
        // tests inject a constant so they never touch the registry.
        private readonly Func<bool> _devicePreparationProbe;

        public DiagnosticsPackageService(AgentConfiguration configuration, AgentLogger logger, BackendApiClient apiClient)
            : this(configuration, logger, apiClient, null, null, null, null, null)
        {
        }

        // Test seam: allows xUnit fixtures to redirect log/state/spool/data folders to a
        // temp dir without touching real %ProgramData% paths. Production callers always go
        // through the public ctor. The five named overrides map onto the catalog's section
        // ids; sectionFolderOverrides covers every other section (RealmJoin*, ImeBootstrapper…).
        internal DiagnosticsPackageService(
            AgentConfiguration configuration,
            AgentLogger logger,
            BackendApiClient apiClient,
            string agentLogFolderOverride,
            string imeLogFolderOverride,
            string agentStateFolderOverride,
            string agentSpoolFolderOverride,
            string agentDataFolderOverride,
            IReadOnlyDictionary<string, string> sectionFolderOverrides = null,
            Func<bool> devicePreparationProbe = null)
        {
            _configuration = configuration;
            _logger = logger;
            _apiClient = apiClient;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

            var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
            void Map(string sectionId, string folder)
            {
                if (folder != null) overrides[sectionId] = folder;
            }
            Map("AgentLogs", agentLogFolderOverride);
            Map("ImeLogs", imeLogFolderOverride);
            Map("AgentState", agentStateFolderOverride);
            Map("AgentSpool", agentSpoolFolderOverride);
            Map("AgentMarkers", agentDataFolderOverride);
            if (sectionFolderOverrides != null)
            {
                foreach (var kv in sectionFolderOverrides)
                    overrides[kv.Key] = kv.Value;
            }
            _sectionFolderOverrides = overrides;

            // Tests that pass folder overrides but no probe must stay registry-free: a seam
            // caller gets "not WDP" by default, the production ctor gets the real marker.
            _devicePreparationProbe = devicePreparationProbe
                ?? (overrides.Count == 0
                    ? (Func<bool>)EnrollmentRegistryDetector.IsDeterministicDevicePreparation
                    : () => false);
        }

        /// <summary>
        /// Creates a diagnostics ZIP and uploads it to the configured Blob Storage container.
        /// Returns a DiagnosticsUploadResult with BlobName set on success, or ErrorCode set on failure.
        /// Returns null if the upload was skipped (mode=Off, not configured, or OnFailure+succeeded).
        /// This method is non-fatal: all exceptions are caught and logged.
        /// </summary>
        /// <param name="enrollmentSucceeded">
        /// True for a successful enrollment, false for a failed one (drives the OnFailure mode
        /// check and sessioninfo.txt content). Pass true for WhiteGlove pre-provisioning (it
        /// succeeded up to this point). Pass <b>null</b> when the session has no outcome yet
        /// (on-demand server-requested collection mid-enrollment): the OnFailure gate does not
        /// apply — there is no success to skip on — and sessioninfo.txt records "In Progress".
        /// </param>
        /// <param name="fileNameSuffix">
        /// Optional suffix inserted before the .zip extension.
        /// Example: "preprov" → AgentDiagnostics-{sessionId}-{timestamp}-preprov.zip
        /// Null (default) → AgentDiagnostics-{sessionId}-{timestamp}.zip
        /// </param>
        public virtual async Task<DiagnosticsUploadResult> CreateAndUploadAsync(bool? enrollmentSucceeded, string fileNameSuffix = null)
        {
            try
            {
                // Check if upload is needed based on configuration
                var mode = _configuration.DiagnosticsUploadMode ?? "Off";
                if (string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Debug("Diagnostics upload disabled (mode=Off)");
                    return null;
                }

                if (!_configuration.DiagnosticsUploadEnabled)
                {
                    _logger.Debug("Diagnostics upload skipped: not configured for this tenant");
                    return null;
                }

                // Null outcome (on-demand mid-session) passes through: the OnFailure gate only
                // skips a KNOWN success — an in-flight session has nothing to skip on.
                if (string.Equals(mode, "OnFailure", StringComparison.OrdinalIgnoreCase) && enrollmentSucceeded == true)
                {
                    _logger.Info("Diagnostics upload skipped: enrollment succeeded and mode=OnFailure");
                    return null;
                }

                _logger.Info($"Creating diagnostics package (mode={mode}, enrollmentSucceeded={(enrollmentSucceeded.HasValue ? enrollmentSucceeded.ToString() : "n/a")}{(fileNameSuffix != null ? $", suffix={fileNameSuffix}" : "")})...");

                // Build ZIP in memory
                var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                var suffix = string.IsNullOrEmpty(fileNameSuffix) ? "" : $"-{fileNameSuffix}";
                var zipFileName = $"AgentDiagnostics-{_configuration.SessionId}-{timestamp}{suffix}.zip";

                var zipBytes = BuildArchiveBytes(enrollmentSucceeded);

                _logger.Info($"Diagnostics package created: {zipFileName} ({zipBytes.Length / 1024} KB)");

                // Fetch a short-lived upload URL just before uploading — never stored in config or on disk
                _logger.Info("Requesting diagnostics upload URL from backend...");
                var uploadUrlResponse = await _apiClient.GetDiagnosticsUploadUrlAsync(
                    _configuration.TenantId,
                    _configuration.SessionId,
                    zipFileName);

                if (uploadUrlResponse == null || !uploadUrlResponse.Success || string.IsNullOrEmpty(uploadUrlResponse.UploadUrl))
                {
                    var errorCode = uploadUrlResponse?.Message ?? "Failed to get diagnostics upload URL from backend";
                    _logger.Warning($"Failed to get diagnostics upload URL from backend — skipping upload: {errorCode}");
                    return new DiagnosticsUploadResult { ErrorCode = errorCode };
                }

                var sasUrlPrefix = BuildSasUrlPrefix(uploadUrlResponse.UploadUrl);

                // Resolve the final PUT target: Hosted SAS is already blob-scoped at
                // {tenantId}/{filename} and must be used as-is; CustomerSas (or a null
                // Destination from an older backend) is container-scoped and we append
                // the blob filename ourselves.
                var blobUploadUrl = BuildBlobUploadUrl(uploadUrlResponse.UploadUrl, zipFileName, uploadUrlResponse.Destination);

                // BlobName for the Sessions row: prefer the backend-supplied path because
                // it encodes destination-specific layout (e.g. tenant-prefix for Hosted).
                // Fall back to the local filename so the agent still works against older
                // backends that don't return BlobName.
                var persistedBlobName = !string.IsNullOrEmpty(uploadUrlResponse.BlobName)
                    ? uploadUrlResponse.BlobName
                    : zipFileName;

                // Upload to Blob Storage using the freshly obtained URL
                var (uploaded, uploadErrorCode) = await UploadToBlobStorageAsync(zipFileName, zipBytes, blobUploadUrl);
                if (uploaded)
                {
                    _logger.Info($"Diagnostics package uploaded successfully: {persistedBlobName}");
                    return new DiagnosticsUploadResult
                    {
                        BlobName = persistedBlobName,
                        Destination = uploadUrlResponse.Destination,
                        SasUrlPrefix = sasUrlPrefix,
                    };
                }

                return new DiagnosticsUploadResult
                {
                    Destination = uploadUrlResponse.Destination,
                    SasUrlPrefix = sasUrlPrefix,
                    ErrorCode = uploadErrorCode,
                };
            }
            catch (Exception ex)
            {
                _logger.Warning($"Diagnostics package creation/upload failed (non-fatal): {ex.Message}");
                return new DiagnosticsUploadResult { ErrorCode = ex.Message };
            }
        }

        /// <summary>
        /// Returns a safe, truncated SAS URL prefix for logging — shows account/container but not the token.
        /// Example: "https://account.blob.core.windows.net/diagnostics?sp=(truncated)"
        /// </summary>
        private static string BuildSasUrlPrefix(string sasUrl)
        {
            try
            {
                var qIndex = sasUrl.IndexOf('?');
                if (qIndex < 0) return sasUrl;

                // Keep only the first query param name to show which container/permissions are set
                var basePath = sasUrl.Substring(0, qIndex);
                var query = sasUrl.Substring(qIndex + 1);
                var firstParam = query.Split('&')[0].Split('=')[0];
                return $"{basePath}?{firstParam}=(truncated)";
            }
            catch
            {
                return "(url unavailable)";
            }
        }

        // Chicken-and-egg guard: the agent-log snapshot is zipped BEFORE packaging finishes, so
        // packaging problems (blocked paths, missing files, failed copies) are invisible in the
        // uploaded archive's own log. This manifest records every packaging decision and travels
        // INSIDE the ZIP (field case: sessions a11102f4 / 3ae7528b, missing evtx undiagnosable).
        private StringBuilder _manifest;

        private void ManifestLine(string text)
        {
            _manifest?.AppendLine($"[{DateTime.UtcNow:HH:mm:ss.fff}Z] {text}");
        }

        // Builds the diagnostics ZIP body in-memory. Extracted from CreateAndUploadAsync so
        // tests can assert archive contents without going through the upload path.
        internal virtual byte[] BuildArchiveBytes(bool? enrollmentSucceeded)
        {
            var tracker = new BudgetTracker(Budget);
            _manifest = new StringBuilder();
            using (var ms = new MemoryStream())
            {
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                {
                    // 1. sessioninfo.txt — informational metadata, not subject to caps.
                    AddSessionInfo(archive, enrollmentSucceeded);

                    // 2. Built-in sections — ONE catalog shared with the backend (the portal
                    //    shows exactly this list): agent logs, IME logs (+ the Device Preparation
                    //    bootstrapper event log on WDP), agent state (recursive: `.quarantine`
                    //    and `.part1-<utc>/` buckets ride along), telemetry spool, top-level
                    //    markers, and the RealmJoin logs behind the tenant's watcher toggle.
                    //    The scenario probe is a registry read — evaluate it once per build.
                    var isDevicePreparation = ProbeDevicePreparation();
                    ManifestLine($"SCENARIO: devicePreparation={isDevicePreparation} realmJoinWatcher={_configuration.EnableRealmJoinWatcher}");
                    foreach (var section in DiagnosticsBuiltInSections.All)
                        AddBuiltInSection(archive, section, tracker, isDevicePreparation);

                    // 3. Configured additional log paths (global + tenant, validated by guards)
                    foreach (var entry in _configuration.DiagnosticsLogPaths ?? new System.Collections.Generic.List<Shared.Models.DiagnosticsLogPath>())
                    {
                        // Resolve %LOGGED_ON_USER_PROFILE% token and get profile path for guard exception
                        var userProfilePath = UserProfileResolver.ContainsUserProfileToken(entry.Path)
                            ? UserProfileResolver.GetLoggedOnUserProfilePath() : null;

                        if (!DiagnosticsPathGuards.IsDiagnosticsPathAllowed(entry.Path, _configuration.UnrestrictedMode, userProfilePath))
                        {
                            _logger.Warning($"Diagnostics path blocked by guard: {entry.Path}");
                            ManifestLine($"BLOCKED (path guard): {entry.Path}");
                            continue;
                        }
                        var expandedPath = UserProfileResolver.ExpandCustomTokens(entry.Path);
                        if (expandedPath == null)
                        {
                            _logger.Warning($"Diagnostics path skipped (no user session for token): {entry.Path}");
                            ManifestLine($"SKIPPED (no user session for token): {entry.Path}");
                            continue;
                        }
                        var folder = Path.GetDirectoryName(expandedPath);
                        var pattern = Path.GetFileName(expandedPath);
                        if (string.IsNullOrEmpty(folder)) continue;
                        if (string.IsNullOrEmpty(pattern) || !pattern.Contains(".")) pattern = "*";
                        var zipFolder = $"AdditionalLogs/{Path.GetFileName(folder)}";
                        ManifestLine($"CONFIGURED PATH: '{entry.Path}' -> folder='{folder}' pattern='{pattern}'");
                        AddLogFiles(archive, folder, zipFolder, pattern, tracker, entry.IncludeSubfolders);
                    }

                    // Packaging manifest — always written, even when empty of problems, so its
                    // absence itself is meaningful (archive predates the feature).
                    try
                    {
                        var manifestEntry = archive.CreateEntry("package-manifest.txt", CompressionLevel.Optimal);
                        using (var writer = new StreamWriter(manifestEntry.Open()))
                            writer.Write(_manifest.ToString());
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Failed to write package manifest: {ex.Message}");
                    }

                    // Always last: emit truncation report only if any file was skipped.
                    if (tracker.HasSkips)
                        WriteTruncatedMarker(archive, tracker);
                }

                return ms.ToArray();
            }
        }

        private bool ProbeDevicePreparation()
        {
            try
            {
                return _devicePreparationProbe();
            }
            catch (Exception ex)
            {
                // Fail toward Classic like every other WDP gate: the evtx export is skipped,
                // never the whole package.
                _logger.Warning($"Device Preparation probe failed — treating as Classic: {ex.Message}");
                ManifestLine($"SCENARIO PROBE FAILED (treated as Classic): {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Pure gate: is this built-in section collected under the given configuration and
        /// enrollment scenario? Unknown conditions (a newer Shared catalog than this agent)
        /// fail closed.
        /// </summary>
        internal static bool IsSectionActive(DiagnosticsBuiltInSection section, AgentConfiguration configuration, bool isDevicePreparation)
        {
            switch (section.Condition)
            {
                case DiagnosticsSectionCondition.Always:
                    return true;
                case DiagnosticsSectionCondition.RealmJoinWatcher:
                    return configuration.EnableRealmJoinWatcher;
                case DiagnosticsSectionCondition.DevicePreparation:
                    return isDevicePreparation;
                default:
                    return false;
            }
        }

        private static string DescribeInactiveCondition(DiagnosticsSectionCondition condition)
        {
            switch (condition)
            {
                case DiagnosticsSectionCondition.RealmJoinWatcher:
                    return "RealmJoin Watcher disabled";
                case DiagnosticsSectionCondition.DevicePreparation:
                    return "not a Device Preparation enrollment";
                default:
                    return $"unknown condition {condition}";
            }
        }

        private void AddBuiltInSection(ZipArchive archive, DiagnosticsBuiltInSection section, BudgetTracker tracker, bool isDevicePreparation)
        {
            if (!IsSectionActive(section, _configuration, isDevicePreparation))
            {
                ManifestLine($"BUILT-IN SKIPPED ({DescribeInactiveCondition(section.Condition)}): {section.Id}");
                return;
            }

            // Override (test seam) or catalog source folder with %ProgramData% /
            // %LOGGED_ON_USER_PROFILE% expanded; null means the token is present but no
            // interactive user has been detected yet — same skip as configured paths.
            var folder = _sectionFolderOverrides.TryGetValue(section.Id, out var overrideFolder)
                ? overrideFolder
                : UserProfileResolver.ExpandCustomTokens(section.SourceFolder);
            if (folder == null)
            {
                _logger.Warning($"Built-in diagnostics section skipped (no user session for token): {section.Id}");
                ManifestLine($"BUILT-IN SKIPPED (no user session for token): {section.Id} '{section.SourceFolder}'");
                return;
            }

            ManifestLine($"BUILT-IN: {section.Id} -> folder='{folder}' zip='{section.ZipFolder}' recursive={section.IncludeSubfolders}");
            foreach (var pattern in section.Patterns)
                AddLogFiles(archive, folder, section.ZipFolder, pattern, tracker, section.IncludeSubfolders);
        }

        private void AddSessionInfo(ZipArchive archive, bool? enrollmentSucceeded)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Session ID: {_configuration.SessionId}");
            sb.AppendLine($"Tenant ID: {_configuration.TenantId}");
            sb.AppendLine($"Device Name: {Environment.MachineName}");
            sb.AppendLine($"Timestamp: {DateTime.UtcNow:O}");
            sb.AppendLine($"Enrollment Result: {(enrollmentSucceeded == null ? "In Progress" : enrollmentSucceeded.Value ? "Succeeded" : "Failed")}");

            // Hardware info via existing DeviceInfoProvider (WMI)
            sb.AppendLine($"Manufacturer: {DeviceInfoProvider.GetManufacturer()}");
            sb.AppendLine($"Model: {DeviceInfoProvider.GetModel()}");
            sb.AppendLine($"Serial Number: {DeviceInfoProvider.GetSerialNumber()}");

            var entry = archive.CreateEntry("sessioninfo.txt");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(sb.ToString());
        }

        // Pure decision helper: file/dir attributes that mark NTFS reparse points
        // (junctions, symlinks, mount points). We never follow these to avoid traversing
        // outside the intended source folder or creating cycles in the recursive walk.
        internal static bool IsReparsePoint(FileAttributes attrs) =>
            (attrs & FileAttributes.ReparsePoint) != 0;

        // Recursive enumeration that skips reparse-point directories. Materializes the
        // result into a list so the caller can iterate without holding a directory handle.
        // Errors during enumeration are swallowed (logged via _logger by the caller).
        private static List<string> EnumerateFilesNoReparseDirs(string folder, string pattern, bool recurse)
        {
            var result = new List<string>();
            CollectFilesNoReparseDirs(folder, pattern, recurse, result);
            return result;
        }

        private static void CollectFilesNoReparseDirs(string folder, string pattern, bool recurse, List<string> result)
        {
            string[] files;
            try { files = Directory.GetFiles(folder, pattern, SearchOption.TopDirectoryOnly); }
            catch { return; }
            result.AddRange(files);

            if (!recurse) return;

            string[] dirs;
            try { dirs = Directory.GetDirectories(folder); }
            catch { return; }

            foreach (var sub in dirs)
            {
                FileAttributes attrs;
                try { attrs = File.GetAttributes(sub); }
                catch { continue; }
                if (IsReparsePoint(attrs)) continue;
                CollectFilesNoReparseDirs(sub, pattern, recurse: true, result);
            }
        }

        private void AddLogFiles(ZipArchive archive, string sourceFolder, string zipFolder, string searchPattern,
            BudgetTracker tracker, bool includeSubfolders = false)
        {
            if (!Directory.Exists(sourceFolder))
            {
                _logger.Debug($"Log folder not found, skipping: {sourceFolder}");
                ManifestLine($"FOLDER MISSING: {sourceFolder} (pattern '{searchPattern}')");
                return;
            }

            List<string> files;
            try
            {
                files = EnumerateFilesNoReparseDirs(sourceFolder, searchPattern, includeSubfolders);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to enumerate log files in {sourceFolder}: {ex.Message}");
                ManifestLine($"ENUMERATION FAILED: {sourceFolder} (pattern '{searchPattern}'): {ex.Message}");
                return;
            }

            if (files.Count == 0)
                ManifestLine($"NO MATCH: {sourceFolder} pattern '{searchPattern}'");

            foreach (var file in files)
            {
                try
                {
                    // Per-file reparse check: a top-level file in the folder may itself be a
                    // symlink/junction even though its parent directory is real.
                    FileAttributes attrs;
                    try { attrs = File.GetAttributes(file); }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Failed to read attributes for {file}: {ex.Message}");
                        continue;
                    }
                    if (IsReparsePoint(attrs))
                    {
                        _logger.Warning($"Skipping reparse-point file: {file}");
                        tracker.RecordSkip(file, "reparse", 0);
                        ManifestLine($"SKIPPED (reparse point): {file}");
                        continue;
                    }

                    long length;
                    try { length = new FileInfo(file).Length; }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Failed to stat {file}: {ex.Message}");
                        continue;
                    }

                    // Active event-log channels hold their .evtx exclusively locked — a raw
                    // FileStream open fails with a sharing violation. Export the channel via
                    // wevtutil instead and stream the export (deleted below). Session a11102f4:
                    // the tenant-configured BootstrapperAgent channel never made it into the ZIP.
                    string streamSource = file;
                    string tempExport = null;
                    if (file.EndsWith(".evtx", StringComparison.OrdinalIgnoreCase))
                    {
                        tempExport = TryExportEventLogChannel(file);
                        if (tempExport != null)
                        {
                            streamSource = tempExport;
                            try { length = new FileInfo(tempExport).Length; }
                            catch { /* keep original length estimate */ }
                        }
                        else
                        {
                            // Export failed (channel not resolvable / wevtutil error) — fall through
                            // to the raw copy. Inactive or archived evtx files are not locked, so
                            // the plain FileStream often still succeeds; if the channel IS active
                            // and locked, the existing per-file catch logs the failure.
                            _logger.Warning($"Event log export unavailable for {file} — attempting raw copy.");
                            ManifestLine($"EVTX EXPORT FAILED (falling back to raw copy): {file}");
                        }
                    }

                    try
                    {
                        if (length > tracker.Budget.MaxSingleFileBytes)
                        {
                            _logger.Warning($"Skipping oversized file ({length} bytes > {tracker.Budget.MaxSingleFileBytes} cap): {file}");
                            tracker.RecordSkip(file, "size", length);
                            ManifestLine($"SKIPPED (single-file cap, {length} bytes): {file}");
                            continue;
                        }
                        if (tracker.WouldExceedCount())
                        {
                            _logger.Warning($"Skipping file (file-count cap {tracker.Budget.MaxFileCount} reached): {file}");
                            tracker.RecordSkip(file, "count", length);
                            ManifestLine($"SKIPPED (file-count cap): {file}");
                            continue;
                        }
                        if (tracker.WouldExceedTotal(length))
                        {
                            _logger.Warning($"Skipping file (total-bytes cap {tracker.Budget.MaxTotalUncompressedBytes} reached): {file}");
                            tracker.RecordSkip(file, "total", length);
                            ManifestLine($"SKIPPED (total-bytes cap): {file}");
                            continue;
                        }

                        // Preserve subfolder structure in the ZIP when includeSubfolders is enabled
                        var relativePath = file.Substring(sourceFolder.Length).TrimStart(Path.DirectorySeparatorChar);
                        var entryName = $"{zipFolder}/{relativePath.Replace(Path.DirectorySeparatorChar, '/')}";

                        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                        // Stream directly from disk into the entry — no per-file MemoryStream/byte[]
                        // copy. FileShare.ReadWrite avoids locking conflicts with active log writers.
                        using (var fs = new FileStream(streamSource, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var entryStream = entry.Open())
                        {
                            fs.CopyTo(entryStream);
                        }

                        tracker.RecordIncluded(length);
                        _logger.Debug($"Added to diagnostics package: {entryName} ({length / 1024} KB)");
                        ManifestLine($"ADDED: {entryName} ({length} bytes)");
                    }
                    finally
                    {
                        if (tempExport != null)
                        {
                            try { File.Delete(tempExport); } catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to add log file to diagnostics package: {file} - {ex.Message}");
                    ManifestLine($"FAILED (copy): {file}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Resolves the event-log CHANNEL that writes to the given .evtx file by matching the
        /// registered channels' LogFilePath — never by guessing a naming convention: most
        /// channels follow the "%4 encodes '/'" file naming, but not all do (field case
        /// 2026-08-17: Microsoft-Autopilot-BootstrapperAgent/BootstrapperAgentServiceLogProvider
        /// writes plain "BootstrapperAgentServiceLogProvider.evtx"). Falls back to the %4
        /// convention only when no registered channel claims the file.
        /// </summary>
        internal static string ResolveChannelForEvtxFile(string evtxPath)
        {
            try
            {
                var target = Path.GetFullPath(evtxPath);
                using (var session = new System.Diagnostics.Eventing.Reader.EventLogSession())
                {
                    foreach (var logName in session.GetLogNames())
                    {
                        try
                        {
                            var config = new System.Diagnostics.Eventing.Reader.EventLogConfiguration(logName, session);
                            var logFile = config.LogFilePath;
                            if (string.IsNullOrEmpty(logFile)) continue;
                            var expanded = Path.GetFullPath(Environment.ExpandEnvironmentVariables(logFile));
                            if (string.Equals(expanded, target, StringComparison.OrdinalIgnoreCase))
                                return logName;
                        }
                        catch { /* individual channel unreadable — keep scanning */ }
                    }
                }
            }
            catch { /* enumeration unavailable — fall back to convention */ }

            return Path.GetFileNameWithoutExtension(evtxPath).Replace("%4", "/");
        }

        /// <summary>
        /// Exports the event-log channel behind a winevt .evtx file to a temp copy via
        /// <c>wevtutil epl</c> (active channels hold their file exclusively locked, so a raw
        /// copy fails). Channel resolution happens against the registered channels — see
        /// <see cref="ResolveChannelForEvtxFile"/>. Returns the temp file path (caller deletes)
        /// or null when the export failed.
        /// </summary>
        private string TryExportEventLogChannel(string evtxPath)
        {
            string tempPath = null;
            try
            {
                var channel = ResolveChannelForEvtxFile(evtxPath);
                tempPath = Path.Combine(Path.GetTempPath(), $"am-evtx-{Guid.NewGuid():N}.evtx");
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "wevtutil.exe"),
                    Arguments = $"epl \"{channel}\" \"{tempPath}\" /ow:true",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                };
                using (var process = System.Diagnostics.Process.Start(psi))
                {
                    if (process == null) return null;
                    if (!process.WaitForExit(30000))
                    {
                        try { process.Kill(); } catch { }
                        _logger.Warning($"wevtutil export timed out for channel '{channel}'.");
                        ManifestLine($"EVTX EXPORT TIMEOUT: channel '{channel}' ({evtxPath})");
                        try { File.Delete(tempPath); } catch { }
                        return null;
                    }
                    if (process.ExitCode != 0)
                    {
                        var stderr = process.StandardError.ReadToEnd();
                        _logger.Warning($"wevtutil export failed for channel '{channel}' (exit {process.ExitCode}): {stderr}");
                        ManifestLine($"EVTX EXPORT ERROR: channel '{channel}' exit {process.ExitCode}: {stderr}");
                        try { File.Delete(tempPath); } catch { }
                        return null;
                    }
                }
                if (File.Exists(tempPath))
                {
                    ManifestLine($"EVTX EXPORTED: channel '{channel}' -> {evtxPath}");
                    return tempPath;
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.Warning($"Event log export failed for {evtxPath}: {ex.Message}");
                if (tempPath != null)
                {
                    try { File.Delete(tempPath); } catch { }
                }
                return null;
            }
        }

        private static void WriteTruncatedMarker(ZipArchive archive, BudgetTracker tracker)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Diagnostics package was truncated. Some files were not included.");
            sb.AppendLine();
            sb.AppendLine("Caps in effect:");
            sb.AppendLine($"  MaxSingleFileBytes:        {tracker.Budget.MaxSingleFileBytes}");
            sb.AppendLine($"  MaxTotalUncompressedBytes: {tracker.Budget.MaxTotalUncompressedBytes}");
            sb.AppendLine($"  MaxFileCount:              {tracker.Budget.MaxFileCount}");
            sb.AppendLine();
            sb.AppendLine($"Included: {tracker.FileCount} files, {tracker.TotalBytes} bytes");
            sb.AppendLine($"Skipped:  {tracker.Skipped.Count} files");
            sb.AppendLine();
            sb.AppendLine("Skip list (path | reason | size):");
            foreach (var s in tracker.Skipped)
            {
                sb.AppendLine($"  {s.Path} | {s.Reason} | {s.Size}");
            }

            var entry = archive.CreateEntry("_TRUNCATED.txt");
            using (var writer = new StreamWriter(entry.Open(), Encoding.UTF8))
                writer.Write(sb.ToString());
        }

        /// <summary>
        /// Suffix allowlist for the Azure Blob Storage hosts the agent is permitted to PUT
        /// the diagnostics ZIP to. Anything outside this list is rejected before the upload
        /// so a tampered or mis-configured <c>uploadUrlResponse.UploadUrl</c> cannot redirect
        /// the diag-ZIP (hostname/serial/UPN-bearing logs) to an attacker endpoint.
        /// </summary>
        private static readonly string[] AllowedBlobHostSuffixes =
        {
            ".blob.core.windows.net",        // Azure Public
            ".blob.core.usgovcloudapi.net",  // Azure US Government
            ".blob.core.chinacloudapi.cn",   // Azure China
            ".blob.core.cloudapi.de",        // Azure Germany (legacy)
        };

        /// <summary>
        /// Returns true iff <paramref name="url"/> is a syntactically valid absolute URI with
        /// scheme <c>https</c> and a host that ends with one of <see cref="AllowedBlobHostSuffixes"/>.
        /// Internal-static so the V2 test suite can pin accept/reject paths without HTTP.
        /// </summary>
        internal static bool IsAllowedBlobUploadUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
            var host = uri.Host;
            foreach (var suffix in AllowedBlobHostSuffixes)
            {
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Resolves the final blob PUT URL based on the destination advertised by the
        /// backend. Pure helper, internal-static so the V2 test suite can pin both
        /// branches without exercising any HTTP transport:
        /// <list type="bullet">
        ///   <item><b>Hosted</b> (or any case-insensitive match): the SAS is already
        ///         blob-scoped at <c>{tenantId}/{filename}</c> and must be used as-is.
        ///         Appending the local filename would produce a double-name URL.</item>
        ///   <item><b>CustomerSas</b> / null / unknown (legacy backend without the
        ///         field): the SAS is container-scoped; insert the blob filename
        ///         before the query string. Preserves prior behaviour.</item>
        /// </list>
        /// </summary>
        internal static string BuildBlobUploadUrl(string sasUrl, string blobFileName, string destination)
        {
            if (!string.IsNullOrEmpty(destination)
                && string.Equals(destination, "Hosted", StringComparison.OrdinalIgnoreCase))
            {
                return sasUrl;
            }

            // CustomerSas or null/unknown — container-scoped SAS, insert blob name.
            var questionMarkIndex = sasUrl.IndexOf('?');
            if (questionMarkIndex >= 0)
            {
                var basePath = sasUrl.Substring(0, questionMarkIndex).TrimEnd('/');
                var queryString = sasUrl.Substring(questionMarkIndex);
                return $"{basePath}/{blobFileName}{queryString}";
            }
            return $"{sasUrl.TrimEnd('/')}/{blobFileName}";
        }

        /// <summary>
        /// Uploads the ZIP bytes to Azure Blob Storage via PUT to the pre-built
        /// <paramref name="blobUploadUrl"/>. The URL is destination-aware (built by
        /// <see cref="BuildBlobUploadUrl"/>) and ready to PUT without further mutation.
        /// <paramref name="blobName"/> is kept for log lines only.
        /// Returns (success, errorCode) — errorCode is null on success, otherwise the last HTTP error.
        /// </summary>
        private async Task<(bool success, string errorCode)> UploadToBlobStorageAsync(string blobName, byte[] data, string blobUploadUrl)
        {
            // Pre-flight: refuse to PUT diag-ZIP to anything outside the Azure Blob Storage
            // host allowlist (incl. wrong scheme). Permanent failure — no retry could fix it.
            if (!IsAllowedBlobUploadUrl(blobUploadUrl))
            {
                _logger.Warning(
                    $"Blob upload rejected: URL is not an allowed Azure Blob Storage endpoint " +
                    $"(expected https://*.blob.core.windows.net or sovereign-cloud equivalent). " +
                    $"URL prefix: {BuildSasUrlPrefix(blobUploadUrl)}");
                return (false, "url_host_rejected");
            }

            var blobUrl = blobUploadUrl;

            const int maxRetries = 3;
            string lastErrorCode = null;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    _logger.Info($"Uploading diagnostics package (attempt {attempt}/{maxRetries}, {data.Length / 1024} KB)...");

                    using var content = new ByteArrayContent(data);
                    content.Headers.Add("x-ms-blob-type", "BlockBlob");
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");

                    var response = await _httpClient.PutAsync(blobUrl, content);

                    if (response.IsSuccessStatusCode)
                    {
                        return (true, null);
                    }

                    var statusCode = (int)response.StatusCode;
                    lastErrorCode = $"HTTP {statusCode} {response.ReasonPhrase}";
                    var responseBody = await response.Content.ReadAsStringAsync();

                    // Auth errors (401/403) are permanent — SAS token invalid or expired, retrying won't help
                    if (statusCode == 401 || statusCode == 403)
                    {
                        _logger.Warning($"Blob upload auth error (not retryable): {lastErrorCode} - {responseBody}");
                        return (false, lastErrorCode);
                    }

                    _logger.Warning($"Blob upload attempt {attempt} failed: {lastErrorCode} - {responseBody}");
                }
                catch (Exception ex)
                {
                    lastErrorCode = ex.Message;
                    _logger.Warning($"Blob upload attempt {attempt} failed: {ex.Message}");
                }

                if (attempt < maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 2s, 4s, 8s
                    _logger.Info($"Retrying blob upload in {delay.TotalSeconds}s...");
                    await Task.Delay(delay);
                }
            }

            _logger.Warning($"Diagnostics package upload failed after {maxRetries} attempts");
            return (false, lastErrorCode);
        }
    }
}

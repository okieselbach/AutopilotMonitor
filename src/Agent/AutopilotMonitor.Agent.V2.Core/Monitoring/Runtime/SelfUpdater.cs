using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Shared;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime
{
    /// <summary>
    /// Fast self-update at agent startup: checks version-v2.json in blob storage,
    /// downloads the V2 ZIP if newer, swaps files (rename-trick for locked binaries),
    /// and restarts via PowerShell Wait-Process.
    ///
    /// Design priority: speed over update — better to run the old version than delay startup.
    /// V2 uses its own manifest (version-v2.json) + ZIP (AutopilotMonitor-Agent-V2.zip) to
    /// stay isolated from the legacy V1 release line.
    /// </summary>
    static class SelfUpdater
    {
        private const int VersionCheckTimeoutMs = 2500;  // 2.5s — covers cold DNS/TLS on fresh Autopilot boot
        private const int DownloadTimeoutMs = 10000;     // 10s — abort if too slow
        private const string OldFileSuffix = ".old";

        /// <summary>
        /// Writes an init banner to the log file to visually separate this agent process
        /// from install-mode logs that share the same file.
        /// </summary>
        public static void LogInit(string agentVersion)
        {
            LogToFile($"======================= Agent init (v{agentVersion}) =======================");
        }

        /// <summary>
        /// Writes a message to the agent log file. Used for pre-update logging
        /// when the full AgentLogger is not yet initialized.
        /// </summary>
        public static void Log(string message)
        {
            LogToFile(message);
        }

        /// <summary>
        /// Deletes leftover .old files from a previous self-update.
        /// Called early in startup before any other logic.
        /// </summary>
        public static void CleanupPreviousUpdate(string agentDir, Action<string> log)
        {
            try
            {
                if (!Directory.Exists(agentDir))
                    return;

                foreach (var oldFile in Directory.GetFiles(agentDir, "*" + OldFileSuffix))
                {
                    try
                    {
                        File.Delete(oldFile);
                    }
                    catch
                    {
                        // Best-effort: file may still be locked if previous process hasn't fully exited
                    }
                }

                // Also clean up any leftover staging directory
                var stagingDir = Environment.ExpandEnvironmentVariables(Constants.AgentUpdateStagingDir);
                if (Directory.Exists(stagingDir))
                {
                    try { Directory.Delete(stagingDir, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"Self-update cleanup warning: {ex.Message}");
            }
        }

        /// <summary>
        /// Optional: backend-provided SHA-256 hash of the latest agent ZIP.
        /// Set from the last AgentConfigResponse before calling CheckAndApplyUpdateAsync.
        /// Takes priority over the hash in version-v2.json (separate trust channel).
        /// </summary>
        public static string BackendExpectedSha256 { get; set; }

        /// <summary>
        /// Optional: graceful-shutdown request hook, set by the runtime host once its
        /// shutdown machinery is armed (before the orchestrator starts) and cleared in its
        /// shutdown finally. Same static-hook idiom as <see cref="BackendExpectedSha256"/>.
        /// <para>
        /// Field case (session b9b92d89, 2026-07-09): the runtime-hash-mismatch trigger runs
        /// the update pipeline on a thread-pool task while the orchestrator is live; the old
        /// <see cref="RestartAgent"/> then called <c>Environment.Exit</c> directly, giving
        /// the ProcessExit handlers only the CLR's ~2-3s window — far too short for the full
        /// drain (ingress → orchestrator.Stop → spool flush → snapshot). The kill landed
        /// mid-SignalLog-append and produced a duplicated line. With this hook wired, the
        /// restart routes through the host's normal graceful shutdown instead; the returned
        /// <c>true</c> tells <see cref="RestartAgent"/> to wait (bounded) for the process to
        /// exit on its own rather than hard-exiting.
        /// </para>
        /// <para>
        /// Contract: returns <c>true</c> when a graceful shutdown was initiated (host loop
        /// signalled); <c>false</c> to request the immediate-exit fallback. Must be cheap
        /// and non-blocking. Exceptions are treated as <c>false</c>.
        /// </para>
        /// </summary>
        public static Func<bool> RequestGracefulShutdown { get; set; }

        /// <summary>
        /// Upper bound for the graceful-shutdown wait in <see cref="RestartAgent"/>. MUST
        /// stay well below the restart script's <c>Wait-Process -Timeout 30</c>: if the old
        /// process outlives that timeout, PowerShell starts the new agent while the old one
        /// is still running and the new instance kills itself via the multi-instance guard —
        /// leaving the device without an agent until the next boot trigger.
        /// </summary>
        internal const int GracefulExitFallbackMs = 15000;

        /// <summary>
        /// Checks for a newer agent version and applies the update if available.
        /// On success, restarts the process and never returns.
        /// On any failure, returns normally so the current version continues.
        /// </summary>
        /// <param name="forceUpdate">
        /// When true, bypasses the IsNewerVersion check — used by the runtime-hash-mismatch trigger
        /// to cover hotfixes that reuse the same version number.
        /// </param>
        /// <param name="triggerReason">
        /// "startup" (default) or "runtime_hash_mismatch". Recorded in the marker file so the
        /// agent_version_check event (outcome=updated) can distinguish the two paths.
        /// </param>
        /// <param name="downloadTimeoutMsOverride">
        /// Overrides the ZIP download timeout. Startup path leaves null (fast-fail, 10s) to preserve
        /// Autopilot boot speed. Runtime trigger passes 60000 because monitoring is already running
        /// and we know an update is required.
        /// </param>
        /// <param name="allowDowngrade">
        /// When false (default), a latest version strictly lower than <paramref name="currentVersion"/>
        /// is rejected even when <paramref name="forceUpdate"/> is true — prevents the production
        /// <c>version-v2.json</c> from silently overwriting a higher-versioned dev/pre-release agent via
        /// the runtime hash-mismatch path. Set to true only for controlled rollback scenarios.
        /// </param>
        public static async Task CheckAndApplyUpdateAsync(
            string currentVersion,
            string agentDir,
            bool consoleMode,
            bool forceUpdate = false,
            string triggerReason = "startup",
            int? downloadTimeoutMsOverride = null,
            bool allowDowngrade = false)
        {
            Action<string> log = msg =>
            {
                LogToFile(msg);
                if (consoleMode) Console.WriteLine(msg);
            };

            var isStartupTrigger = string.Equals(triggerReason, "startup", StringComparison.Ordinal);
            var downloadTimeoutMs = downloadTimeoutMsOverride ?? DownloadTimeoutMs;
            var totalSw = Stopwatch.StartNew();
            long versionCheckMs = 0, downloadMs = 0, verifyMs = 0, extractMs = 0, swapMs = 0;
            long zipSizeBytes = 0;
            string latestVersion = null;

            // PR5: entry-marker line so forensics can grep for "Self-update started" and see
            // both the trigger and the version baseline. The previous first-output line was
            // either a downgrade-decision or a "no update needed" — for the happy path nothing
            // logged at all until the cmp result.
            log($"Self-update: started (current={currentVersion}, trigger={triggerReason}, force={forceUpdate}, allowDowngrade={allowDowngrade})");

            try
            {
                // Step 1: Fetch version-v2.json (2.5s timeout)
                var versionSw = Stopwatch.StartNew();
                string manifestSha256;
                (latestVersion, manifestSha256) = await GetLatestVersionAsync(log);
                versionSw.Stop();
                versionCheckMs = versionSw.ElapsedMilliseconds;

                if (latestVersion == null)
                {
                    if (isStartupTrigger)
                        WriteSkipMarker("version_check_failed", currentVersion, null, "version-v2.json fetch failed or timed out", log);
                    return; // Could not determine latest version — continue with current
                }

                // Step 2: Version comparison. Three-way decision:
                //   cmp < 0  → latest < current  = downgrade
                //   cmp == 0 → latest == current = same version (hash repair only, requires forceUpdate)
                //   cmp > 0  → latest > current  = upgrade
                var cmp = CompareVersions(currentVersion, latestVersion);

                if (cmp < 0)
                {
                    // Downgrade — forward-only policy blocks this by default even when forceUpdate=true.
                    // Protects dev/pre-release builds from being silently replaced by the production
                    // version advertised by the prod backend (runtime_hash_mismatch force path).
                    if (!allowDowngrade)
                    {
                        log($"Self-update: downgrade BLOCKED — current={currentVersion}, latest={latestVersion}, trigger={triggerReason}, allowDowngrade=false");
                        WriteDowngradeBlockedMarker(currentVersion, latestVersion, triggerReason, log);
                        return;
                    }
                    log($"Self-update: downgrade ALLOWED (admin override) — current={currentVersion}, latest={latestVersion}, trigger={triggerReason}");
                }
                else if (cmp == 0)
                {
                    // Same version: only proceed when forceUpdate (e.g. runtime hash mismatch = binary repair).
                    if (!forceUpdate)
                    {
                        log($"Self-update: current version {currentVersion} is up to date (latest: {latestVersion})");
                        if (isStartupTrigger)
                            WriteCheckedMarker(currentVersion, latestVersion, versionCheckMs, log);
                        return;
                    }
                    log($"Self-update: same-version REPAIR (trigger={triggerReason}) — reinstalling {currentVersion}");
                }
                else if (cmp > 0)
                {
                    log($"Self-update: newer version available — current={currentVersion}, latest={latestVersion}");
                }
                else
                {
                    // cmp == int.MinValue sentinel from CompareVersions → unparsable version string on
                    // either side. Fall back to the pre-fix behaviour: proceed only when forceUpdate.
                    if (!forceUpdate)
                    {
                        log($"Self-update: cannot compare versions (current={currentVersion}, latest={latestVersion}) — skipping");
                        if (isStartupTrigger)
                            WriteSkipMarker("version_compare_failed", currentVersion, latestVersion, "version parse failed", log);
                        return;
                    }
                    log($"Self-update: FORCE mode (trigger={triggerReason}) — current={currentVersion}, latest={latestVersion}, proceeding despite unparsable versions");
                }

                // Step 3: Download ZIP (timeout = downloadTimeoutMs)
                // ZIP lives under %ProgramData% (SYSTEM/Admin ACL) instead of C:\Windows\Temp so
                // a non-admin local user cannot plant a junction at the ZIP filename.
                var zipDir = Environment.ExpandEnvironmentVariables(Constants.AgentUpdateDownloadDir);
                Directory.CreateDirectory(zipDir);
                var zipPath = Path.Combine(zipDir, "AutopilotMonitor-Agent-Update.zip");
                var downloadTimerSw = Stopwatch.StartNew();
                var downloadOk = await DownloadZipAsync(zipPath, downloadTimeoutMs, log);
                downloadTimerSw.Stop();
                downloadMs = downloadTimerSw.ElapsedMilliseconds;
                if (!downloadOk)
                {
                    if (isStartupTrigger)
                        WriteSkipMarker("download_failed", currentVersion, latestVersion, $"ZIP download failed within {downloadTimeoutMs}ms", log);
                    return;
                }
                try { zipSizeBytes = new FileInfo(zipPath).Length; } catch { /* best-effort */ }
                // PR5: download success line — inner helper logs failure paths but stays silent on success.
                log($"Self-update: ZIP downloaded ({zipSizeBytes / 1024} KB in {downloadMs}ms)");

                // Step 3b: Verify SHA-256 integrity (backend hash has priority over version-v2.json hash)
                string expectedSha256;
                if (!string.IsNullOrEmpty(BackendExpectedSha256))
                {
                    expectedSha256 = BackendExpectedSha256;
                    log("Self-update: using backend hash for integrity verification (cached config — trusted channel)");
                }
                else if (!string.IsNullOrEmpty(manifestSha256))
                {
                    expectedSha256 = manifestSha256;
                    log("Self-update: using version-v2.json hash for integrity verification (blob storage)");
                }
                else
                {
                    expectedSha256 = null;
                }

                var verifySw = Stopwatch.StartNew();
                var integrityOk = VerifyZipIntegrity(zipPath, expectedSha256, log);
                verifySw.Stop();
                verifyMs = verifySw.ElapsedMilliseconds;

                if (!integrityOk)
                {
                    CleanupStaging(null, zipPath);
                    if (isStartupTrigger)
                        WriteSkipMarker("integrity_mismatch", currentVersion, latestVersion, "SHA-256 mismatch on downloaded ZIP", log);
                    return;
                }
                // PR5: integrity verified — silent before, opaque whether the verify step actually ran.
                log($"Self-update: ZIP integrity verified ({verifyMs}ms)");

                // Step 4: Extract to staging directory
                var stagingDir = Environment.ExpandEnvironmentVariables(Constants.AgentUpdateStagingDir);
                var extractSw = Stopwatch.StartNew();
                var extractOk = ExtractToStaging(zipPath, stagingDir, log);
                extractSw.Stop();
                extractMs = extractSw.ElapsedMilliseconds;
                if (!extractOk)
                {
                    if (isStartupTrigger)
                        WriteSkipMarker("extract_failed", currentVersion, latestVersion, "ZIP extraction failed", log);
                    return;
                }
                // PR5: extract success log line so forensics can see the full pipeline progressing.
                log($"Self-update: extract succeeded ({extractMs}ms, target={stagingDir})");

                // Step 5: Validate staging
                var stagedExe = Path.Combine(stagingDir, "AutopilotMonitor.Agent.exe");
                if (!File.Exists(stagedExe))
                {
                    log("Self-update: staging validation failed — AutopilotMonitor.Agent.exe not found in ZIP");
                    CleanupStaging(stagingDir, zipPath);
                    if (isStartupTrigger)
                        WriteSkipMarker("extract_failed", currentVersion, latestVersion, "AutopilotMonitor.Agent.exe not found in ZIP", log);
                    return;
                }

                // Step 6: Swap files (rename locked → .old, copy new)
                var swapTimerSw = Stopwatch.StartNew();
                var swapOk = SwapFiles(agentDir, stagingDir, log);
                swapTimerSw.Stop();
                swapMs = swapTimerSw.ElapsedMilliseconds;
                if (!swapOk)
                {
                    CleanupStaging(stagingDir, zipPath);
                    if (isStartupTrigger)
                        WriteSkipMarker("swap_failed", currentVersion, latestVersion, "file swap failed", log);
                    return;
                }

                // Step 7: Clean up staging + temp ZIP
                CleanupStaging(stagingDir, zipPath);

                // Step 8: Write marker file so the new agent can emit an event on next startup
                totalSw.Stop();
                WriteSelfUpdateMarker(
                    previousVersion: currentVersion,
                    newVersion: latestVersion,
                    triggerReason: triggerReason,
                    versionCheckMs: versionCheckMs,
                    downloadMs: downloadMs,
                    zipSizeBytes: zipSizeBytes,
                    verifyMs: verifyMs,
                    extractMs: extractMs,
                    swapMs: swapMs,
                    totalUpdateMs: totalSw.ElapsedMilliseconds,
                    log: log);

                // Step 9: Restart via PowerShell Wait-Process
                log($"Self-update: files swapped successfully, restarting agent (v{latestVersion})...");
                RestartAgent(agentDir, log);

                // RestartAgent calls Environment.Exit — we should never reach here
            }
            catch (Exception ex)
            {
                log($"Self-update: unexpected error — {ex.Message}. Continuing with current version.");
                if (isStartupTrigger)
                {
                    try { WriteSkipMarker("unexpected_error", currentVersion, latestVersion, ex.Message, log); } catch { }
                }
            }
        }

        /// <summary>
        /// Fetches version-v2.json from blob storage and returns the version string and optional SHA-256 hash.
        /// Returns (null, null) if the check fails or times out.
        /// </summary>
        private static async Task<(string version, string sha256)> GetLatestVersionAsync(Action<string> log)
        {
            try
            {
                var versionUrl = $"{Constants.AgentBlobBaseUrl}/{Constants.AgentVersionFileNameForLine(2)}";

                using (var handler = new HttpClientHandler())
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(VersionCheckTimeoutMs) })
                {
                    var json = await client.GetStringAsync(versionUrl);
                    var obj = JObject.Parse(json);
                    var version = obj["version"]?.ToString();

                    if (string.IsNullOrWhiteSpace(version))
                    {
                        log("Self-update: version-v2.json has no 'version' field");
                        return (null, null);
                    }

                    var sha256 = obj["sha256"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(sha256))
                        log($"Self-update: version-v2.json has SHA-256 hash for integrity verification");

                    return (version.Trim(), sha256?.Trim());
                }
            }
            catch (TaskCanceledException)
            {
                log($"Self-update: version check timed out ({VersionCheckTimeoutMs}ms) — skipping update");
                return (null, null);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("404"))
            {
                log("Self-update: version-v2.json not found (404) — skipping update");
                return (null, null);
            }
            catch (Exception ex)
            {
                log($"Self-update: version check failed — {ex.Message}");
                return (null, null);
            }
        }

        /// <summary>
        /// Verifies the SHA-256 hash of the downloaded ZIP against the expected hash.
        /// Returns true if the hash matches or if no expected hash is available (backward compat).
        /// </summary>
        private static bool VerifyZipIntegrity(string zipPath, string expectedSha256, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(expectedSha256))
            {
                log("Self-update: no SHA-256 hash available — skipping integrity check (backward compat)");
                return true;
            }

            try
            {
                using (var sha256 = SHA256.Create())
                using (var stream = File.OpenRead(zipPath))
                {
                    var hashBytes = sha256.ComputeHash(stream);
                    var actualHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

                    if (string.Equals(actualHash, expectedSha256.ToLowerInvariant(), StringComparison.Ordinal))
                    {
                        log("Self-update: SHA-256 integrity check passed");
                        return true;
                    }

                    log($"Self-update: SHA-256 MISMATCH — expected={expectedSha256}, actual={actualHash}. Aborting update.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                log($"Self-update: SHA-256 verification failed — {ex.Message}. Aborting update.");
                return false;
            }
        }

        /// <summary>
        /// Compares two version strings. Returns true if latest is newer than current.
        /// Strips SemVer suffixes (+metadata, -prerelease) before parsing because
        /// System.Version cannot handle them (e.g. "1.0.386+b7f8d3c..." would fail).
        /// </summary>
        private static bool IsNewerVersion(string current, string latest)
        {
            return CompareVersions(current, latest) > 0;
        }

        /// <summary>
        /// Compares two version strings. Returns negative when latest is older than current,
        /// zero when equal, positive when latest is newer than current. Returns
        /// <see cref="int.MinValue"/> when either side is not a parseable <see cref="Version"/>
        /// (callers must treat this as "cannot compare" rather than an ordered result).
        /// Strips SemVer suffixes (+metadata, -prerelease) before parsing because
        /// System.Version cannot handle them.
        /// </summary>
        internal static int CompareVersions(string current, string latest)
        {
            if (!Version.TryParse(StripVersionSuffix(current), out var currentVer))
                return int.MinValue;
            if (!Version.TryParse(StripVersionSuffix(latest), out var latestVer))
                return int.MinValue;

            return latestVer.CompareTo(currentVer);
        }

        private static string StripVersionSuffix(string version)
        {
            if (string.IsNullOrEmpty(version))
                return version;

            var plusIndex = version.IndexOf('+');
            if (plusIndex >= 0)
                version = version.Substring(0, plusIndex);

            var dashIndex = version.IndexOf('-');
            if (dashIndex >= 0)
                version = version.Substring(0, dashIndex);

            return version;
        }

        /// <summary>
        /// Downloads the agent ZIP to a temp path. Returns false on failure.
        /// </summary>
        private static async Task<bool> DownloadZipAsync(string zipPath, int timeoutMs, Action<string> log)
        {
            try
            {
                var zipUrl = $"{Constants.AgentBlobBaseUrl}/{Constants.AgentZipFileNameForLine(2)}";

                // Clean up any previous download (fragment from aborted run, or symlink/junction).
                // Pre-delete + FileMode.CreateNew below: CreateNew refuses to open an existing
                // entry (incl. reparse points), so a planted junction surviving the Delete fails
                // fast instead of letting the write follow the reparse target.
                if (File.Exists(zipPath))
                    File.Delete(zipPath);

                using (var handler = new HttpClientHandler())
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) })
                {
                    using (var response = await client.GetAsync(zipUrl))
                    {
                        response.EnsureSuccessStatusCode();
                        using (var fileStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            await response.Content.CopyToAsync(fileStream);
                        }
                    }
                }

                log($"Self-update: ZIP downloaded ({new FileInfo(zipPath).Length / 1024}KB)");
                return true;
            }
            catch (TaskCanceledException)
            {
                log($"Self-update: ZIP download timed out ({timeoutMs}ms) — aborting update");
                try { File.Delete(zipPath); } catch { }
                return false;
            }
            catch (Exception ex)
            {
                log($"Self-update: ZIP download failed — {ex.Message}");
                try { File.Delete(zipPath); } catch { }
                return false;
            }
        }

        /// <summary>
        /// Extracts the ZIP to the staging directory. Returns false on failure.
        ///
        /// Zip-Slip hardening: pre-KB-4534974 .NET Framework 4.8 patch levels honour ".." path
        /// segments in <see cref="ZipArchiveEntry.FullName"/>, so a malicious ZIP could escape
        /// the staging dir (e.g. ..\..\System32\evil.dll). We iterate entries manually and
        /// reject any whose resolved absolute path falls outside the staging root.
        /// </summary>
        private static bool ExtractToStaging(string zipPath, string stagingDir, Action<string> log)
        {
            try
            {
                // Clean up any previous staging
                if (Directory.Exists(stagingDir))
                    Directory.Delete(stagingDir, true);
                Directory.CreateDirectory(stagingDir);

                // Normalise staging root for path-traversal validation. The trailing separator is
                // required so "C:\stage" does not falsely match "C:\stage-evil\…".
                var stagingRoot = Path.GetFullPath(stagingDir);
                if (!stagingRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    stagingRoot += Path.DirectorySeparatorChar;

                int fileCount = 0;
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        // Reject absolute paths up-front; Path.Combine would otherwise honour them
                        // and discard the staging prefix.
                        if (Path.IsPathRooted(entry.FullName))
                        {
                            log($"Self-update: ZIP extraction rejected — entry has absolute path: {entry.FullName}");
                            return false;
                        }

                        var destFullPath = Path.GetFullPath(Path.Combine(stagingDir, entry.FullName));
                        if (!destFullPath.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            log($"Self-update: ZIP extraction rejected — entry escapes staging dir: {entry.FullName}");
                            return false;
                        }

                        // Directory entries (zip convention: name ends with "/")
                        if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                            entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                        {
                            Directory.CreateDirectory(destFullPath);
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(destFullPath));
                        entry.ExtractToFile(destFullPath, overwrite: false);
                        fileCount++;
                    }
                }

                log($"Self-update: ZIP extracted to staging ({fileCount} files)");
                return true;
            }
            catch (Exception ex)
            {
                log($"Self-update: ZIP extraction failed — {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Swaps files from staging into the agent directory.
        /// For locked files: rename to .old (Windows allows renaming locked files), then copy new.
        /// Returns false if the swap fails critically.
        /// </summary>
        private static bool SwapFiles(string agentDir, string stagingDir, Action<string> log)
        {
            try
            {
                var stagedFiles = Directory.GetFiles(stagingDir);
                int swapped = 0;

                foreach (var stagedFile in stagedFiles)
                {
                    var fileName = Path.GetFileName(stagedFile);
                    var targetPath = Path.Combine(agentDir, fileName);

                    try
                    {
                        if (File.Exists(targetPath))
                        {
                            // Try direct overwrite first
                            try
                            {
                                File.Copy(stagedFile, targetPath, overwrite: true);
                                swapped++;
                                continue;
                            }
                            catch (IOException)
                            {
                                // File is locked — use rename trick
                            }

                            // Rename locked file to .old (Windows allows this even for locked files)
                            var oldPath = targetPath + OldFileSuffix;
                            if (File.Exists(oldPath))
                            {
                                try { File.Delete(oldPath); } catch { }
                            }

                            File.Move(targetPath, oldPath);
                        }

                        File.Copy(stagedFile, targetPath);
                        swapped++;
                    }
                    catch (Exception ex)
                    {
                        log($"Self-update: failed to swap {fileName} — {ex.Message}");
                        // Continue with other files; partial update is better than no update
                        // for non-critical files (configs, etc.)
                    }
                }

                log($"Self-update: swapped {swapped}/{stagedFiles.Length} files");

                // Critical check: the main exe must have been swapped
                var mainExe = Path.Combine(agentDir, "AutopilotMonitor.Agent.exe");
                if (!File.Exists(mainExe))
                {
                    log("Self-update: CRITICAL — main exe missing after swap, aborting");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                log($"Self-update: file swap failed — {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Restarts the agent using PowerShell Wait-Process (same pattern as SummaryDialog.SelfCleanup).
        /// Wait-Process uses OS handles — zero polling, exits within milliseconds of process termination.
        /// <para>
        /// Exit strategy: when the runtime host has wired <see cref="RequestGracefulShutdown"/>
        /// (orchestrator live), the restart routes through the host's graceful shutdown — the
        /// script's Wait-Process holds until the process exits on its own, so we spend part of
        /// its 30s budget on a REAL drain instead of the ~2-3s ProcessExit window an inline
        /// <c>Environment.Exit</c> would leave. A bounded hard-exit fallback
        /// (<see cref="GracefulExitFallbackMs"/>) guarantees the update still applies if the
        /// shutdown pipeline ever hangs. Startup-phase updates (hook not set) keep the
        /// original immediate exit — no orchestrator exists yet, nothing to drain.
        /// </para>
        /// </summary>
        private static void RestartAgent(string agentDir, Action<string> log)
        {
            var pid = Process.GetCurrentProcess().Id;
            var agentExePath = Path.Combine(agentDir, "AutopilotMonitor.Agent.exe");

            // PowerShell single-quoted strings only need ' escaped as ''. Defends against
            // paths containing apostrophes (redirected ProgramData, OEM-customized roots);
            // without escaping such a path breaks out of the string literal in EncodedCommand.
            var safeAgentExePath = agentExePath.Replace("'", "''");

            // PowerShell Wait-Process: waits for our process to actually exit (no polling),
            // then immediately starts the new agent. 30s timeout as safety net — the graceful
            // path below must stay well inside it (see GracefulExitFallbackMs doc).
            var psScript = $"Wait-Process -Id {pid} -Timeout 30 -ErrorAction SilentlyContinue; " +
                           $"& '{safeAgentExePath}'";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));

            var psi = new ProcessStartInfo
            {
                FileName = SystemPaths.PowerShell,
                Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi);

            if (TryInitiateGracefulShutdown(log))
            {
                log($"Self-update: restart script launched — graceful shutdown initiated, " +
                    $"hard-exit fallback in {GracefulExitFallbackMs / 1000}s.");

                // Normal path: the host loop unblocks, its finally runs the full
                // TerminationPipeline (drain, orchestrator.Stop, spool flush) and the process
                // exits before this sleep completes — Wait-Process picks that up instantly.
                // The sleep only ever finishes when the shutdown pipeline hangs.
                System.Threading.Thread.Sleep(GracefulExitFallbackMs);
                log("Self-update: graceful shutdown did not complete in time — forcing exit.");
            }
            else
            {
                log("Self-update: restart script launched, exiting current process");
            }

            Environment.Exit(0);
        }

        /// <summary>
        /// Invokes <see cref="RequestGracefulShutdown"/> fail-safe. Extracted from
        /// <see cref="RestartAgent"/> so the hook contract is unit-testable without touching
        /// <c>Environment.Exit</c>. Returns <c>true</c> only when the hook exists, did not
        /// throw, and reported that a graceful shutdown was initiated.
        /// </summary>
        internal static bool TryInitiateGracefulShutdown(Action<string> log)
        {
            var requestShutdown = RequestGracefulShutdown;
            if (requestShutdown == null)
            {
                return false;
            }

            try
            {
                return requestShutdown();
            }
            catch (Exception ex)
            {
                // Hook may race the host's own shutdown (cleared/disposed machinery) —
                // fall back to the immediate exit, which is safe once the host is gone.
                log?.Invoke($"Self-update: graceful-shutdown hook threw ({ex.Message}) — falling back to immediate exit.");
                return false;
            }
        }

        /// <summary>
        /// Writes a small JSON marker so the next agent startup can emit an agent_version_check event
        /// with outcome=updated. Best-effort — failure here must not block the update. Includes phase
        /// timings, trigger reason, and exit timestamp so the downstream event can carry aggregable
        /// telemetry (downtime, total update duration, etc.).
        /// </summary>
        private static void WriteSelfUpdateMarker(
            string previousVersion,
            string newVersion,
            string triggerReason,
            long versionCheckMs,
            long downloadMs,
            long zipSizeBytes,
            long verifyMs,
            long extractMs,
            long swapMs,
            long totalUpdateMs,
            Action<string> log)
        {
            try
            {
                var markerPath = Environment.ExpandEnvironmentVariables(Constants.SelfUpdateMarkerFile);
                var dir = Path.GetDirectoryName(markerPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var now = DateTime.UtcNow;
                var payload = new JObject
                {
                    ["outcome"]         = "updated",
                    ["previousVersion"] = previousVersion ?? "unknown",
                    ["newVersion"]      = newVersion ?? "unknown",
                    ["triggerReason"]   = triggerReason ?? "startup",
                    ["updatedAtUtc"]    = now.ToString("O"),
                    // exitAtUtc is written here — actual Environment.Exit happens a few hundred ms later
                    // via PowerShell Wait-Process, which is close enough for downtime telemetry.
                    ["exitAtUtc"]       = now.ToString("O"),
                    ["versionCheckMs"]  = versionCheckMs,
                    ["downloadMs"]      = downloadMs,
                    ["zipSizeBytes"]    = zipSizeBytes,
                    ["verifyMs"]        = verifyMs,
                    ["extractMs"]       = extractMs,
                    ["swapMs"]          = swapMs,
                    ["totalUpdateMs"]   = totalUpdateMs
                };

                File.WriteAllText(markerPath, payload.ToString(Newtonsoft.Json.Formatting.None));
                log("Self-update: marker file written for next-startup event");
            }
            catch (Exception ex)
            {
                log($"Self-update: could not write marker file — {ex.Message} (non-critical)");
            }
        }

        /// <summary>
        /// Writes a small JSON marker so the next agent startup can emit an agent_version_check event
        /// with outcome=skipped (or check_failed if the version-v2.json fetch itself failed). Only called
        /// on the startup trigger path (runtime-triggered failures are logged normally because the full
        /// logger is already up). Best-effort — never throws.
        /// </summary>
        private static void WriteSkipMarker(string reason, string currentVersion, string latestVersion, string errorDetail, Action<string> log)
        {
            try
            {
                var markerPath = Environment.ExpandEnvironmentVariables(Constants.SelfUpdateSkippedMarkerFile);
                var dir = Path.GetDirectoryName(markerPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var detail = errorDetail ?? string.Empty;
                if (detail.Length > 200) detail = detail.Substring(0, 200);

                // version_check_failed = we never got a latestVersion; everything else = check succeeded
                // but the download/verify/extract/swap step failed.
                var outcome = string.Equals(reason, "version_check_failed", StringComparison.Ordinal)
                    ? "check_failed"
                    : "skipped";

                var payload = new JObject
                {
                    ["outcome"]        = outcome,
                    ["reason"]         = reason ?? "unknown",
                    ["currentVersion"] = currentVersion ?? "unknown",
                    ["latestVersion"]  = latestVersion,   // may be null if version check failed
                    ["skippedAtUtc"]   = DateTime.UtcNow.ToString("O"),
                    ["errorDetail"]    = detail
                };

                File.WriteAllText(markerPath, payload.ToString(Newtonsoft.Json.Formatting.None));
                log($"Self-update: skip marker written (outcome={outcome}, reason={reason})");
            }
            catch (Exception ex)
            {
                log($"Self-update: could not write skip marker — {ex.Message} (non-critical)");
            }
        }

        /// <summary>
        /// Writes a downgrade-blocked marker so the next agent startup can emit an
        /// <c>agent_version_check</c> event with <c>outcome=downgrade_blocked</c>. Uses the
        /// existing skipped-marker channel so <see cref="Core.Monitoring.Core.VersionCheckEventBuilder"/>
        /// picks it up alongside other skip reasons. Best-effort — never throws.
        /// </summary>
        private static void WriteDowngradeBlockedMarker(string currentVersion, string latestVersion, string triggerReason, Action<string> log)
        {
            try
            {
                var markerPath = Environment.ExpandEnvironmentVariables(Constants.SelfUpdateSkippedMarkerFile);
                var dir = Path.GetDirectoryName(markerPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var payload = new JObject
                {
                    ["outcome"]        = "downgrade_blocked",
                    ["reason"]         = "downgrade_blocked",
                    ["currentVersion"] = currentVersion ?? "unknown",
                    ["latestVersion"]  = latestVersion ?? "unknown",
                    ["triggerReason"]  = triggerReason ?? "unknown",
                    ["skippedAtUtc"]   = DateTime.UtcNow.ToString("O"),
                    ["errorDetail"]    = string.Empty
                };

                File.WriteAllText(markerPath, payload.ToString(Newtonsoft.Json.Formatting.None));
                log($"Self-update: downgrade-blocked marker written (current={currentVersion}, latest={latestVersion}, trigger={triggerReason})");
            }
            catch (Exception ex)
            {
                log($"Self-update: could not write downgrade-blocked marker — {ex.Message} (non-critical)");
            }
        }

        /// <summary>
        /// Writes a small JSON marker on the happy path (current version is already up to date) so
        /// the next agent startup can emit an agent_version_check event with outcome=up_to_date.
        /// Subject to session-scoped dedup by the MonitoringService emitter. Best-effort — never throws.
        /// </summary>
        private static void WriteCheckedMarker(string currentVersion, string latestVersion, long versionCheckMs, Action<string> log)
        {
            try
            {
                var markerPath = Environment.ExpandEnvironmentVariables(Constants.SelfUpdateCheckedMarkerFile);
                var dir = Path.GetDirectoryName(markerPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var payload = new JObject
                {
                    ["outcome"]        = "up_to_date",
                    ["currentVersion"] = currentVersion ?? "unknown",
                    ["latestVersion"]  = latestVersion ?? "unknown",
                    ["versionCheckMs"] = versionCheckMs,
                    ["checkedAtUtc"]   = DateTime.UtcNow.ToString("O")
                };

                File.WriteAllText(markerPath, payload.ToString(Newtonsoft.Json.Formatting.None));
                log("Self-update: checked marker written (up_to_date) for next-startup event");
            }
            catch (Exception ex)
            {
                log($"Self-update: could not write checked marker — {ex.Message} (non-critical)");
            }
        }

        /// <summary>
        /// Cleans up staging directory and temp ZIP file.
        /// </summary>
        private static void CleanupStaging(string stagingDir, string zipPath)
        {
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
        }

        /// <summary>
        /// Appends a log line to the main agent log file (logger isn't initialized yet at update time).
        /// Uses the same date-based naming and format as AgentLogger: agent_YYYYMMDD.log
        /// </summary>
        private static void LogToFile(string message)
        {
            try
            {
                var logDir = Environment.ExpandEnvironmentVariables(Constants.LogDirectory);
                Directory.CreateDirectory(logDir);
                var logFileName = $"agent_{DateTime.Now:yyyyMMdd}.log";
                var logPath = Path.Combine(logDir, logFileName);
                File.AppendAllText(logPath, $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [INFO] {message}{Environment.NewLine}");
            }
            catch
            {
                // Best-effort logging — never block on log failure
            }
        }
    }
}

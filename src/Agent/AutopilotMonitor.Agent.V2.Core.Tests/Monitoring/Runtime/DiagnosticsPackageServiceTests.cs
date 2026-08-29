#nullable enable
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Runtime
{
    /// <summary>
    /// PR1-B: diagnostics archive must include AgentState/, AgentSpool/, and top-level
    /// completion markers (whiteglove.complete, clean-exit) — V1 sessions had only AgentLogs/
    /// and ImeLogs/, which left forensics blind to decision-engine state and pending uploads.
    /// </summary>
    public sealed class DiagnosticsPackageServiceTests
    {
        private static AgentConfiguration Cfg(string sessionId = "S1") => new AgentConfiguration
        {
            SessionId = sessionId,
            TenantId = "T1",
            ApiBaseUrl = "http://localhost",
        };

        private sealed class Rig : System.IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public TempDirectory LogsTmp { get; } = new TempDirectory();
            public TempDirectory ImeTmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }

            public string DataFolder => Tmp.Path;
            public string StateFolder { get; }
            public string SpoolFolder { get; }

            public Rig()
            {
                Logger = new AgentLogger(LogsTmp.Path);
                StateFolder = Path.Combine(Tmp.Path, "State");
                SpoolFolder = Path.Combine(Tmp.Path, "Spool");
                Directory.CreateDirectory(StateFolder);
                Directory.CreateDirectory(SpoolFolder);
            }

            public DiagnosticsPackageService Build(
                System.Action<AgentConfiguration>? mutateConfig = null,
                IReadOnlyDictionary<string, string>? sectionFolderOverrides = null,
                System.Func<bool>? devicePreparationProbe = null)
            {
                // BackendApiClient is required by the public ctor but BuildArchiveBytes
                // never touches it — construct with a throwaway HttpClient. Tests only
                // exercise BuildArchiveBytes, which returns before any HTTP traffic.
                var apiClient = new BackendApiClient(
                    httpClient: new System.Net.Http.HttpClient(),
                    baseUrl: "http://localhost",
                    manufacturer: string.Empty,
                    model: string.Empty,
                    serialNumber: string.Empty,
                    useBootstrapTokenAuth: false,
                    bootstrapToken: null,
                    agentVersion: "0.0.0",
                    logger: Logger);
                var cfg = Cfg();
                mutateConfig?.Invoke(cfg);
                return new DiagnosticsPackageService(
                    cfg,
                    Logger,
                    apiClient,
                    agentLogFolderOverride: LogsTmp.Path,
                    imeLogFolderOverride: ImeTmp.Path,
                    agentStateFolderOverride: StateFolder,
                    agentSpoolFolderOverride: SpoolFolder,
                    agentDataFolderOverride: DataFolder,
                    sectionFolderOverrides: sectionFolderOverrides,
                    devicePreparationProbe: devicePreparationProbe);
            }

            public void Dispose()
            {
                Tmp.Dispose();
                LogsTmp.Dispose();
                ImeTmp.Dispose();
            }
        }

        [Fact]
        public void BuildArchiveBytes_includes_state_files_under_AgentState_prefix()
        {
            using var rig = new Rig();
            File.WriteAllText(Path.Combine(rig.StateFolder, "snapshot.json"), "{\"stage\":\"Completed\"}");
            File.WriteAllText(Path.Combine(rig.StateFolder, "journal.jsonl"), "{\"ord\":1}\n");
            File.WriteAllText(Path.Combine(rig.StateFolder, "signal-log.jsonl"), "{\"ord\":1}\n");
            File.WriteAllText(Path.Combine(rig.StateFolder, "ime-tracker-state.json"), "{}");
            File.WriteAllText(Path.Combine(rig.StateFolder, "enrollment-complete.marker"), "");
            File.WriteAllText(Path.Combine(rig.StateFolder, "final-status.json"), "{\"outcome\":\"Succeeded\"}");

            var bytes = rig.Build().BuildArchiveBytes(enrollmentSucceeded: true);

            var entries = ZipEntryNames(bytes);
            Assert.Contains("AgentState/snapshot.json", entries);
            Assert.Contains("AgentState/journal.jsonl", entries);
            Assert.Contains("AgentState/signal-log.jsonl", entries);
            Assert.Contains("AgentState/ime-tracker-state.json", entries);
            Assert.Contains("AgentState/enrollment-complete.marker", entries);
            Assert.Contains("AgentState/final-status.json", entries);
        }

        [Fact]
        public void BuildArchiveBytes_includes_spool_files_under_AgentSpool_prefix()
        {
            using var rig = new Rig();
            File.WriteAllText(Path.Combine(rig.SpoolFolder, "spool.jsonl"), "{\"itemId\":\"a\"}\n");
            File.WriteAllText(Path.Combine(rig.SpoolFolder, "upload-cursor.json"), "{\"lastItemId\":\"a\"}");

            var bytes = rig.Build().BuildArchiveBytes(enrollmentSucceeded: true);

            var entries = ZipEntryNames(bytes);
            Assert.Contains("AgentSpool/spool.jsonl", entries);
            Assert.Contains("AgentSpool/upload-cursor.json", entries);
        }

        [Fact]
        public void BuildArchiveBytes_includes_top_level_markers_under_AgentMarkers_prefix()
        {
            using var rig = new Rig();
            // Top-level markers in the data folder (NOT under State/).
            File.WriteAllText(Path.Combine(rig.DataFolder, "whiteglove.complete"), "");
            File.WriteAllText(Path.Combine(rig.DataFolder, "agent_clean_exit.marker"), "");

            var bytes = rig.Build().BuildArchiveBytes(enrollmentSucceeded: true);

            var entries = ZipEntryNames(bytes);
            Assert.Contains("AgentMarkers/whiteglove.complete", entries);
            Assert.Contains("AgentMarkers/agent_clean_exit.marker", entries);
        }

        // ============================================================ package manifest ====
        // The agent-log snapshot is zipped BEFORE packaging finishes, so packaging problems
        // are invisible in the uploaded archive's own log (field case: missing evtx in
        // sessions a11102f4/3ae7528b). The manifest travels INSIDE the ZIP instead.

        private static string? ReadZipEntryText(byte[] bytes, string entryName)
        {
            using var ms = new MemoryStream(bytes);
            using var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);
            var entry = archive.GetEntry(entryName);
            if (entry == null) return null;
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }

        [Fact]
        public void BuildArchiveBytes_always_writes_package_manifest_with_added_entries()
        {
            using var rig = new Rig();
            File.WriteAllText(Path.Combine(rig.StateFolder, "snapshot.json"), "{}");

            var bytes = rig.Build().BuildArchiveBytes(enrollmentSucceeded: true);

            var manifest = ReadZipEntryText(bytes, "package-manifest.txt");
            Assert.NotNull(manifest);
            Assert.Contains("ADDED: AgentState/snapshot.json", manifest);
            // Empty standard folders (e.g. ImeLogs) surface as NO MATCH lines — the silent
            // nothing-found case is exactly what the manifest exists to make visible.
            Assert.Contains("NO MATCH:", manifest);
        }

        [Fact]
        public void BuildArchiveBytes_manifest_records_guard_blocked_additional_path()
        {
            using var rig = new Rig();
            var blockedPath = @"C:\Windows\System32\config\SYSTEM";

            var bytes = rig.Build(cfg => cfg.DiagnosticsLogPaths.Add(
                new AutopilotMonitor.Shared.Models.DiagnosticsLogPath { Path = blockedPath }))
                .BuildArchiveBytes(enrollmentSucceeded: true);

            var manifest = ReadZipEntryText(bytes, "package-manifest.txt");
            Assert.NotNull(manifest);
            Assert.Contains($"BLOCKED (path guard): {blockedPath}", manifest);
        }

        [Fact]
        public void BuildArchiveBytes_manifest_records_users_path_blocked_even_in_unrestricted()
        {
            using var rig = new Rig();
            // The test temp dir lives under C:\Users — blocked by the always-on privacy guard
            // even in unrestricted mode. The manifest must say so instead of staying silent.
            var configuredPath = Path.Combine(rig.DataFolder, "does-not-exist.evtx");

            var bytes = rig.Build(cfg =>
                {
                    cfg.UnrestrictedMode = true;
                    cfg.DiagnosticsLogPaths.Add(new AutopilotMonitor.Shared.Models.DiagnosticsLogPath { Path = configuredPath });
                })
                .BuildArchiveBytes(enrollmentSucceeded: true);

            var manifest = ReadZipEntryText(bytes, "package-manifest.txt");
            Assert.NotNull(manifest);
            Assert.Contains($"BLOCKED (path guard): {configuredPath}", manifest);
        }

        [Fact]
        public void BuildArchiveBytes_excludes_top_level_session_id_and_bootstrap_config()
        {
            // session.id / bootstrap.json / await-enrollment.json live in the same data folder
            // but are NOT forensic state — keep them out of the archive.
            using var rig = new Rig();
            File.WriteAllText(Path.Combine(rig.DataFolder, "session.id"), "S1");
            File.WriteAllText(Path.Combine(rig.DataFolder, "bootstrap.json"), "{}");

            var bytes = rig.Build().BuildArchiveBytes(enrollmentSucceeded: true);

            var entries = ZipEntryNames(bytes);
            Assert.DoesNotContain(entries, e => e.EndsWith("session.id"));
            Assert.DoesNotContain(entries, e => e.EndsWith("bootstrap.json"));
        }

        [Fact]
        public void BuildArchiveBytes_includes_state_subfolders_when_quarantine_present()
        {
            using var rig = new Rig();
            var quarantine = Path.Combine(rig.StateFolder, ".quarantine");
            Directory.CreateDirectory(quarantine);
            File.WriteAllText(Path.Combine(quarantine, "corrupt.jsonl"), "garbage");

            var bytes = rig.Build().BuildArchiveBytes(enrollmentSucceeded: false);

            var entries = ZipEntryNames(bytes);
            Assert.Contains(entries, e => e.StartsWith("AgentState/.quarantine/") && e.EndsWith("corrupt.jsonl"));
        }

        [Fact]
        public void BuildArchiveBytes_includes_whiteglove_part1_archive_subfolder()
        {
            // Lock-test for the WG Part-2 forensic flow: when StateArchiver has moved
            // Part-1 reducer state into a `.part1-<utc>/` bucket on Part-2 boot, the
            // diagnostics package must carry that bucket along (snapshot/signal-log/
            // journal/reason). Relies on the AgentState section running with
            // includeSubfolders:true and StateFilePatterns covering *.json/*.jsonl/*.txt.
            using var rig = new Rig();
            var bucket = Path.Combine(rig.StateFolder, ".part1-20260504T120000000Z");
            Directory.CreateDirectory(bucket);
            File.WriteAllText(Path.Combine(bucket, "snapshot.json"), "{\"stage\":\"WhiteGloveSealed\"}");
            File.WriteAllText(Path.Combine(bucket, "signal-log.jsonl"), "{\"sig\":1}\n");
            File.WriteAllText(Path.Combine(bucket, "journal.jsonl"), "{\"jrn\":1}\n");
            File.WriteAllText(Path.Combine(bucket, "reason.txt"), "wg_part1_resume_archive");

            var bytes = rig.Build().BuildArchiveBytes(enrollmentSucceeded: true);

            var entries = ZipEntryNames(bytes);
            Assert.Contains("AgentState/.part1-20260504T120000000Z/snapshot.json", entries);
            Assert.Contains("AgentState/.part1-20260504T120000000Z/signal-log.jsonl", entries);
            Assert.Contains("AgentState/.part1-20260504T120000000Z/journal.jsonl", entries);
            Assert.Contains("AgentState/.part1-20260504T120000000Z/reason.txt", entries);
        }

        [Fact]
        public void BuildArchiveBytes_excludes_spool_subfolders()
        {
            // Spool may grow auxiliary subfolders later — current archive policy is to keep
            // the spool section flat (only what is pending upload right now).
            using var rig = new Rig();
            var sub = Path.Combine(rig.SpoolFolder, "archive");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "old.jsonl"), "{}");

            var bytes = rig.Build().BuildArchiveBytes(enrollmentSucceeded: false);

            var entries = ZipEntryNames(bytes);
            Assert.DoesNotContain(entries, e => e.StartsWith("AgentSpool/archive/"));
        }

        [Fact]
        public void BuildArchiveBytes_handles_missing_state_folder_gracefully()
        {
            using var rig = new Rig();
            Directory.Delete(rig.StateFolder, recursive: true);
            Directory.Delete(rig.SpoolFolder, recursive: true);

            // Should not throw; archive simply has no AgentState/AgentSpool entries.
            var bytes = rig.Build().BuildArchiveBytes(enrollmentSucceeded: true);
            Assert.NotEmpty(bytes);

            var entries = ZipEntryNames(bytes);
            Assert.Contains("sessioninfo.txt", entries);
            Assert.DoesNotContain(entries, e => e.StartsWith("AgentState/"));
            Assert.DoesNotContain(entries, e => e.StartsWith("AgentSpool/"));
        }

        // ===================================================== built-in catalog gates ====
        // The RealmJoin sections ride behind the tenant's RealmJoin Watcher toggle, the
        // Device Preparation bootstrapper event log behind the deterministic WDP marker.
        // Every conditional section is skipped WITH a manifest line, never silently.

        /// <summary>RealmJoin system-side folders redirected to temp (user-side goes via UserProfileResolver).</summary>
        private sealed class RealmJoinRig : System.IDisposable
        {
            public TempDirectory WindowsLogs { get; } = new TempDirectory();
            public TempDirectory Choco { get; } = new TempDirectory();
            public TempDirectory UserProfile { get; } = new TempDirectory();
            public string PackagesFolder => Path.Combine(WindowsLogs.Path, "RealmJoin");

            public RealmJoinRig()
            {
                File.WriteAllText(Path.Combine(WindowsLogs.Path, "realmjoin.log"), "rj");
                File.WriteAllText(Path.Combine(WindowsLogs.Path, "realmjoin2.log"), "rj2");
                var pkg = Path.Combine(PackagesFolder, "Packages", "generic-7zip");
                Directory.CreateDirectory(pkg);
                File.WriteAllText(Path.Combine(pkg, "install.log"), "pkg");
                var choco = Path.Combine(Choco.Path, "generic-7zip");
                Directory.CreateDirectory(choco);
                File.WriteAllText(Path.Combine(choco, "2026-06-30_install.log"), "choco");
                var userRj = Path.Combine(UserProfile.Path, "AppData", "Local", "RealmJoin");
                Directory.CreateDirectory(Path.Combine(userRj, "Logs", "generic-office"));
                File.WriteAllText(Path.Combine(userRj, "tray.log"), "tray");
                File.WriteAllText(Path.Combine(userRj, "config.pjson"), "not a log");
                File.WriteAllText(Path.Combine(userRj, "Logs", "RjImeHost.log"), "host");
                File.WriteAllText(Path.Combine(userRj, "Logs", "generic-office", "usersettings.log"), "pkg-user");
            }

            public IReadOnlyDictionary<string, string> Overrides => new Dictionary<string, string>
            {
                ["RealmJoinWindows"] = WindowsLogs.Path,
                ["RealmJoinPackages"] = PackagesFolder,
                ["RealmJoinChoco"] = Choco.Path,
            };

            public void Dispose()
            {
                WindowsLogs.Dispose();
                Choco.Dispose();
                UserProfile.Dispose();
            }
        }

        [Fact]
        public void BuildArchiveBytes_skips_realmjoin_sections_when_watcher_disabled()
        {
            using var rig = new Rig();
            using var rj = new RealmJoinRig();
            UserProfileResolver.SetForTesting(rj.UserProfile.Path);
            try
            {
                var bytes = rig.Build(sectionFolderOverrides: rj.Overrides).BuildArchiveBytes(enrollmentSucceeded: true);

                var entries = ZipEntryNames(bytes);
                Assert.DoesNotContain(entries, e => e.StartsWith("RealmJoinLogs/"));
                var manifest = ReadZipEntryText(bytes, "package-manifest.txt")!;
                Assert.Contains("SCENARIO: devicePreparation=False realmJoinWatcher=False", manifest);
                Assert.Contains("BUILT-IN SKIPPED (RealmJoin Watcher disabled): RealmJoinWindows", manifest);
                Assert.Contains("BUILT-IN SKIPPED (RealmJoin Watcher disabled): RealmJoinUserLogs", manifest);
            }
            finally
            {
                UserProfileResolver.Reset();
            }
        }

        [Fact]
        public void BuildArchiveBytes_includes_realmjoin_sections_when_watcher_enabled()
        {
            using var rig = new Rig();
            using var rj = new RealmJoinRig();
            UserProfileResolver.SetForTesting(rj.UserProfile.Path);
            try
            {
                var bytes = rig.Build(cfg => cfg.EnableRealmJoinWatcher = true, rj.Overrides)
                    .BuildArchiveBytes(enrollmentSucceeded: false);

                var entries = ZipEntryNames(bytes);
                // ZIP layout mirrors the disk layout: flat client logs + package tree under
                // Windows/, tray logs + per-user Logs tree under User/.
                Assert.Contains("RealmJoinLogs/Windows/realmjoin.log", entries);
                Assert.Contains("RealmJoinLogs/Windows/realmjoin2.log", entries);
                Assert.Contains("RealmJoinLogs/Windows/RealmJoin/Packages/generic-7zip/install.log", entries);
                Assert.Contains("RealmJoinLogs/Choco/generic-7zip/2026-06-30_install.log", entries);
                Assert.Contains("RealmJoinLogs/User/tray.log", entries);
                Assert.Contains("RealmJoinLogs/User/Logs/RjImeHost.log", entries);
                Assert.Contains("RealmJoinLogs/User/Logs/generic-office/usersettings.log", entries);
                // Only tray*.log is collected from the user RealmJoin root — config stays out.
                Assert.DoesNotContain(entries, e => e.EndsWith("config.pjson"));
                var manifest = ReadZipEntryText(bytes, "package-manifest.txt")!;
                Assert.Contains("BUILT-IN: RealmJoinUserLogs -> folder=", manifest);
                Assert.DoesNotContain("BUILT-IN SKIPPED (RealmJoin Watcher disabled)", manifest);
            }
            finally
            {
                UserProfileResolver.Reset();
            }
        }

        [Fact]
        public void BuildArchiveBytes_skips_realmjoin_user_sections_without_user_session()
        {
            using var rig = new Rig();
            using var rj = new RealmJoinRig();
            // Token present, no interactive user detected → user-profile sections skip with a
            // manifest line; the system-side sections are unaffected.
            UserProfileResolver.SetForTesting(null!);
            try
            {
                var bytes = rig.Build(cfg => cfg.EnableRealmJoinWatcher = true, rj.Overrides)
                    .BuildArchiveBytes(enrollmentSucceeded: true);

                var entries = ZipEntryNames(bytes);
                Assert.Contains("RealmJoinLogs/Windows/realmjoin.log", entries);
                Assert.DoesNotContain(entries, e => e.StartsWith("RealmJoinLogs/User"));
                var manifest = ReadZipEntryText(bytes, "package-manifest.txt")!;
                Assert.Contains("BUILT-IN SKIPPED (no user session for token): RealmJoinUserTray", manifest);
                Assert.Contains("BUILT-IN SKIPPED (no user session for token): RealmJoinUserLogs", manifest);
            }
            finally
            {
                UserProfileResolver.Reset();
            }
        }

        [Fact]
        public void BuildArchiveBytes_collects_bootstrapper_event_log_only_on_device_preparation()
        {
            using var rig = new Rig();
            using var winevt = new TempDirectory();
            // Synthetic evtx: no registered channel claims the file, so the export falls
            // back to a raw copy — the point here is the scenario gate, not wevtutil.
            File.WriteAllBytes(Path.Combine(winevt.Path, "BootstrapperAgentServiceLogProvider.evtx"), new byte[] { 1, 2, 3 });
            var overrides = new Dictionary<string, string> { ["ImeBootstrapperEventLog"] = winevt.Path };

            var classic = rig.Build(sectionFolderOverrides: overrides, devicePreparationProbe: () => false)
                .BuildArchiveBytes(enrollmentSucceeded: true);
            Assert.DoesNotContain("ImeLogs/BootstrapperAgentServiceLogProvider.evtx", ZipEntryNames(classic));
            Assert.Contains("BUILT-IN SKIPPED (not a Device Preparation enrollment): ImeBootstrapperEventLog",
                ReadZipEntryText(classic, "package-manifest.txt"));

            var wdp = rig.Build(sectionFolderOverrides: overrides, devicePreparationProbe: () => true)
                .BuildArchiveBytes(enrollmentSucceeded: true);
            Assert.Contains("ImeLogs/BootstrapperAgentServiceLogProvider.evtx", ZipEntryNames(wdp));
            Assert.Contains("SCENARIO: devicePreparation=True", ReadZipEntryText(wdp, "package-manifest.txt"));
        }

        [Fact]
        public void BuildArchiveBytes_treats_failing_device_preparation_probe_as_classic()
        {
            using var rig = new Rig();
            var bytes = rig.Build(devicePreparationProbe: () => throw new System.InvalidOperationException("registry boom"))
                .BuildArchiveBytes(enrollmentSucceeded: true);

            var manifest = ReadZipEntryText(bytes, "package-manifest.txt")!;
            Assert.Contains("SCENARIO PROBE FAILED (treated as Classic): registry boom", manifest);
            Assert.Contains("BUILT-IN SKIPPED (not a Device Preparation enrollment): ImeBootstrapperEventLog", manifest);
            Assert.Contains("sessioninfo.txt", ZipEntryNames(bytes)); // archive still built
        }

        [Theory]
        [InlineData(DiagnosticsSectionCondition.Always, false, false, true)]
        [InlineData(DiagnosticsSectionCondition.RealmJoinWatcher, false, true, false)]
        [InlineData(DiagnosticsSectionCondition.RealmJoinWatcher, true, false, true)]
        [InlineData(DiagnosticsSectionCondition.DevicePreparation, true, false, false)]
        [InlineData(DiagnosticsSectionCondition.DevicePreparation, false, true, true)]
        [InlineData((DiagnosticsSectionCondition)999, true, true, false)]
        public void IsSectionActive_gates_on_condition(DiagnosticsSectionCondition condition, bool watcher, bool wdp, bool expected)
        {
            var section = new DiagnosticsBuiltInSection("X", "X", @"C:\X", new[] { "*.log" }, false, "x", condition);
            var cfg = Cfg();
            cfg.EnableRealmJoinWatcher = watcher;

            Assert.Equal(expected, DiagnosticsPackageService.IsSectionActive(section, cfg, wdp));
        }

        private static string[] ZipEntryNames(byte[] zipBytes)
        {
            using var ms = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
            return archive.Entries.Select(e => e.FullName).ToArray();
        }

        private static string ZipEntryText(byte[] zipBytes, string entryName)
        {
            using var ms = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = archive.GetEntry(entryName);
            Assert.NotNull(entry);
            using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }

        // ── Nullable outcome (on-demand mid-session) — gate + sessioninfo semantics ──

        /// <summary>
        /// Sentinel subclass: throws before any HTTP so gate-pass-through is observable as a
        /// non-null error result (a skip returns null BEFORE the archive is ever built).
        /// </summary>
        private sealed class ThrowOnBuildService : DiagnosticsPackageService
        {
            public ThrowOnBuildService(AgentConfiguration cfg, AgentLogger logger, BackendApiClient api)
                : base(cfg, logger, api) { }
            internal override byte[] BuildArchiveBytes(bool? enrollmentSucceeded) =>
                throw new System.InvalidOperationException("gate-passed-sentinel");
        }

        private static DiagnosticsPackageService BuildGateProbe(AgentLogger logger, string mode)
        {
            var cfg = Cfg();
            cfg.DiagnosticsUploadEnabled = true;
            cfg.DiagnosticsUploadMode = mode;
            var apiClient = new BackendApiClient(
                httpClient: new System.Net.Http.HttpClient(),
                baseUrl: "http://localhost",
                manufacturer: string.Empty,
                model: string.Empty,
                serialNumber: string.Empty,
                useBootstrapTokenAuth: false,
                bootstrapToken: null,
                agentVersion: "0.0.0",
                logger: logger);
            return new ThrowOnBuildService(cfg, logger, apiClient);
        }

        [Fact]
        public async System.Threading.Tasks.Task CreateAndUploadAsync_null_outcome_passes_OnFailure_gate()
        {
            // On-demand mid-session (no outcome yet): the OnFailure gate must NOT skip —
            // there is no known success to skip on. Reaching the archive build (sentinel
            // throw → error result) proves the gate was passed; a skip would return Skipped.
            using var rig = new Rig();
            var result = await BuildGateProbe(rig.Logger, "OnFailure")
                .CreateAndUploadAsync(enrollmentSucceeded: null, fileNameSuffix: "server-requested");

            Assert.NotNull(result);
            Assert.Equal("gate-passed-sentinel", result!.ErrorCode);
        }

        [Fact]
        public async System.Threading.Tasks.Task CreateAndUploadAsync_known_success_still_skipped_by_OnFailure_gate()
        {
            using var rig = new Rig();
            var result = await BuildGateProbe(rig.Logger, "OnFailure")
                .CreateAndUploadAsync(enrollmentSucceeded: true);

            Assert.NotNull(result);
            Assert.True(result!.Skipped);
            Assert.False(result.Success);
            Assert.Equal(DiagnosticsUploadResult.SkipOnFailureSucceeded, result.ErrorCode);
        }

        [Fact]
        public async System.Threading.Tasks.Task CreateAndUploadAsync_null_outcome_still_blocked_by_mode_Off()
        {
            using var rig = new Rig();
            var result = await BuildGateProbe(rig.Logger, "Off")
                .CreateAndUploadAsync(enrollmentSucceeded: null);

            // The gate name travels in ErrorCode so the dispatcher's server_action_failed
            // event says "mode_off" instead of a generic "diagnostics_upload_failed".
            Assert.NotNull(result);
            Assert.True(result!.Skipped);
            Assert.Equal(DiagnosticsUploadResult.SkipModeOff, result.ErrorCode);
        }

        [Theory]
        [InlineData(true, "Succeeded")]
        [InlineData(false, "Failed")]
        [InlineData(null, "In Progress")]
        public void BuildArchiveBytes_sessioninfo_reflects_nullable_outcome(bool? outcome, string expected)
        {
            using var rig = new Rig();
            var bytes = rig.Build().BuildArchiveBytes(outcome);

            var sessionInfo = ZipEntryText(bytes, "sessioninfo.txt");
            Assert.Contains($"Enrollment Result: {expected}", sessionInfo);
        }

        // ── BuildBlobUploadUrl — destination-aware URL construction ─────────────────

        [Theory]
        [InlineData("Hosted")]
        [InlineData("hosted")]
        [InlineData("HOSTED")]
        public void BuildBlobUploadUrl_HostedDestination_ReturnsSasUnchanged(string destination)
        {
            // Hosted SAS is already blob-scoped at {tenantId}/{filename}; the agent must
            // PUT exactly to that URL. Appending the local filename would produce a
            // double-name URL like .../diagnostics/{tenantId}/{filename}/{filename}.
            const string hostedSas = "https://account.blob.core.windows.net/diagnostics/11111111-1111-1111-1111-111111111111/AgentDiagnostics-x.zip?sig=abc";
            var result = DiagnosticsPackageService.BuildBlobUploadUrl(hostedSas, "AgentDiagnostics-x.zip", destination);
            Assert.Equal(hostedSas, result);
        }

        [Fact]
        public void BuildBlobUploadUrl_CustomerSas_AppendsBlobNameBeforeQuery()
        {
            const string containerSas = "https://customer.blob.core.windows.net/diagnostics?sv=2024-10-04&sig=xyz";
            var result = DiagnosticsPackageService.BuildBlobUploadUrl(containerSas, "AgentDiagnostics-x.zip", "CustomerSas");
            Assert.Equal(
                "https://customer.blob.core.windows.net/diagnostics/AgentDiagnostics-x.zip?sv=2024-10-04&sig=xyz",
                result);
        }

        [Fact]
        public void BuildBlobUploadUrl_NullDestination_AppendsBlobName_LegacyBackendCompat()
        {
            // An older backend without the Destination field returns null. The agent must
            // preserve the historical container-SAS append behaviour so CustomerSas
            // uploads continue to work seamlessly after a backend rollout.
            const string containerSas = "https://customer.blob.core.windows.net/diag?sig=abc";
            var result = DiagnosticsPackageService.BuildBlobUploadUrl(containerSas, "diag.zip", null);
            Assert.Equal("https://customer.blob.core.windows.net/diag/diag.zip?sig=abc", result);
        }

        [Fact]
        public void BuildBlobUploadUrl_UnknownDestination_FallsBackToCustomerSasBehaviour()
        {
            // Defence-in-depth: an unrecognised destination string (server bug, manual
            // edit) must NOT silently treat the SAS as blob-scoped — the agent would PUT
            // to a container URL and Azure would reject. Append-blob-name is the safe
            // default and matches CustomerSas behaviour.
            const string containerSas = "https://customer.blob.core.windows.net/diag?sig=abc";
            var result = DiagnosticsPackageService.BuildBlobUploadUrl(containerSas, "diag.zip", "Vendor");
            Assert.EndsWith("/diag.zip?sig=abc", result);
        }

        [Fact]
        public void BuildBlobUploadUrl_SasWithoutQueryString_AppendsBlobName()
        {
            // Defensive path — SAS without `?` is unlikely in practice but the helper
            // mirrors the V1 behaviour for it. Confirms the no-query branch is taken.
            const string noQuery = "https://customer.blob.core.windows.net/diag";
            var result = DiagnosticsPackageService.BuildBlobUploadUrl(noQuery, "diag.zip", "CustomerSas");
            Assert.Equal("https://customer.blob.core.windows.net/diag/diag.zip", result);
        }

        [Fact]
        public void BuildBlobUploadUrl_HostedWithoutQueryString_StillReturnsUnchanged()
        {
            // Hosted SAS would always have `?sig=...` but the helper's branch order
            // means Hosted wins before the query-string check.
            const string hostedNoQuery = "https://account.blob.core.windows.net/diagnostics/tenant/x.zip";
            var result = DiagnosticsPackageService.BuildBlobUploadUrl(hostedNoQuery, "x.zip", "Hosted");
            Assert.Equal(hostedNoQuery, result);
        }

        private static string ReadEntry(byte[] zipBytes, string entryName)
        {
            using var ms = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = archive.Entries.First(e => e.FullName == entryName);
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }

        // Isolated rig for budget/cap tests: keeps the agent logger output OUT of the
        // archive scope so cap assertions are not contaminated by logger noise.
        // Test files are written to ContentDir (mapped to AgentLogs in the archive);
        // every other source folder override points at an empty dir.
        private sealed class BudgetRig : System.IDisposable
        {
            public TempDirectory LoggerDir { get; } = new TempDirectory();   // logger writes here, not enumerated
            public TempDirectory ContentDir { get; } = new TempDirectory();  // test writes go here → AgentLogs
            public TempDirectory EmptyDir { get; } = new TempDirectory();    // unused source folders
            public AgentLogger Logger { get; }

            public BudgetRig()
            {
                Logger = new AgentLogger(LoggerDir.Path);
            }

            public DiagnosticsPackageService Build(DiagnosticsBudget? budget = null)
            {
                var apiClient = new BackendApiClient(
                    httpClient: new System.Net.Http.HttpClient(),
                    baseUrl: "http://localhost",
                    manufacturer: string.Empty,
                    model: string.Empty,
                    serialNumber: string.Empty,
                    useBootstrapTokenAuth: false,
                    bootstrapToken: null,
                    agentVersion: "0.0.0",
                    logger: Logger);

                var svc = new DiagnosticsPackageService(
                    Cfg(),
                    Logger,
                    apiClient,
                    agentLogFolderOverride: ContentDir.Path,
                    imeLogFolderOverride: EmptyDir.Path,
                    agentStateFolderOverride: EmptyDir.Path,
                    agentSpoolFolderOverride: EmptyDir.Path,
                    agentDataFolderOverride: EmptyDir.Path);

                if (budget != null) svc.Budget = budget;
                return svc;
            }

            public void Dispose()
            {
                LoggerDir.Dispose();
                ContentDir.Dispose();
                EmptyDir.Dispose();
            }
        }

        [Fact]
        public void BuildArchiveBytes_skips_file_exceeding_per_file_cap()
        {
            using var rig = new BudgetRig();
            File.WriteAllBytes(Path.Combine(rig.ContentDir.Path, "huge.log"), new byte[5000]);
            File.WriteAllBytes(Path.Combine(rig.ContentDir.Path, "small.log"), new byte[500]);

            var svc = rig.Build(new DiagnosticsBudget
            {
                MaxSingleFileBytes = 1024,
                MaxTotalUncompressedBytes = 1024L * 1024 * 1024,
                MaxFileCount = 1000,
            });

            var bytes = svc.BuildArchiveBytes(enrollmentSucceeded: true);
            var entries = ZipEntryNames(bytes);

            Assert.DoesNotContain("AgentLogs/huge.log", entries);
            Assert.Contains("AgentLogs/small.log", entries);
            Assert.Contains("_TRUNCATED.txt", entries);

            var truncated = ReadEntry(bytes, "_TRUNCATED.txt");
            Assert.Contains("huge.log", truncated);
            Assert.Contains("size", truncated);
        }

        [Fact]
        public void BuildArchiveBytes_stops_at_total_cap()
        {
            using var rig = new BudgetRig();
            for (int i = 0; i < 10; i++)
                File.WriteAllBytes(Path.Combine(rig.ContentDir.Path, $"f{i:D2}.log"), new byte[1024]);

            var svc = rig.Build(new DiagnosticsBudget
            {
                MaxSingleFileBytes = 100L * 1024 * 1024,
                MaxTotalUncompressedBytes = 4096,
                MaxFileCount = 1000,
            });

            var bytes = svc.BuildArchiveBytes(enrollmentSucceeded: true);
            var entries = ZipEntryNames(bytes);

            var fileEntries = entries.Where(e => e.StartsWith("AgentLogs/")).ToArray();
            // 4096 / 1024 = 4 max files fit; subsequent files are skipped.
            Assert.Equal(4, fileEntries.Length);
            Assert.Contains("_TRUNCATED.txt", entries);
            var truncated = ReadEntry(bytes, "_TRUNCATED.txt");
            Assert.Contains("total", truncated);
        }

        [Fact]
        public void BuildArchiveBytes_stops_at_file_count_cap()
        {
            using var rig = new BudgetRig();
            for (int i = 0; i < 10; i++)
                File.WriteAllBytes(Path.Combine(rig.ContentDir.Path, $"f{i:D2}.log"), new byte[100]);

            var svc = rig.Build(new DiagnosticsBudget
            {
                MaxSingleFileBytes = 100L * 1024 * 1024,
                MaxTotalUncompressedBytes = 100L * 1024 * 1024,
                MaxFileCount = 3,
            });

            var bytes = svc.BuildArchiveBytes(enrollmentSucceeded: true);
            var entries = ZipEntryNames(bytes);

            var fileEntries = entries.Where(e => e.StartsWith("AgentLogs/")).ToArray();
            Assert.Equal(3, fileEntries.Length);
            Assert.Contains("_TRUNCATED.txt", entries);
            var truncated = ReadEntry(bytes, "_TRUNCATED.txt");
            Assert.Contains("count", truncated);
        }

        [Fact]
        public void BuildArchiveBytes_omits_truncated_marker_when_no_skips()
        {
            using var rig = new BudgetRig();
            File.WriteAllBytes(Path.Combine(rig.ContentDir.Path, "tiny.log"), new byte[100]);

            var svc = rig.Build();   // default budget: 100 MB / 500 MB / 5000

            var bytes = svc.BuildArchiveBytes(enrollmentSucceeded: true);
            var entries = ZipEntryNames(bytes);

            Assert.Contains("AgentLogs/tiny.log", entries);
            Assert.DoesNotContain("_TRUNCATED.txt", entries);
        }

        [Fact]
        public void IsReparsePoint_returns_true_for_reparse_attribute()
        {
            Assert.True(DiagnosticsPackageService.IsReparsePoint(FileAttributes.ReparsePoint));
            Assert.True(DiagnosticsPackageService.IsReparsePoint(FileAttributes.ReparsePoint | FileAttributes.Directory));
            Assert.True(DiagnosticsPackageService.IsReparsePoint(FileAttributes.ReparsePoint | FileAttributes.Hidden));
        }

        [Fact]
        public void IsReparsePoint_returns_false_for_normal_files()
        {
            Assert.False(DiagnosticsPackageService.IsReparsePoint(FileAttributes.Normal));
            Assert.False(DiagnosticsPackageService.IsReparsePoint(FileAttributes.Directory));
            Assert.False(DiagnosticsPackageService.IsReparsePoint(FileAttributes.ReadOnly | FileAttributes.Hidden));
            Assert.False(DiagnosticsPackageService.IsReparsePoint(FileAttributes.Archive));
        }

        // ========================================== handle-validated reads (TOCTOU) ====
        // Every guard up to here inspects a PATH: the config guard, the enumeration's reparse
        // skip, the per-file attribute check. A local user who can write under a recursive
        // source folder swaps a subdirectory for a junction between enumeration and open, and
        // the bytes would come from the junction target. The read is validated on the handle
        // that is actually copied, so the swapped file is skipped and the manifest says why.

        private static string AllZipEntryTexts(byte[] zipBytes)
        {
            using var ms = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
            var sb = new StringBuilder();
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                sb.AppendLine(reader.ReadToEnd());
            }
            return sb.ToString();
        }

        [Fact]
        public void BuildArchiveBytes_skips_file_whose_parent_became_a_junction_after_enumeration()
        {
            using var rig = new Rig();
            using var outside = new TempDirectory();
            var sub1 = Path.Combine(rig.StateFolder, "sub1");
            var sub2 = Path.Combine(rig.StateFolder, "sub2");
            Directory.CreateDirectory(sub1);
            Directory.CreateDirectory(sub2);
            File.WriteAllText(Path.Combine(sub1, "decoy.txt"), "decoy");
            File.WriteAllText(Path.Combine(sub2, "x.txt"), "harmless");
            File.WriteAllText(Path.Combine(outside.Path, "x.txt"), "OUTSIDE-THE-VALIDATED-FOLDER");

            var svc = rig.Build();
            var swapped = false;
            svc.BeforeSourceFileOpen = candidate =>
            {
                if (swapped || !candidate.EndsWith(Path.Combine("sub2", "x.txt"), System.StringComparison.OrdinalIgnoreCase))
                    return;
                // Enumeration is done and sub2 passed its reparse check — now it becomes a junction.
                Directory.Delete(sub2, recursive: true);
                NtfsLinks.CreateJunction(sub2, outside.Path);
                swapped = true;
            };

            try
            {
                var bytes = svc.BuildArchiveBytes(enrollmentSucceeded: true);

                Assert.True(swapped, "race seam never fired for sub2/x.txt");
                var entries = ZipEntryNames(bytes);
                Assert.Contains("AgentState/sub1/decoy.txt", entries);
                Assert.DoesNotContain("AgentState/sub2/x.txt", entries);
                Assert.DoesNotContain("OUTSIDE-THE-VALIDATED-FOLDER", AllZipEntryTexts(bytes));

                var manifest = ReadZipEntryText(bytes, "package-manifest.txt");
                Assert.NotNull(manifest);
                Assert.Contains("SKIPPED (resolved outside validated folder)", manifest!);
                Assert.Contains("ADDED: AgentState/sub1/decoy.txt", manifest!);
            }
            finally
            {
                NtfsLinks.RemoveLink(sub2);
            }
        }

        [Fact]
        public void BuildArchiveBytes_rejects_source_folder_that_is_a_junction()
        {
            using var rig = new Rig();
            using var outside = new TempDirectory();
            File.WriteAllText(Path.Combine(outside.Path, "snapshot.json"), "{\"leak\":true}");
            var link = Path.Combine(rig.Tmp.Path, "StateLink");
            NtfsLinks.CreateJunction(link, outside.Path);

            try
            {
                var bytes = rig.Build(sectionFolderOverrides: new Dictionary<string, string> { ["AgentState"] = link })
                    .BuildArchiveBytes(enrollmentSucceeded: true);

                var entries = ZipEntryNames(bytes);
                Assert.DoesNotContain(entries, e => e.StartsWith("AgentState/"));
                var manifest = ReadZipEntryText(bytes, "package-manifest.txt");
                Assert.NotNull(manifest);
                Assert.Contains("FOLDER REJECTED (reparse point)", manifest!);
            }
            finally
            {
                NtfsLinks.RemoveLink(link);
            }
        }
    }
}

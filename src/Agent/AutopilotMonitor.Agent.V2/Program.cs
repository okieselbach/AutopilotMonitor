using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Runtime;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Termination;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2
{
    /// <summary>
    /// V2-Agent entry point. Plan §4.x M4.5.b + M4.6.α.
    /// <para>
    /// Boot sequence (same order as Legacy for feature parity):
    /// </para>
    /// <list type="number">
    ///   <item><c>--help</c> / <c>--version</c> short-circuit</item>
    ///   <item><c>--install</c> forks to <see cref="RunInstallMode"/> and exits</item>
    ///   <item>Multi-instance guard — single-agent invariant</item>
    ///   <item>Register <c>ProcessExit</c> → writes <c>clean-exit.marker</c></item>
    ///   <item>Ensure agent directories exist</item>
    ///   <item><see cref="SelfUpdater.LogInit"/> + <see cref="SelfUpdater.CleanupPreviousUpdate"/></item>
    ///   <item>Load cached <c>remote-config.json</c> → <see cref="SelfUpdater.BackendExpectedSha256"/> + <c>AllowAgentDowngrade</c></item>
    ///   <item><see cref="SelfUpdater.CheckAndApplyUpdateAsync"/> — on success restarts the process; on failure continues with current binary</item>
    ///   <item><see cref="DetectPreviousExit"/> — reads markers + event log to classify last shutdown</item>
    ///   <item>Resolve TenantId (registry → bootstrap-config.json fallback)</item>
    ///   <item>Build <see cref="AgentConfiguration"/> (CLI args + persisted bootstrap / await-enrollment config)</item>
    ///   <item>Get/create SessionId via <see cref="SessionIdPersistence"/></item>
    ///   <item><see cref="CheckEnrollmentCompleteMarker"/> — file-based enrollment-complete-marker detection + cleanup retry</item>
    ///   <item><see cref="CheckSessionAgeEmergencyBreak"/> — absolute session-age watchdog</item>
    ///   <item>(Optional) Wait for MDM certificate in <c>--await-enrollment</c> mode</item>
    ///   <item>Build <see cref="BackendApiClient"/> + <see cref="RemoteConfigService"/> → fetch config</item>
    ///   <item><see cref="BootstrapConfigCleanup.TryDeleteIfCertReadyAsync"/> — H-2 mitigation post-cert</item>
    ///   <item>Build mTLS <see cref="HttpClient"/> → <see cref="BackendTelemetryUploader"/></item>
    ///   <item>Build <see cref="DefaultComponentFactory"/> + <see cref="EnrollmentOrchestrator"/></item>
    ///   <item><c>orchestrator.Start()</c> → emit <see cref="VersionCheckEventBuilder"/>-derived event</item>
    ///   <item>Wait for Ctrl+C / ProcessExit / EnrollmentTerminated</item>
    ///   <item><c>orchestrator.Stop()</c>, exit 0</item>
    /// </list>
    /// </summary>
    public static partial class Program
    {
        private const string DefaultStateDirectory = @"%ProgramData%\AutopilotMonitor";
        private const string DefaultLogDirectory = @"%ProgramData%\AutopilotMonitor\Logs";
        private const string DefaultAgentSubdirectory = "Agent";
        private const string DefaultStateSubdirectory = "State";
        private const string DefaultSpoolSubdirectory = "Spool";
        private const string CachedRemoteConfigPath = @"%ProgramData%\AutopilotMonitor\Config\remote-config.json";

        public static int Main(string[] args)
        {
            if (args.Contains("--help") || args.Contains("-h") || args.Contains("-?"))
            {
                PrintUsage();
                return 0;
            }

            if (args.Contains("--version"))
            {
                PrintVersion();
                return 0;
            }

            // --install forks to a separate flow that exits when done (never falls through into RunAgent).
            if (args.Contains("--install"))
            {
                return RunInstallMode(args);
            }

            // --run-gather-rules / --run-ime-matching: standalone diagnostic modes (M4.6.δ).
            // They neither touch the Scheduled Task nor the live agent's spool — safe to run
            // alongside a normal monitoring agent for troubleshooting.
            if (args.Contains("--run-gather-rules"))
            {
                return RunGatherRulesMode(args);
            }

            if (args.Contains("--run-ime-matching"))
            {
                return RunImeMatchingMode(args);
            }

            var consoleMode = args.Contains("--console") || Environment.UserInteractive;

            // Multi-instance guard — prevents a second agent process from running alongside one
            // that was started by the Scheduled Task.
            if (IsAnotherAgentInstanceRunning())
            {
                var msg = "Another agent process is already running. This instance will exit.";
                if (consoleMode) Console.Error.WriteLine($"ERROR: {msg}");
                try
                {
                    var earlyLogger = new AgentLogger(Environment.ExpandEnvironmentVariables(DefaultLogDirectory));
                    earlyLogger.Warning(msg);
                }
                catch { /* best-effort */ }
                return 1;
            }

            var dataDirectory = Environment.ExpandEnvironmentVariables(DefaultStateDirectory);
            var logDirectory = Environment.ExpandEnvironmentVariables(DefaultLogDirectory);

            try { Directory.CreateDirectory(dataDirectory); } catch { }
            try { Directory.CreateDirectory(logDirectory); } catch { }

            // Register the clean-exit marker writer BEFORE any risky startup work so an OS
            // shutdown during self-update still produces a "clean" classification.
            RegisterCleanExitMarker(dataDirectory);

            // Register crash-dump handlers as early as possible — before SelfUpdate, before
            // any other startup work — so an unhandled exception anywhere downstream still
            // produces a MiniDump + CrashRecord. SessionId/TenantId are unknown at this point;
            // they're filled in later by AgentRuntimeHost via a second RegisterHandlers call.
            AutopilotMonitor.Agent.V2.Core.Diagnostics.CrashDumpCapture.RegisterHandlers(
                programDataDirectory: dataDirectory,
                sessionId: "(pre-runtime)",
                tenantId: "(pre-runtime)",
                agentVersion: GetAgentVersion());

            // Startup self-update. Legacy parity: cleanup leftover .old files, load cached
            // backend hash / downgrade policy, then attempt the update. Failures here never
            // abort startup — we prefer to run the current version than delay.
            SelfUpdater.LogInit(GetAgentVersion());

            var agentDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            SelfUpdater.CleanupPreviousUpdate(agentDir, msg => { if (consoleMode) Console.Out.WriteLine(msg); });

            var allowAgentDowngrade = LoadCachedSelfUpdateContext();

            try
            {
                SelfUpdater.CheckAndApplyUpdateAsync(
                    currentVersion: GetAgentVersion(),
                    agentDir: agentDir,
                    consoleMode: consoleMode,
                    allowDowngrade: allowAgentDowngrade).GetAwaiter().GetResult();
            }
            catch (Exception selfUpdateEx)
            {
                // SelfUpdater is designed to swallow its own errors; catch anything that still
                // escapes so startup does not abort on an unexpected failure in the update path.
                SelfUpdater.Log($"Self-update outer exception: {selfUpdateEx.Message}");
            }

            // At this point either (a) no update was applied, or (b) update applied but restart
            // didn't happen — continue startup with the current binary.

            var logger = new AgentLogger(logDirectory) { EnableConsoleOutput = consoleMode };
            logger.Info($"AutopilotMonitor.Agent.V2 starting (version {GetAgentVersion()}).");
            // PR3-A3: empty args looked broken (`Command line: `). Make the no-args case explicit.
            var formattedArgs = FormatArgsForLog(args);
            logger.Info($"Command line: {(string.IsNullOrEmpty(formattedArgs) ? "(no args)" : formattedArgs)}");

            try
            {
                return RunAgent(args, logger, dataDirectory, logDirectory, consoleMode);
            }
            catch (Exception ex)
            {
                logger.Error("V2 agent startup failed.", ex);
                WriteCrashLog(logDirectory, ex);
                if (consoleMode) Console.Error.WriteLine($"FATAL: {ex.Message}");
                return 1;
            }
        }

        // ---------------------------------------------------------------- Orchestration

        private static int RunAgent(
            string[] args,
            AgentLogger logger,
            string dataDirectory,
            string logDirectory,
            bool consoleMode)
        {
            var stateSubdir = Path.Combine(dataDirectory, DefaultStateSubdirectory);
            var transportDir = Path.Combine(dataDirectory, DefaultSpoolSubdirectory);

            // Phase 1+2 (previous-exit, persisted-config, TenantId, AgentConfig, SessionId,
            // completion-marker / emergency-break guards, optional --await-enrollment cert wait)
            // is encapsulated in AgentBootstrap; see Runtime/AgentBootstrap.cs for the V1-parity
            // exit-code mapping (0 = guard handled, 2 = no TenantId, 3 = await timeout).
            var bootstrap = Runtime.AgentBootstrap.Run(args, logger, dataDirectory, logDirectory, stateSubdir, consoleMode);
            if (bootstrap.ShouldExit)
            {
                return bootstrap.ExitCode;
            }

            // Phase 3 (BackendApiClient + reporters + auth-failure tracker) is encapsulated
            // in BackendClientFactory; see Runtime/BackendClientFactory.cs.
            var auth = Runtime.BackendClientFactory.BuildAuthClients(bootstrap.AgentConfig, GetAgentVersion(), logger);

            // Phase 4 (RemoteConfig fetch + Merge + tracker/logger refresh + binary-integrity
            // verification + bootstrap-config cleanup) is encapsulated in AgentRuntimeConfig.
            // See Runtime/AgentRuntimeConfig.cs.
            var runtimeConfig = Runtime.AgentRuntimeConfig.Resolve(bootstrap.AgentConfig, auth, GetAgentVersion(), consoleMode, logger);

            // Control-channel kill-switch: a live-fetch DeviceKillSignal terminates the agent
            // BEFORE mTLS/registration spin-up. Covers agents the telemetry-channel kill can
            // never reach (paused drain, empty spool) at their next process start / boot.
            // Checked BEFORE any endpoint migration — a kill from the current backend is
            // terminal, not re-homed.
            if (CheckConfigKillSignal(
                    runtimeConfig.RemoteConfig,
                    runtimeConfig.RemoteConfigService.LastFetchOutcome,
                    dataDirectory, stateSubdir,
                    () => new CleanupService(bootstrap.AgentConfig, logger),
                    logger, consoleMode))
            {
                return 0;
            }

            // Endpoint migration (config-channel re-home): the backend served a validated
            // MigrateToApiBaseUrl on a live fetch. Rebuild Phase 3 clients against the new base
            // URL and re-run Phase 4 there (single hop — the second Resolve cannot migrate
            // again), then re-check the kill signal against the NEW backend's config.
            if (runtimeConfig.PendingMigrationTarget != null)
            {
                logger.Warning(
                    $"Endpoint migration: backend re-homed this agent from {bootstrap.AgentConfig.ApiBaseUrl} " +
                    $"to {runtimeConfig.PendingMigrationTarget} — rebuilding backend clients.");

                bootstrap.AgentConfig.ApiBaseUrl = runtimeConfig.PendingMigrationTarget;

                // Best-effort disposal of the old-URL client stack before it goes out of scope.
                try { auth.BackendApiClient?.Dispose(); } catch { }

                auth = Runtime.BackendClientFactory.BuildAuthClients(bootstrap.AgentConfig, GetAgentVersion(), logger);
                runtimeConfig = Runtime.AgentRuntimeConfig.Resolve(
                    bootstrap.AgentConfig, auth, GetAgentVersion(), consoleMode, logger,
                    allowEndpointMigration: false);

                if (CheckConfigKillSignal(
                        runtimeConfig.RemoteConfig,
                        runtimeConfig.RemoteConfigService.LastFetchOutcome,
                        dataDirectory, stateSubdir,
                        () => new CleanupService(bootstrap.AgentConfig, logger),
                        logger, consoleMode))
                {
                    return 0;
                }
            }

            // Phase 5 (mTLS HttpClient + BackendTelemetryUploader) is encapsulated in
            // BackendClientFactory. Construction failures map to V1-parity exit codes
            // (4 = mTLS, 5 = uploader). See Runtime/BackendClientFactory.cs.
            var telemetry = Runtime.BackendClientFactory.BuildTelemetryClients(bootstrap.AgentConfig, auth, GetAgentVersion(), logger);
            if (telemetry.ShouldExit)
            {
                return telemetry.ExitCode;
            }

            // Phase 6 (POST /api/agent/register-session with retry + outcome-based exit-code
            // mapping + on-failure client disposal) is encapsulated in BackendSessionRegistration.
            // Exit codes: 6 = AuthFailed, 7 = anything else. See Runtime/BackendSessionRegistration.cs.
            var registration = Runtime.BackendSessionRegistration.Register(
                bootstrap.AgentConfig, auth, telemetry.MtlsHttpClient, GetAgentVersion(), consoleMode, logger);
            if (registration.ShouldExit)
            {
                return registration.ExitCode;
            }
            // Phase 7+8 (orchestrator + shutdown lifecycle + onIngressReady wiring +
            // initial signal posts + analyzer/probes startup + shutdown.Wait + finally)
            // is encapsulated in AgentRuntimeHost. See Runtime/AgentRuntimeHost.cs.
            return Runtime.AgentRuntimeHost.Run(
                bootstrap: bootstrap,
                auth: auth,
                runtimeConfig: runtimeConfig,
                telemetry: telemetry,
                registration: registration,
                dataDirectory: dataDirectory,
                stateSubdir: stateSubdir,
                transportDir: transportDir,
                agentVersion: GetAgentVersion(),
                consoleMode: consoleMode,
                logger: logger);
        }

        // ---------------------------------------------------------------- Configuration

        internal static AgentConfiguration BuildAgentConfiguration(
            string[] args,
            string tenantId,
            string sessionId,
            BootstrapConfigFile bootstrapConfig,
            AwaitEnrollmentConfigFile awaitConfig)
        {
            var apiBaseUrl = GetArgValue(args, "--api-url", "--backend-api") ?? Constants.ApiBaseUrl;
            var imeLogPathOverride = GetArgValue(args, "--ime-log-path");
            var imeMatchLogPath = GetArgValue(args, "--ime-match-log");

            // Dev / test — IME log replay (V1 compat mode). Feeds ImeLogTracker.SimulationMode
            // + SpeedFactor so recorded raw IME logs are replayed at an accelerated rate; signal
            // timestamps + ingress ordinals are regenerated as the replay runs.
            var replayLogDir = GetArgValue(args, "--replay-log-dir");
            var replaySpeedFactorRaw = GetArgValue(args, "--replay-speed-factor");
            var replaySpeedFactor = 50.0;
            if (!string.IsNullOrEmpty(replaySpeedFactorRaw)
                && double.TryParse(replaySpeedFactorRaw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedSpeed)
                && parsedSpeed > 0)
            {
                replaySpeedFactor = parsedSpeed;
            }

            var bootstrapToken = GetArgValue(args, "--bootstrap-token")
                ?? bootstrapConfig?.BootstrapToken;

            var awaitEnrollment = args.Contains("--await-enrollment") || awaitConfig != null;
            var rebootOnComplete = args.Contains("--reboot-on-complete");
            var disableGeoLocation = args.Contains("--disable-geolocation");
            var keepLogFile = args.Contains("--keep-logfile");
            var noCleanup = args.Contains("--no-cleanup");

            var awaitTimeoutRaw = GetArgValue(args, "--await-enrollment-timeout");
            var awaitTimeoutMinutes = 480;
            if (!string.IsNullOrEmpty(awaitTimeoutRaw) && int.TryParse(awaitTimeoutRaw, out var parsedTimeout))
                awaitTimeoutMinutes = parsedTimeout;
            else if (awaitConfig != null)
                awaitTimeoutMinutes = awaitConfig.TimeoutMinutes;

            // TenantId wait — CLI wins over persisted bootstrap-config.json which wins
            // over the agent-side default (600 s). Hybrid-AAD-joined devices typically
            // need ~5 min for the AAD device cert to land in the registry; 600 s leaves
            // headroom. Pass `--tenant-id-wait 0` to opt out (legacy fast-fail).
            var tenantIdWaitRaw = GetArgValue(args, "--tenant-id-wait");
            const int tenantIdWaitDefaultSeconds = 600;
            int tenantIdWaitSeconds = tenantIdWaitDefaultSeconds;
            if (!string.IsNullOrEmpty(tenantIdWaitRaw) && int.TryParse(tenantIdWaitRaw, out var parsedTenantIdWait))
                tenantIdWaitSeconds = parsedTenantIdWait;
            else if (bootstrapConfig != null)
                tenantIdWaitSeconds = bootstrapConfig.TenantIdWaitSeconds;

            var useBootstrapTokenAuth = !string.IsNullOrEmpty(bootstrapToken);

            var cliLogLevel = GetArgValue(args, "--log-level");
            var logLevel = AgentLogLevel.Info;
            if (!string.IsNullOrEmpty(cliLogLevel) && Enum.TryParse<AgentLogLevel>(cliLogLevel, ignoreCase: true, out var parsedLevel))
                logLevel = parsedLevel;

            return new AgentConfiguration
            {
                ApiBaseUrl = apiBaseUrl,
                SessionId = sessionId,
                TenantId = tenantId,
                SpoolDirectory = Environment.ExpandEnvironmentVariables(Constants.SpoolDirectory),
                LogDirectory = Environment.ExpandEnvironmentVariables(Constants.LogDirectory),
                UploadIntervalSeconds = Constants.DefaultUploadIntervalSeconds,
                MaxBatchSize = Constants.MaxBatchSize,
                LogLevel = logLevel,
                UseClientCertAuth = !useBootstrapTokenAuth,
                BootstrapToken = bootstrapToken,
                UseBootstrapTokenAuth = useBootstrapTokenAuth,
                AwaitEnrollment = awaitEnrollment,
                AwaitEnrollmentTimeoutMinutes = awaitTimeoutMinutes,
                TenantIdWaitSeconds = tenantIdWaitSeconds,
                RebootOnComplete = rebootOnComplete,
                EnableGeoLocation = !disableGeoLocation,
                ImeLogPathOverride = imeLogPathOverride,
                ImeMatchLogPath = imeMatchLogPath,
                KeepLogFile = keepLogFile,
                SelfDestructOnComplete = !noCleanup,
                CommandLineArgs = FormatArgsForLog(args),
                ReplayLogDir = replayLogDir,
                ReplaySpeedFactor = replaySpeedFactor,
            };
        }

        private static bool LoadCachedSelfUpdateContext()
        {
            try
            {
                var path = Environment.ExpandEnvironmentVariables(CachedRemoteConfigPath);
                if (!File.Exists(path)) return false;

                var json = File.ReadAllText(path);
                var cached = Newtonsoft.Json.JsonConvert.DeserializeObject<AgentConfigResponse>(json);
                if (cached == null) return false;

                if (!string.IsNullOrEmpty(cached.LatestAgentSha256))
                {
                    SelfUpdater.BackendExpectedSha256 = cached.LatestAgentSha256;
                    SelfUpdater.Log(
                        $"Self-update: loaded backend integrity hash from cached config (sha256={cached.LatestAgentSha256.Substring(0, Math.Min(12, cached.LatestAgentSha256.Length))}...)");
                }

                return cached.AllowAgentDowngrade;
            }
            catch (Exception ex)
            {
                SelfUpdater.Log($"Self-update: cached config read failed: {ex.Message}");
                return false;
            }
        }

        internal static string GetArgValue(string[] args, params string[] names)
        {
            if (args == null || args.Length < 2 || names == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                foreach (var name in names)
                {
                    if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                        return args[i + 1];
                }
            }
            return null;
        }

        private static string FormatArgsForLog(string[] args)
        {
            if (args == null || args.Length == 0) return string.Empty;

            var parts = new System.Collections.Generic.List<string>(args.Length);
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (string.Equals(a, "--bootstrap-token", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    parts.Add(a);
                    parts.Add("[redacted]");
                    i++;
                    continue;
                }
                parts.Add(a);
            }
            return string.Join(" ", parts);
        }

        // ---------------------------------------------------------------- Info

        private static void PrintUsage()
        {
            var v = GetAgentVersion();
            Console.Out.WriteLine($"Autopilot Monitor Agent V2 v{v}");
            Console.Out.WriteLine();
            Console.Out.WriteLine("Usage: AutopilotMonitor.Agent.exe [options]");
            Console.Out.WriteLine();
            Console.Out.WriteLine("Modes:");
            Console.Out.WriteLine("  --install                         Deploy payload, create Scheduled Task, and start it");
            Console.Out.WriteLine("  --tenant-id <ID>                  Tenant ID for bootstrap-config (used with --install)");
            Console.Out.WriteLine("  (default)                         Run enrollment monitoring");
            Console.Out.WriteLine();
            Console.Out.WriteLine("General options:");
            Console.Out.WriteLine("  --help, -h, -?                    Show this help message");
            Console.Out.WriteLine("  --version                         Print version and exit");
            Console.Out.WriteLine("  --console                         Enable console output (mirrors log to stdout)");
            Console.Out.WriteLine("  --log-level <LEVEL>               Override log level (Info, Debug, Verbose, Trace)");
            Console.Out.WriteLine("  --new-session                     Force a new session ID (delete persisted session)");
            Console.Out.WriteLine("  --keep-logfile                    Preserve log directory after self-destruct cleanup");
            Console.Out.WriteLine("  --no-cleanup                      Disable self-destruct on enrollment completion");
            Console.Out.WriteLine("  --reboot-on-complete              Reboot the device after enrollment completes");
            Console.Out.WriteLine("  --disable-geolocation             Skip geo-location detection");
            Console.Out.WriteLine();
            Console.Out.WriteLine("Authentication:");
            Console.Out.WriteLine("  --bootstrap-token <TOKEN>         Use bootstrap token auth (pre-MDM OOBE phase)");
            Console.Out.WriteLine();
            Console.Out.WriteLine("Await-enrollment mode:");
            Console.Out.WriteLine("  --await-enrollment                Wait for MDM certificate before starting monitoring");
            Console.Out.WriteLine("  --await-enrollment-timeout <MIN>  Timeout in minutes (default: 480)");
            Console.Out.WriteLine();
            Console.Out.WriteLine("TenantId resolution:");
            Console.Out.WriteLine("  --tenant-id-wait <SEC>            On miss, wait up to N seconds for the registry to");
            Console.Out.WriteLine("                                    populate (RegistryWatcher on Enrollments + CloudDomainJoin).");
            Console.Out.WriteLine("                                    0 = disabled (legacy fast-fail). At --install time the value");
            Console.Out.WriteLine("                                    is persisted to bootstrap-config.json so the Scheduled Task");
            Console.Out.WriteLine("                                    picks it up on each run.");
            Console.Out.WriteLine();
            Console.Out.WriteLine("Overrides:");
            Console.Out.WriteLine("  --api-url <URL>                   Override backend API base URL (alias: --backend-api)");
            Console.Out.WriteLine("  --ime-log-path <PATH>             Override IME logs directory");
            Console.Out.WriteLine("  --ime-match-log <PATH>            Write matched IME log lines to file (debug)");
            Console.Out.WriteLine();
            Console.Out.WriteLine("Dev / Test:");
            Console.Out.WriteLine("  --replay-log-dir <PATH>           Replay IME logs from this directory (simulation mode)");
            Console.Out.WriteLine("  --replay-speed-factor <N>         Time-compression factor for log replay (default: 50)");
        }

        private static void PrintVersion()
        {
            Console.Out.WriteLine(GetAgentVersion());
        }

        // internal (not private) so AgentBootstrap can stamp the best-effort emergency-break report
        // with the running agent version (tasks/enrollment-status-reclassification.md).
        internal static string GetAgentVersion()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info)) return info;

                var file = asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
                if (!string.IsNullOrWhiteSpace(file)) return file;

                return asm.GetName().Version?.ToString() ?? "0.0.0.0";
            }
            catch
            {
                return "0.0.0.0";
            }
        }
    }
}

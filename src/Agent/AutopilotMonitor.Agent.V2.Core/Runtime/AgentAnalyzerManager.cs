#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Runtime
{
    /// <summary>
    /// Manages the three agent analyzers: initialisation from remote config, startup execution
    /// on a background thread, and shutdown execution with optional WhiteGlove part discriminator.
    /// Plan §4.x M4.6.δ.
    /// <para>
    /// Ported from Legacy <c>Monitoring/Runtime/AnalyzerManager.cs</c> with the sole change that
    /// V2 reads the <see cref="AnalyzerConfiguration"/> directly from <see cref="AgentConfigResponse.Analyzers"/>
    /// rather than through a <c>RemoteConfigService</c> indirection — the service-level cache is
    /// already resolved by <c>Program.cs</c> before it instantiates this manager.
    /// </para>
    /// </summary>
    public sealed class AgentAnalyzerManager
    {
        private readonly AgentConfiguration _configuration;
        private readonly AgentLogger _logger;
        private readonly InformationalEventPost _post;
        private readonly AnalyzerConfiguration _analyzerConfig;
        private readonly Persistence.StartupEventGate? _startupEventGate;

        private readonly List<IAgentAnalyzer> _analyzers = new List<IAgentAnalyzer>();
        private bool _initialised;

        public AgentAnalyzerManager(
            AgentConfiguration configuration,
            AgentLogger logger,
            InformationalEventPost post,
            AnalyzerConfiguration? analyzerConfig,
            Persistence.StartupEventGate? startupEventGate = null)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _analyzerConfig = analyzerConfig ?? new AnalyzerConfiguration();
            _startupEventGate = startupEventGate;
        }

        /// <summary>Exposed for tests / observability.</summary>
        public IReadOnlyList<IAgentAnalyzer> Analyzers => _analyzers;

        public void Initialize()
        {
            if (_initialised) return;
            _initialised = true;
            _analyzers.Clear();

            if (_analyzerConfig.EnableLocalAdminAnalyzer)
            {
                _analyzers.Add(new LocalAdminAnalyzer(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    _post,
                    _logger,
                    _analyzerConfig.LocalAdminAllowedAccounts,
                    _startupEventGate));
                _logger.Info("LocalAdminAnalyzer registered");
            }
            else
            {
                _logger.Info("LocalAdminAnalyzer disabled by remote config");
            }

            if (_analyzerConfig.EnableSoftwareInventoryAnalyzer)
            {
                _analyzers.Add(new SoftwareInventoryAnalyzer(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    _post,
                    _logger,
                    _startupEventGate));
                _logger.Info("SoftwareInventoryAnalyzer registered");
            }
            else
            {
                _logger.Info("SoftwareInventoryAnalyzer disabled by remote config");
            }

            if (_analyzerConfig.EnableIntegrityBypassAnalyzer)
            {
                _analyzers.Add(new IntegrityBypassAnalyzer(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    _post,
                    _logger,
                    _startupEventGate));
                _logger.Info("IntegrityBypassAnalyzer registered");
            }
            else
            {
                _logger.Info("IntegrityBypassAnalyzer disabled by remote config");
            }

            // ConsolePrefetchScanner — startup-forensic half of the OOBE-console / Shift+F10
            // detection (same opt-out flag, default ON, as the live ConsoleBypass host). Scans for a CMD.EXE-*.pf
            // prefetch artifact that ran after boot, covering the pre-agent OOBE window the live
            // watcher cannot see. Restart-deduped via the StartupEventGate.
            if (_analyzerConfig.EnableConsoleBypassDetection)
            {
                _analyzers.Add(new ConsolePrefetchScanner(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    _post,
                    _logger,
                    _startupEventGate));
                _logger.Info("ConsolePrefetchScanner registered");
            }
            else
            {
                _logger.Info("ConsolePrefetchScanner disabled by remote config");
            }

            // AutoLogon analyzer is ALWAYS registered (no remote-config toggle): it reports raw
            // Winlogon facts at Info severity and the grading lives in backend analyze-rules. Its
            // startup run is a no-op; it fires at DeviceSetup-phase completion and at final shutdown.
            _analyzers.Add(new AutoLogonAnalyzer(
                _configuration.SessionId,
                _configuration.TenantId,
                _post,
                _logger));
            _logger.Info("AutoLogonAnalyzer registered (always-on)");

            _logger.Info($"Analyzers initialized: {_analyzers.Count} active");
        }

        /// <summary>
        /// Runs <see cref="IAgentAnalyzer.AnalyzeAtStartup"/> on a background thread for every
        /// registered analyzer. Fire-and-forget: one analyzer throwing must not affect the others
        /// or the caller's critical path.
        /// </summary>
        public void RunStartup()
        {
            if (!_initialised) Initialize();
            if (_analyzers.Count == 0) return;

            var snapshot = new List<IAgentAnalyzer>(_analyzers);
            _logger.Info($"Scheduling {snapshot.Count} startup analyzer(s) on background thread");

            Task.Run(() =>
            {
                foreach (var analyzer in snapshot)
                {
                    try { analyzer.AnalyzeAtStartup(); }
                    catch (Exception ex) { _logger.Error($"Analyzer {analyzer.Name} threw during startup", ex); }
                }
            });
        }

        /// <summary>
        /// Runs <see cref="IAgentAnalyzer.AnalyzeAtShutdown"/> synchronously so the caller can
        /// sequence it before <c>CleanupService.ExecuteSelfDestruct</c>. <see cref="SoftwareInventoryAnalyzer"/>
        /// receives the optional WhiteGlove-part discriminator via its overload.
        /// </summary>
        public void RunShutdown(int? whiteGlovePart = null)
        {
            if (!_initialised) Initialize();
            if (_analyzers.Count == 0) return;

            _logger.Info($"Running {_analyzers.Count} shutdown analyzer(s) (whiteGlovePart={whiteGlovePart?.ToString() ?? "none"})");

            foreach (var analyzer in _analyzers)
            {
                try
                {
                    if (analyzer is SoftwareInventoryAnalyzer softwareAnalyzer)
                        softwareAnalyzer.AnalyzeAtShutdown(whiteGlovePart);
                    else
                        analyzer.AnalyzeAtShutdown();
                }
                catch (Exception ex)
                {
                    _logger.Error($"Analyzer {analyzer.Name} threw during shutdown", ex);
                }
            }
        }

        /// <summary>
        /// Runs ONLY the <see cref="AutoLogonAnalyzer"/> at DeviceSetup-phase completion. Triggered
        /// by <c>AutoLogonDeviceSetupTrigger</c> when the agent observes
        /// <c>DeviceSetupProvisioningComplete</c>. Captures any AutoLogon injected by a
        /// device-targeted provisioning script / app before the user phase; the eventual final
        /// shutdown re-scans via <see cref="RunShutdown"/> to capture user-phase changes.
        /// </summary>
        public void RunDeviceSetupCompleteAutoLogonCheck()
        {
            if (!_initialised) Initialize();
            if (_analyzers.Count == 0) return;

            foreach (var analyzer in _analyzers)
            {
                if (analyzer is not AutoLogonAnalyzer autoLogonAnalyzer) continue;

                try
                {
                    _logger.Info("DeviceSetup complete — running AutoLogonAnalyzer.AnalyzeAtDeviceSetupComplete()");
                    autoLogonAnalyzer.AnalyzeAtDeviceSetupComplete();
                }
                catch (Exception ex)
                {
                    _logger.Error("AutoLogonAnalyzer threw during DeviceSetup-complete check", ex);
                }
                return; // AutoLogonAnalyzer is registered at most once
            }

            _logger.Warning("DeviceSetup-complete AutoLogon check requested but AutoLogonAnalyzer is not registered");
        }

        /// <summary>
        /// Runs ONLY the <see cref="SoftwareInventoryAnalyzer"/> shutdown snapshot tagged with
        /// <c>whiteGlovePart=1</c>. Triggered by <c>WhiteGloveInventoryTrigger</c> when the
        /// agent observes the WhiteGlove pre-provisioning success event (Windows Event 62407)
        /// — i.e. while the OOBE "Continue / Reseal" dialog is shown but BEFORE the admin
        /// clicks Reseal and Sysprep reboots the box.
        /// <para>
        /// Deliberately does NOT run <see cref="LocalAdminAnalyzer"/> or
        /// <see cref="IntegrityBypassAnalyzer"/>: those snapshots are meaningful at a real
        /// final agent shutdown. At the WG-Part-1 hand-off the system is still in SYSTEM
        /// context with no real user yet — those analyzers would either no-op or report
        /// noise. They run normally at the eventual final shutdown via
        /// <see cref="RunShutdown"/>.
        /// </para>
        /// </summary>
        public void RunWhiteGlovePart1InventorySnapshot()
        {
            if (!_initialised) Initialize();
            if (_analyzers.Count == 0) return;

            foreach (var analyzer in _analyzers)
            {
                if (analyzer is not SoftwareInventoryAnalyzer softwareAnalyzer) continue;

                try
                {
                    _logger.Info("WhiteGlove Part 1 inventory snapshot — running SoftwareInventoryAnalyzer.AnalyzeAtShutdown(whiteGlovePart: 1)");
                    softwareAnalyzer.AnalyzeAtShutdown(whiteGlovePart: 1);
                }
                catch (Exception ex)
                {
                    _logger.Error("SoftwareInventoryAnalyzer threw during WhiteGlove Part 1 snapshot", ex);
                }
                return; // SoftwareInventoryAnalyzer is registered at most once
            }

            _logger.Warning("WhiteGlove Part 1 inventory snapshot requested but SoftwareInventoryAnalyzer is not registered (disabled by remote config?)");
        }
    }
}

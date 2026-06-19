#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Security;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Lifecycle host for the <see cref="ConsoleBypassWatcher"/>. Opt-OUT per tenant
    /// (AnalyzerConfiguration.EnableConsoleBypassDetection, default ON): created by
    /// <c>DefaultComponentFactory</c> unless the flag is explicitly disabled. Owns the watcher and translates each
    /// detected SYSTEM-console spawn into a Warning <c>oobe_console_spawned</c> event on the single
    /// rail. The event carries the full process signature plus an honest <c>coverageNote</c> /
    /// <c>coverageComplete:false</c> — the live watcher only sees consoles spawned after the agent
    /// started; the pre-agent OOBE window is covered (coarsely) by the ConsolePrefetchScanner.
    /// </summary>
    internal sealed class ConsoleBypassHost : ICollectorHost
    {
        private const string CoverageNote =
            "best-effort: only consoles spawned after the agent started are observable; a Shift+F10 " +
            "pressed earlier in OOBE (before agent install) is covered coarsely by console_prefetch_detected";

        public string Name => "ConsoleBypassWatcher";

        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;
        private readonly ConsoleBypassWatcher _watcher;
        private int _disposed;

        public ConsoleBypassHost(
            string sessionId,
            string tenantId,
            ISignalIngressSink ingress,
            IClock clock,
            AgentLogger logger,
            ConsoleBypassWatcher? watcher = null)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _post = new InformationalEventPost(ingress, clock, logger);
            _watcher = watcher ?? new ConsoleBypassWatcher(logger);
            _watcher.BypassConsoleDetected += OnBypassConsoleDetected;
        }

        public void Start() => _watcher.Start();

        public void Stop() => Dispose();

        private void OnBypassConsoleDetected(object? sender, ConsoleSpawnInfo info)
        {
            try
            {
                _post.Emit(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = Constants.EventTypes.OobeConsoleSpawned,
                    Severity = EventSeverity.Warning,
                    Source = Name,
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"SYSTEM console spawned during enrollment: {info.ProcessName} " +
                              $"(parent {info.ParentProcessName}) — possible Shift+F10",
                    ImmediateUpload = true,
                    Data = new Dictionary<string, object>
                    {
                        { "decision", "console_spawn_detected" },
                        { "processName", info.ProcessName },
                        { "processId", info.ProcessId },
                        { "parentProcessName", info.ParentProcessName },
                        { "parentProcessId", info.ParentProcessId },
                        { "parentMatchesWinlogon", true },
                        { "sessionId", info.SessionId },
                        { "detectedVia", info.DetectedVia },
                        { "coverageNote", CoverageNote },
                        { "coverageComplete", false },
                    },
                });
            }
            catch (Exception ex)
            {
                _logger.Warning($"[ConsoleBypassHost] failed to emit oobe_console_spawned: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _watcher.BypassConsoleDetected -= OnBypassConsoleDetected; } catch { }
            try { _watcher.Dispose(); } catch { }
        }

        // Test seam.
        internal ConsoleBypassWatcher WatcherForTest => _watcher;
    }
}

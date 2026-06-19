#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Security;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Lifecycle host for the <see cref="ConsoleBypassWatcher"/>. Opt-OUT per tenant
    /// (AnalyzerConfiguration.EnableConsoleBypassDetection, default ON): created by
    /// <c>DefaultComponentFactory</c> unless the flag is explicitly disabled. Owns the watcher and
    /// translates each detected interactive-console spawn into a Warning <c>oobe_console_spawned</c>
    /// event on the single rail. The event carries the process signature (session, owner, command line,
    /// confidence) plus an honest <c>coverageNote</c> / <c>coverageComplete:false</c> — the live watcher
    /// only sees consoles spawned after the agent started; the pre-agent OOBE window is covered
    /// (coarsely) by the ConsolePrefetchScanner. The composition root calls <see cref="Stop"/> once the
    /// real-user desktop arrives (Shift+F10 no longer possible) to avoid post-enrollment false positives.
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
            _watcher.WatcherArmFailed += OnWatcherArmFailed;
        }

        public void Start() => _watcher.Start();

        public void Stop() => Dispose();

        /// <summary>
        /// Lifecycle gate: called by the composition root when the real-user desktop has arrived.
        /// From that point Shift+F10 is no longer possible, so any further cmd is an ordinary user
        /// action — stopping the watcher avoids flagging it. Idempotent.
        /// </summary>
        public void StopForDesktopArrival()
        {
            if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1) return;
            _logger.Info("[ConsoleBypassHost] real-user desktop arrived — Shift+F10 no longer possible, stopping console watcher");
            Dispose();
        }

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
                    Message = $"Interactive console spawned during enrollment: {info.ProcessName} " +
                              $"(session {info.SessionId}, {info.Confidence} confidence) — possible Shift+F10",
                    ImmediateUpload = true,
                    Data = new Dictionary<string, object>
                    {
                        { "decision", "console_spawn_detected" },
                        { "processName", info.ProcessName },
                        { "processId", info.ProcessId },
                        { "parentProcessId", info.ParentProcessId },
                        { "sessionId", info.SessionId },
                        { "owner", info.Owner ?? "unknown" },
                        { "commandLine", info.CommandLine ?? "unavailable" },
                        { "confidence", info.Confidence },
                        { "classification", info.Classification },
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

        // The WMI live watcher could not arm — surface it as collector_degraded so the backend can
        // tell "no console detected" from "live detector never started" (MON-D1 pattern).
        private void OnWatcherArmFailed(object? sender, Exception ex)
        {
            CollectorDegradationReporter.Report(_post, _sessionId, _tenantId, Name, "watcher_arm_failed", ex);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _watcher.BypassConsoleDetected -= OnBypassConsoleDetected; } catch { }
            try { _watcher.WatcherArmFailed -= OnWatcherArmFailed; } catch { }
            try { _watcher.Dispose(); } catch { }
        }

        // Test seam.
        internal ConsoleBypassWatcher WatcherForTest => _watcher;
    }
}

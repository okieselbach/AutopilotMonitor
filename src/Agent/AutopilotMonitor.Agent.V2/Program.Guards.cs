using System;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2
{
    /// <summary>
    /// Startup guards ported from Legacy <c>Program.cs</c>. Plan §4.x M4.6.α.
    /// <list type="bullet">
    ///   <item><b>Multi-instance guard</b> — prevents a second agent process from running alongside
    ///     one that was started by the Scheduled Task.</item>
    ///   <item><b>clean-exit.marker</b> — written in <see cref="AppDomain.ProcessExit"/> so the next
    ///     start can distinguish clean shutdown from hard kill / reboot kill / exception crash.</item>
    ///   <item><b>Previous-exit classification</b> — reads the marker, crash logs and the system
    ///     event log (boot time 6009) and determines the previous exit type.</item>
    ///   <item><b>Enrollment-complete marker check</b> — ghost-restart detection: if a previous
    ///     session completed successfully but the Scheduled Task survived self-destruct, retry
    ///     cleanup and exit.</item>
    ///   <item><b>Session-age emergency break</b> — absolute cap (<c>AbsoluteMaxSessionHours</c>)
    ///     across restarts prevents zombie agents from persisting forever.</item>
    ///   <item><b>Crash log writer</b> — writes a last-resort crash_*.log next to the agent log
    ///     directory when an uncaught exception escapes <c>Main</c>.</item>
    /// </list>
    /// </summary>
    public static partial class Program
    {
        internal const string CleanExitMarkerFileName = "clean-exit.marker";
        internal const string EnrollmentCompleteMarkerFileName = "enrollment-complete.marker";
        private const string CrashLogPrefix = "crash_";

        // ----------------------------------------------------------------- Multi-instance guard

        /// <summary>
        /// <c>true</c> when another process with the same filename (module name) is alive.
        /// Race-safe by design: we count processes sharing our name; if 1, only we exist.
        /// </summary>
        internal static bool IsAnotherAgentInstanceRunning()
        {
            try
            {
                var self = Process.GetCurrentProcess();
                var siblings = Process.GetProcessesByName(self.ProcessName);
                return siblings.Length > 1;
            }
            catch
            {
                // Without WMI or PerfCounter rights the probe will fail — assume no conflict so
                // we do not block startup on a diagnostic false negative.
                return false;
            }
        }

        // ----------------------------------------------------------------- clean-exit marker

        /// <summary>
        /// Writes the <c>clean-exit.marker</c> file directly. Called both from the
        /// <see cref="AppDomain.ProcessExit"/> handler registered by
        /// <see cref="RegisterCleanExitMarker"/> AND — explicitly, before
        /// <c>EnrollmentTerminationHandler._signalShutdown</c> returns — from the V2 graceful
        /// shutdown sequence so admin-triggered reseal-reboots can't pre-empt the marker.
        /// Best-effort: I/O exceptions are swallowed (the marker is observability, not load-bearing).
        /// </summary>
        public static void WriteCleanExitMarker(string dataDirectory)
        {
            if (string.IsNullOrEmpty(dataDirectory)) return;
            try
            {
                var markerPath = Path.Combine(dataDirectory, CleanExitMarkerFileName);
                File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O"));
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Registers a <see cref="AppDomain.ProcessExit"/> handler that writes <c>clean-exit.marker</c>
        /// as a last action so the next start can distinguish clean shutdown from hard kill.
        /// <para>Safe to call multiple times — duplicate handlers are filtered by delegate equality.</para>
        /// </summary>
        internal static EventHandler RegisterCleanExitMarker(string dataDirectory)
        {
            EventHandler handler = (_, _) => WriteCleanExitMarker(dataDirectory);
            AppDomain.CurrentDomain.ProcessExit += handler;
            return handler;
        }

        // ----------------------------------------------------------------- Previous-exit detection

        /// <summary>Lightweight bag returned by <see cref="DetectPreviousExit"/>.</summary>
        internal sealed class PreviousExitSummary
        {
            public string ExitType { get; internal set; }             // "first_run" | "clean" | "exception_crash" | "hard_kill" | "reboot_kill"
            public string CrashExceptionType { get; internal set; }   // populated when ExitType == "exception_crash"
            public DateTime? LastBootUtc { get; internal set; }       // populated when ExitType == "reboot_kill"
        }

        /// <summary>
        /// Reads <c>clean-exit.marker</c> + <c>crash_*.log</c> + session files + (if needed) the
        /// Windows event log to classify the previous exit. Deletes the marker and crash logs so
        /// the next cycle starts fresh. Legacy parity — same rules, same outputs.
        /// </summary>
        internal static PreviousExitSummary DetectPreviousExit(string dataDirectory, string logDirectory)
        {
            var summary = new PreviousExitSummary();

            var cleanExitMarker = Path.Combine(dataDirectory, CleanExitMarkerFileName);
            var hadCleanExit = File.Exists(cleanExitMarker);
            var crashLogs = Directory.Exists(logDirectory)
                ? Directory.GetFiles(logDirectory, CrashLogPrefix + "*.log")
                : Array.Empty<string>();

            if (hadCleanExit)
            {
                summary.ExitType = "clean";
            }
            else if (crashLogs.Length > 0)
            {
                summary.ExitType = "exception_crash";
                try
                {
                    Array.Sort(crashLogs, StringComparer.Ordinal);
                    var mostRecent = crashLogs[crashLogs.Length - 1];
                    var crashContent = File.ReadAllText(mostRecent);
                    var fatalIdx = crashContent.IndexOf("FATAL: ", StringComparison.Ordinal);
                    if (fatalIdx >= 0)
                    {
                        var afterFatal = crashContent.Substring(fatalIdx + 7);
                        var colonIdx = afterFatal.IndexOf(':');
                        if (colonIdx > 0)
                            summary.CrashExceptionType = afterFatal.Substring(0, colonIdx).Trim();
                    }
                }
                catch { /* best-effort */ }
            }
            else
            {
                // No clean-exit marker and no crash log — either first run, hard kill or reboot kill.
                var persistence = new SessionIdPersistence(dataDirectory);
                if (persistence.SessionExists())
                {
                    summary.ExitType = "hard_kill";
                    summary.LastBootUtc = GetLastBootTimeFromEventLog();
                    var sessionCreated = persistence.LoadSessionCreatedAt();
                    if (summary.LastBootUtc.HasValue && sessionCreated.HasValue
                        && summary.LastBootUtc.Value > sessionCreated.Value)
                    {
                        summary.ExitType = "reboot_kill";
                    }
                }
                else
                {
                    summary.ExitType = "first_run";
                }
            }

            // Clean up markers so the next cycle starts from zero.
            try { File.Delete(cleanExitMarker); } catch { }
            foreach (var crashLog in crashLogs)
                try { File.Delete(crashLog); } catch { }

            return summary;
        }

        /// <summary>
        /// Queries the Windows System event log for the latest OS boot event (ID 6009). Returns
        /// <c>null</c> when the query fails (e.g. on reduced-privilege runs in dev environments).
        /// </summary>
        internal static DateTime? GetLastBootTimeFromEventLog()
        {
            try
            {
                var query = new EventLogQuery(
                    "System",
                    PathType.LogName,
                    "*[System[Provider[@Name='EventLog'] and (EventID=6009)]]")
                {
                    ReverseDirection = true,
                };
                using (var reader = new EventLogReader(query))
                {
                    var record = reader.ReadEvent();
                    if (record?.TimeCreated != null)
                    {
                        using (record)
                            return record.TimeCreated.Value.ToUniversalTime();
                    }
                }
            }
            catch { /* permission denied, event log service down — fall through */ }
            return null;
        }

        // ----------------------------------------------------------------- Marker checks

        /// <summary>
        /// <c>true</c> when the file-based <c>enrollment-complete.marker</c> signals that a
        /// previous session already completed enrollment — written by
        /// <see cref="CheckSessionAgeEmergencyBreak"/> right before it fires the cleanup retry,
        /// so that a next-boot observing the marker exits cleanly even if the cleanup retry
        /// failed to remove the Scheduled Task. Triggers another self-destruct attempt when
        /// <paramref name="selfDestructOnComplete"/> is set.
        /// <para>
        /// The <c>HKLM\SOFTWARE\AutopilotMonitor\Deployed</c> registry value is NOT consulted
        /// here: it is a bootstrap-script reentry lock (read by Install-AutopilotMonitor-v2.ps1
        /// Guard 1), set by <c>--install</c> and intentionally preserved across cleanup. It
        /// therefore cannot distinguish a fresh install from a ghost restart and is unsuitable
        /// as an agent-side ghost-restart signal.
        /// </para>
        /// </summary>
        internal static bool CheckEnrollmentCompleteMarker(
            string stateDirectory,
            bool selfDestructOnComplete,
            Func<CleanupService> cleanupServiceFactory,
            AgentLogger logger,
            bool consoleMode)
        {
            // File-based enrollment-complete marker.
            var markerPath = Path.Combine(stateDirectory, EnrollmentCompleteMarkerFileName);
            if (!File.Exists(markerPath)) return false;

            logger.Info("Enrollment-complete marker detected from a previous session.");

            if (!selfDestructOnComplete)
            {
                if (consoleMode)
                    Console.Out.WriteLine("Enrollment already completed (no cleanup configured). Agent will exit.");
                return true;
            }

            logger.Info("Enrollment already completed. Attempting self-destruct retry (scheduled task may have failed).");
            if (consoleMode) Console.Out.WriteLine("Enrollment already completed. Attempting cleanup retry...");

            TryRetryCleanup(cleanupServiceFactory, logger, "complete_marker");
            return true;
        }

        /// <summary>
        /// Emergency break: exits the agent if the current session has been alive longer than
        /// <c>AbsoluteMaxSessionHours</c>. Respects WhiteGlove-paused sessions (the age clock
        /// restarts on Part-2 resume). Legacy parity.
        /// </summary>
        internal static bool CheckSessionAgeEmergencyBreak(
            string dataDirectory,
            string stateDirectory,
            int absoluteMaxSessionHours,
            bool selfDestructOnComplete,
            Func<CleanupService> cleanupServiceFactory,
            AgentLogger logger,
            bool consoleMode,
            Action onBreakFired = null)
        {
            try
            {
                var persistence = new SessionIdPersistence(dataDirectory);

                if (persistence.IsWhiteGloveResume())
                {
                    logger.Info("Emergency break: WhiteGlove resume detected — skipping session age check.");
                    return false;
                }

                var sessionCreatedAt = persistence.LoadSessionCreatedAt();
                if (sessionCreatedAt == null)
                {
                    if (persistence.SessionExists())
                    {
                        persistence.SaveSessionCreatedAt(DateTime.UtcNow);
                        logger.Info("Emergency break: initialised missing session.created for existing session.");
                    }
                    return false;
                }

                var sessionAgeHours = (DateTime.UtcNow - sessionCreatedAt.Value).TotalHours;
                if (sessionAgeHours <= absoluteMaxSessionHours)
                {
                    logger.Info($"Emergency break: session age {sessionAgeHours:F1}h within limit ({absoluteMaxSessionHours}h).");
                    return false;
                }

                logger.Warning($"EMERGENCY BREAK: session age {sessionAgeHours:F1}h exceeds maximum {absoluteMaxSessionHours}h — forcing cleanup.");
                if (consoleMode)
                    Console.Out.WriteLine($"EMERGENCY: Session is {sessionAgeHours:F1}h old (max: {absoluteMaxSessionHours}h). Forcing cleanup.");

                // Best-effort: tell the backend the agent is emergency-breaking so the otherwise-silent
                // 48h absolute cap is no longer a blind spot in the timeline
                // (tasks/enrollment-status-reclassification.md). Fired BEFORE cleanup, while the
                // session state and network are still intact. Never throws — a send failure (e.g. no
                // network) must not block the cleanup/exit that is the whole point of this guard.
                try { onBreakFired?.Invoke(); }
                catch (Exception cbEx) { logger.Debug($"Emergency-break notify callback failed: {cbEx.Message}"); }

                // Write enrollment-complete.marker so the next start exits cleanly even if
                // the cleanup retry below fails (Scheduled Task still alive, files locked, …).
                try
                {
                    Directory.CreateDirectory(stateDirectory);
                    var markerPath = Path.Combine(stateDirectory, EnrollmentCompleteMarkerFileName);
                    File.WriteAllText(markerPath,
                        $"Emergency break at {DateTime.UtcNow:O} (session age: {sessionAgeHours:F1}h)");
                }
                catch (Exception ex) { logger.Warning($"Failed to write enrollment-complete.marker: {ex.Message}"); }

                if (selfDestructOnComplete)
                {
                    TryRetryCleanup(cleanupServiceFactory, logger, "emergency_break");
                }

                persistence.Delete(logger);
                logger.Info("Emergency break: cleanup completed. Agent will exit.");
                return true;
            }
            catch (Exception ex)
            {
                logger.Error("Emergency break check failed.", ex);
                return false;
            }
        }

        /// <summary>
        /// Kill-switch on the control channel: honours a <c>DeviceKillSignal</c> served on the
        /// <c>agent/config</c> response. Config is fetched at every process start (boot via
        /// Scheduled Task), so this reaches agents the telemetry channel cannot — a drain
        /// paused indefinitely by a prior block, an empty spool, or a binary that never
        /// uploads again. Kill semantics mirror the telemetry-channel kill
        /// (<c>forceSelfDestruct=true</c>): cleanup runs regardless of
        /// <c>SelfDestructOnComplete</c>. Only a LIVE fetch is honoured — cached/default
        /// configs strip the flag (see <c>RemoteConfigService.CacheConfig</c>), and this guard
        /// re-checks the outcome as defence-in-depth.
        /// </summary>
        internal static bool CheckConfigKillSignal(
            AgentConfigResponse remoteConfig,
            RemoteConfigFetchOutcome fetchOutcome,
            string dataDirectory,
            string stateDirectory,
            Func<CleanupService> cleanupServiceFactory,
            AgentLogger logger,
            bool consoleMode)
        {
            if (remoteConfig == null || !remoteConfig.DeviceKillSignal) return false;

            if (fetchOutcome != RemoteConfigFetchOutcome.Succeeded)
            {
                logger.Warning($"Config kill signal ignored — config source is {fetchOutcome}, not a live fetch.");
                return false;
            }

            logger.Warning("KILL: backend signalled DeviceKillSignal on the config channel — terminating with forced self-destruct.");
            if (consoleMode)
                Console.Out.WriteLine("KILL: administrator issued a remote kill signal. Cleaning up and exiting.");

            // Write enrollment-complete.marker first so the next start exits cleanly even if
            // the cleanup below fails (Scheduled Task still alive, files locked, …) — same
            // guarantee-of-termination pattern as the session-age emergency break.
            try
            {
                Directory.CreateDirectory(stateDirectory);
                var markerPath = Path.Combine(stateDirectory, EnrollmentCompleteMarkerFileName);
                File.WriteAllText(markerPath, $"Kill signal at {DateTime.UtcNow:O} (config channel)");
            }
            catch (Exception ex) { logger.Warning($"Failed to write enrollment-complete.marker: {ex.Message}"); }

            // forceSelfDestruct — no SelfDestructOnComplete gate, matching the telemetry-kill.
            TryRetryCleanup(cleanupServiceFactory, logger, "kill_signal");

            try { new SessionIdPersistence(dataDirectory).Delete(logger); }
            catch (Exception ex) { logger.Warning($"Failed to delete persisted session after kill: {ex.Message}"); }

            logger.Info("Kill signal handled — agent exiting.");
            return true;
        }

        private static void TryRetryCleanup(
            Func<CleanupService> cleanupServiceFactory,
            AgentLogger logger,
            string reason)
        {
            if (cleanupServiceFactory == null) return;
            try
            {
                var service = cleanupServiceFactory();
                service.ExecuteSelfDestruct();
                logger.Info($"Cleanup retry ({reason}) completed.");
            }
            catch (Exception ex)
            {
                logger.Warning($"Cleanup retry ({reason}) failed: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------- Crash log

        /// <summary>
        /// Writes a last-resort crash_*.log next to the agent log directory when an uncaught
        /// exception escapes <see cref="Main"/>. The next start reads this file and emits an
        /// <c>exception_crash</c> previous-exit classification.
        /// </summary>
        internal static void WriteCrashLog(string logDirectory, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(logDirectory);
                var crashPath = Path.Combine(logDirectory, $"{CrashLogPrefix}{DateTime.UtcNow:yyyyMMdd_HHmmss}.log");
                File.WriteAllText(crashPath, $"[{DateTime.UtcNow:u}] FATAL: {ex}");
            }
            catch { /* nowhere left to log */ }
        }

        // ----------------------------------------------------------------- Bootstrap-config read

        /// <summary>
        /// Reads a persisted <c>bootstrap-config.json</c> (if present) that was written by
        /// <c>--install</c>. Returns <c>null</c> on miss or corrupt file.
        /// </summary>
        internal static BootstrapConfigFile TryReadBootstrapConfig(string dataDirectory, AgentLogger logger)
        {
            try
            {
                var path = Path.Combine(dataDirectory, BootstrapConfigFileName);
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                return Newtonsoft.Json.JsonConvert.DeserializeObject<BootstrapConfigFile>(json);
            }
            catch (Exception ex)
            {
                logger?.Debug($"TryReadBootstrapConfig: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Reads a persisted <c>await-enrollment.json</c> (if present). Returns <c>null</c> on
        /// miss or corrupt file.
        /// </summary>
        internal static AwaitEnrollmentConfigFile TryReadAwaitEnrollmentConfig(string dataDirectory, AgentLogger logger)
        {
            try
            {
                var path = Path.Combine(dataDirectory, AwaitEnrollmentConfigFileName);
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                return Newtonsoft.Json.JsonConvert.DeserializeObject<AwaitEnrollmentConfigFile>(json);
            }
            catch (Exception ex)
            {
                logger?.Debug($"TryReadAwaitEnrollmentConfig: {ex.Message}");
                return null;
            }
        }

        internal static void DeleteAwaitEnrollmentConfig(string dataDirectory, AgentLogger logger)
        {
            try
            {
                var path = Path.Combine(dataDirectory, AwaitEnrollmentConfigFileName);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    logger?.Info("Await-enrollment: config file removed; subsequent restarts proceed normally.");
                }
            }
            catch (Exception ex)
            {
                logger?.Warning($"Await-enrollment: could not remove config file: {ex.Message}");
            }
        }
    }
}

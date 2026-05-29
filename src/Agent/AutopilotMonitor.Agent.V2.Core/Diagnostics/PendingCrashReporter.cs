#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Diagnostics
{
    /// <summary>
    /// Scans <see cref="CrashDumpCapture.CrashesDirectory"/> at agent start, emits one
    /// <c>previous_crash_detected</c> event per pending <see cref="CrashRecord"/>, then
    /// applies the retention policy. The dump files themselves stay on disk so that a
    /// diagnostics-upload (customer opt-in) can collect them via the Crashes/ folder.
    /// </summary>
    public static class PendingCrashReporter
    {
        public static void ScanAndEmit(
            AgentConfiguration configuration,
            string programDataDirectory,
            AgentLogger logger,
            InformationalEventPost post)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (post == null) throw new ArgumentNullException(nameof(post));

            var crashesDir = Path.Combine(programDataDirectory, CrashDumpCapture.CrashesDirectoryName);
            if (!Directory.Exists(crashesDir)) return;

            string[] recordFiles;
            try { recordFiles = Directory.GetFiles(crashesDir, "*.json"); }
            catch (Exception ex)
            {
                logger.Warning($"PendingCrashReporter: enumerate crashes dir threw: {ex.Message}");
                return;
            }

            foreach (var recordFile in recordFiles)
            {
                CrashRecord? record = TryReadRecord(recordFile, logger);
                if (record == null)
                {
                    TryDelete(recordFile);
                    continue;
                }

                try
                {
                    post.Emit(BuildEvent(configuration, record));
                    // Mark JSON consumed so we don't re-emit on next start. The .dmp stays for upload.
                    TryDelete(recordFile);
                }
                catch (Exception ex)
                {
                    logger.Warning($"PendingCrashReporter: emit failed for '{recordFile}': {ex.Message}");
                }
            }

            CrashDumpCapture.ApplyRetention(crashesDir);
        }

        private static CrashRecord? TryReadRecord(string path, AgentLogger logger)
        {
            try
            {
                var json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<CrashRecord>(json);
            }
            catch (Exception ex)
            {
                logger.Warning($"PendingCrashReporter: parse failed for '{path}': {ex.Message}");
                return null;
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }

        // ----------------------------------------------------------- Event builder (internal for tests)

        internal static EnrollmentEvent BuildEvent(AgentConfiguration configuration, CrashRecord record) =>
            new EnrollmentEvent
            {
                SessionId = configuration.SessionId,
                TenantId = configuration.TenantId,
                EventType = Constants.EventTypes.PreviousCrashDetected,
                Severity = EventSeverity.Warning,
                Source = "PendingCrashReporter",
                Phase = EnrollmentPhase.Unknown,
                Timestamp = DateTime.UtcNow,
                Message = BuildMessage(record),
                Data = new Dictionary<string, object>
                {
                    { "crashedAt", record.CrashedAt.ToString("o") },
                    { "previousSessionId", (object?)record.SessionId ?? "unknown" },
                    { "previousTenantId", (object?)record.TenantId ?? "unknown" },
                    { "previousAgentVersion", (object?)record.AgentVersion ?? "unknown" },
                    { "trigger", record.Trigger ?? "unknown" },
                    { "exceptionType", (object?)record.ExceptionType ?? "unknown" },
                    { "exceptionMessage", (object?)record.ExceptionMessage ?? "" },
                    { "stackTrace", (object?)record.StackTrace ?? "" },
                    { "dumpFilePath", (object?)record.DumpFilePath ?? "(no dump)" },
                    { "dumpFileSizeBytes", (object?)record.DumpFileSizeBytes ?? 0 },
                    { "dumpWriteSucceeded", record.DumpWriteSucceeded },
                },
                ImmediateUpload = true,
            };

        private static string BuildMessage(CrashRecord record)
        {
            var type = record.ExceptionType ?? "unknown";
            var dumpInfo = record.DumpWriteSucceeded
                ? $"dump captured ({record.DumpFileSizeBytes ?? 0} bytes)"
                : "no dump";
            return $"Previous agent process crashed: {type} via {record.Trigger}; {dumpInfo}";
        }
    }
}

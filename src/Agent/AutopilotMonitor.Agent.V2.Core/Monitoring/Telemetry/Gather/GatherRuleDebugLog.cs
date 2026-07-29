using System;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather
{
    /// <summary>
    /// Append-only trace file for gather-rule evaluation, enabled per tenant via
    /// EnableGatherRuleDebugLog (or --gather-debug-log). Explains every evaluation
    /// decision — including the outcomes that produce no event — so customers can
    /// diagnose rules that never show up in the timeline. Every write failure is
    /// swallowed: tracing must never break gathering.
    /// </summary>
    public sealed class GatherRuleDebugLog
    {
        public const string StageConfig = "config";
        public const string StagePhase = "phase";
        public const string StageTrigger = "trigger";
        public const string StageScope = "scope";
        public const string StageExec = "exec";
        public const string StageCollector = "collector";
        public const string StageGuard = "guard";
        public const string StageEmit = "emit";
        public const string StageSuppress = "suppress";
        public const string StageLogParser = "logparser";
        public const string StageError = "error";

        private const long DefaultMaxBytes = 10 * 1024 * 1024;

        private readonly string _path;
        private readonly long _maxBytes;
        private readonly Action<string> _echo;
        private readonly object _lock = new object();
        private bool _headerWritten;

        public GatherRuleDebugLog(string path, AgentLogger logger, Action<string> echo = null, long maxBytes = DefaultMaxBytes)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            _path = path;
            _maxBytes = maxBytes;
            _echo = echo;
            logger?.Info($"GatherRuleDebugLog: enabled -> {path}");
        }

        /// <summary>
        /// Writes one trace line: <c>{utc} | {ruleId} | {stage} | {message}</c>.
        /// Newlines in <paramref name="message"/> are flattened except for the error
        /// stage, where exception stack traces stay multi-line.
        /// </summary>
        public void Write(string ruleId, string stage, string message)
        {
            try
            {
                if (stage != StageError && message != null && message.IndexOf('\n') >= 0)
                    message = message.Replace("\r", "").Replace('\n', ' ');

                var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} | {ruleId ?? "-"} | {stage} | {message}";

                lock (_lock)
                {
                    EnsureFileReadyLocked();
                    File.AppendAllText(_path, line + Environment.NewLine);
                }

                _echo?.Invoke(line);
            }
            catch { }
        }

        /// <summary>
        /// One-shot helper for callers that have no executor (e.g. the flag is on but the
        /// backend delivered zero gather rules) — the customer's trace file should explain
        /// that too instead of silently not existing.
        /// </summary>
        public static void WriteStandalone(string path, string message, AgentLogger logger)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                new GatherRuleDebugLog(path, logger).Write(null, StageConfig, message);
            }
            catch { }
        }

        // Lazy on first write: create the directory and emit a header line. Before every
        // append, rotate when the file exceeds maxBytes — keeping exactly one .old
        // generation (worst case ~2x maxBytes on disk).
        private void EnsureFileReadyLocked()
        {
            if (!_headerWritten)
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
            }

            var info = new FileInfo(_path);
            if (info.Exists && info.Length > _maxBytes)
            {
                var oldPath = Path.ChangeExtension(_path, null) + ".old.log";
                if (File.Exists(oldPath))
                    File.Delete(oldPath);
                File.Move(_path, oldPath);
                File.AppendAllText(_path,
                    $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} | - | {StageConfig} | -- rotated (previous file exceeded {_maxBytes} bytes) --" + Environment.NewLine);
            }

            if (!_headerWritten)
            {
                File.AppendAllText(_path,
                    $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} | - | {StageConfig} | -- gather rule debug log started --" + Environment.NewLine);
                _headerWritten = true;
            }
        }
    }
}

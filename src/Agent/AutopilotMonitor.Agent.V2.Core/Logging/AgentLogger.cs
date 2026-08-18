using System;
using System.IO;

namespace AutopilotMonitor.Agent.V2.Core.Logging
{
    /// <summary>
    /// Log verbosity levels for the agent.
    /// Info  = normal operational messages (default)
    /// Debug = includes DEBUG-tagged lines (component state, config, decisions)
    /// Verbose = includes VERBOSE-tagged lines (per-event details, hot-path tracing)
    /// </summary>
    public enum AgentLogLevel
    {
        Info = 0,
        Debug = 1,
        Verbose = 2,
        Trace = 3
    }

    /// <summary>
    /// Simple file-based logger for the agent.
    /// Optionally mirrors output to the console when EnableConsoleOutput is set.
    /// </summary>
    public class AgentLogger
    {
        // Rotation cap. With per-enrollment lifecycle (30 min – 4 h) and verbose mode the
        // active log can grow into the hundreds of MB territory; 50 MB keeps individual
        // files manageable for forensics tooling without hiding any data — rotated files
        // are kept alongside the active one with a numeric suffix.
        private const long DefaultMaxFileSizeBytes = 50L * 1024 * 1024;

        private AgentLogLevel _logLevel;
        private readonly object _lockObject = new object();

        private readonly long _maxFileSizeBytes;
        private readonly string _logDirectory;
        private readonly string _logFileBaseName;   // "agent"
        private string _logFilePath;                // mutable — rotates to "-002", "-003" ...
        private int _rotationSuffix;                // 0 = base file, 2+ = "-002" etc.

        /// <summary>
        /// When true, log entries are also written to Console.Out (same format as the log file).
        /// Set via --console flag or programmatically.
        /// </summary>
        public bool EnableConsoleOutput { get; set; }

        public AgentLogger(string logDirectory, AgentLogLevel logLevel = AgentLogLevel.Info, long maxFileSizeBytes = DefaultMaxFileSizeBytes)
        {
            _logLevel = logLevel;
            _maxFileSizeBytes = maxFileSizeBytes > 0 ? maxFileSizeBytes : DefaultMaxFileSizeBytes;
            _logDirectory = logDirectory;

            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            // One canonical name, no date: the agent only lives for the duration of an
            // enrollment, and a date-based split rotates at local midnight — which can fall
            // mid-enrollment (local time may still be on the OOBE default timezone). Size
            // rotation below bounds pathological growth. Legacy `agent_YYYYMMDD*.log` files
            // from a pre-rename agent are ignored by the suffix probe and left in place.
            _logFileBaseName = "agent";
            _rotationSuffix = ProbeNextRotationSuffix(logDirectory, _logFileBaseName);
            _logFilePath = BuildLogFilePath();
        }

        // Picks up the highest existing rotation suffix so a process restart doesn't
        // overwrite previous segments. Returns 0 when no log file exists yet — the
        // first segment uses the unsuffixed name `agent.log`.
        private static int ProbeNextRotationSuffix(string dir, string baseName)
        {
            if (!Directory.Exists(dir)) return 0;
            string[] existing;
            try { existing = Directory.GetFiles(dir, baseName + "*.log"); }
            catch { return 0; }

            int max = 0;
            foreach (var path in existing)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase))
                {
                    if (max < 1) max = 1;
                    continue;
                }
                if (name.Length > baseName.Length + 1 &&
                    name.StartsWith(baseName + "-", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(name.Substring(baseName.Length + 1), out var suffix) &&
                    suffix > max)
                {
                    max = suffix;
                }
            }
            // 0 means "no file yet" → use the unsuffixed base; otherwise continue at the
            // highest existing suffix so the active writer appends to the most recent file
            // (rotation kicks in once it crosses the cap).
            return max;
        }

        private string BuildLogFilePath()
        {
            var name = _rotationSuffix < 2
                ? $"{_logFileBaseName}.log"
                : $"{_logFileBaseName}-{_rotationSuffix:D3}.log";
            return Path.Combine(_logDirectory, name);
        }

        /// <summary>
        /// Updates the active log level at runtime (e.g. after remote config is applied).
        /// </summary>
        public void SetLogLevel(AgentLogLevel level)
        {
            _logLevel = level;
        }

        public AgentLogLevel LogLevel => _logLevel;

        public void Trace(string message)
        {
            if (_logLevel >= AgentLogLevel.Trace)
                Log("TRACE", message);
        }

        public void Verbose(string message)
        {
            if (_logLevel >= AgentLogLevel.Verbose)
                Log("VERBOSE", message);
        }

        public void Debug(string message)
        {
            if (_logLevel >= AgentLogLevel.Debug)
                Log("DEBUG", message);
        }

        public void Info(string message)
        {
            Log("INFO", message);
        }

        public void Warning(string message)
        {
            Log("WARN", message);
        }

        public void Error(string message, Exception ex = null)
        {
            var fullMessage = ex != null ? $"{message} - {ex}" : message;
            Log("ERROR", fullMessage);
        }

        private void Log(string level, string message)
        {
            try
            {
                lock (_lockObject)
                {
                    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    var logEntry = $"[{timestamp}] [{level}] {SanitizeLogMessage(message)}";

                    RotateIfOverCap(timestamp);

                    File.AppendAllText(_logFilePath, logEntry + Environment.NewLine);

                    if (EnableConsoleOutput)
                    {
                        try { Console.WriteLine(logEntry); } catch { }
                    }
                }
            }
            catch
            {
                // Swallow logging errors to prevent crashes
            }
        }

        // Best-effort size check. FileInfo.Length is briefly cached, so under bursty writes
        // the active file can drift slightly past the cap before rotation kicks in — that's
        // acceptable. Rotation is non-fatal: any failure here is silently ignored so the
        // logger never breaks the agent.
        private void RotateIfOverCap(string timestamp)
        {
            try
            {
                var fi = new FileInfo(_logFilePath);
                if (!fi.Exists || fi.Length < _maxFileSizeBytes) return;

                _rotationSuffix = _rotationSuffix < 2 ? 2 : _rotationSuffix + 1;
                _logFilePath = BuildLogFilePath();

                var capMb = _maxFileSizeBytes / (1024 * 1024);
                var rotateMsg = $"[{timestamp}] [INFO] AgentLogger: rotated from previous segment ({capMb}MB cap reached).";
                File.AppendAllText(_logFilePath, rotateMsg + Environment.NewLine);
            }
            catch
            {
                // Best-effort: never break logging on a rotation glitch.
            }
        }

        /// <summary>
        /// Sanitizes log messages to prevent log injection attacks.
        /// Escapes control characters that could forge log entries.
        /// Transliterates typographic punctuation to ASCII: the log file is written as
        /// UTF-8 without BOM, and customer-side viewers (e.g. Outlook web attachment
        /// preview) guess cp1252 and render an em dash as mojibake ("â€”"). Only our own
        /// punctuation is mapped — genuine non-ASCII payload (device names, paths) is
        /// kept verbatim. Redaction patterns can be added here later as needed.
        /// </summary>
        private static string SanitizeLogMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;

            // Escape newlines and carriage returns to prevent log injection
            message = message.Replace("\r", "\\r").Replace("\n", "\\n");

            message = message
                .Replace('\u2014', '-')      // — em dash
                .Replace('\u2013', '-')      // – en dash
                .Replace("\u2192", "->")     // → arrow
                .Replace("\u2026", "...")    // … ellipsis
                .Replace('\u2018', '\'')     // ' left single quote
                .Replace('\u2019', '\'')     // ' right single quote
                .Replace('\u201C', '"')      // " left double quote
                .Replace('\u201D', '"')      // " right double quote
                .Replace('\u00A0', ' ');     // non-breaking space

            return message;
        }
    }
}

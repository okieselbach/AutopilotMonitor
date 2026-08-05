using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Shared.Logging;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2
{
    /// <summary>
    /// <c>--run-ime-matching</c> standalone IME log pattern-matching mode. Plan §4.x M4.6.δ.
    /// <para>
    /// Parses IME log files from a given path (single file or directory) with the same pattern
    /// set the live agent uses, and writes all matches to <c>ime_pattern_matching.log</c> next
    /// to the input. Parity-port of Legacy <c>Program.ImeMatchingMode.cs</c>.
    /// </para>
    /// </summary>
    public static partial class Program
    {
        // Same file-name patterns the live ImeLogTracker watches.
        private static readonly string[] ImeLogFilePatterns = new[]
        {
            "IntuneManagementExtension.log",
            "_IntuneManagementExtension.log",
            "IntuneManagementExtension-????????-??????.log",
            "AppWorkload.log",
            "AppWorkload-????????-??????.log",
            "AgentExecutor.log",
            "AgentExecutor-????????-??????.log",
            "HealthScripts.log",
            "HealthScripts-????????-??????.log",
        };

        private const string ImePatternsGuidPattern = @"(?<id>[a-z0-9]{8}-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{12})";
        private const string ImePatternsGitHubUrl = "https://raw.githubusercontent.com/okieselbach/Autopilot-Monitor/refs/heads/main/rules/dist/ime-log-patterns.json";

        internal static int RunImeMatchingMode(string[] args)
        {
            Console.WriteLine("Autopilot Monitor Agent V2 — IME Pattern Matching");
            Console.WriteLine("==================================================");
            Console.WriteLine();

            var pathIndex = Array.IndexOf(args, "--run-ime-matching");
            if (pathIndex < 0 || pathIndex + 1 >= args.Length)
            {
                Console.Error.WriteLine("ERROR: --run-ime-matching requires a path argument (file or directory).");
                Console.Error.WriteLine("Usage: AutopilotMonitor.Agent.exe --run-ime-matching <path> [--patterns <file>]");
                return 1;
            }

            var inputPath = args[pathIndex + 1];
            var patternsFile = GetArgValue(args, "--patterns");

            var logFiles = new List<string>();
            string outputDir;

            if (File.Exists(inputPath))
            {
                logFiles.Add(Path.GetFullPath(inputPath));
                outputDir = Path.GetDirectoryName(Path.GetFullPath(inputPath));
                Console.WriteLine($"Mode:       Single file");
                Console.WriteLine($"Input:      {Path.GetFullPath(inputPath)}");
            }
            else if (Directory.Exists(inputPath))
            {
                var fullDir = Path.GetFullPath(inputPath);
                foreach (var pattern in ImeLogFilePatterns)
                {
                    try { logFiles.AddRange(Directory.GetFiles(fullDir, pattern)); }
                    catch (DirectoryNotFoundException) { }
                }
                logFiles.Sort(StringComparer.OrdinalIgnoreCase);
                outputDir = fullDir;

                Console.WriteLine($"Mode:       Directory scan");
                Console.WriteLine($"Input:      {fullDir}");
                Console.WriteLine($"Log files:  {logFiles.Count}");

                if (logFiles.Count == 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("No IME log files found in the specified directory.");
                    Console.WriteLine("Expected files: IntuneManagementExtension*.log, AppWorkload*.log, AgentExecutor*.log, HealthScripts*.log");
                    return 1;
                }
            }
            else
            {
                Console.Error.WriteLine($"ERROR: Path not found: {inputPath}");
                return 1;
            }

            var patterns = !string.IsNullOrEmpty(patternsFile)
                ? LoadImePatternsFromFile(patternsFile)
                : LoadImePatternsFromGitHub();

            if (patterns == null || patterns.Count == 0)
            {
                Console.Error.WriteLine("ERROR: No patterns loaded. Cannot proceed.");
                return 1;
            }

            Console.WriteLine($"Loaded:     {patterns.Count} patterns ({patterns.Count(p => p.Enabled)} enabled)");

            var compiledPatterns = CompileImeMatchingPatterns(patterns);
            Console.WriteLine($"Compiled:   {compiledPatterns.Count} patterns");

            var outputPath = Path.Combine(outputDir, "ime_pattern_matching.log");
            Console.WriteLine($"Output:     {outputPath}");
            Console.WriteLine();

            if (File.Exists(outputPath)) File.Delete(outputPath);

            int totalLines = 0;
            int totalMatches = 0;
            var matchCountByPattern = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var filePath in logFiles)
            {
                var fileName = Path.GetFileName(filePath);
                Console.Write($"  Scanning {fileName}...");

                int fileLines = 0;
                int fileMatches = 0;

                try
                {
                    using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            fileLines++;
                            string messageToMatch = CmTraceLogParser.TryParseLine(line, out var entry)
                                ? entry.Message
                                : line;
                            if (string.IsNullOrEmpty(messageToMatch)) continue;

                            foreach (var pattern in compiledPatterns)
                            {
                                try
                                {
                                    var match = pattern.Regex.Match(messageToMatch);
                                    if (match.Success)
                                    {
                                        fileMatches++;
                                        var logEntry = $"[{fileName}] [{pattern.PatternId}] {line}";
                                        File.AppendAllText(outputPath, logEntry + Environment.NewLine);

                                        if (!matchCountByPattern.ContainsKey(pattern.PatternId))
                                            matchCountByPattern[pattern.PatternId] = 0;
                                        matchCountByPattern[pattern.PatternId]++;
                                    }
                                }
                                catch (RegexMatchTimeoutException) { }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($" ERROR: {ex.Message}");
                    continue;
                }

                totalLines += fileLines;
                totalMatches += fileMatches;
                Console.WriteLine($" {fileLines} lines, {fileMatches} matches");
            }

            Console.WriteLine();
            Console.WriteLine("=== Summary ===");
            Console.WriteLine($"Files scanned:  {logFiles.Count}");
            Console.WriteLine($"Lines parsed:   {totalLines}");
            Console.WriteLine($"Total matches:  {totalMatches}");
            Console.WriteLine($"Output:         {outputPath}");

            if (matchCountByPattern.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Matches by pattern:");
                foreach (var kvp in matchCountByPattern.OrderByDescending(x => x.Value))
                    Console.WriteLine($"  {kvp.Key,-40} {kvp.Value,5}");
            }

            return 0;
        }

        private static List<ImeLogPattern> LoadImePatternsFromGitHub()
        {
            try
            {
                Console.Write("  Downloading patterns from GitHub...");
                using (var handler = new HttpClientHandler())
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) })
                {
                    var json = client.GetStringAsync(ImePatternsGitHubUrl).GetAwaiter().GetResult();
                    var wrapper = JsonConvert.DeserializeObject<PatternsFileWrapper>(json);
                    var patterns = wrapper?.Rules ?? new List<ImeLogPattern>();
                    Console.WriteLine(" OK");
                    return patterns;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" FAILED: {ex.Message}");

                // Fallback: cached remote-config.json (written by previous live agent runs).
                try
                {
                    var cachePath = Environment.ExpandEnvironmentVariables(CachedRemoteConfigPath);
                    if (File.Exists(cachePath))
                    {
                        Console.Write("  Falling back to cached remote-config.json...");
                        var json = File.ReadAllText(cachePath);
                        var config = JsonConvert.DeserializeObject<CachedConfigWrapper>(json);
                        if (config?.ImeLogPatterns != null && config.ImeLogPatterns.Count > 0)
                        {
                            Console.WriteLine(" OK");
                            return config.ImeLogPatterns;
                        }
                    }
                }
                catch { /* falls through to null */ }

                Console.WriteLine("  No fallback available.");
                return null;
            }
        }

        private static List<ImeLogPattern> LoadImePatternsFromFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    Console.Error.WriteLine($"ERROR: Patterns file not found: {filePath}");
                    return null;
                }
                var json = File.ReadAllText(filePath);
                var wrapper = JsonConvert.DeserializeObject<PatternsFileWrapper>(json);
                return wrapper?.Rules ?? new List<ImeLogPattern>();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: Failed to load patterns file: {ex.Message}");
                return null;
            }
        }

        private static List<CompiledImeMatchPattern> CompileImeMatchingPatterns(List<ImeLogPattern> patterns)
        {
            var compiled = new List<CompiledImeMatchPattern>();
            foreach (var pattern in patterns.Where(p => p.Enabled))
            {
                try
                {
                    var regexStr = pattern.Pattern.Replace("{GUID}", ImePatternsGuidPattern);
                    var regex = new Regex(
                        regexStr,
                        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline,
                        TimeSpan.FromSeconds(1));
                    compiled.Add(new CompiledImeMatchPattern { PatternId = pattern.PatternId, Regex = regex });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  WARNING: Failed to compile pattern {pattern.PatternId}: {ex.Message}");
                }
            }
            return compiled;
        }

        private sealed class CompiledImeMatchPattern
        {
            public string PatternId { get; set; }
            public Regex Regex { get; set; }
        }

        private sealed class PatternsFileWrapper
        {
            [JsonProperty("rules")]
            public List<ImeLogPattern> Rules { get; set; } = new List<ImeLogPattern>();
        }

        private sealed class CachedConfigWrapper
        {
            [JsonProperty("imeLogPatterns")]
            public List<ImeLogPattern> ImeLogPatterns { get; set; }
        }
    }
}

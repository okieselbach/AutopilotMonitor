#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Harness
{
    /// <summary>
    /// An <see cref="ImeLogTracker"/> fed the shipped IME pattern pack (<c>rules/ime-log-patterns</c>,
    /// sorted as combine.js embeds it) and a CMTrace <c>IntuneManagementExtension.log</c> the test
    /// appends to, so a test drives the real patterns end to end.
    /// </summary>
    internal sealed class ShippedImePackHarness : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();

        public ImeLogTracker Tracker { get; }
        public string StateDir { get; }
        public DateTime Now { get; } = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

        public ShippedImePackHarness()
        {
            StateDir = Path.Combine(_tmp.Path, "State");
            Tracker = Build();
        }

        public static List<ImeLogPattern> Pack()
            => Directory.GetFiles(Path.Combine(RepoPaths.Root(), "rules", "ime-log-patterns"), "*.json")
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => JsonConvert.DeserializeObject<ImeLogPattern>(File.ReadAllText(f))!)
                .ToList();

        /// <summary>A second tracker on the same log and state folder (an agent restart).</summary>
        public ImeLogTracker Build()
        {
            var tracker = new ImeLogTracker(
                logFolder: _tmp.Path,
                patterns: Pack(),
                logger: new AgentLogger(_tmp.Path, AgentLogLevel.Info),
                stateDirectory: StateDir);
            // The historic-replay guard skips app-mutating actions for lines older than 24 h
            // relative to "now": pin both so the appended entries are always fresh.
            tracker.UtcNowProvider = () => Now;
            return tracker;
        }

        public void Append(params string[] messages)
        {
            var lines = messages.Select((m, i) => Entry(m, Now.AddSeconds(-60 + i)));
            File.AppendAllText(Path.Combine(_tmp.Path, "IntuneManagementExtension.log"),
                string.Join(Environment.NewLine, lines) + Environment.NewLine);
        }

        public Task Pass() => Tracker.CheckLogFilesAsync(CancellationToken.None);

        public void Dispose()
        {
            Tracker.Dispose();
            _tmp.Dispose();
        }

        private static string Entry(string message, DateTime utcInstant)
        {
            // CMTrace line with a zero writer offset: the tracker reads the local time and
            // resolves it against the reader zone; only freshness (< 24 h) matters here.
            return $"<![LOG[{message}]LOG]!><time=\"{utcInstant:HH:mm:ss.fffffff}\" date=\"{utcInstant:M-d-yyyy}\" " +
                   "component=\"IntuneManagementExtension\" context=\"\" type=\"1\" thread=\"1\" file=\"\">";
        }
    }
}

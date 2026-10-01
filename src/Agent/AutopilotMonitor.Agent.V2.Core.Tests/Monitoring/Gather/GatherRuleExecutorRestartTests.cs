using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Gather
{
    /// <summary>
    /// An agent restart inside one enrollment session — a new <see cref="GatherRuleExecutor"/> on the
    /// same state directory — continues where the previous run stopped: logparser rules do not
    /// replay their log, on_change rules do not re-emit an unchanged result. Regression anchor:
    /// session c8106021, where every restart replayed the whole HP Image Assistant log (70 events).
    /// </summary>
    [Collection("SerialThreading")] // startup rules execute on the shared ThreadPool
    public sealed class GatherRuleExecutorRestartTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly TempDirectory _stateDir = new TempDirectory();
        private readonly List<EnrollmentEvent> _events = new List<EnrollmentEvent>();
        private readonly object _eventsGate = new object();
        private readonly List<GatherRuleExecutor> _executors = new List<GatherRuleExecutor>();

        // %TEMP% lives under C:\Users (hard-blocked) — log targets sit beside the test assembly.
        private readonly string _logDir = Path.Combine(
            AppContext.BaseDirectory, "gather-restart-tests-" + Guid.NewGuid().ToString("N"));

        public GatherRuleExecutorRestartTests()
        {
            Directory.CreateDirectory(_logDir);
        }

        public void Dispose()
        {
            foreach (var executor in _executors)
                executor.Dispose();
            _tmp.Dispose();
            _stateDir.Dispose();
            try { Directory.Delete(_logDir, recursive: true); } catch { /* best effort */ }
        }

        private string StateFile => Path.Combine(_stateDir.Path, GatherRuleStatePersistence.FileName);

        private List<EnrollmentEvent> Events
        {
            get { lock (_eventsGate) return _events.ToList(); }
        }

        /// <summary>One agent run: a fresh executor whose startup rules run once. Not disposed — a hard kill.</summary>
        private GatherRuleExecutor AgentRun(GatherRule rule, string sessionId = "session-a", string? stateDirectory = "default",
            Action<EnrollmentEvent>? onEvent = null, string? debugLogPath = null)
        {
            var executor = new GatherRuleExecutor(
                sessionId, "tenant",
                evt =>
                {
                    onEvent?.Invoke(evt);
                    lock (_eventsGate) _events.Add(evt);
                },
                new AgentLogger(_tmp.Path, AgentLogLevel.Info),
                debugLogPath: debugLogPath,
                stateDirectory: stateDirectory == "default" ? _stateDir.Path : stateDirectory)
            {
                UnrestrictedMode = true
            };
            _executors.Add(executor);
            executor.UpdateRules(new List<GatherRule> { rule });
            Assert.True(executor.WaitForStartupRules(30));
            return executor;
        }

        private static GatherRule LogRule(string path, string id = "hpia-log-collect") => new GatherRule
        {
            RuleId = id,
            Title = id,
            CollectorType = "logparser",
            Target = path,
            Parameters = new Dictionary<string, string> { ["pattern"] = "^(?<status>Update .+)$", ["format"] = "text" },
            Trigger = "startup",
            OutputEventType = "HPiA-UpdateStatus",
            Enabled = true,
        };

        private static GatherRule OnChangeFileRule(string path) => new GatherRule
        {
            RuleId = "GATHER-OC-FILE",
            Title = "file presence",
            CollectorType = "file",
            Target = path,
            Trigger = "startup",
            OutputEventType = "gather_file_presence",
            Enabled = true,
            EmitMode = "on_change",
        };

        private string WriteLog(string name, string content)
        {
            var path = Path.Combine(_logDir, name);
            File.WriteAllText(path, content);
            return path;
        }

        private int StatusEvents => Events.Count(e => e.EventType == "HPiA-UpdateStatus");

        [Fact]
        public void Restart_does_not_replay_the_log_and_new_lines_arrive_once()
        {
            var path = WriteLog("HP Image Assistant.log", "Update one\nUpdate two\nnoise\n");

            AgentRun(LogRule(path));
            Assert.Equal(2, StatusEvents);

            AgentRun(LogRule(path));                      // reboot, nothing new
            Assert.Equal(2, StatusEvents);

            File.AppendAllText(path, "Update three\n");
            AgentRun(LogRule(path));                      // next restart picks up only the new line
            Assert.Equal(3, StatusEvents);
            Assert.Equal("Update three", Events.Last(e => e.EventType == "HPiA-UpdateStatus").Data["status"]);
        }

        [Fact]
        public void Position_is_on_disk_right_after_the_run_without_a_clean_shutdown()
        {
            var path = WriteLog("hardkill.log", "Update one\n");

            AgentRun(LogRule(path));                      // never disposed

            var saved = JsonConvert.DeserializeObject<GatherRuleStateData>(File.ReadAllText(StateFile))!;
            var position = Assert.Single(saved.LogPositions!);
            Assert.Equal(new FileInfo(path).Length, position.Position);
            Assert.Equal("session-a", saved.SessionId);
        }

        [Fact]
        public void Position_is_saved_only_after_the_events_went_out()
        {
            var path = WriteLog("ordering.log", "Update one\n");
            var savedDuringEmit = new List<bool>();

            AgentRun(LogRule(path), onEvent: _ => savedDuringEmit.Add(File.Exists(StateFile)));

            Assert.Equal(new[] { false }, savedDuringEmit);
            Assert.True(File.Exists(StateFile));
        }

        [Fact]
        public void A_new_session_reads_the_log_from_the_beginning()
        {
            var path = WriteLog("rotated.log", "Update one\nUpdate two\n");
            AgentRun(LogRule(path), sessionId: "session-a");

            AgentRun(LogRule(path), sessionId: "session-b");   // session rotated on the same device

            Assert.Equal(4, StatusEvents);
        }

        [Fact]
        public void A_log_replaced_during_the_reboot_is_read_from_the_beginning()
        {
            var path = WriteLog("replaced.log", "Update one\n");
            AgentRun(LogRule(path, "replaced"));

            // The tool writes a new log after the reboot; it is already longer than the old position.
            File.WriteAllText(path, "Update fresh A\nUpdate fresh B\nUpdate fresh C\n");
            AgentRun(LogRule(path, "replaced"));

            Assert.Equal(new[] { "Update one", "Update fresh A", "Update fresh B", "Update fresh C" },
                Events.Where(e => e.EventType == "HPiA-UpdateStatus").Select(e => (string)e.Data["status"]).ToArray());
        }

        [Fact]
        public void An_unchanged_on_change_result_stays_silent_after_a_restart()
        {
            var watched = Path.Combine(_logDir, "marker.txt");

            AgentRun(OnChangeFileRule(watched));          // absent → first result emits
            AgentRun(OnChangeFileRule(watched));          // reboot, still absent → silent
            Assert.Single(Events, e => e.EventType == "gather_file_presence");

            File.WriteAllText(watched, "present");
            AgentRun(OnChangeFileRule(watched));          // changed → emits

            Assert.Equal(2, Events.Count(e => e.EventType == "gather_file_presence"));
        }

        [Fact]
        public void Without_a_state_directory_every_run_starts_over_and_writes_nothing()
        {
            var path = WriteLog("diagnostic.log", "Update one\n");

            AgentRun(LogRule(path), stateDirectory: null);
            AgentRun(LogRule(path), stateDirectory: null);

            Assert.Equal(2, StatusEvents);
            Assert.False(File.Exists(StateFile));
        }

        [Fact]
        public void Debug_trace_says_what_was_restored_and_where_reading_continues()
        {
            var path = WriteLog("traced.log", "Update one\n");
            var trace = Path.Combine(_tmp.Path, "gather-rules-debug.log");
            AgentRun(LogRule(path));
            File.AppendAllText(path, "Update two\n");

            AgentRun(LogRule(path), debugLogPath: trace);

            var text = File.ReadAllText(trace);
            Assert.Contains("gather state: restored 1 logparser position(s) for 1 rule(s)", text);
            Assert.Contains("hpia-log-collect | config | logparser position restored: traced.log", text);
            Assert.Contains("traced.log: continuing at position", text);
        }
    }
}

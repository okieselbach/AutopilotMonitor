#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Security;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// ConsoleBypassHost — what goes onto the single rail: a console the watcher reports becomes a
    /// Warning <c>oobe_console_spawned</c>; starts the watcher only counts become one Info
    /// <c>console_script_activity</c> per window, capped per run, and whatever is still counted goes
    /// out on every way the host stops. The watcher is driven through its internal entry points with
    /// injected probes; no WMI subscription and no window timer runs here.
    /// </summary>
    [Collection("SerialThreading")] // one test runs a real SignalIngress worker
    public sealed class ConsoleBypassHostTests : IDisposable
    {
        private static readonly DateTime At = new DateTime(2026, 11, 11, 14, 41, 35, DateTimeKind.Utc);
        private const string Cmd = "cmd.exe";
        private const string BareCmd = @"C:\WINDOWS\system32\cmd.exe";
        private const string OobeAccount = "S-1-5-21-1-2-3-1000";
        private const int ScriptHost = 2688;
        private const int GoneLauncher = 9016;

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly FakeSignalIngressSink _sink = new FakeSignalIngressSink();
        private readonly AgentLogger _logger;
        private string? _runningCommandLine; // null = every probed process has exited
        private DateTime _now = At;

        public ConsoleBypassHostTests()
        {
            _logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
        }

        public void Dispose() => _tmp.Dispose();

        private ConsoleBypassWatcher NewWatcher() => new ConsoleBypassWatcher(
            _logger,
            processProbe: (pid, name) => _runningCommandLine == null
                ? null
                : new ProcessProbe(name, 1, 0, _runningCommandLine, "20261111144135.000000+060"),
            isAlive: _ => true,
            parentName: pid => pid == ScriptHost ? "powershell.exe" : null,
            utcNow: () => _now);

        private ConsoleBypassHost Build(ConsoleBypassWatcher watcher, ISignalIngressSink? ingress = null) =>
            new ConsoleBypassHost("sess-console", "tenant-console", ingress ?? _sink, new VirtualClock(At), _logger, watcher);

        private static void Trace(ConsoleBypassWatcher watcher, int pid, int parentPid = ScriptHost) =>
            watcher.HandleStart(pid, parentPid, 1, OobeAccount, "process_start_trace", Cmd);

        private IReadOnlyList<FakeSignalIngressSink.PostedSignal> Events(string eventType) =>
            _sink.Posted.Where(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == eventType).ToList();

        private static IReadOnlyDictionary<string, object> Data(FakeSignalIngressSink.PostedSignal s) =>
            (IReadOnlyDictionary<string, object>)s.TypedPayload!;

        // ---------------------------------------------------------------- consoles

        [Fact]
        public void A_console_is_a_warning_with_immediate_upload_and_the_name_of_its_parent()
        {
            _runningCommandLine = BareCmd;
            var watcher = NewWatcher();
            using var host = Build(watcher);

            Trace(watcher, pid: 9636);

            var s = Assert.Single(Events(Constants.EventTypes.OobeConsoleSpawned));
            Assert.Equal("Warning", s.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("true", s.Payload![SignalPayloadKeys.ImmediateUpload]);
            var data = Data(s);
            Assert.Equal("high", data["confidence"]);
            Assert.Equal("powershell.exe", data["parentProcessName"]);
            Assert.Equal(ScriptHost, data["parentProcessId"]);
            Assert.Equal(OobeAccount, data["owner"]);
            Assert.Equal(BareCmd, data["commandLine"]);
            Assert.Empty(Events(Constants.EventTypes.ConsoleScriptActivity));
        }

        [Fact]
        public void A_low_confidence_console_waits_for_the_next_upload_and_names_a_gone_parent_unknown()
        {
            _runningCommandLine = "cmd.exe /k whoami";
            var watcher = NewWatcher();
            using var host = Build(watcher);

            Trace(watcher, pid: 9636, parentPid: GoneLauncher);

            var s = Assert.Single(Events(Constants.EventTypes.OobeConsoleSpawned));
            Assert.Equal("Warning", s.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("false", s.Payload![SignalPayloadKeys.ImmediateUpload]);
            Assert.Equal("low", Data(s)["confidence"]);
            Assert.Equal("unknown", Data(s)["parentProcessName"]);
        }

        // ---------------------------------------------------------------- script activity

        [Fact]
        public void Counted_starts_become_one_info_line_when_the_window_ends()
        {
            var watcher = NewWatcher();
            using var host = Build(watcher);

            Trace(watcher, pid: 5001);
            _now = At.AddSeconds(1);
            Trace(watcher, pid: 5002);
            _now = At.AddSeconds(2);
            Trace(watcher, pid: 5003);
            Assert.Empty(_sink.Posted);

            host.WindowElapsedForTest();

            Assert.Empty(Events(Constants.EventTypes.OobeConsoleSpawned));
            var s = Assert.Single(Events(Constants.EventTypes.ConsoleScriptActivity));
            Assert.Equal("Info", s.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("false", s.Payload![SignalPayloadKeys.ImmediateUpload]);
            Assert.Equal(At.AddSeconds(2), s.OccurredAtUtc);
            Assert.Equal(
                "Script activity: 3 short-lived cmd.exe processes in session 1, started by powershell.exe — counted, not an interactive console",
                s.Payload![SignalPayloadKeys.Message]);

            var data = Data(s);
            Assert.Equal(3, data["count"]);
            Assert.Equal(Cmd, data["processName"]);
            Assert.Equal(At.ToString("o"), data["firstSeenUtc"]);
            Assert.Equal(At.AddSeconds(2).ToString("o"), data["lastSeenUtc"]);
            Assert.Equal(new[] { 1 }, (List<int>)data["windowsSessions"]);
            Assert.Equal(new[] { OobeAccount }, (List<string>)data["owners"]);
            Assert.Equal(1, data["ownersTotal"]);
            Assert.Equal(1, data["parentsTotal"]);
            var parent = Assert.Single((List<Dictionary<string, object>>)data["parents"]);
            Assert.Equal(ScriptHost, parent["pid"]);
            Assert.Equal("powershell.exe", parent["name"]);
            Assert.Equal(3, parent["children"]);
            Assert.False(data.ContainsKey("capped"));
        }

        [Fact]
        public void A_window_without_counted_starts_reports_nothing()
        {
            using var host = Build(NewWatcher());

            host.WindowElapsedForTest();

            Assert.Empty(_sink.Posted);
        }

        [Fact]
        public void Each_window_reports_only_its_own_starts()
        {
            var watcher = NewWatcher();
            using var host = Build(watcher);

            Trace(watcher, pid: 5001);
            host.WindowElapsedForTest();
            Trace(watcher, pid: 5002);
            Trace(watcher, pid: 5003);
            host.WindowElapsedForTest();

            Assert.Equal(new object[] { 1, 2 }, Events(Constants.EventTypes.ConsoleScriptActivity).Select(s => Data(s)["count"]));
        }

        [Fact]
        public void After_the_cap_no_window_reports_and_the_line_at_stop_says_so()
        {
            var watcher = NewWatcher();
            var host = Build(watcher);

            for (var i = 0; i < ConsoleBypassHost.MaxScriptActivitySummaries; i++)
            {
                Trace(watcher, pid: 5000 + i);
                host.WindowElapsedForTest();
            }
            Assert.Equal(ConsoleBypassHost.MaxScriptActivitySummaries, Events(Constants.EventTypes.ConsoleScriptActivity).Count);

            Trace(watcher, pid: 7001);
            Trace(watcher, pid: 7002);
            host.WindowElapsedForTest();
            Assert.Equal(ConsoleBypassHost.MaxScriptActivitySummaries, Events(Constants.EventTypes.ConsoleScriptActivity).Count);

            host.Stop();

            var all = Events(Constants.EventTypes.ConsoleScriptActivity);
            Assert.Equal(ConsoleBypassHost.MaxScriptActivitySummaries + 1, all.Count);
            Assert.All(all.Take(ConsoleBypassHost.MaxScriptActivitySummaries), s => Assert.False(Data(s).ContainsKey("capped")));
            var last = Data(all[all.Count - 1]);
            Assert.Equal(2, last["count"]);
            Assert.Equal(true, last["capped"]);
        }

        // ---------------------------------------------------------------- stopping

        [Fact]
        public void Stopping_reports_what_was_counted_exactly_once()
        {
            var watcher = NewWatcher();
            var host = Build(watcher);
            Trace(watcher, pid: 5001);
            Trace(watcher, pid: 5002);

            host.Stop();
            host.Stop();
            host.Dispose();

            Assert.Equal(2, Data(Assert.Single(Events(Constants.EventTypes.ConsoleScriptActivity)))["count"]);
        }

        [Fact]
        public void Desktop_arrival_reports_what_was_counted()
        {
            var watcher = NewWatcher();
            var host = Build(watcher);
            Trace(watcher, pid: 5001);

            host.StopForDesktopArrival();

            Assert.Single(Events(Constants.EventTypes.ConsoleScriptActivity));
        }

        [Fact]
        public void The_stop_signal_on_the_decision_rail_reports_what_was_counted()
        {
            var clock = new VirtualClock(At);
            using var ingress = new SignalIngress(
                engine: new DecisionEngine(),
                signalLog: new SignalLogWriter(_tmp.File("signal-log.jsonl")),
                traceCounter: new SessionTraceOrdinalProvider(),
                processor: new FakeDecisionStepProcessor(),
                clock: clock);
            var postedEventTypes = new List<string>();
            ingress.SignalPosted += (kind, payload) =>
            {
                if (kind == DecisionSignalKind.InformationalEvent && payload != null
                    && payload.TryGetValue(SignalPayloadKeys.EventType, out var et))
                {
                    lock (postedEventTypes) postedEventTypes.Add(et);
                }
            };
            var watcher = NewWatcher();
            var host = Build(watcher, ingress);
            ingress.Start();
            host.StartCore(armWatcher: false);
            Trace(watcher, pid: 5001);

            ingress.Post(
                DecisionSignalKind.DesktopArrived, At, "DesktopArrivalDetector",
                new Evidence(EvidenceKind.Raw, "desktop", "evidence-desktop"));

            Assert.True(SpinWait.SpinUntil(() => { lock (postedEventTypes) return postedEventTypes.Contains(Constants.EventTypes.ConsoleScriptActivity); }, 5000),
                "the host stopped on the rail signal without reporting what was counted");

            Trace(watcher, pid: 5002); // the watcher is disposed: nothing is counted any more
            Assert.Null(watcher.TakeScriptActivity());
        }

        [Fact]
        public void A_stopped_ingress_does_not_break_the_stop()
        {
            var watcher = NewWatcher();
            var host = Build(watcher);
            Trace(watcher, pid: 5001);
            _sink.ThrowOnPost = new InvalidOperationException("SignalIngress has been stopped.");

            Assert.Null(Record.Exception(() => host.Dispose()));
        }

        // ---------------------------------------------------------------- timeline text

        private static ConsoleScriptActivity Activity(int count, int[] sessions, int parentsTotal, params (int pid, string? name, int children)[] parents) =>
            new ConsoleScriptActivity(
                Cmd, count, At, At, sessions,
                new[] { OobeAccount }, 1,
                parents.Select(p => new ConsoleScriptParent(p.pid, p.name, p.children)).ToList(), parentsTotal);

        [Fact]
        public void One_process_from_a_parent_that_is_gone()
        {
            Assert.Equal(
                "Script activity: 1 short-lived cmd.exe process in session 1, started by an unknown program — counted, not an interactive console",
                ConsoleBypassHost.DescribeScriptActivity(Activity(1, new[] { 1 }, 1, (6688, null, 1))));
        }

        [Fact]
        public void Several_named_parents_in_two_sessions()
        {
            Assert.Equal(
                "Script activity: 46 short-lived cmd.exe processes in sessions 1, 2, started by 2 parent processes (powershell.exe, cmd.exe) — counted, not an interactive console",
                ConsoleBypassHost.DescribeScriptActivity(Activity(46, new[] { 1, 2 }, 2, (2688, "powershell.exe", 25), (9296, "cmd.exe", 21))));
        }

        [Fact]
        public void More_parents_than_names_end_in_an_ellipsis()
        {
            Assert.Equal(
                "Script activity: 9 short-lived cmd.exe processes in session 2, started by 4 parent processes (python.exe, …) — counted, not an interactive console",
                ConsoleBypassHost.DescribeScriptActivity(Activity(9, new[] { 2 }, 4, (10, "python.exe", 5), (11, null, 4))));
        }

        [Fact]
        public void Parents_without_any_name_are_only_counted()
        {
            Assert.Equal(
                "Script activity: 3 short-lived cmd.exe processes in session 1, started by 3 parent processes — counted, not an interactive console",
                ConsoleBypassHost.DescribeScriptActivity(Activity(3, new[] { 1 }, 3, (10, null, 1), (11, null, 1), (12, null, 1))));
        }
    }
}

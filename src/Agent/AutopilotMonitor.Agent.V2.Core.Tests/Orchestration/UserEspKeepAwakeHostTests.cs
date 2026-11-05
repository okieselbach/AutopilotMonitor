using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;
using Xunit;

// ManualResetEventSlim.Wait inside the controller is driven deterministically here; SpinUntil is
// used to await the host's ThreadPool-dispatched engage/release.
#pragma warning disable xUnit1031

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    [Collection("SerialThreading")]
    public sealed class UserEspKeepAwakeHostTests : IDisposable
    {
        private static readonly DateTime At = new DateTime(2026, 6, 16, 10, 0, 0, DateTimeKind.Utc);

        private readonly TempDirectory _tmp = new TempDirectory();

        /// <summary>Fake execution-state API — records prevent/allow without touching OS power policy.</summary>
        private sealed class FakeKeepAwakeApi : IKeepAwakeApi
        {
            private int _preventCount;
            private int _allowCount;
            public int PreventCount => Volatile.Read(ref _preventCount);
            public int AllowCount => Volatile.Read(ref _allowCount);
            public bool PreventSleep() { Interlocked.Increment(ref _preventCount); return true; }
            public bool AllowSleep() { Interlocked.Increment(ref _allowCount); return true; }
        }

        /// <summary>
        /// Fake whose PreventSleep blocks on a test-controlled gate, so the hold thread can be held
        /// past the controller's engage-ready timeout to exercise the late-Set() path.
        /// </summary>
        private sealed class GatedKeepAwakeApi : IKeepAwakeApi
        {
            private readonly ManualResetEventSlim _preventGate;
            private int _preventCount;
            private int _allowCount;
            public GatedKeepAwakeApi(ManualResetEventSlim preventGate) { _preventGate = preventGate; }
            public int PreventCount => Volatile.Read(ref _preventCount);
            public int AllowCount => Volatile.Read(ref _allowCount);
            public bool PreventSleep() { _preventGate.Wait(); Interlocked.Increment(ref _preventCount); return true; }
            public bool AllowSleep() { Interlocked.Increment(ref _allowCount); return true; }
        }

        private AgentLogger NewLogger() => new AgentLogger(_tmp.Path);

        private SignalIngress BuildIngress(VirtualClock clock, string signalLogName = "signal-log.jsonl") =>
            new SignalIngress(
                engine: new DecisionEngine(),
                signalLog: new SignalLogWriter(_tmp.File(signalLogName)),
                traceCounter: new SessionTraceOrdinalProvider(),
                processor: new FakeDecisionStepProcessor(),
                clock: clock);

        private static Evidence Raw(string id = "kw") => new Evidence(EvidenceKind.Raw, id, $"evidence-{id}");

        private static void PostAccountSetupPhase(SignalIngress ing) =>
            ing.Post(
                DecisionSignalKind.EspPhaseChanged, At, "ImeLogTracker", Raw("phase"),
                payload: new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" });

        private static bool WaitFor(Func<bool> condition, int timeoutMs = 5000) =>
            SpinWait.SpinUntil(condition, timeoutMs);

        [Fact]
        public void Engages_keep_awake_on_AccountSetup_phase()
        {
            var clock = new VirtualClock(At);
            using var ing = BuildIngress(clock);
            var api = new FakeKeepAwakeApi();
            var controller = new KeepAwakeController(NewLogger(), api);
            var host = new UserEspKeepAwakeHost(ing, clock, NewLogger(), controller: controller, espTimeoutProvider: () => null);

            ing.Start();
            host.Start();
            PostAccountSetupPhase(ing);

            Assert.True(WaitFor(() => api.PreventCount == 1), "PreventSleep should be called once on AccountSetup entry");
            Assert.True(controller.IsEngaged);
            Assert.Equal(0, api.AllowCount);

            host.Dispose();
            ing.Stop();
        }

        [Fact]
        public void Does_not_engage_on_DeviceSetup_phase()
        {
            var clock = new VirtualClock(At);
            using var ing = BuildIngress(clock);
            var api = new FakeKeepAwakeApi();
            var controller = new KeepAwakeController(NewLogger(), api);
            var host = new UserEspKeepAwakeHost(ing, clock, NewLogger(), controller: controller, espTimeoutProvider: () => null);

            ing.Start();
            host.Start();
            ing.Post(
                DecisionSignalKind.EspPhaseChanged, At, "ImeLogTracker", Raw("dev"),
                payload: new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" });

            // Give any (incorrect) dispatch a chance to run, then assert nothing engaged.
            Assert.False(WaitFor(() => api.PreventCount > 0, 500));
            Assert.False(controller.IsEngaged);

            host.Dispose();
            ing.Stop();
        }

        private static void PostEspExiting(SignalIngress ing) =>
            ing.Post(DecisionSignalKind.EspExiting, At, "EspAndHelloTracker", Raw("exit"));

        [Fact]
        public void Releases_on_EspExiting_once_AccountSetup_shows_progress()
        {
            // q8n: the real user-ESP page exit is a 62407 that arrives after the AccountSetup
            // registry left notStarted (session a2256107: 09:47:12, progress since 09:38:25).
            var clock = new VirtualClock(At);
            using var ing = BuildIngress(clock);
            var api = new FakeKeepAwakeApi();
            var controller = new KeepAwakeController(NewLogger(), api);
            var progress = false;
            var host = new UserEspKeepAwakeHost(ing, clock, NewLogger(), controller: controller,
                espTimeoutProvider: () => null, accountSetupProgressProbe: () => Volatile.Read(ref progress));

            ing.Start();
            host.Start();
            PostAccountSetupPhase(ing);
            Assert.True(WaitFor(() => api.PreventCount == 1));

            Volatile.Write(ref progress, true);
            PostEspExiting(ing);

            Assert.True(WaitFor(() => api.AllowCount == 1), "AllowSleep should be called once on the user-ESP page exit");
            Assert.False(controller.IsEngaged);

            host.Dispose();
            ing.Stop();
        }

        [Fact]
        public void Handoff_exit_before_AccountSetup_progress_keeps_the_hold()
        {
            // The Device→Account handoff 62407 (a2256107: 09:37:53) precedes any AccountSetup
            // progress — Windows pre-writes the AccountSetup JSON all-notStarted — and must not
            // release. The later exit with progress does.
            var clock = new VirtualClock(At);
            using var ing = BuildIngress(clock);
            var api = new FakeKeepAwakeApi();
            var controller = new KeepAwakeController(NewLogger(), api);
            var progress = false;
            var host = new UserEspKeepAwakeHost(ing, clock, NewLogger(), controller: controller,
                espTimeoutProvider: () => null, accountSetupProgressProbe: () => Volatile.Read(ref progress));

            ing.Start();
            host.Start();
            PostAccountSetupPhase(ing);
            Assert.True(WaitFor(() => api.PreventCount == 1));

            PostEspExiting(ing); // handoff exit, no progress yet
            Assert.False(WaitFor(() => api.AllowCount > 0, 500), "the handoff exit must not release the hold");
            Assert.True(controller.IsEngaged);

            Volatile.Write(ref progress, true);
            PostEspExiting(ing); // the real user-ESP page exit
            Assert.True(WaitFor(() => api.AllowCount == 1));
            Assert.False(controller.IsEngaged);

            host.Dispose();
            ing.Stop();
        }

        [Fact]
        public void AccountSetupProvisioningComplete_no_longer_releases()
        {
            // The synthesized completion fired a median 3 min before the page closed in 77 % of
            // keep-awake sessions (q8n) — it is no longer a release trigger; the cap/stop/exit are.
            var clock = new VirtualClock(At);
            using var ing = BuildIngress(clock);
            var api = new FakeKeepAwakeApi();
            var controller = new KeepAwakeController(NewLogger(), api);
            var host = new UserEspKeepAwakeHost(ing, clock, NewLogger(), controller: controller,
                espTimeoutProvider: () => null, accountSetupProgressProbe: () => true);

            ing.Start();
            host.Start();
            PostAccountSetupPhase(ing);
            Assert.True(WaitFor(() => api.PreventCount == 1));

            ing.Post(DecisionSignalKind.AccountSetupProvisioningComplete, At, "ProvisioningStatusTracker", Raw("done"));

            Assert.False(WaitFor(() => api.AllowCount > 0, 500), "completion must not release the hold any more");
            Assert.True(controller.IsEngaged);

            host.Dispose();
            ing.Stop();
        }

        [Fact]
        public void EspExiting_before_engage_is_ignored()
        {
            // Exit signals while idle (e.g. a Device-ESP exit before AccountSetup) neither release
            // nor poison the later engage.
            var clock = new VirtualClock(At);
            using var ing = BuildIngress(clock);
            var api = new FakeKeepAwakeApi();
            var controller = new KeepAwakeController(NewLogger(), api);
            var host = new UserEspKeepAwakeHost(ing, clock, NewLogger(), controller: controller,
                espTimeoutProvider: () => null, accountSetupProgressProbe: () => true);

            ing.Start();
            host.Start();
            PostEspExiting(ing);
            Assert.False(WaitFor(() => api.AllowCount > 0, 300));

            PostAccountSetupPhase(ing);
            Assert.True(WaitFor(() => api.PreventCount == 1));
            Assert.True(controller.IsEngaged);
            Assert.Equal(0, api.AllowCount);

            host.Dispose();
            ing.Stop();
        }

        [Fact]
        public void Engage_is_idempotent_across_repeated_phase_signals()
        {
            var clock = new VirtualClock(At);
            using var ing = BuildIngress(clock);
            var api = new FakeKeepAwakeApi();
            var controller = new KeepAwakeController(NewLogger(), api);
            var host = new UserEspKeepAwakeHost(ing, clock, NewLogger(), controller: controller, espTimeoutProvider: () => null);

            ing.Start();
            host.Start();
            PostAccountSetupPhase(ing);
            PostAccountSetupPhase(ing);
            PostAccountSetupPhase(ing);

            Assert.True(WaitFor(() => api.PreventCount == 1));
            // Allow the pool to settle; a second prevent would be a bug.
            Assert.False(WaitFor(() => api.PreventCount > 1, 500));

            host.Dispose();
            ing.Stop();
        }

        [Fact]
        public void Stop_releases_hold()
        {
            var clock = new VirtualClock(At);
            using var ing = BuildIngress(clock);
            var api = new FakeKeepAwakeApi();
            var controller = new KeepAwakeController(NewLogger(), api);
            var host = new UserEspKeepAwakeHost(ing, clock, NewLogger(), controller: controller, espTimeoutProvider: () => null);

            ing.Start();
            host.Start();
            PostAccountSetupPhase(ing);
            Assert.True(WaitFor(() => api.PreventCount == 1));

            host.Stop();

            Assert.Equal(1, api.AllowCount);
            Assert.False(controller.IsEngaged);

            ing.Stop();
        }

        [Fact]
        public void Safety_cap_releases_hold_without_completion_signal()
        {
            var clock = new VirtualClock(At);
            using var ing = BuildIngress(clock);
            var api = new FakeKeepAwakeApi();
            var controller = new KeepAwakeController(NewLogger(), api);
            var host = new UserEspKeepAwakeHost(
                ing, clock, NewLogger(),
                controller: controller,
                safetyCapOverride: TimeSpan.FromMilliseconds(75),
                espTimeoutProvider: () => null);

            ing.Start();
            host.Start();
            PostAccountSetupPhase(ing);
            Assert.True(WaitFor(() => api.PreventCount == 1));

            // No completion signal — the safety cap must release the hold on its own.
            Assert.True(WaitFor(() => api.AllowCount == 1), "safety cap should release the keep-awake hold");
            Assert.False(controller.IsEngaged);

            host.Dispose();
            ing.Stop();
        }

        // -------- RealmJoin hand-over (D-413) --------

        /// <summary>Records keep_awake_released payloads from the rail (posted on ThreadPool threads).</summary>
        private sealed class ReleaseRecorder
        {
            private readonly object _lock = new object();
            private readonly List<Dictionary<string, string>> _released = new List<Dictionary<string, string>>();

            public ReleaseRecorder(SignalIngress ing) => ing.SignalPosted += OnPosted;

            private void OnPosted(DecisionSignalKind kind, IReadOnlyDictionary<string, string>? payload)
            {
                if (kind != DecisionSignalKind.InformationalEvent || payload == null) return;
                if (!payload.TryGetValue(SignalPayloadKeys.EventType, out var eventType)
                    || eventType != SharedEventTypes.KeepAwakeReleased) return;
                var copy = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in payload) copy[kv.Key] = kv.Value;
                lock (_lock) { _released.Add(copy); }
            }

            public List<Dictionary<string, string>> Snapshot()
            {
                lock (_lock) { return new List<Dictionary<string, string>>(_released); }
            }
        }

        private static void PostRealmJoin(SignalIngress ing, DecisionSignalKind kind, int? phase)
        {
            var payload = new Dictionary<string, string>();
            if (phase.HasValue) payload[DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = phase.Value.ToString();
            ing.Post(kind, At, "RealmJoinWatcher", Raw("rj"), payload: payload);
        }

        private UserEspKeepAwakeHost NewHost(
            SignalIngress ing,
            VirtualClock clock,
            KeepAwakeController controller,
            Func<bool>? progress = null,
            TimeSpan? safetyCap = null,
            TimeSpan? realmJoinWait = null,
            StartupEventGate? gate = null) =>
            new UserEspKeepAwakeHost(ing, clock, NewLogger(),
                controller: controller,
                safetyCapOverride: safetyCap,
                espTimeoutProvider: () => null,
                accountSetupProgressProbe: progress ?? (() => true),
                realmJoinStartWaitOverride: realmJoinWait,
                startupGate: gate);

        [Fact]
        public void Page_exit_with_RealmJoin_still_Blank_holds_until_RealmJoin_starts()
        {
            // The common RealmJoin shape: detected during the ESP, deployment starts after the
            // user-ESP page closed. The gap in between is what the hold now covers.
            var clock = new VirtualClock(At);
            using var ing = BuildIngress(clock);
            var recorder = new ReleaseRecorder(ing);
            var api = new FakeKeepAwakeApi();
            var controller = new KeepAwakeController(NewLogger(), api);
            var host = NewHost(ing, clock, controller, realmJoinWait: TimeSpan.FromMinutes(5));

            ing.Start();
            host.Start();
            PostAccountSetupPhase(ing);
            Assert.True(WaitFor(() => api.PreventCount == 1));
            PostRealmJoin(ing, DecisionSignalKind.RealmJoinDetected, 0);

            PostEspExiting(ing);
            Assert.True(WaitFor(() => host.AwaitingRealmJoinForTest), "the page exit must start the RealmJoin wait");
            PostEspExiting(ing); // a repeated exit changes nothing
            Assert.False(WaitFor(() => api.AllowCount > 0, 500), "the hold must outlive the page exit while RealmJoin is Blank");
            Assert.True(controller.IsEngaged);

            clock.Advance(TimeSpan.FromSeconds(90));
            PostRealmJoin(ing, DecisionSignalKind.RealmJoinPhaseChanged, 101);

            Assert.True(WaitFor(() => api.AllowCount == 1), "RealmJoin leaving Blank must release the hold");
            Assert.True(WaitFor(() => recorder.Snapshot().Count == 1));
            var released = recorder.Snapshot()[0];
            Assert.Equal(UserEspKeepAwakeHost.ReasonRealmJoinStarted, released["reason"]);
            Assert.Equal("90", released["waitedSeconds"]);

            host.Dispose();
            ing.Stop();
        }

        [Fact]
        public void RealmJoin_wait_bound_releases_when_RealmJoin_never_starts()
        {
            // RealmJoin may never leave Blank in the session — the wait is bounded.
            var clock = new VirtualClock(At);
            using var ing = BuildIngress(clock);
            var recorder = new ReleaseRecorder(ing);
            var api = new FakeKeepAwakeApi();
            var controller = new KeepAwakeController(NewLogger(), api);
            var host = NewHost(ing, clock, controller, realmJoinWait: TimeSpan.FromMilliseconds(150));

            ing.Start();
            host.Start();
            PostAccountSetupPhase(ing);
            Assert.True(WaitFor(() => api.PreventCount == 1));
            PostRealmJoin(ing, DecisionSignalKind.RealmJoinDetected, 0);
            PostEspExiting(ing);

            Assert.True(WaitFor(() => api.AllowCount == 1), "the RealmJoin wait must end the hold");
            Assert.True(WaitFor(() => recorder.Snapshot().Count == 1));
            var released = recorder.Snapshot()[0];
            Assert.Equal(UserEspKeepAwakeHost.ReasonRealmJoinNotStarted, released["reason"]);
            Assert.True(released.ContainsKey("waitMinutes"));

            host.Dispose();
            ing.Stop();
        }

        [Fact]
        public void Page_exit_replaces_the_ESP_safety_cap_with_the_RealmJoin_wait()
        {
            // One timer, one bound per phase of the hold: once the page closed, the ESP-sized cap
            // must not end the RealmJoin wait.
            var clock = new VirtualClock(At);
            using var ing = BuildIngress(clock);
            var recorder = new ReleaseRecorder(ing);
            var api = new FakeKeepAwakeApi();
            var controller = new KeepAwakeController(NewLogger(), api);
            var host = NewHost(ing, clock, controller,
                safetyCap: TimeSpan.FromSeconds(1), realmJoinWait: TimeSpan.FromMinutes(5));

            ing.Start();
            host.Start();
            PostRealmJoin(ing, DecisionSignalKind.RealmJoinDetected, 0);
            PostAccountSetupPhase(ing);
            Assert.True(WaitFor(() => api.PreventCount == 1));
            PostEspExiting(ing);
            Assert.True(WaitFor(() => host.AwaitingRealmJoinForTest));

            Assert.False(WaitFor(() => api.AllowCount > 0, 1500), "the superseded ESP cap must not fire");

            PostRealmJoin(ing, DecisionSignalKind.RealmJoinPhaseChanged, 101);
            Assert.True(WaitFor(() => api.AllowCount == 1));
            Assert.True(WaitFor(() => recorder.Snapshot().Count == 1));
            Assert.Equal(UserEspKeepAwakeHost.ReasonRealmJoinStarted, recorder.Snapshot()[0]["reason"]);

            host.Dispose();
            ing.Stop();
        }

        [Fact]
        public void Page_exit_without_RealmJoin_releases_as_before()
        {
            var clock = new VirtualClock(At);
            using var ing = BuildIngress(clock);
            var recorder = new ReleaseRecorder(ing);
            var api = new FakeKeepAwakeApi();
            var controller = new KeepAwakeController(NewLogger(), api);
            var host = NewHost(ing, clock, controller, realmJoinWait: TimeSpan.FromMinutes(5));

            ing.Start();
            host.Start();
            PostAccountSetupPhase(ing);
            Assert.True(WaitFor(() => api.PreventCount == 1));
            PostEspExiting(ing);

            Assert.True(WaitFor(() => api.AllowCount == 1));
            Assert.True(WaitFor(() => recorder.Snapshot().Count == 1));
            Assert.Equal(UserEspKeepAwakeHost.ReasonUserEspExited, recorder.Snapshot()[0]["reason"]);
            Assert.False(host.AwaitingRealmJoinForTest);

            host.Dispose();
            ing.Stop();
        }

        [Theory]
        [InlineData(DecisionSignalKind.RealmJoinPhaseChanged, 100)]
        [InlineData(DecisionSignalKind.RealmJoinPhaseChanged, 210)]
        [InlineData(DecisionSignalKind.RealmJoinResolved, null)]
        public void RealmJoin_leaving_Blank_before_the_page_exit_releases_the_hold(DecisionSignalKind kind, int? phase)
        {
            // RealmJoin can start shortly before the page closes; it keeps the device awake from
            // there. Resolved carries no phase and still means "left Blank".
            var clock = new VirtualClock(At);
            using var ing = BuildIngress(clock);
            var recorder = new ReleaseRecorder(ing);
            var api = new FakeKeepAwakeApi();
            var controller = new KeepAwakeController(NewLogger(), api);
            var host = NewHost(ing, clock, controller, realmJoinWait: TimeSpan.FromMinutes(5));

            ing.Start();
            host.Start();
            PostAccountSetupPhase(ing);
            Assert.True(WaitFor(() => api.PreventCount == 1));
            PostRealmJoin(ing, DecisionSignalKind.RealmJoinDetected, 0);
            Assert.False(WaitFor(() => api.AllowCount > 0, 300), "detection alone must not release");

            PostRealmJoin(ing, kind, phase);

            Assert.True(WaitFor(() => api.AllowCount == 1));
            Assert.True(WaitFor(() => recorder.Snapshot().Count == 1));
            var released = recorder.Snapshot()[0];
            Assert.Equal(UserEspKeepAwakeHost.ReasonRealmJoinStarted, released["reason"]);
            Assert.False(released.ContainsKey("waitedSeconds"));

            host.Dispose();
            ing.Stop();
        }

        [Fact]
        public void RealmJoin_already_deploying_before_AccountSetup_prevents_the_engage()
        {
            // Agent restart: the watcher reports the running phase from its start, and the
            // AccountSetup phase is posted again afterwards — there is nothing to hand over.
            var clock = new VirtualClock(At);
            using var ing = BuildIngress(clock);
            var api = new FakeKeepAwakeApi();
            var controller = new KeepAwakeController(NewLogger(), api);
            var host = NewHost(ing, clock, controller, realmJoinWait: TimeSpan.FromMinutes(5));

            ing.Start();
            host.Start();
            PostRealmJoin(ing, DecisionSignalKind.RealmJoinDetected, 101);
            PostAccountSetupPhase(ing);

            Assert.False(WaitFor(() => api.PreventCount > 0, 500), "no hold once RealmJoin deploys");
            Assert.False(controller.IsEngaged);

            host.Dispose();
            ing.Stop();
        }

        // -------- Page-closed latch across agent restarts (D-414) --------

        /// <summary>
        /// One agent run: its own ingress and host on the shared state folder. A later run is a new
        /// <see cref="StartupEventGate"/> loading the same folder, exactly what a restart does.
        /// </summary>
        private sealed class AgentRun : IDisposable
        {
            public SignalIngress Ingress { get; }
            public FakeKeepAwakeApi Api { get; } = new FakeKeepAwakeApi();
            public KeepAwakeController Controller { get; }
            public StartupEventGate Gate { get; }
            public UserEspKeepAwakeHost Host { get; }

            public AgentRun(UserEspKeepAwakeHostTests t, string stateDir, string runName, Func<bool> progress)
            {
                var clock = new VirtualClock(At);
                Ingress = t.BuildIngress(clock, $"signal-log-{runName}.jsonl");
                Controller = new KeepAwakeController(t.NewLogger(), Api);
                Gate = new StartupEventGate(stateDir, t.NewLogger());
                Host = t.NewHost(Ingress, clock, Controller, progress: progress,
                    realmJoinWait: TimeSpan.FromMinutes(5), gate: Gate);
                Ingress.Start();
                Host.Start();
            }

            public void Dispose()
            {
                Host.Dispose();
                Ingress.Stop();
                Ingress.Dispose();
            }
        }

        [Fact]
        public void Closed_page_keeps_a_later_agent_run_from_engaging()
        {
            // After an agent restart the AccountSetup phase reaches the rail again (IME logs it at
            // the next sign-in, the adapter's dedup lives per process). The page is gone, so no hold.
            using var stateDir = new TempDirectory();
            using (var first = new AgentRun(this, stateDir.Path, "1", () => true))
            {
                PostAccountSetupPhase(first.Ingress);
                Assert.True(WaitFor(() => first.Api.PreventCount == 1));
                PostEspExiting(first.Ingress);
                Assert.True(WaitFor(() => first.Api.AllowCount == 1));
                Assert.True(WaitFor(() => first.Gate.AlreadySucceeded(UserEspKeepAwakeHost.UserEspPageClosedKey)));
            }

            using var second = new AgentRun(this, stateDir.Path, "2", () => true);
            PostAccountSetupPhase(second.Ingress);

            Assert.False(WaitFor(() => second.Api.PreventCount > 0, 500), "the page already closed in this enrollment");
            Assert.False(second.Controller.IsEngaged);
        }

        [Fact]
        public void Restart_before_the_page_closed_engages_again()
        {
            // A reboot in the middle of the user ESP: the page is still to come, so the next run holds.
            using var stateDir = new TempDirectory();
            using (var first = new AgentRun(this, stateDir.Path, "1", () => true))
            {
                PostAccountSetupPhase(first.Ingress);
                Assert.True(WaitFor(() => first.Api.PreventCount == 1));
            }

            using var second = new AgentRun(this, stateDir.Path, "2", () => true);
            PostAccountSetupPhase(second.Ingress);

            Assert.True(WaitFor(() => second.Api.PreventCount == 1), "the user ESP is still running");
            Assert.True(second.Controller.IsEngaged);
        }

        [Fact]
        public void Handoff_exit_without_AccountSetup_progress_sets_no_latch()
        {
            using var stateDir = new TempDirectory();
            using (var first = new AgentRun(this, stateDir.Path, "1", () => false))
            {
                PostAccountSetupPhase(first.Ingress);
                Assert.True(WaitFor(() => first.Api.PreventCount == 1));
                PostEspExiting(first.Ingress); // the Device→Account handoff
                Assert.False(WaitFor(() => first.Api.AllowCount > 0, 300));
                Assert.False(first.Gate.AlreadySucceeded(UserEspKeepAwakeHost.UserEspPageClosedKey));
            }

            using var second = new AgentRun(this, stateDir.Path, "2", () => true);
            PostAccountSetupPhase(second.Ingress);

            Assert.True(WaitFor(() => second.Api.PreventCount == 1));
        }

        [Fact]
        public void Page_exit_while_waiting_for_RealmJoin_is_latched()
        {
            // The hold outlived the page while RealmJoin was Blank, then the agent restarted: after
            // the reboot RealmJoin needs the next sign-in anyway, so the next run does not hold.
            using var stateDir = new TempDirectory();
            using (var first = new AgentRun(this, stateDir.Path, "1", () => true))
            {
                PostRealmJoin(first.Ingress, DecisionSignalKind.RealmJoinDetected, 0);
                PostAccountSetupPhase(first.Ingress);
                Assert.True(WaitFor(() => first.Api.PreventCount == 1));
                PostEspExiting(first.Ingress);
                Assert.True(WaitFor(() => first.Host.AwaitingRealmJoinForTest));
                Assert.True(WaitFor(() => first.Gate.AlreadySucceeded(UserEspKeepAwakeHost.UserEspPageClosedKey)));
            }

            using var second = new AgentRun(this, stateDir.Path, "2", () => true);
            PostRealmJoin(second.Ingress, DecisionSignalKind.RealmJoinDetected, 0);
            PostAccountSetupPhase(second.Ingress);

            Assert.False(WaitFor(() => second.Api.PreventCount > 0, 500));
        }

        [Fact]
        public void Page_exit_seen_while_idle_is_latched_and_blocks_a_later_engage()
        {
            // The exit counts in every hold state — here the host never engaged before it.
            using var stateDir = new TempDirectory();
            using var run = new AgentRun(this, stateDir.Path, "1", () => true);

            PostEspExiting(run.Ingress);
            Assert.True(WaitFor(() => run.Gate.AlreadySucceeded(UserEspKeepAwakeHost.UserEspPageClosedKey)),
                "an exit seen while idle must still be latched");

            PostAccountSetupPhase(run.Ingress);
            Assert.False(WaitFor(() => run.Api.PreventCount > 0, 500));
        }

        // -------- KeepAwakeController: late-confirm (ready-timeout) edge --------

        [Fact]
        public void Controller_engage_survives_hold_thread_confirming_after_ready_timeout()
        {
            using var preventGate = new ManualResetEventSlim(false);
            var api = new GatedKeepAwakeApi(preventGate);
            using var controller = new KeepAwakeController(
                NewLogger(), api, engageReadyTimeout: TimeSpan.FromMilliseconds(100));

            // PreventSleep is gated shut, so the hold thread is parked inside it and cannot reach
            // ready.Set() before Engage()'s ready-wait times out.
            var confirmed = controller.Engage();
            Assert.False(confirmed);             // timeout path — not confirmed
            Assert.True(controller.IsEngaged);   // hold was still requested
            Assert.Equal(0, api.PreventCount);   // hold thread still parked in PreventSleep

            // Now let the hold thread proceed: it returns from PreventSleep and calls ready.Set()
            // AFTER Engage() already returned on the timeout path. This pins down the late-confirm
            // contract — the hold still applies and a later Release still works.
            //
            // (Note: on net48 ManualResetEventSlim.Set() after Dispose() is a benign no-op — verified
            // empirically — so the controller's "don't dispose `ready` on timeout" guard is
            // defense-in-depth, not a crash fix here. The guard still matters if the wait primitive
            // is ever swapped for one whose Set() rejects after Dispose; this test would then catch
            // it because the hold thread would die at ready.Set() and never reach AllowSleep below.)
            preventGate.Set();
            Assert.True(WaitFor(() => api.PreventCount == 1), "hold thread should apply PreventSleep once the gate opens");

            // Release still works cleanly after the late confirm.
            Assert.True(controller.Release());
            Assert.True(WaitFor(() => api.AllowCount == 1));
            Assert.False(controller.IsEngaged);
        }

        // -------- Cap-resolution policy (pure) --------

        [Theory]
        [InlineData(60, 90, 90, "esp_timeout")]    // Intune ESP default 60 + 30 margin
        [InlineData(120, 90, 150, "esp_timeout")]  // longer ESP policy scales the cap
        [InlineData(45, 90, 75, "esp_timeout")]
        [InlineData(null, 90, 90, "default")]      // ESP timeout unavailable → fallback
        [InlineData(0, 90, 90, "default")]         // non-positive ESP value treated as unavailable
        [InlineData(null, 120, 120, "default")]    // custom fallback honoured
        public void ResolveSafetyCapMinutes_derives_cap_from_esp_timeout_or_fallback(
            int? espTimeout, int fallback, int expectedCap, string expectedSource)
        {
            var (capMinutes, capSource) = UserEspKeepAwakeHost.ResolveSafetyCapMinutes(espTimeout, fallback);
            Assert.Equal(expectedCap, capMinutes);
            Assert.Equal(expectedSource, capSource);
        }

        public void Dispose() => _tmp.Dispose();
    }
}

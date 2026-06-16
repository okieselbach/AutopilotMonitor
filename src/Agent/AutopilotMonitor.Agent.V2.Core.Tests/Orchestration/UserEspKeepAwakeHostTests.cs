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

        private SignalIngress BuildIngress(VirtualClock clock) =>
            new SignalIngress(
                engine: new DecisionEngine(),
                signalLog: new SignalLogWriter(_tmp.File("signal-log.jsonl")),
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
            var host = new UserEspKeepAwakeHost("S1", "T1", ing, clock, NewLogger(), controller: controller, espTimeoutProvider: () => null);

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
            var host = new UserEspKeepAwakeHost("S1", "T1", ing, clock, NewLogger(), controller: controller, espTimeoutProvider: () => null);

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

        [Fact]
        public void Releases_on_AccountSetupProvisioningComplete()
        {
            var clock = new VirtualClock(At);
            using var ing = BuildIngress(clock);
            var api = new FakeKeepAwakeApi();
            var controller = new KeepAwakeController(NewLogger(), api);
            var host = new UserEspKeepAwakeHost("S1", "T1", ing, clock, NewLogger(), controller: controller, espTimeoutProvider: () => null);

            ing.Start();
            host.Start();
            PostAccountSetupPhase(ing);
            Assert.True(WaitFor(() => api.PreventCount == 1));

            ing.Post(DecisionSignalKind.AccountSetupProvisioningComplete, At, "ProvisioningStatusTracker", Raw("done"));

            Assert.True(WaitFor(() => api.AllowCount == 1), "AllowSleep should be called once on AccountSetup completion");
            Assert.False(controller.IsEngaged);

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
            var host = new UserEspKeepAwakeHost("S1", "T1", ing, clock, NewLogger(), controller: controller, espTimeoutProvider: () => null);

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
            var host = new UserEspKeepAwakeHost("S1", "T1", ing, clock, NewLogger(), controller: controller, espTimeoutProvider: () => null);

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
                "S1", "T1", ing, clock, NewLogger(),
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

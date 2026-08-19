#nullable enable
using System;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Codex review P2 (2026-08-19) — <see cref="ImeLogHost"/> starts AFTER <c>EspAndHelloHost</c>,
    /// and <c>ImeLogTracker.Start()</c> restores the persisted package states silently (LoadState
    /// raises no <c>OnAppStateChanged</c>). On an agent restart the Shell-Core ESP-exit replay
    /// therefore runs against an empty app list. Without the post-restore nudge the exact reboot
    /// case the completion recovery exists for — apps already terminal BEFORE the reboot, only the
    /// final exit lost to the downtime — would still never complete.
    /// </summary>
    public sealed class ImeLogHostStateRestoredCallbackTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();

        public void Dispose() => _tmp.Dispose();

        private ImeLogHost CreateHost(Action? onStateRestored)
            => new ImeLogHost(
                sessionId: "s",
                tenantId: "t",
                logger: new AgentLogger(_tmp.Path, AgentLogLevel.Info),
                ingress: new FakeSignalIngressSink(),
                clock: new VirtualClock(new DateTime(2026, 8, 19, 8, 43, 16, DateTimeKind.Utc)),
                imeLogPathOverride: _tmp.Path,
                imeMatchLogPath: null,
                imePatterns: null,
                stateDirectory: _tmp.Path,
                whiteGloveSealingPatternIds: null,
                onStateRestored: onStateRestored);

        [Fact]
        public void Start_invokes_the_state_restored_callback_exactly_once()
        {
            var calls = 0;
            using var host = CreateHost(() => calls++);

            host.Start();
            try { Assert.Equal(1, calls); }
            finally { host.Stop(); }
        }

        [Fact]
        public void A_throwing_callback_does_not_break_host_start()
        {
            // Fail-soft like every other co-wired callback in the orchestration layer: a broken
            // re-check must never take the IME log host — the session's primary signal source —
            // down with it.
            using var host = CreateHost(() => throw new InvalidOperationException("recheck boom"));

            var ex = Record.Exception(() => host.Start());
            try { Assert.Null(ex); }
            finally { host.Stop(); }
        }

        [Fact]
        public void No_callback_is_a_valid_configuration()
        {
            using var host = CreateHost(null);

            var ex = Record.Exception(() => host.Start());
            try { Assert.Null(ex); }
            finally { host.Stop(); }
        }
    }
}

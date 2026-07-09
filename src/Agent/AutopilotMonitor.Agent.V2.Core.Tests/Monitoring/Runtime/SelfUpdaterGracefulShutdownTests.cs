using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Runtime
{
    /// <summary>
    /// Contract tests for <see cref="SelfUpdater.TryInitiateGracefulShutdown"/> — the
    /// coordination seam between the runtime self-update restart and the runtime host
    /// (session b9b92d89, 2026-07-09: an uncoordinated <c>Environment.Exit</c> from the
    /// update thread killed the live orchestrator mid-SignalLog-append and produced the
    /// duplicated-line recovery crash-loop). The host wires
    /// <see cref="SelfUpdater.RequestGracefulShutdown"/> before <c>orchestrator.Start</c>
    /// and clears it in its shutdown finally; RestartAgent hard-exits only when this
    /// helper returns <c>false</c>.
    /// <para>
    /// Static-hook state: each test saves/restores the hook so parallel-running test
    /// classes never observe a leaked value.
    /// </para>
    /// </summary>
    public sealed class SelfUpdaterGracefulShutdownTests : IDisposable
    {
        private readonly Func<bool> _savedHook;

        public SelfUpdaterGracefulShutdownTests()
        {
            _savedHook = SelfUpdater.RequestGracefulShutdown;
            SelfUpdater.RequestGracefulShutdown = null;
        }

        public void Dispose() => SelfUpdater.RequestGracefulShutdown = _savedHook;

        [Fact]
        public void No_hook_wired_returns_false_immediate_exit_path()
        {
            // Startup-phase updates: no orchestrator exists, direct Environment.Exit is safe.
            Assert.False(SelfUpdater.TryInitiateGracefulShutdown(log: null));
        }

        [Fact]
        public void Hook_returning_true_initiates_graceful_path()
        {
            var invoked = false;
            SelfUpdater.RequestGracefulShutdown = () => { invoked = true; return true; };

            Assert.True(SelfUpdater.TryInitiateGracefulShutdown(log: null));
            Assert.True(invoked);
        }

        [Fact]
        public void Hook_returning_false_falls_back_to_immediate_exit()
        {
            SelfUpdater.RequestGracefulShutdown = () => false;

            Assert.False(SelfUpdater.TryInitiateGracefulShutdown(log: null));
        }

        [Fact]
        public void Hook_throwing_is_swallowed_logged_and_falls_back()
        {
            // Race with the host's shutdown finally: the hook closure may touch an already
            // disposed ManualResetEventSlim. The helper must degrade to the immediate-exit
            // fallback (safe once the host is gone) instead of crashing the update thread.
            var logLines = new List<string>();
            SelfUpdater.RequestGracefulShutdown =
                () => throw new ObjectDisposedException("shutdown");

            Assert.False(SelfUpdater.TryInitiateGracefulShutdown(logLines.Add));
            Assert.Contains(logLines, l => l.Contains("graceful-shutdown hook threw"));
        }

        [Fact]
        public void Graceful_fallback_stays_inside_restart_script_wait_window()
        {
            // The restart script uses `Wait-Process -Timeout 30`; if the old process
            // outlives it, the new agent starts alongside and self-kills via the
            // multi-instance guard — no agent left until next boot. The hard-exit
            // fallback must therefore stay well below 30s.
            Assert.InRange(SelfUpdater.GracefulExitFallbackMs, 1000, 25000);
        }
    }
}

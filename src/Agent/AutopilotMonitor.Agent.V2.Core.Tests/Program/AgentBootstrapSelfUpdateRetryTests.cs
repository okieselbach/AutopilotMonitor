using System;
using System.IO;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Runtime;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Contract tests for <see cref="AgentBootstrap.RetrySelfUpdateAfterEnrollment"/> —
    /// the post-cert one-shot self-update retry (field case sits-d.cloud, 2026-08-09):
    /// guard on <see cref="SelfUpdater.LastVersionCheckSucceeded"/>, the relaxed
    /// timeouts/trigger it passes to the updater, and the swallow-everything exception
    /// contract (bootstrap must continue on any updater failure). Uses the
    /// <see cref="AgentBootstrap.UpdateInvokerOverride"/> seam — no network, no process
    /// side effects.
    /// <para>
    /// Static-hook state: each test saves/restores both statics in ctor/Dispose so
    /// parallel-running test classes never observe a leaked value (idiom of
    /// SelfUpdaterGracefulShutdownTests).
    /// </para>
    /// </summary>
    public sealed class AgentBootstrapSelfUpdateRetryTests : IDisposable
    {
        private readonly AgentBootstrap.SelfUpdateInvoker _savedInvoker;
        private readonly bool _savedVersionCheckFlag;

        public AgentBootstrapSelfUpdateRetryTests()
        {
            _savedInvoker = AgentBootstrap.UpdateInvokerOverride;
            _savedVersionCheckFlag = SelfUpdater.LastVersionCheckSucceeded;
            AgentBootstrap.UpdateInvokerOverride = null;
            SelfUpdater.LastVersionCheckSucceeded = false;
        }

        public void Dispose()
        {
            AgentBootstrap.UpdateInvokerOverride = _savedInvoker;
            SelfUpdater.LastVersionCheckSucceeded = _savedVersionCheckFlag;
        }

        private static AgentLogger NewLogger(string path)
            => new AgentLogger(Path.Combine(path, "logs"), AgentLogLevel.Info);

        [Fact]
        public void Skips_retry_when_startup_version_check_already_succeeded()
        {
            using var tmp = new TempDirectory();
            var invoked = false;
            SelfUpdater.LastVersionCheckSucceeded = true;
            AgentBootstrap.UpdateInvokerOverride = (v, dir, console, reason, dl, downgrade, vc) =>
            {
                invoked = true;
                return Task.CompletedTask;
            };

            AgentBootstrap.RetrySelfUpdateAfterEnrollment(NewLogger(tmp.Path), consoleMode: false);

            Assert.False(invoked);
        }

        [Fact]
        public void Retries_with_await_enrollment_trigger_and_relaxed_timeouts()
        {
            using var tmp = new TempDirectory();
            string? capturedVersion = null, capturedDir = null, capturedReason = null;
            bool? capturedConsole = null, capturedDowngrade = null;
            int capturedDownloadMs = 0, capturedVersionCheckMs = 0;
            AgentBootstrap.UpdateInvokerOverride = (v, dir, console, reason, dl, downgrade, vc) =>
            {
                capturedVersion = v;
                capturedDir = dir;
                capturedConsole = console;
                capturedReason = reason;
                capturedDownloadMs = dl;
                capturedDowngrade = downgrade;
                capturedVersionCheckMs = vc;
                return Task.CompletedTask;
            };

            AgentBootstrap.RetrySelfUpdateAfterEnrollment(NewLogger(tmp.Path), consoleMode: true);

            Assert.Equal("await_enrollment", capturedReason);
            // Relaxed vs. the boot-speed-tuned startup check (10s/2.5s) — enrollment is
            // already past OOBE's critical path here.
            Assert.Equal(60000, capturedDownloadMs);
            Assert.Equal(10000, capturedVersionCheckMs);
            // No cached remote config on a fresh --await-enrollment install → no downgrade override.
            Assert.False(capturedDowngrade);
            Assert.True(capturedConsole);
            Assert.False(string.IsNullOrEmpty(capturedVersion));
            Assert.False(string.IsNullOrEmpty(capturedDir));
        }

        [Fact]
        public void Synchronous_invoker_exception_is_swallowed()
        {
            using var tmp = new TempDirectory();
            AgentBootstrap.UpdateInvokerOverride =
                (v, dir, console, reason, dl, downgrade, vc) => throw new InvalidOperationException("boom");

            var ex = Record.Exception(
                () => AgentBootstrap.RetrySelfUpdateAfterEnrollment(NewLogger(tmp.Path), consoleMode: false));

            Assert.Null(ex);
        }

        [Fact]
        public void Faulted_update_task_is_swallowed()
        {
            using var tmp = new TempDirectory();
            AgentBootstrap.UpdateInvokerOverride = (v, dir, console, reason, dl, downgrade, vc) =>
                Task.FromException(new IOException("download interrupted"));

            var ex = Record.Exception(
                () => AgentBootstrap.RetrySelfUpdateAfterEnrollment(NewLogger(tmp.Path), consoleMode: false));

            Assert.Null(ex);
        }
    }
}

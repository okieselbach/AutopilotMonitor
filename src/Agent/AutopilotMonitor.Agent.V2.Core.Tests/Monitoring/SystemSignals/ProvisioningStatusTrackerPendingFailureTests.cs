using System;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// <see cref="ProvisioningStatusTracker.TryGetPendingFailureArgs"/> exposes the registry
    /// failure detail while its settle window is open and nothing once the window has fired —
    /// the coordinator reads it when a Shell-Core failure terminalises first (session 683f1eff).
    /// </summary>
    public sealed class ProvisioningStatusTrackerPendingFailureTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 9, 8, 18, 11, 2, DateTimeKind.Utc);

        private const string InProgressJson = @"{
            ""categorySucceeded"": null,
            ""AppsSubcategory"": {""subcategoryState"":""inProgress"",""subcategoryStatusText"":""Apps (Working)""}
        }";
        private const string AppsFailedJson = @"{
            ""categorySucceeded"": null,
            ""AppsSubcategory"": {""subcategoryState"":""failed"",""subcategoryStatusText"":""Apps (0x80070652)""}
        }";

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public ProvisioningStatusTracker Tracker { get; }

            public Fixture()
            {
                var post = new InformationalEventPost(new FakeSignalIngressSink(), new VirtualClock(Fixed));
                var logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Tracker = new ProvisioningStatusTracker(
                    sessionId: "S1",
                    tenantId: "T1",
                    post: post,
                    logger: logger,
                    appxScanner: new FakeAppxDeploymentFailureScanner(),
                    backgroundDispatcher: action => action());
            }

            public void Dispose()
            {
                Tracker.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void Nothing_pending_before_any_failure()
        {
            using var f = new Fixture();
            Assert.Null(f.Tracker.TryGetPendingFailureArgs());

            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", InProgressJson);
            Assert.Null(f.Tracker.TryGetPendingFailureArgs());
        }

        [Fact]
        public void Armed_settle_window_exposes_the_registry_detail()
        {
            using var f = new Fixture();
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", InProgressJson);
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", AppsFailedJson);

            var pending = f.Tracker.TryGetPendingFailureArgs();

            Assert.NotNull(pending);
            Assert.Equal("Provisioning_DeviceSetup_Apps_Failed", pending!.FailureType);
            Assert.Equal("0x80070652", pending.ErrorCode);
            Assert.Equal("Apps", pending.FailedSubcategory);
            Assert.Equal("DeviceSetup", pending.Category);
        }

        [Fact]
        public void Expired_settle_window_leaves_nothing_pending()
        {
            using var f = new Fixture();
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", InProgressJson);
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", AppsFailedJson);

            f.Tracker.TriggerSettleTimerForTest("DeviceSetupCategory.Status");

            Assert.Null(f.Tracker.TryGetPendingFailureArgs());
        }
    }
}

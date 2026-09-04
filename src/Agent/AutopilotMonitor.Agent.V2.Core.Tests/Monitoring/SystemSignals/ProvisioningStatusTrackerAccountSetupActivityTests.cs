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
    /// Backlog q8n — <see cref="ProvisioningStatusTracker.HasAccountSetupActivity"/> must mean
    /// observed progress, not registry presence. Windows writes
    /// <c>AccountSetupCategory.Status</c> with every subcategory <c>notStarted</c> BEFORE the
    /// Device-ESP page exits, so a presence check labelled the intermediate Device→Account
    /// Shell-Core 62407 as the final ESP exit; the user-apps-settled synthesis then completed
    /// AccountSetup minutes early and released the User-ESP keep-awake while the ESP page was
    /// still up (session a2256107, standby on battery at 09:43 with the real exit at 09:47).
    /// Both <c>EspAndHelloTracker</c> guards (<c>IsIntermediateDeviceEspExit</c>,
    /// <c>IsConfirmedPostAccountSetupExit</c>) hang off this property.
    /// </summary>
    public sealed class ProvisioningStatusTrackerAccountSetupActivityTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 9, 4, 9, 37, 42, DateTimeKind.Utc);

        // Verbatim shape of the pre-written registry JSON seen at first_seen on 25H2/26200.
        private const string AllNotStartedJson = @"{
            ""categoryState"":""notStarted"",
            ""AccountSetup.WaitingForAadRegistrationSubcategory"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.PrepareMultifactorAuth"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.SecurityPoliciesSubcategory"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.CertificatesSubcategory"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.NetworkConnectionsSubcategory"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.AppsSubcategory"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.SendResultsToMdmServer"":{""subcategoryState"":""notStarted""}
        }";

        private const string FirstProgressJson = @"{
            ""categoryState"":""inProgress"",
            ""AccountSetup.WaitingForAadRegistrationSubcategory"":{""subcategoryState"":""succeeded"",""subcategoryStatusText"":""succeeded""},
            ""AccountSetup.PrepareMultifactorAuth"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.SecurityPoliciesSubcategory"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.CertificatesSubcategory"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.NetworkConnectionsSubcategory"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.AppsSubcategory"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.SendResultsToMdmServer"":{""subcategoryState"":""notStarted""}
        }";

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public FakeSignalIngressSink Sink { get; } = new FakeSignalIngressSink();
            public ProvisioningStatusTracker Tracker { get; }

            public Fixture()
            {
                var clock = new VirtualClock(Fixed);
                var post = new InformationalEventPost(Sink, clock);
                var logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Tracker = new ProvisioningStatusTracker(
                    sessionId: "S1",
                    tenantId: "T1",
                    post: post,
                    logger: logger);
            }

            public void Dispose()
            {
                Tracker.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void NothingTracked_IsNotActivity()
        {
            using var f = new Fixture();
            Assert.False(f.Tracker.HasAccountSetupActivity);
        }

        [Fact]
        public void PreWrittenAllNotStartedJson_IsNotActivity()
        {
            // The registry JSON exists (7 subcategories) but nothing has started — this is the
            // state at the intermediate Device-ESP exit and must NOT confirm it as final.
            using var f = new Fixture();
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", AllNotStartedJson);

            Assert.False(f.Tracker.HasAccountSetupActivity);
        }

        [Fact]
        public void FirstSubcategoryLeavingNotStarted_IsActivity()
        {
            using var f = new Fixture();
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", AllNotStartedJson);
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", FirstProgressJson);

            Assert.True(f.Tracker.HasAccountSetupActivity);
        }

        [Theory]
        [InlineData("inProgress")]
        [InlineData("in_progress")]
        [InlineData("failed")]
        [InlineData("notRequired")]
        public void AnyNonNotStartedState_IsActivity(string state)
        {
            using var f = new Fixture();
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status",
                "{\"categoryState\":\"inProgress\"," +
                "\"AccountSetup.AppsSubcategory\":{\"subcategoryState\":\"" + state + "\"}," +
                "\"AccountSetup.CertificatesSubcategory\":{\"subcategoryState\":\"notStarted\"}}");

            Assert.True(f.Tracker.HasAccountSetupActivity);
        }

        [Fact]
        public void DeviceSetupProgress_DoesNotCountAsAccountSetupActivity()
        {
            // Adversarial: a fully progressed DeviceSetup category must not leak into the
            // AccountSetup answer.
            using var f = new Fixture();
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status",
                "{\"categoryState\":\"succeeded\",\"DeviceSetup.AppsSubcategory\":{\"subcategoryState\":\"succeeded\"}}");
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", AllNotStartedJson);

            Assert.False(f.Tracker.HasAccountSetupActivity);
        }
    }
}
